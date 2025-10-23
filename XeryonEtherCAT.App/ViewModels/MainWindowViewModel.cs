using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using XeryonEtherCAT.App.Commands;
using XeryonEtherCAT.Core;
using XeryonEtherCAT.Ethercat.Interfaces;
using XeryonEtherCAT.Ethercat.Models;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class MainWindowViewModel : ViewModelBase
{
    private readonly IXeryonEthercatService _service;
    private readonly Dispatcher _dispatcher;
    private readonly AsyncCommand _connectCommand;
    private readonly AsyncCommand _disconnectCommand;
    private readonly AsyncCommand _refreshInterfacesCommand;
    private EthercatInterfaceInfo? _selectedInterface;
    private string _statusMessage = "Disconnected";
    private bool _isBusy;

    public MainWindowViewModel(IXeryonEthercatService service)
    {
        _service = service;
        _dispatcher = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _service.ConnectionStateChanged += OnConnectionStateChanged;
        _service.AxisStatusChanged += OnAxisStatusChanged;

        Interfaces = new ObservableCollection<EthercatInterfaceInfo>();
        Axes = new ObservableCollection<AxisViewModel>();

        _connectCommand = new AsyncCommand(ConnectAsync, () => !IsBusy && ConnectionState != ConnectionState.Connected);
        _disconnectCommand = new AsyncCommand(DisconnectAsync, () => ConnectionState == ConnectionState.Connected);
        _refreshInterfacesCommand = new AsyncCommand(LoadInterfacesAsync);
    }

    public ObservableCollection<EthercatInterfaceInfo> Interfaces { get; }

    public ObservableCollection<AxisViewModel> Axes { get; }

    public ConnectionState ConnectionState { get; private set; } = ConnectionState.Disconnected;

    public EthercatInterfaceInfo? SelectedInterface
    {
        get => _selectedInterface;
        set => SetField(ref _selectedInterface, value);
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            var previous = _isBusy;
            SetField(ref _isBusy, value);
            if (previous != value)
            {
                _connectCommand.RaiseCanExecuteChanged();
                _disconnectCommand.RaiseCanExecuteChanged();
            }
        }
    }

    public AsyncCommand ConnectCommand => _connectCommand;
    public AsyncCommand DisconnectCommand => _disconnectCommand;
    public AsyncCommand RefreshInterfacesCommand => _refreshInterfacesCommand;

    public async Task InitializeAsync()
    {
        BuildAxisViewModels();
        await LoadInterfacesAsync().ConfigureAwait(false);
    }

    private async Task LoadInterfacesAsync()
    {
        try
        {
            IsBusy = true;
            var adapters = await _service.GetAvailableInterfacesAsync().ConfigureAwait(false);
            await _dispatcher.InvokeAsync(() =>
            {
                var previousSelection = SelectedInterface?.InterfaceId;
                Interfaces.Clear();
                EthercatInterfaceInfo? nextSelection = null;
                foreach (var adapter in adapters)
                {
                    Interfaces.Add(adapter);
                    if (previousSelection is not null && nextSelection is null &&
                        string.Equals(adapter.InterfaceId, previousSelection, StringComparison.OrdinalIgnoreCase))
                    {
                        nextSelection = adapter;
                    }
                }

                if (nextSelection is not null)
                {
                    SelectedInterface = nextSelection;
                }
                else if (Interfaces.Count > 0)
                {
                    SelectedInterface = Interfaces[0];
                }
                else
                {
                    SelectedInterface = null;
                }
            });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Adapter discovery failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task ConnectAsync()
    {
        var selected = SelectedInterface;
        if (selected is null || string.IsNullOrWhiteSpace(selected.InterfaceId))
        {
            StatusMessage = "Select the network adapter that is connected to the EtherCAT network.";
            return;
        }

        try
        {
            IsBusy = true;
            StatusMessage = $"Connecting to {selected.DisplayName}...";
            var configurations = Axes.Select(vm => vm.Configuration).ToList();
            await _service.ConnectAsync(selected.InterfaceId, configurations).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "Connection canceled.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Connection failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            IsBusy = true;
            await _service.DisconnectAsync().ConfigureAwait(false);
            StatusMessage = "Disconnected";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Disconnect failed: {ex.Message}";
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void BuildAxisViewModels()
    {
        Axes.Clear();
        for (var i = 1; i <= 4; i++)
        {
            var configuration = new AxisConfiguration(i, $"Drive #{i}")
            {
                CountsPerUnit = 1.0,
                DefaultSpeed = 50_000,
                DefaultAcceleration = 1_000_000,
                DefaultDeceleration = 1_000_000
            };

            Axes.Add(new AxisViewModel(_service, configuration));
        }
    }

    private void OnAxisStatusChanged(object? sender, AxisStatusChangedEventArgs e)
    {
        var axis = Axes.FirstOrDefault(vm => string.Equals(vm.Name, e.Status.AxisName, StringComparison.OrdinalIgnoreCase));
        if (axis != null)
        {
            _dispatcher.Invoke(() => axis.UpdateStatus(e.Status));
        }
    }

    private void OnConnectionStateChanged(object? sender, ConnectionStateChangedEventArgs e)
    {
        _dispatcher.Invoke(() =>
        {
            ConnectionState = e.CurrentState;
            if (e.Exception is not null)
            {
                StatusMessage = $"{e.CurrentState}: {e.Exception.Message}";
            }
            else
            {
                StatusMessage = e.CurrentState switch
                {
                    ConnectionState.Connected when SelectedInterface is { } adapter => $"Connected to {adapter.DisplayName}",
                    ConnectionState.Connecting when SelectedInterface is { } adapter => $"Connecting to {adapter.DisplayName}...",
                    ConnectionState.Reconnecting => "Reconnecting...",
                    ConnectionState.Disconnected => "Disconnected",
                    _ => e.CurrentState.ToString()
                };
            }
            _connectCommand.RaiseCanExecuteChanged();
            _disconnectCommand.RaiseCanExecuteChanged();
        });
    }
}
