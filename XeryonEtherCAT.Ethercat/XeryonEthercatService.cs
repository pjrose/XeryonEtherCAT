using System.Collections.Generic;
using System;
using System.Linq;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading.Tasks;
using EC_Net;
using IEC_Net;
using XeryonEtherCAT.Core;
using XeryonEtherCAT.Ethercat.Interfaces;
using XeryonEtherCAT.Ethercat.Internal;
using XeryonEtherCAT.Ethercat.Models;

namespace XeryonEtherCAT.Ethercat;

public sealed class XeryonEthercatService : IXeryonEthercatService
{
    private readonly object _syncRoot = new();
    private readonly XeryonEthercatOptions _options;
    private EtherCATMaster? _master;
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private string? _interfaceName;
    private IReadOnlyList<AxisConfiguration> _axisDefinitions = Array.Empty<AxisConfiguration>();
    private readonly Dictionary<string, AxisContext> _axesByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, AxisContext> _axesByIndex = new();
    private readonly ConcurrentDictionary<string, AxisStatus> _latestStatuses = new(StringComparer.OrdinalIgnoreCase);
    private ConnectionState _connectionState = ConnectionState.Disconnected;

    public event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    public event EventHandler<AxisStatusChangedEventArgs>? AxisStatusChanged;

    public XeryonEthercatService()
        : this(null)
    {
    }

    public XeryonEthercatService(XeryonEthercatOptions? options)
    {
        _options = options ?? new XeryonEthercatOptions();
    }

    public ConnectionState ConnectionState => _connectionState;

    public IReadOnlyDictionary<string, AxisConfiguration> AxisConfigurations
    {
        get
        {
            lock (_syncRoot)
            {
                return _axesByName.ToDictionary(kvp => kvp.Key, kvp => kvp.Value.Configuration);
            }
        }
    }

    public async Task<IReadOnlyList<EthercatInterfaceInfo>> GetAvailableInterfacesAsync(CancellationToken cancellationToken = default)
    {
        return await Task.Run(() =>
        {
            using var master = new EtherCATMaster();
            if (!master.TryGetAvailableInterfaces(out List<Tuple<string, string, string>>? interfaces) || interfaces is null)
            {
                interfaces = master.GetAvailableInterfaces();
            }

            return (IReadOnlyList<EthercatInterfaceInfo>)interfaces
                .Select(CreateInterfaceInfo)
                .Where(info => !string.IsNullOrWhiteSpace(info.InterfaceId))
                .GroupBy(info => info.InterfaceId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(info => info.DisplayName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }, cancellationToken).ConfigureAwait(false);
    }


    public Task ConnectAsync(string interfaceName, IEnumerable<AxisConfiguration> axes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(interfaceName);
        ArgumentNullException.ThrowIfNull(axes);

        var axisList = axes.ToList();
        if (axisList.Count == 0)
        {
            throw new ArgumentException("At least one axis must be specified.", nameof(axes));
        }

        return Task.Run(() => ConnectInternal(interfaceName, axisList, cancellationToken), cancellationToken);
    }

    private void ConnectInternal(string interfaceName, IReadOnlyList<AxisConfiguration> axisList, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var normalizedInterface = interfaceName.Trim();
        if (string.IsNullOrEmpty(normalizedInterface))
        {
            throw new ArgumentException("Interface name cannot be empty.", nameof(interfaceName));
        }

        lock (_syncRoot)
        {
            if (_connectionState is ConnectionState.Connecting or ConnectionState.Connected or ConnectionState.Reconnecting)
            {
                throw new InvalidOperationException("The service is already connected or in the process of connecting.");
            }

            UpdateConnectionState(ConnectionState.Connecting, null);
        }

        cancellationToken.ThrowIfCancellationRequested();

        EtherCATMaster? master = null;
        var masterAssigned = false;
        CancellationTokenSource? loopCts = null;

        try
        {
            master = new EtherCATMaster();
            var slaveCount = master.StartActivity(normalizedInterface, _options.IoMapSize, true);
            var highestAxisIndex = axisList.Max(a => a.AxisIndex);
            if (highestAxisIndex > slaveCount)
            {
                throw new InvalidOperationException($"The EtherCAT network reported {slaveCount} slave(s), but configuration requires access to axis index {highestAxisIndex}.");
            }

            InitializeAxisContexts(master, axisList);

            loopCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            lock (_syncRoot)
            {
                _interfaceName = normalizedInterface;
                _axisDefinitions = axisList;
                _master = master;
                masterAssigned = true;
                _loopCts = loopCts;
            }

            var loopTask = Task.Run(() => ProcessLoopAsync(loopCts.Token), loopCts.Token);

            lock (_syncRoot)
            {
                _loopTask = loopTask;
            }

            UpdateConnectionState(ConnectionState.Connected, null);
        }
        catch (OperationCanceledException)
        {
            if (!masterAssigned)
            {
                loopCts?.Dispose();
                if (master is not null)
                {
                    try
                    {
                        master.StopActivity();
                    }
                    catch
                    {
                    }

                    master.Dispose();
                }
            }

            CleanupInternal();
            UpdateConnectionState(ConnectionState.Disconnected, null);
            throw;
        }
        catch (Exception ex)
        {
            if (!masterAssigned)
            {
                loopCts?.Dispose();
                if (master is not null)
                {
                    try
                    {
                        master.StopActivity();
                    }
                    catch
                    {
                    }

                    master.Dispose();
                }
            }

            CleanupInternal();
            UpdateConnectionState(ConnectionState.Faulted, ex);
            throw;
        }
    }



    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        CancellationTokenRegistration registration = default;
        try
        {
            if (_loopCts != null)
            {
                registration = cancellationToken.Register(() => _loopCts.Cancel());
                _loopCts.Cancel();
                if (_loopTask != null)
                {
                    await _loopTask.ConfigureAwait(false);
                }
            }
        }
        finally
        {
            registration.Dispose();
            CleanupInternal();
            UpdateConnectionState(ConnectionState.Disconnected, null);
        }
    }

    public async Task SendCommandAsync(string axisName, AxisCommand command, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(axisName);
        ArgumentNullException.ThrowIfNull(command);

        AxisContext axis;
        lock (_syncRoot)
        {
            if (!_axesByName.TryGetValue(axisName, out axis!))
            {
                throw new KeyNotFoundException($"Axis '{axisName}' is not configured.");
            }
        }

        await Task.Run(() => axis.UpdateCommand(command), cancellationToken).ConfigureAwait(false);
    }

    public Task StopAxisAsync(string axisName, CancellationToken cancellationToken = default)
        => SendCommandAsync(axisName, new AxisCommand("STOP"), cancellationToken);

    public Task ResetAxisAsync(string axisName, CancellationToken cancellationToken = default)
        => SendCommandAsync(axisName, new AxisCommand("RSET"), cancellationToken);

    public AxisStatus? GetCachedStatus(string axisName)
    {
        return _latestStatuses.TryGetValue(axisName, out var status) ? status : null;
    }

    public void Dispose()
    {
        _loopCts?.Cancel();
        _loopTask?.Wait(TimeSpan.FromSeconds(1));
        CleanupInternal();
        GC.SuppressFinalize(this);
    }

    private async Task ProcessLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(_options.PollingInterval, cancellationToken).ConfigureAwait(false);
            }
            catch (TaskCanceledException)
            {
                break;
            }

            EtherCATMaster? master;
            lock (_syncRoot)
            {
                master = _master;
            }

            if (master is null)
            {
                continue;
            }

            try
            {
                master.InOutSync();
                foreach (var axis in EnumerateAxes())
                {
                    var status = axis.BuildStatus();
                    _latestStatuses[axis.Configuration.Name] = status;
                    AxisStatusChanged?.Invoke(this, new AxisStatusChangedEventArgs(status));

                    if (axis.RequiresExecuteReset && (status.Flags & AxisStatusFlags.ExecuteAcknowledged) == AxisStatusFlags.ExecuteAcknowledged)
                    {
                        axis.ResetExecuteFlag();
                    }
                }

                if (_connectionState == ConnectionState.Reconnecting)
                {
                    UpdateConnectionState(ConnectionState.Connected, null);
                }
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                HandleCommunicationFault(ex);
            }
        }
    }

    private IEnumerable<AxisContext> EnumerateAxes()
    {
        lock (_syncRoot)
        {
            return _axesByName.Values.ToList();
        }
    }

    private void HandleCommunicationFault(Exception exception)
    {
        UpdateConnectionState(ConnectionState.Reconnecting, exception);
        if (_interfaceName is null || _axisDefinitions.Count == 0)
        {
            return;
        }

        try
        {
            _master?.StopActivity();
        }
        catch
        {
            // Ignore failures while stopping.
        }

        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                _master?.Dispose();
                var master = new EtherCATMaster();
                master.StartActivity(_interfaceName, _options.IoMapSize, true);
                InitializeAxisContexts(master, _axisDefinitions);
                lock (_syncRoot)
                {
                    _master = master;
                }
                UpdateConnectionState(ConnectionState.Connected, null);
                return;
            }
            catch
            {
                Thread.Sleep(_options.ReconnectInterval);
            }
        }

        UpdateConnectionState(ConnectionState.Faulted, exception);
    }

    private void InitializeAxisContexts(EtherCATMaster master, IReadOnlyList<AxisConfiguration> axes)
    {
        lock (_syncRoot)
        {
            _axesByName.Clear();
            _axesByIndex.Clear();
            _latestStatuses.Clear();

            foreach (var axis in axes)
            {
                var slave = new EtherCATSlave(master, axis.AxisIndex);
                var context = AxisContext.Create(slave, axis);
                _axesByName.Add(axis.Name, context);
                _axesByIndex.Add(axis.AxisIndex, context);
            }
        }
    }

    private void CleanupInternal()
    {
        lock (_syncRoot)
        {
            _loopCts?.Dispose();
            _loopCts = null;
            _loopTask = null;

            if (_master is not null)
            {
                try
                {
                    _master.StopActivity();
                }
                catch
                {
                    // ignored
                }

                _master.Dispose();
                _master = null;
            }

            _axesByName.Clear();
            _axesByIndex.Clear();
            _latestStatuses.Clear();
            _axisDefinitions = Array.Empty<AxisConfiguration>();
            _interfaceName = null;
        }
    }

    private void UpdateConnectionState(ConnectionState state, Exception? exception)
    {
        ConnectionState previous;
        lock (_syncRoot)
        {
            previous = _connectionState;
            _connectionState = state;
        }

        if (previous != state || exception != null)
        {
            ConnectionStateChanged?.Invoke(this, new ConnectionStateChangedEventArgs(previous, state, exception));
        }
    }

    private static EthercatInterfaceInfo CreateInterfaceInfo(Tuple<string, string, string> tuple)
    {
        var id = (tuple.Item1 ?? string.Empty).Trim();
        var description = string.IsNullOrWhiteSpace(tuple.Item2) ? null : tuple.Item2.Trim();
        var address = string.IsNullOrWhiteSpace(tuple.Item3) ? null : tuple.Item3.Trim();

        var parts = new List<string>();
        if (!string.IsNullOrEmpty(description))
        {
            parts.Add(description);
        }

        if (!string.IsNullOrEmpty(id) && !parts.Contains(id, StringComparer.OrdinalIgnoreCase))
        {
            parts.Add(id);
        }

        if (!string.IsNullOrEmpty(address))
        {
            parts.Add(address);
        }

        if (parts.Count == 0)
        {
            parts.Add("EtherCAT Adapter");
        }

        var displayName = string.Join(" – ", parts);
        return new EthercatInterfaceInfo(id, displayName, description, address);
    }
}
