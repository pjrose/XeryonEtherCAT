using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia.Threading;
using XeryonEtherCAT.App.Commands;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Services;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class AxisViewModel : ViewModelBase
{
    private readonly XeryonAxis _axis;
    private readonly XeryonEtherCatService _service;
    private readonly AsyncRelayCommand _stepUpCommand;
    private readonly AsyncRelayCommand _stepDownCommand;
    private readonly AsyncRelayCommand _moveToTargetCommand;
    private readonly AsyncRelayCommand _goToZeroCommand;
    private readonly AsyncRelayCommand _stopCommand;
    private readonly AsyncRelayCommand _resetCommand;

    private double _stepSize = 1_000;
    private double _targetPosition;
    private double _absolutePosition;
    private double _speed = 500;
    private ushort _acceleration = 100;
    private ushort _deceleration = 100;
    private string _statusSummary = string.Empty;

    public AxisViewModel(XeryonAxis axis, XeryonEtherCatService service)
    {
        _axis = axis;
        _service = service;
        _targetPosition = axis.ActualPosition;
        _absolutePosition = axis.ActualPosition;
        _statusSummary = axis.Status.ToString();

        _axis.StatusChanged += OnAxisStatusChanged;

        _stepUpCommand = new AsyncRelayCommand(() => StepAsync(1));
        _stepDownCommand = new AsyncRelayCommand(() => StepAsync(-1));
        _moveToTargetCommand = new AsyncRelayCommand(MoveToTargetAsync);
        _goToZeroCommand = new AsyncRelayCommand(GoToZeroAsync);
        _stopCommand = new AsyncRelayCommand(StopAsync);
        _resetCommand = new AsyncRelayCommand(ResetAsync);
    }

    public string Name => _axis.Name;

    public int AxisNumber => _axis.AxisNumber;

    public double StepSize
    {
        get => _stepSize;
        set => SetProperty(ref _stepSize, Math.Max(1, value));
    }

    public double TargetPosition
    {
        get => _targetPosition;
        set => SetProperty(ref _targetPosition, value);
    }

    public double AbsolutePosition
    {
        get => _absolutePosition;
        private set => SetProperty(ref _absolutePosition, value);
    }

    public double Speed
    {
        get => _speed;
        set => SetProperty(ref _speed, Math.Max(1, value));
    }

    public ushort Acceleration
    {
        get => _acceleration;
        set => SetProperty(ref _acceleration, (ushort)Math.Max(1, (int)value));
    }

    public ushort Deceleration
    {
        get => _deceleration;
        set => SetProperty(ref _deceleration, (ushort)Math.Max(1, (int)value));
    }

    public string StatusSummary
    {
        get => _statusSummary;
        private set => SetProperty(ref _statusSummary, value);
    }

    public ICommand StepUpCommand => _stepUpCommand;

    public ICommand StepDownCommand => _stepDownCommand;

    public ICommand MoveToTargetCommand => _moveToTargetCommand;

    public ICommand GoToZeroCommand => _goToZeroCommand;

    public ICommand StopCommand => _stopCommand;

    public ICommand ResetCommand => _resetCommand;

    private async Task StepAsync(int direction)
    {
        var delta = (int)Math.Round(StepSize, MidpointRounding.AwayFromZero) * direction;
        var command = XeryonAxisCommand.Step(delta, (uint)Math.Round(Speed), Acceleration, Deceleration);
        await _service.EnqueueCommandAsync(_axis.AxisNumber, command, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task MoveToTargetAsync()
    {
        var command = XeryonAxisCommand.MoveTo((int)Math.Round(TargetPosition, MidpointRounding.AwayFromZero), (uint)Math.Round(Speed), Acceleration, Deceleration);
        await _service.EnqueueCommandAsync(_axis.AxisNumber, command, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task GoToZeroAsync()
    {
        var command = XeryonAxisCommand.MoveTo(0, (uint)Math.Round(Speed), Acceleration, Deceleration);
        await _service.EnqueueCommandAsync(_axis.AxisNumber, command, CancellationToken.None).ConfigureAwait(false);
    }

    private async Task StopAsync()
    {
        await _service.EnqueueCommandAsync(_axis.AxisNumber, XeryonAxisCommand.Stop(), CancellationToken.None).ConfigureAwait(false);
    }

    private async Task ResetAsync()
    {
        await _service.EnqueueCommandAsync(_axis.AxisNumber, XeryonAxisCommand.Reset(), CancellationToken.None).ConfigureAwait(false);
    }

    private void OnAxisStatusChanged(object? sender, AxisStatusChangedEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            AbsolutePosition = e.Axis.ActualPosition;
            StatusSummary = FormatStatus(e.NewStatus);
        });
    }

    private static string FormatStatus(AxisStatusFlags status)
    {
        if (status == AxisStatusFlags.None)
        {
            return "Idle";
        }

        var active = Enum.GetValues(typeof(AxisStatusFlags))
            .Cast<AxisStatusFlags>()
            .Where(flag => flag != AxisStatusFlags.None && status.HasFlag(flag))
            .Select(flag => flag.ToString());

        return string.Join(", ", active);
    }
}
