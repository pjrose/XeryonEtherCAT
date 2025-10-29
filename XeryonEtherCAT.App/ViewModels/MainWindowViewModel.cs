using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using XeryonEtherCAT.App.Commands;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Options;
using XeryonEtherCAT.Core.Services;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly IEthercatDriveService _service;
    private readonly DispatcherTimer _pollTimer;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private bool _initialized;
    private string _connectionStatus = "Initializing";
    private string _cycleMetrics = string.Empty;
    private int _selectedSlave = 1;
    private int _targetPosition = 10000;
    private int _jogVelocity = 20000;

    public MainWindowViewModel()
    {
        _service = new EthercatDriveService(new EthercatDriveOptions
        {
            CyclePeriod = TimeSpan.FromMilliseconds(5),
            EnableCycleTraceLogging = false
        }, NullLogger<EthercatDriveService>.Instance, new SimulatedSoemClient());

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
    }

    public ObservableCollection<DriveStatusViewModel> Drives { get; } = new();

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

    private void OnFaulted(object? sender, SoemFaultEvent e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = $"Fault on slave {e.Slave}: {e.Error.Code}";
        });
    }

    public async ValueTask DisposeAsync()
    {
        _pollTimer.Stop();
        _lifetimeCts.Cancel();
        _service.Faulted -= OnFaulted;
        await _service.DisposeAsync().ConfigureAwait(false);
        _lifetimeCts.Dispose();
    }
}
