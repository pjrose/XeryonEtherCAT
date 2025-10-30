using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;
using XeryonEtherCAT.App.Commands;
using XeryonEtherCAT.App.Logging;
using XeryonEtherCAT.App.Services;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Options;
using XeryonEtherCAT.Core.Services;
using XeryonEtherCAT.Core.Utilities;
using XeryonEtherCAT.Integrations.Mqtt;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private const int MaxLogEntries = 500;
    private const int MaxEventEntries = 200;

    private readonly IEthercatDriveService _service;
    private readonly DispatcherTimer _pollTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly AsyncEventQueue<DriveStatusChangeEvent> _statusQueue;
    private readonly AsyncEventQueue<SoemFaultEvent> _faultQueue;
    private readonly AsyncEventQueue<LogEntryViewModel> _logQueue;
    private readonly LogConsoleService _consoleService = new();
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _uiLogger;
    private EthercatMqttBridge? _mqttBridge;
    private bool _initialized;
    private string _connectionStatus = "Initializing";
    private string _cycleMetrics = string.Empty;
    private int _selectedSlave = 1;
    private int _targetPosition = 10000;
    private int _jogVelocity = 20000;
    private bool _showConsoleLogs;
    private string _mqttHost = "localhost";
    private int _mqttPort = 1883;
    private bool _mqttConnected;

    public MainWindowViewModel()
    {
        _statusQueue = new AsyncEventQueue<DriveStatusChangeEvent>(change =>
        {
            Dispatcher.UIThread.Post(() => ProcessStatusChange(change));
            return ValueTask.CompletedTask;
        });

        _faultQueue = new AsyncEventQueue<SoemFaultEvent>(fault =>
        {
            Dispatcher.UIThread.Post(() => ProcessFault(fault));
            return ValueTask.CompletedTask;
        });

        _logQueue = new AsyncEventQueue<LogEntryViewModel>(entry =>
        {
            Dispatcher.UIThread.Post(() => ProcessLogEntry(entry));
            return ValueTask.CompletedTask;
        });

        var relayProvider = new RelayLoggerProvider(entry =>
        {
            if (!_logQueue.TryEnqueue(entry))
            {
                ProcessLogEntry(entry);
            }
        }, "Dashboard");

        _loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Debug);
            builder.AddProvider(relayProvider);
        });
        _uiLogger = _loggerFactory.CreateLogger<MainWindowViewModel>();

        _service = new EthercatDriveService(new EthercatDriveOptions
        {
            CyclePeriod = TimeSpan.FromMilliseconds(5),
            EnableCycleTraceLogging = false
        }, _loggerFactory.CreateLogger<EthercatDriveService>(), new SimulatedSoemClient());

        _pollTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(200)
        };
        _pollTimer.Tick += (_, _) => RefreshSnapshot();

        EnableCommand = new AsyncRelayCommand(() => ExecutePerSlaveAsync(s => _service.EnableAsync(s, true, _lifetimeCts.Token)));
        DisableCommand = new AsyncRelayCommand(() => ExecutePerSlaveAsync(s => _service.EnableAsync(s, false, _lifetimeCts.Token)));
        MoveCommand = new AsyncRelayCommand(() => _service.MoveAbsoluteAsync(SelectedSlave, TargetPosition, 30000, 1000, 1000, TimeSpan.FromSeconds(2), _lifetimeCts.Token));
        IndexCommand = new AsyncRelayCommand(() => _service.IndexAsync(SelectedSlave, 0, 15000, 1000, 1000, TimeSpan.FromSeconds(3), _lifetimeCts.Token));
        JogPositiveCommand = new AsyncRelayCommand(() => _service.JogAsync(SelectedSlave, 1, JogVelocity, 800, 800, _lifetimeCts.Token));
        JogNegativeCommand = new AsyncRelayCommand(() => _service.JogAsync(SelectedSlave, -1, JogVelocity, 800, 800, _lifetimeCts.Token));
        JogStopCommand = new AsyncRelayCommand(() => _service.JogAsync(SelectedSlave, 0, 0, 0, 0, _lifetimeCts.Token));
        HaltCommand = new AsyncRelayCommand(() => _service.HaltAsync(SelectedSlave, _lifetimeCts.Token));
        StopCommand = new AsyncRelayCommand(() => _service.StopAsync(SelectedSlave, _lifetimeCts.Token));
        ResetCommand = new AsyncRelayCommand(() => _service.ResetAsync(SelectedSlave, _lifetimeCts.Token));
        ClearLogCommand = new RelayCommand(() => Logs.Clear());
        ToggleMqttCommand = new AsyncRelayCommand(ToggleMqttAsync);
    }

    public ObservableCollection<DriveStatusViewModel> Drives { get; } = new();

    public ObservableCollection<DriveEventViewModel> StatusEvents { get; } = new();

    public ObservableCollection<LogEntryViewModel> Logs { get; } = new();

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public string CycleMetrics
    {
        get => _cycleMetrics;
        private set => SetProperty(ref _cycleMetrics, value);
    }

    public int SelectedSlave
    {
        get => _selectedSlave;
        set => SetProperty(ref _selectedSlave, value);
    }

    public int TargetPosition
    {
        get => _targetPosition;
        set => SetProperty(ref _targetPosition, value);
    }

    public int JogVelocity
    {
        get => _jogVelocity;
        set => SetProperty(ref _jogVelocity, value);
    }

    public AsyncRelayCommand EnableCommand { get; }

    public AsyncRelayCommand DisableCommand { get; }

    public AsyncRelayCommand MoveCommand { get; }

    public AsyncRelayCommand IndexCommand { get; }

    public AsyncRelayCommand JogPositiveCommand { get; }

    public AsyncRelayCommand JogNegativeCommand { get; }

    public AsyncRelayCommand JogStopCommand { get; }

    public AsyncRelayCommand HaltCommand { get; }

    public AsyncRelayCommand StopCommand { get; }

    public AsyncRelayCommand ResetCommand { get; }

    public RelayCommand ClearLogCommand { get; }

    public AsyncRelayCommand ToggleMqttCommand { get; }

    public bool ShowConsoleLogs
    {
        get => _showConsoleLogs;
        set
        {
            if (SetProperty(ref _showConsoleLogs, value))
            {
                if (value)
                {
                    _consoleService.EnsureConsole();
                }
                else
                {
                    _consoleService.ReleaseConsole();
                }
            }
        }
    }

    public string MqttHost
    {
        get => _mqttHost;
        set
        {
            if (SetProperty(ref _mqttHost, value))
            {
                OnPropertyChanged(nameof(MqttStatus));
            }
        }
    }

    public int MqttPort
    {
        get => _mqttPort;
        set
        {
            if (SetProperty(ref _mqttPort, value))
            {
                OnPropertyChanged(nameof(MqttStatus));
            }
        }
    }

    public bool MqttConnected
    {
        get => _mqttConnected;
        private set
        {
            if (SetProperty(ref _mqttConnected, value))
            {
                OnPropertyChanged(nameof(MqttStatus));
                OnPropertyChanged(nameof(MqttButtonText));
            }
        }
    }

    public string MqttStatus => MqttConnected ? $"MQTT connected to {MqttHost}:{MqttPort}" : "MQTT offline";

    public string MqttButtonText => MqttConnected ? "Disconnect MQTT" : "Connect MQTT";

    public async Task InitializeAsync()
    {
        if (_initialized)
        {
            return;
        }

        await _service.InitializeAsync("simulated", _lifetimeCts.Token).ConfigureAwait(false);
        var slaveCount = await _service.GetSlaveCountAsync().ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Drives.Clear();
            for (var i = 1; i <= slaveCount; i++)
            {
                Drives.Add(new DriveStatusViewModel(i));
            }

            SelectedSlave = 1;
            ConnectionStatus = $"Online - {slaveCount} drives";
        });

        _service.Faulted += OnFaulted;
        _service.StatusChanged += OnStatusChanged;
        _pollTimer.Start();
        _initialized = true;
    }

    private async Task ExecutePerSlaveAsync(Func<int, Task> action)
    {
        var slave = SelectedSlave;
        if (Drives.All(d => d.Slave != slave))
        {
            return;
        }

        await action(slave).ConfigureAwait(false);
    }

    private void RefreshSnapshot()
    {
        SoemStatusSnapshot snapshot;
        try
        {
            snapshot = _service.GetStatus();
        }
        catch
        {
            return;
        }

        Dispatcher.UIThread.Post(() =>
        {
            for (var i = 0; i < Drives.Count && i < snapshot.DriveStates.Length; i++)
            {
                Drives[i].Update(snapshot.DriveStates[i]);
            }

            CycleMetrics = $"WKC {snapshot.Health.LastWkc}/{snapshot.Health.GroupExpectedWkc} | Cycle {snapshot.CycleTime.TotalMilliseconds:F2} ms";
        });
    }

    private void OnStatusChanged(object? sender, DriveStatusChangeEvent e)
    {
        _statusQueue.TryEnqueue(e);
    }

    private void OnFaulted(object? sender, SoemFaultEvent e)
    {
        _faultQueue.TryEnqueue(e);
    }

    private void ProcessStatusChange(DriveStatusChangeEvent change)
    {
        var index = change.Slave - 1;
        if (index >= 0 && index < Drives.Count)
        {
            Drives[index].Update(change.CurrentStatus);
        }

        StatusEvents.Insert(0, new DriveEventViewModel(change));
        while (StatusEvents.Count > MaxEventEntries)
        {
            StatusEvents.RemoveAt(StatusEvents.Count - 1);
        }
    }

    private void ProcessFault(SoemFaultEvent fault)
    {
        ConnectionStatus = $"Fault on slave {fault.Slave}: {fault.Error.Code}";
        StatusEvents.Insert(0, new DriveEventViewModel(new DriveStatusChangeEvent(fault.Slave, DateTimeOffset.UtcNow, fault.Status, fault.Status, 0u, fault.Error.Code.ToString())));
        while (StatusEvents.Count > MaxEventEntries)
        {
            StatusEvents.RemoveAt(StatusEvents.Count - 1);
        }
        _uiLogger.LogWarning("Fault on slave {Slave}: {Error}", fault.Slave, fault.Error);
    }

    private void ProcessLogEntry(LogEntryViewModel entry)
    {
        Logs.Insert(0, entry);
        while (Logs.Count > MaxLogEntries)
        {
            Logs.RemoveAt(Logs.Count - 1);
        }

        if (ShowConsoleLogs)
        {
            Console.WriteLine(entry.ToString());
        }
    }

    private async Task ToggleMqttAsync()
    {
        if (_mqttBridge is null)
        {
            var options = new EthercatMqttBridgeOptions
            {
                BrokerHost = MqttHost,
                BrokerPort = MqttPort
            };

            var bridgeLogger = _loggerFactory.CreateLogger<EthercatMqttBridge>();
            var bridge = new EthercatMqttBridge(_service, options, bridgeLogger);

            try
            {
                await bridge.StartAsync(_lifetimeCts.Token).ConfigureAwait(false);
                _mqttBridge = bridge;
                MqttConnected = true;
                _uiLogger.LogInformation("MQTT bridge connected to {Host}:{Port}", MqttHost, MqttPort);
            }
            catch (Exception ex)
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
                _uiLogger.LogError(ex, "Failed to connect MQTT bridge to {Host}:{Port}", MqttHost, MqttPort);
                _logQueue.TryEnqueue(new LogEntryViewModel(DateTimeOffset.UtcNow, LogLevel.Error, "MQTT", $"Failed to connect to {MqttHost}:{MqttPort}: {ex.Message}", ex));
            }
        }
        else
        {
            try
            {
                await _mqttBridge.StopAsync(_lifetimeCts.Token).ConfigureAwait(false);
            }
            finally
            {
                await _mqttBridge.DisposeAsync().ConfigureAwait(false);
                _mqttBridge = null;
                MqttConnected = false;
                _uiLogger.LogInformation("MQTT bridge disconnected");
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _pollTimer.Stop();
        _lifetimeCts.Cancel();
        _service.Faulted -= OnFaulted;
        _service.StatusChanged -= OnStatusChanged;
        await _service.DisposeAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();
        await _statusQueue.DisposeAsync().ConfigureAwait(false);
        await _faultQueue.DisposeAsync().ConfigureAwait(false);
        await _logQueue.DisposeAsync().ConfigureAwait(false);
        if (_mqttBridge is not null)
        {
            await _mqttBridge.DisposeAsync().ConfigureAwait(false);
        }
        _consoleService.Dispose();
        _loggerFactory.Dispose();
    }
}
