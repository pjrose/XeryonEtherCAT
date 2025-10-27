using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Models;

namespace XeryonEtherCAT.Core.Internal.Soem;

public sealed class SoemEtherCatMaster : IEtherCatMaster
{
    private readonly IEtherCatMaster _inner;

    public SoemEtherCatMaster()
    {
        try
        {
            _inner = new SoemShimMaster();
        }
        catch (DllNotFoundException)
        {
            _inner = new Simulation.SimulatedEtherCatMaster();
        }
        catch (EntryPointNotFoundException)
        {
            _inner = new Simulation.SimulatedEtherCatMaster();
        }
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged
    {
        add => _inner.ConnectionStateChanged += value;
        remove => _inner.ConnectionStateChanged -= value;
    }

    public ConnectionState ConnectionState => _inner.ConnectionState;

    public Task<IReadOnlyList<EtherCatSlaveInfo>> ConnectAsync(string networkInterfaceName, CancellationToken cancellationToken)
        => _inner.ConnectAsync(networkInterfaceName, cancellationToken);

    public Task DisconnectAsync(CancellationToken cancellationToken)
        => _inner.DisconnectAsync(cancellationToken);

    public Task ExchangeProcessDataAsync(ReadOnlySpan<XeryonCommandFrame> commands, Span<XeryonStatusFrame> statuses, CancellationToken cancellationToken)
        => _inner.ExchangeProcessDataAsync(commands, statuses, cancellationToken);

    public ValueTask DisposeAsync()
        => _inner.DisposeAsync();

    private sealed class SoemShimMaster : IEtherCatMaster
    {
        private const int CommandStride = 20;
        private const int StatusStride = 24;
        private const int TimeoutMicroseconds = 2000;

        private IntPtr _handle = IntPtr.Zero;
        private ConnectionState _state = ConnectionState.Disconnected;
        private byte[] _outputBuffer = Array.Empty<byte>();
        private byte[] _inputBuffer = Array.Empty<byte>();
        private int _slaveCount;

        public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

        public ConnectionState ConnectionState => _state;

        public Task<IReadOnlyList<EtherCatSlaveInfo>> ConnectAsync(string networkInterfaceName, CancellationToken cancellationToken)
        {
            if (_handle != IntPtr.Zero)
            {
                throw new InvalidOperationException("SOEM master is already connected.");
            }

            _handle = SoemShimNative.Initialize(networkInterfaceName);
            if (_handle == IntPtr.Zero)
            {
                throw new DllNotFoundException("Failed to load the SOEM shim. Ensure that libsoemshim is compiled and available on the native library search path.");
            }

            if (SoemShimNative.GetProcessSizes(_handle, out var outputs, out var inputs) == 0)
            {
                throw new InvalidOperationException("Unable to query process data sizes from SOEM.");
            }

            _slaveCount = SoemShimNative.GetSlaveCount(_handle);
            _outputBuffer = new byte[outputs];
            _inputBuffer = new byte[inputs];

            var nativeInfos = new SoemShimNative.SoemSlaveInfo[Math.Max(_slaveCount, 1)];
            var count = SoemShimNative.ScanSlaves(_handle, nativeInfos, nativeInfos.Length);

            var results = new List<EtherCatSlaveInfo>(count);
            for (var i = 0; i < count; i++)
            {
                var info = nativeInfos[i];
                results.Add(new EtherCatSlaveInfo(info.Position, info.VendorId, info.ProductCode, info.Revision, info.Name ?? string.Empty));
            }

            UpdateState(ConnectionState.Operational);
            return Task.FromResult<IReadOnlyList<EtherCatSlaveInfo>>(results);
        }

        public Task DisconnectAsync(CancellationToken cancellationToken)
        {
            if (_handle != IntPtr.Zero)
            {
                SoemShimNative.Shutdown(_handle);
                _handle = IntPtr.Zero;
                UpdateState(ConnectionState.Disconnected);
            }

            return Task.CompletedTask;
        }

        public Task ExchangeProcessDataAsync(ReadOnlySpan<XeryonCommandFrame> commands, Span<XeryonStatusFrame> statuses, CancellationToken cancellationToken)
        {
            if (_handle == IntPtr.Zero)
            {
                throw new InvalidOperationException("SOEM master is not connected.");
            }

            if (statuses.Length < _slaveCount)
            {
                throw new ArgumentException("Status buffer is smaller than the number of slaves returned by SOEM.", nameof(statuses));
            }

            EnsureOutputCapacity(commands.Length * CommandStride);
            EnsureInputCapacity(statuses.Length * StatusStride);

            var span = _outputBuffer.AsSpan();
            span.Clear();

            for (var i = 0; i < commands.Length && i * CommandStride + CommandStride <= span.Length; i++)
            {
                commands[i].WriteTo(span.Slice(i * CommandStride, CommandStride));
            }

            var result = SoemShimNative.ExchangeProcessData(_handle, _outputBuffer, _outputBuffer.Length, _inputBuffer, _inputBuffer.Length, TimeoutMicroseconds);
            if (result < 0)
            {
                UpdateState(ConnectionState.Degraded, new InvalidOperationException($"SOEM returned a negative work counter ({result})."));
                return Task.CompletedTask;
            }

            var inputSpan = _inputBuffer.AsSpan();
            for (var i = 0; i < statuses.Length && i * StatusStride + StatusStride <= inputSpan.Length; i++)
            {
                statuses[i] = XeryonStatusFrame.FromProcessData(inputSpan.Slice(i * StatusStride, StatusStride));
            }

            return Task.CompletedTask;
        }

        public ValueTask DisposeAsync()
        {
            if (_handle != IntPtr.Zero)
            {
                SoemShimNative.Shutdown(_handle);
                _handle = IntPtr.Zero;
            }

            return ValueTask.CompletedTask;
        }

        private void EnsureOutputCapacity(int required)
        {
            if (_outputBuffer.Length >= required)
            {
                return;
            }

            Array.Resize(ref _outputBuffer, required);
        }

        private void EnsureInputCapacity(int required)
        {
            if (_inputBuffer.Length >= required)
            {
                return;
            }

            Array.Resize(ref _inputBuffer, required);
        }

        private void UpdateState(ConnectionState newState, Exception? exception = null)
        {
            if (_state == newState && exception is null)
            {
                return;
            }

            var previous = _state;
            _state = newState;
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(newState, previous, exception));
        }
    }
}
