using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Options;

namespace XeryonEtherCAT.Core.Services;

public sealed class XeryonEtherCatService : IAsyncDisposable
{
    private readonly IEtherCatMaster _master;
    private readonly XeryonEtherCatOptions _options;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _axisGate = new();
    private XeryonAxis[] _axisSnapshot = Array.Empty<XeryonAxis>();
    private Dictionary<int, XeryonAxis> _axesByIndex = new();
    private Dictionary<string, XeryonAxis> _axesByName = new(StringComparer.OrdinalIgnoreCase);
    private Task? _ioTask;
    private int _connectionState = (int)ConnectionState.Disconnected;

    public XeryonEtherCatService(XeryonEtherCatOptions options, IEtherCatMaster? master = null)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _master = master ?? new Internal.Soem.SoemEtherCatMaster();
        _master.ConnectionStateChanged += HandleMasterConnectionStateChanged;
    }

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public event EventHandler<AxisStatusChangedEventArgs>? AxisStatusChanged;

    public ConnectionState ConnectionState => (ConnectionState)Volatile.Read(ref _connectionState);

    public IReadOnlyList<XeryonAxis> Axes => new ReadOnlyCollection<XeryonAxis>(_axisSnapshot);

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
        _ioTask = Task.Run(() => RunIoAsync(_cts.Token), CancellationToken.None);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _cts.Cancel();
        if (_ioTask is not null)
        {
            await _ioTask.ConfigureAwait(false);
        }

        await _master.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        UpdateState(ConnectionState.Disconnected);

        lock (_axisGate)
        {
            foreach (var axis in _axisSnapshot)
            {
                axis.StatusChanged -= HandleAxisStatusChanged;
            }

            _axisSnapshot = Array.Empty<XeryonAxis>();
            _axesByIndex.Clear();
            _axesByName.Clear();
        }
    }

    public async Task EnqueueCommandAsync(int axisNumber, XeryonAxisCommand command, CancellationToken cancellationToken)
    {
        var axis = GetAxisByIndex(axisNumber);
        await axis.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task EnqueueCommandAsync(string axisName, XeryonAxisCommand command, CancellationToken cancellationToken)
    {
        var axis = GetAxisByName(axisName);
        await axis.EnqueueCommandAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public XeryonAxis GetAxisByIndex(int axisNumber)
    {
        lock (_axisGate)
        {
            if (!_axesByIndex.TryGetValue(axisNumber, out var axis))
            {
                throw new KeyNotFoundException($"Axis {axisNumber} is not available.");
            }

            return axis;
        }
    }

    public XeryonAxis GetAxisByName(string axisName)
    {
        lock (_axisGate)
        {
            if (!_axesByName.TryGetValue(axisName, out var axis))
            {
                throw new KeyNotFoundException($"Axis '{axisName}' is not available.");
            }

            return axis;
        }
    }

    public void SetAxisName(int axisNumber, string name)
    {
        var axis = GetAxisByIndex(axisNumber);
        axis.Name = name;

        lock (_axisGate)
        {
            RebuildAxisLookup();
        }
    }

    public async Task<IReadOnlyList<EtherCatSlaveInfo>> DiscoverAsync(string networkInterfaceName, CancellationToken cancellationToken)
    {
        await using var temporaryMaster = new Internal.Soem.SoemEtherCatMaster();
        var slaves = await temporaryMaster.ConnectAsync(networkInterfaceName, cancellationToken).ConfigureAwait(false);
        await temporaryMaster.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        return slaves;
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_ioTask is not null)
        {
            await _ioTask.ConfigureAwait(false);
        }

        await _master.DisconnectAsync(CancellationToken.None).ConfigureAwait(false);
        await _master.DisposeAsync().ConfigureAwait(false);
        _cts.Dispose();
    }

    private async Task ConnectInternalAsync(CancellationToken cancellationToken)
    {
        var slaves = await _master.ConnectAsync(_options.NetworkInterfaceName, cancellationToken).ConfigureAwait(false);

        var matching = slaves
            .Where(s => s.VendorId == _options.VendorId && s.ProductCode == _options.ProductCode)
            .Where(s => _options.Revision == 0 || s.Revision == _options.Revision)
            .Take(_options.MaximumAxes)
            .ToList();

        if (matching.Count == 0)
        {
            throw new InvalidOperationException("No Xeryon drives were discovered on the selected interface.");
        }

        var axes = new List<XeryonAxis>(matching.Count);
        for (var i = 0; i < matching.Count; i++)
        {
            var axisNumber = i + 1;
            var slave = matching[i];
            var name = _options.AxisNames.TryGetValue(axisNumber, out var configuredName)
                ? configuredName
                : $"Drive #{axisNumber}";
            var axis = new XeryonAxis(axisNumber, slave.Position, slave.ProductCode, slave.Revision, name);
            axis.StatusChanged += HandleAxisStatusChanged;
            axes.Add(axis);
        }

        lock (_axisGate)
        {
            foreach (var existing in _axisSnapshot)
            {
                existing.StatusChanged -= HandleAxisStatusChanged;
            }

            _axisSnapshot = axes.OrderBy(a => a.SlavePosition).ToArray();
            _axesByIndex = axes.ToDictionary(a => a.AxisNumber);
            _axesByName = axes.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
        }

        UpdateState(ConnectionState.Degraded);
    }

    private async Task RunIoAsync(CancellationToken cancellationToken)
    {
        XeryonCommandFrame[]? commandBuffer = null;
        XeryonStatusFrame[]? statusBuffer = null;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var axes = _axisSnapshot;
                if (axes.Length == 0)
                {
                    await Task.Delay(_options.CycleTime, cancellationToken).ConfigureAwait(false);
                    continue;
                }

                commandBuffer ??= new XeryonCommandFrame[axes.Length];
                statusBuffer ??= new XeryonStatusFrame[axes.Length];

                if (commandBuffer.Length < axes.Length)
                {
                    commandBuffer = new XeryonCommandFrame[axes.Length];
                }

                if (statusBuffer.Length < axes.Length)
                {
                    statusBuffer = new XeryonStatusFrame[axes.Length];
                }

                for (var i = 0; i < axes.Length; i++)
                {
                    commandBuffer[i] = axes[i].GetNextCommandFrame();
                }

                await _master.ExchangeProcessDataAsync(commandBuffer, statusBuffer, cancellationToken).ConfigureAwait(false);

                for (var i = 0; i < axes.Length; i++)
                {
                    axes[i].UpdateStatus(statusBuffer[i]);
                }

                UpdateState(ConnectionState.Operational);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                UpdateState(ConnectionState.Degraded, ex);

                if (!_options.AutoReconnect)
                {
                    throw;
                }

                try
                {
                    await AttemptReconnectAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception reconnectEx)
                {
                    UpdateState(ConnectionState.Degraded, reconnectEx);
                    await Task.Delay(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
                }
            }

            await Task.Delay(_options.CycleTime, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task AttemptReconnectAsync(CancellationToken cancellationToken)
    {
        try
        {
            await _master.DisconnectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // Ignore disconnect errors - we are reconnecting anyway.
        }

        await Task.Delay(_options.ReconnectDelay, cancellationToken).ConfigureAwait(false);
        await ConnectInternalAsync(cancellationToken).ConfigureAwait(false);
    }

    private void HandleMasterConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        UpdateState(e.NewState, e.Exception);
    }

    private void HandleAxisStatusChanged(object? sender, AxisStatusChangedEventArgs e)
    {
        AxisStatusChanged?.Invoke(this, e);
    }

    private void UpdateState(ConnectionState newState, Exception? exception = null)
    {
        var previousState = (ConnectionState)Interlocked.Exchange(ref _connectionState, (int)newState);
        if (previousState != newState || exception is not null)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(newState, previousState, exception));
        }
    }

    private void RebuildAxisLookup()
    {
        var axes = _axisSnapshot.OrderBy(a => a.SlavePosition).ToArray();
        _axesByIndex = axes.ToDictionary(a => a.AxisNumber);
        _axesByName = axes.ToDictionary(a => a.Name, a => a, StringComparer.OrdinalIgnoreCase);
        _axisSnapshot = axes;
    }
}
