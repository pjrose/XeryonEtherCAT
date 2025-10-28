using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using XeryonEtherCAT.Core.Models;

namespace XeryonEtherCAT.Core.Internal.Soem;

public sealed class SimulatedSoemClient : ISoemClient
{
    private readonly object _gate = new();
    private readonly List<SimulatedSlave> _slaves = new();
    private readonly int _expectedWkc;
    private bool _disposed;
    private IntPtr _handle;
    private int _nextHandle = 1;
    private SoemShim.SoemHealth _health;

    public SimulatedSoemClient(int slaveCount = 2)
    {
        if (slaveCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slaveCount));
        }

        _expectedWkc = slaveCount * 4;
        for (var i = 0; i < slaveCount; i++)
        {
            _slaves.Add(new SimulatedSlave());
        }

        _health = new SoemShim.SoemHealth
        {
            group_expected_wkc = _expectedWkc,
            last_wkc = _expectedWkc,
            slaves_found = slaveCount,
            slaves_op = slaveCount,
            bytes_in = Marshal.SizeOf<SoemShim.DriveTxPDO>() * slaveCount,
            bytes_out = Marshal.SizeOf<SoemShim.DriveRxPDO>() * slaveCount,
            al_status_code = 0
        };
    }

    public IntPtr Initialize(string iface)
    {
        lock (_gate)
        {
            _handle = new IntPtr(_nextHandle++);
            ResetSlaves();
            return _handle;
        }
    }

    public void Shutdown(IntPtr handle)
    {
        lock (_gate)
        {
            if (_handle == handle)
            {
                _handle = IntPtr.Zero;
            }
        }
    }

    public int GetSlaveCount(IntPtr handle)
    {
        lock (_gate)
        {
            EnsureHandle(handle);
            return _slaves.Count;
        }
    }

    public int GetExpectedRxBytes()
        => Marshal.SizeOf<SoemShim.DriveRxPDO>() * _slaves.Count;

    public int GetExpectedTxBytes()
        => Marshal.SizeOf<SoemShim.DriveTxPDO>() * _slaves.Count;

    public int WriteRxPdo(IntPtr handle, int slaveIndex, ref SoemShim.DriveRxPDO pdo)
    {
        lock (_gate)
        {
            EnsureHandle(handle);
            var idx = slaveIndex - 1;
            if ((uint)idx >= _slaves.Count)
            {
                return -1;
            }

            _slaves[idx].Pending = pdo;
            return 0;
        }
    }

    public int ReadTxPdo(IntPtr handle, int slaveIndex, out SoemShim.DriveTxPDO pdo)
    {
        lock (_gate)
        {
            EnsureHandle(handle);
            var idx = slaveIndex - 1;
            if ((uint)idx >= _slaves.Count)
            {
                pdo = default;
                return -1;
            }

            var slave = _slaves[idx];
            pdo = slave.CreateTx();
            return 0;
        }
    }

    public int ExchangeProcessData(IntPtr handle, int timeoutUs)
    {
        lock (_gate)
        {
            EnsureHandle(handle);
            foreach (var slave in _slaves)
            {
                slave.Process();
            }

            _health.last_wkc = _expectedWkc;
            return _expectedWkc;
        }
    }

    public int GetHealth(IntPtr handle, out SoemShim.SoemHealth health)
    {
        lock (_gate)
        {
            EnsureHandle(handle);
            health = _health;
            return 1;
        }
    }

    public int TryRecover(IntPtr handle, int timeoutMs)
    {
        lock (_gate)
        {
            EnsureHandle(handle);
            _health.last_wkc = _expectedWkc;
            return 1;
        }
    }

    public string DrainErrorList(IntPtr handle, StringBuilder? buffer = null)
    {
        return string.Empty;
    }

    public int ListNetworkAdapterNames()
    {
        return 0;
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private void EnsureHandle(IntPtr handle)
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(SimulatedSoemClient));
        }

        if (handle == IntPtr.Zero || handle != _handle)
        {
            throw new InvalidOperationException("Invalid simulated handle.");
        }
    }

    private void ResetSlaves()
    {
        foreach (var slave in _slaves)
        {
            slave.Reset();
        }
    }

    private sealed class SimulatedSlave
    {
        public SoemShim.DriveRxPDO Pending;
        public int Position;
        public DriveStatus Status = DriveStatus.AmplifiersEnabled | DriveStatus.MotorOn | DriveStatus.ClosedLoop | DriveStatus.EncoderValid | DriveStatus.PositionReached;

        public void Reset()
        {
            Pending.Command = new byte[32];
            Position = 0;
            Status = DriveStatus.AmplifiersEnabled | DriveStatus.MotorOn | DriveStatus.ClosedLoop | DriveStatus.EncoderValid | DriveStatus.PositionReached;
        }

        public void Process()
        {
            if (Pending.Command == null)
            {
                Pending.Command = new byte[32];
            }

            var keyword = GetCommandKeyword(Pending.Command);
            if (Pending.Execute == 0)
            {
                return;
            }

            Status |= DriveStatus.ExecuteAck;
            Status &= ~DriveStatus.SafetyTimeout;
            Status &= ~DriveStatus.ErrorLimit;
            Status &= ~DriveStatus.PositionFail;

            switch (keyword)
            {
                case "DPOS":
                    Position = Pending.Parameter;
                    Status |= DriveStatus.PositionReached;
                    Status &= ~DriveStatus.Scanning;
                    break;
                case "SCAN":
                    if (Pending.Parameter == 0)
                    {
                        Status &= ~DriveStatus.Scanning;
                        Status |= DriveStatus.PositionReached;
                    }
                    else
                    {
                        Status |= DriveStatus.Scanning;
                        Status &= ~DriveStatus.PositionReached;
                        Position += Pending.Parameter * Math.Max(1, Pending.Velocity / 1000);
                    }
                    break;
                case "INDX":
                    Status |= DriveStatus.SearchingIndex;
                    Status |= DriveStatus.EncoderValid;
                    Status |= DriveStatus.PositionReached;
                    Status &= ~DriveStatus.SearchingIndex;
                    Position = 0;
                    break;
                case "ENBL":
                    if (Pending.Parameter == 0)
                    {
                        Status &= ~(DriveStatus.MotorOn | DriveStatus.ClosedLoop);
                    }
                    else
                    {
                        Status |= DriveStatus.AmplifiersEnabled | DriveStatus.MotorOn | DriveStatus.ClosedLoop | DriveStatus.PositionReached;
                        Status &= ~DriveStatus.ForceZero;
                    }
                    break;
                case "RSET":
                    Status &= ~(DriveStatus.ErrorLimit | DriveStatus.SafetyTimeout | DriveStatus.PositionFail | DriveStatus.ForceZero);
                    Status |= DriveStatus.PositionReached;
                    break;
                case "HALT":
                    Status &= ~DriveStatus.Scanning;
                    Status |= DriveStatus.PositionReached;
                    break;
                case "STOP":
                    Status |= DriveStatus.ForceZero;
                    Status &= ~DriveStatus.Scanning;
                    Status &= ~DriveStatus.PositionReached;
                    break;
            }

            Pending.Execute = 0;
        }

        public SoemShim.DriveTxPDO CreateTx()
        {
            var tx = new SoemShim.DriveTxPDO
            {
                ActualPosition = 0,
                AmplifiersEnabled = 0,
                EndStop = 0,
                ThermalProtection1 = 0,
                ThermalProtection2 = 0,
                ForceZero = 0,
                MotorOn = 0,
                ClosedLoop = 0,
                EncoderIndex = 0,
                EncoderValid = 0,
                SearchingIndex = 0,
                PositionReached = 0,
                ErrorCompensation = 0,
                EncoderError = 0,
                Scanning = 0,
                LeftEndStop = 0,
                RightEndStop = 0,
                ErrorLimit = 0,
                SearchingOptimalFrequency = 0,
                SafetyTimeout = 0,
                ExecuteAck = 0,
                EmergencyStop = 0,
                PositionFail = 0,
                Slot = 0
            };
            return tx;
        }

        private static string GetCommandKeyword(byte[] command)
        {
            var len = Array.IndexOf(command, (byte)0);
            if (len < 0)
            {
                len = command.Length;
            }

            return Encoding.ASCII.GetString(command, 0, Math.Max(0, len)).Trim();
        }


    }
}
