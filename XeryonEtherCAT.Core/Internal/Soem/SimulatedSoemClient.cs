using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

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
        public SoemShim.DriveTxPDO Status;

        public void Reset()
        {
            Pending.Command = new byte[32];
            Position = 0;
            Status = new SoemShim.DriveTxPDO
            {
                AmplifiersEnabled = 1,
                MotorOn = 1,
                ClosedLoop = 1,
                EncoderValid = 1,
                PositionReached = 1
            };
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

            Status.ExecuteAck = 1;
            Status.SafetyTimeout = 0;
            Status.ErrorLimit = 0;
            Status.PositionFail = 0;

            switch (keyword)
            {
                case "DPOS":
                    Position = Pending.Parameter;
                    Status.PositionReached = 1;
                    Status.Scanning = 0;
                    break;
                case "SCAN":
                    if (Pending.Parameter == 0)
                    {
                        Status.Scanning = 0;
                        Status.PositionReached = 1;
                    }
                    else
                    {
                        Status.Scanning = 1;
                        Status.PositionReached = 0;
                        Position += Pending.Parameter * Math.Max(1, Pending.Velocity / 1000);
                    }
                    break;
                case "INDX":
                    Status.SearchingIndex = 1;
                    Status.EncoderValid = 1;
                    Status.PositionReached = 1;
                    Status.SearchingIndex = 0;
                    Position = 0;
                    break;
                case "ENBL":
                    if (Pending.Parameter == 0)
                    {
                        Status.AmplifiersEnabled = 0;
                        Status.MotorOn = 0;
                        Status.ClosedLoop = 0;
                    }
                    else
                    {
                        Status.AmplifiersEnabled = 1;
                        Status.MotorOn = 1;
                        Status.ClosedLoop = 1;
                        Status.PositionReached = 1;
                        Status.ForceZero = 0;
                    }
                    break;
                case "RSET":
                    Status.ErrorLimit = 0;
                    Status.SafetyTimeout = 0;
                    Status.PositionFail = 0;
                    Status.ForceZero = 0;
                    Status.PositionReached = 1;
                    break;
                case "HALT":
                    Status.Scanning = 0;
                    Status.PositionReached = 1;
                    break;
                case "STOP":
                    Status.ForceZero = 1;
                    Status.Scanning = 0;
                    Status.PositionReached = 0;
                    break;
            }

            Pending.Execute = 0;
            Status.ActualPosition = Position;
        }

        public SoemShim.DriveTxPDO CreateTx()
            => Status;

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
