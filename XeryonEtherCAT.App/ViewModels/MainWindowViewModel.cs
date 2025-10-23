using System;
using System.Collections.ObjectModel;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Threading;
using XeryonEtherCAT.Core.Internal.Simulation;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Options;
using XeryonEtherCAT.Core.Services;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly XeryonEtherCatService _service;
    private string _connectionStatus = "Disconnected";
    private bool _isOperational;

    public MainWindowViewModel()
    {
        var options = new XeryonEtherCatOptions
        {
            NetworkInterfaceName = "simulated",
            AutoReconnect = true,
            CycleTime = TimeSpan.FromMilliseconds(50)
        };

        for (var i = 1; i <= 4; i++)
        {
            options.AxisNames[i] = $"Drive {i}";
        }

        _service = new XeryonEtherCatService(options, new SimulatedEtherCatMaster());
        _service.ConnectionStateChanged += OnConnectionStateChanged;
    }

    public ObservableCollection<AxisViewModel> Axes { get; } = new();

    public string ConnectionStatus
    {
        get => _connectionStatus;
        private set => SetProperty(ref _connectionStatus, value);
    }

    public bool IsOperational
    {
        get => _isOperational;
        private set => SetProperty(ref _isOperational, value);
    }

    public async Task InitializeAsync()
    {
        await _service.StartAsync(CancellationToken.None).ConfigureAwait(false);

        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            Axes.Clear();
            foreach (var axis in _service.Axes)
            {
                Axes.Add(new AxisViewModel(axis, _service));
            }
        });
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            ConnectionStatus = $"Connection: {e.NewState}";
            IsOperational = e.NewState == ConnectionState.Operational;
        });
    }
}
