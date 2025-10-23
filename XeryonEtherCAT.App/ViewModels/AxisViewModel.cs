using System;
using System.Globalization;
using System.Threading.Tasks;
using XeryonEtherCAT.App.Commands;
using XeryonEtherCAT.Core;
using XeryonEtherCAT.Ethercat.Interfaces;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class AxisViewModel : ViewModelBase
{
    private readonly IXeryonEthercatService _service;
    private readonly AxisConfiguration _configuration;
    private readonly AsyncCommand _moveAbsoluteCommand;
    private readonly AsyncCommand _stepPositiveCommand;
    private readonly AsyncCommand _stepNegativeCommand;
    private readonly AsyncCommand _stopCommand;
    private readonly AsyncCommand _resetCommand;

    private double _targetPosition;
    private double _stepSize = 1.0;
    private double _speed;
    private double _acceleration;
    private double _deceleration;
    private double _currentPosition;
    private string _statusSummary = "-";
    private byte _slot;

    public AxisViewModel(IXeryonEthercatService service, AxisConfiguration configuration)
    {
        _service = service;
        _configuration = configuration;
        _speed = configuration.DefaultSpeed / configuration.CountsPerUnit;
        _acceleration = configuration.DefaultAcceleration / configuration.CountsPerUnit;
        _deceleration = configuration.DefaultDeceleration / configuration.CountsPerUnit;

        _moveAbsoluteCommand = new AsyncCommand(MoveToTargetAsync, () => _service.ConnectionState == ConnectionState.Connected);
        _stepPositiveCommand = new AsyncCommand(() => StepAsync(StepSize), () => _service.ConnectionState == ConnectionState.Connected);
        _stepNegativeCommand = new AsyncCommand(() => StepAsync(-StepSize), () => _service.ConnectionState == ConnectionState.Connected);
        _stopCommand = new AsyncCommand(() => _service.StopAxisAsync(Name));
        _resetCommand = new AsyncCommand(() => _service.ResetAxisAsync(Name));
    }

    public string Name => _configuration.Name;

    public int AxisIndex => _configuration.AxisIndex;

    public AxisConfiguration Configuration => _configuration;

    public double TargetPosition
    {
        get => _targetPosition;
        set => SetField(ref _targetPosition, value);
    }

    public double StepSize
    {
        get => _stepSize;
        set => SetField(ref _stepSize, Math.Max(0.0, value));
    }

    public double Speed
    {
        get => _speed;
        set => SetField(ref _speed, Math.Max(0.0, value));
    }

    public double Acceleration
    {
        get => _acceleration;
        set => SetField(ref _acceleration, Math.Max(0.0, value));
    }

    public double Deceleration
    {
        get => _deceleration;
        set => SetField(ref _deceleration, Math.Max(0.0, value));
    }

    public double CurrentPosition
    {
        get => _currentPosition;
        private set => SetField(ref _currentPosition, value);
    }

    public string StatusSummary
    {
        get => _statusSummary;
        private set => SetField(ref _statusSummary, value);
    }

    public byte Slot
    {
        get => _slot;
        private set => SetField(ref _slot, value);
    }

    public AsyncCommand MoveAbsoluteCommand => _moveAbsoluteCommand;
    public AsyncCommand StepPositiveCommand => _stepPositiveCommand;
    public AsyncCommand StepNegativeCommand => _stepNegativeCommand;
    public AsyncCommand StopCommand => _stopCommand;
    public AsyncCommand ResetCommand => _resetCommand;

    public void UpdateStatus(AxisStatus status)
    {
        if (!string.Equals(status.AxisName, Name, StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        CurrentPosition = AxisUnitConverter.ToEngineeringUnits(status.ActualPosition, _configuration);
        Slot = status.Slot;
        StatusSummary = status.Flags == AxisStatusFlags.None
            ? "Idle"
            : status.Flags.ToString();
    }

    private async Task MoveToTargetAsync()
    {
        var targetCounts = AxisUnitConverter.ToDeviceCounts(TargetPosition, _configuration);
        var speedCounts = (uint)Math.Max(0, AxisUnitConverter.ToDeviceCounts(Speed, _configuration));
        var accelerationCounts = ClampToUInt16(AxisUnitConverter.ToDeviceCounts(Acceleration, _configuration));
        var decelerationCounts = ClampToUInt16(AxisUnitConverter.ToDeviceCounts(Deceleration, _configuration));

        var command = new AxisCommand("MOVE", targetCounts, speedCounts, accelerationCounts, decelerationCounts);

        await _service.SendCommandAsync(Name, command).ConfigureAwait(false);
    }

    private async Task StepAsync(double step)
    {
        var target = CurrentPosition + step;
        TargetPosition = target;
        await MoveToTargetAsync().ConfigureAwait(false);
    }

    private static ushort ClampToUInt16(int value)
    {
        if (value < 0)
        {
            return 0;
        }

        if (value > ushort.MaxValue)
        {
            return ushort.MaxValue;
        }

        return (ushort)value;
    }
}
