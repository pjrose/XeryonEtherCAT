using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Models;

namespace XeryonEtherCAT.Core.Internal.Simulation;

/// <summary>
/// Simple EtherCAT master simulator that mimics the behaviour of four Xeryon axes.
/// This implementation is used by the sample UI and as a fallback when the native SOEM shim is not available.
/// </summary>
public sealed class SimulatedEtherCatMaster : IEtherCatMaster
{
    private readonly List<AxisSimulation> _axes;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private ConnectionState _state = ConnectionState.Disconnected;

    public SimulatedEtherCatMaster(int axisCount = 4)
    {
        if (axisCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(axisCount));
        }

        _axes = Enumerable.Range(1, axisCount)
            .Select(i => new AxisSimulation(i))
            .ToList();
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public ConnectionState ConnectionState => _state;

    public Task<IReadOnlyList<EtherCatSlaveInfo>> ConnectAsync(string networkInterfaceName, CancellationToken cancellationToken)
    {
        _state = ConnectionState.Operational;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Operational, ConnectionState.Disconnected));

        IReadOnlyList<EtherCatSlaveInfo> slaves = _axes
            .Select(axis => new EtherCatSlaveInfo(axis.AxisNumber, 0x0000004E, 0x00000001, 0x00000001, $"Simulated Drive #{axis.AxisNumber}"))
            .ToList();

        return Task.FromResult(slaves);
    }

    public Task DisconnectAsync(CancellationToken cancellationToken)
    {
        _state = ConnectionState.Disconnected;
        ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(ConnectionState.Disconnected, ConnectionState.Operational));
        return Task.CompletedTask;
    }

    public Task ExchangeProcessDataAsync(ReadOnlySpan<XeryonCommandFrame> commands, Span<XeryonStatusFrame> statuses, CancellationToken cancellationToken)
    {
        if (statuses.Length < _axes.Count)
        {
            throw new ArgumentException("Status buffer is smaller than the number of simulated axes.", nameof(statuses));
        }

        var elapsed = _stopwatch.Elapsed;
        _stopwatch.Restart();

        for (var i = 0; i < _axes.Count; i++)
        {
            var axis = _axes[i];
            var frame = i < commands.Length ? commands[i] : XeryonCommandFrame.Empty;
            axis.Apply(frame, elapsed);
            statuses[i] = axis.CreateStatusFrame();
        }

        return Task.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        _stopwatch.Stop();
        return ValueTask.CompletedTask;
    }

    private sealed class AxisSimulation
    {
        private readonly object _gate = new();
        private double _currentPosition;
        private double _targetPosition;
        private bool _isMoving;
        private uint _speed;
        private ushort _acceleration;
        private ushort _deceleration;
        private AxisStatusFlags _status = AxisStatusFlags.ClosedLoop | AxisStatusFlags.EncoderValid | AxisStatusFlags.AmplifiersEnabled | AxisStatusFlags.MotorOn;

        public AxisSimulation(int axisNumber)
        {
            AxisNumber = axisNumber;
        }

        public int AxisNumber { get; }

        public void Apply(in XeryonCommandFrame command, TimeSpan elapsed)
        {
            lock (_gate)
            {
                if (command.Command.Equals("STOP", StringComparison.OrdinalIgnoreCase))
                {
                    _isMoving = false;
                    _status &= ~AxisStatusFlags.Scanning;
                }
                else if (command.Command.Equals("RSET", StringComparison.OrdinalIgnoreCase))
                {
                    _currentPosition = 0;
                    _targetPosition = 0;
                    _status |= AxisStatusFlags.PositionReached;
                    _isMoving = false;
                }
                else if (command.Command.Equals("SCAN", StringComparison.OrdinalIgnoreCase))
                {
                    if (command.ExecuteFlag > 0)
                    {
                        _status |= AxisStatusFlags.Scanning;
                        _isMoving = true;
                        _targetPosition = command.TargetPosition >= 0 ? 1_000_000 : -1_000_000;
                        _speed = Math.Max(command.Speed, 100);
                    }
                    else
                    {
                        _status &= ~AxisStatusFlags.Scanning;
                        _isMoving = false;
                    }
                }
                else if (command.Command.Equals("DPOS", StringComparison.OrdinalIgnoreCase))
                {
                    _targetPosition = command.TargetPosition;
                    _speed = command.Speed;
                    _acceleration = command.Acceleration;
                    _deceleration = command.Deceleration;
                    _isMoving = command.ExecuteFlag > 0;
                    if (_isMoving)
                    {
                        _status &= ~AxisStatusFlags.PositionReached;
                    }
                }
                else if (command.Command.Equals("STEP", StringComparison.OrdinalIgnoreCase) && command.ExecuteFlag > 0)
                {
                    _targetPosition += command.TargetPosition;
                    _speed = command.Speed;
                    _acceleration = command.Acceleration;
                    _deceleration = command.Deceleration;
                    _isMoving = true;
                    _status &= ~AxisStatusFlags.PositionReached;
                }

                UpdateMotion(elapsed);
            }
        }

        private void UpdateMotion(TimeSpan elapsed)
        {
            if (!_isMoving)
            {
                _status |= AxisStatusFlags.PositionReached;
                return;
            }

            var direction = Math.Sign(_targetPosition - _currentPosition);
            if (direction == 0)
            {
                _isMoving = false;
                _status |= AxisStatusFlags.PositionReached;
                return;
            }

            var speedPerSecond = Math.Max(100, _speed);
            var delta = direction * speedPerSecond * elapsed.TotalSeconds;

            if (Math.Abs(_targetPosition - _currentPosition) <= Math.Abs(delta))
            {
                _currentPosition = _targetPosition;
                _isMoving = false;
                _status |= AxisStatusFlags.PositionReached;
            }
            else
            {
                _currentPosition += delta;
                _status &= ~AxisStatusFlags.PositionReached;
            }
        }

        public XeryonStatusFrame CreateStatusFrame()
        {
            lock (_gate)
            {
                var actual = (int)Math.Round(_currentPosition);
                return new XeryonStatusFrame(actual, _status, (byte)AxisNumber);
            }
        }
    }
}
