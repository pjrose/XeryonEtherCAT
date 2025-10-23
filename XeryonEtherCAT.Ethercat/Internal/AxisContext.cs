using System;
using EC_Net;
using IEC_Net;
using XeryonEtherCAT.Core;

namespace XeryonEtherCAT.Ethercat.Internal;

internal sealed class AxisContext
{
    private readonly object _syncRoot = new();

    private AxisContext(
        AxisConfiguration configuration,
        EtherCATSlave slave,
        ReceivePDOMapping command,
        ReceivePDOMapping targetPosition,
        ReceivePDOMapping speed,
        ReceivePDOMapping acceleration,
        ReceivePDOMapping deceleration,
        ReceivePDOMapping execute,
        TransmitPDOMapping actualPosition,
        IReadOnlyDictionary<AxisStatusFlags, TransmitPDOMapping> statusBits,
        TransmitPDOMapping slot)
    {
        Configuration = configuration;
        Slave = slave;
        Command = command;
        TargetPosition = targetPosition;
        Speed = speed;
        Acceleration = acceleration;
        Deceleration = deceleration;
        Execute = execute;
        ActualPosition = actualPosition;
        StatusBits = statusBits;
        Slot = slot;
    }

    public AxisConfiguration Configuration { get; }

    public EtherCATSlave Slave { get; }

    public ReceivePDOMapping Command { get; }

    public ReceivePDOMapping TargetPosition { get; }

    public ReceivePDOMapping Speed { get; }

    public ReceivePDOMapping Acceleration { get; }

    public ReceivePDOMapping Deceleration { get; }

    public ReceivePDOMapping Execute { get; }

    public TransmitPDOMapping ActualPosition { get; }

    public IReadOnlyDictionary<AxisStatusFlags, TransmitPDOMapping> StatusBits { get; }

    public TransmitPDOMapping Slot { get; }

    public AxisStatus? LastStatus { get; set; }

    public bool RequiresExecuteReset { get; set; }

    public void UpdateCommand(AxisCommand command)
    {
        lock (_syncRoot)
        {
            Command.Value = EncodeCommand(command.Command);

            if (command.TargetPosition is int target)
            {
                TargetPosition.Value = target;
            }

            if (command.Speed is uint speed)
            {
                Speed.Value = speed;
            }

            if (command.Acceleration is ushort acceleration)
            {
                Acceleration.Value = acceleration;
            }

            if (command.Deceleration is ushort deceleration)
            {
                Deceleration.Value = deceleration;
            }

            Execute.Value = (byte)(command.Execute ? 1 : 0);
            RequiresExecuteReset = command.Execute;
        }
    }

    public void ResetExecuteFlag()
    {
        lock (_syncRoot)
        {
            Execute.Value = (byte)0;
            RequiresExecuteReset = false;
        }
    }

    public AxisStatus BuildStatus()
    {
        var actualPosition = Convert.ToInt32(ActualPosition.Value);
        var flags = AxisStatusFlags.None;
        foreach (var pair in StatusBits)
        {
            if (pair.Value.Value is bool b && b)
            {
                flags |= pair.Key;
            }
        }

        var slotValue = Slot.Value switch
        {
            byte b => b,
            sbyte sb => unchecked((byte)sb),
            _ => Convert.ToByte(Slot.Value ?? 0)
        };

        var status = new AxisStatus(Configuration.Name, Configuration.AxisIndex, actualPosition, flags, slotValue, DateTime.UtcNow);
        LastStatus = status;
        return status;
    }

    public static AxisContext Create(EtherCATSlave slave, AxisConfiguration configuration)
    {
        var command = slave.AddRxPDOMapping(0, 0x1600, 1, typeof(uint));
        var targetPosition = slave.AddRxPDOMapping(0, 0x1600, 2, typeof(int));
        var speed = slave.AddRxPDOMapping(0, 0x1600, 3, typeof(uint));
        var acceleration = slave.AddRxPDOMapping(0, 0x1600, 4, typeof(ushort));
        var deceleration = slave.AddRxPDOMapping(0, 0x1600, 5, typeof(ushort));
        var execute = slave.AddRxPDOMapping(0, 0x1600, 6, typeof(byte));

        var actualPosition = slave.AddTxPDOMapping(0, 0x1A00, 1, typeof(int));

        var statusBits = new Dictionary<AxisStatusFlags, TransmitPDOMapping>
        {
            [AxisStatusFlags.AmplifiersEnabled] = slave.AddTxPDOMapping(0, 0x1A00, 2, typeof(bool)),
            [AxisStatusFlags.EndStop] = slave.AddTxPDOMapping(0, 0x1A00, 3, typeof(bool)),
            [AxisStatusFlags.ThermalProtection1] = slave.AddTxPDOMapping(0, 0x1A00, 4, typeof(bool)),
            [AxisStatusFlags.ThermalProtection2] = slave.AddTxPDOMapping(0, 0x1A00, 5, typeof(bool)),
            [AxisStatusFlags.ForceZero] = slave.AddTxPDOMapping(0, 0x1A00, 6, typeof(bool)),
            [AxisStatusFlags.MotorOn] = slave.AddTxPDOMapping(0, 0x1A00, 7, typeof(bool)),
            [AxisStatusFlags.ClosedLoop] = slave.AddTxPDOMapping(0, 0x1A00, 8, typeof(bool)),
            [AxisStatusFlags.EncoderAtIndex] = slave.AddTxPDOMapping(0, 0x1A00, 9, typeof(bool)),
            [AxisStatusFlags.EncoderValid] = slave.AddTxPDOMapping(0, 0x1A00, 10, typeof(bool)),
            [AxisStatusFlags.SearchingIndex] = slave.AddTxPDOMapping(0, 0x1A00, 11, typeof(bool)),
            [AxisStatusFlags.PositionReached] = slave.AddTxPDOMapping(0, 0x1A00, 12, typeof(bool)),
            [AxisStatusFlags.ErrorCompensation] = slave.AddTxPDOMapping(0, 0x1A00, 13, typeof(bool)),
            [AxisStatusFlags.EncoderError] = slave.AddTxPDOMapping(0, 0x1A00, 14, typeof(bool)),
            [AxisStatusFlags.Scanning] = slave.AddTxPDOMapping(0, 0x1A00, 15, typeof(bool)),
            [AxisStatusFlags.LeftEndStop] = slave.AddTxPDOMapping(0, 0x1A00, 16, typeof(bool)),
            [AxisStatusFlags.RightEndStop] = slave.AddTxPDOMapping(0, 0x1A00, 17, typeof(bool)),
            [AxisStatusFlags.ErrorLimit] = slave.AddTxPDOMapping(0, 0x1A00, 18, typeof(bool)),
            [AxisStatusFlags.SearchingOptimalFrequency] = slave.AddTxPDOMapping(0, 0x1A00, 19, typeof(bool)),
            [AxisStatusFlags.SafetyTimeoutTriggered] = slave.AddTxPDOMapping(0, 0x1A00, 20, typeof(bool)),
            [AxisStatusFlags.ExecuteAcknowledged] = slave.AddTxPDOMapping(0, 0x1A00, 21, typeof(bool)),
            [AxisStatusFlags.EmergencyStop] = slave.AddTxPDOMapping(0, 0x1A00, 22, typeof(bool)),
            [AxisStatusFlags.PositionFail] = slave.AddTxPDOMapping(0, 0x1A00, 23, typeof(bool))
        };

        var slot = slave.AddTxPDOMapping(0, 0x1A00, 24, typeof(byte));

        return new AxisContext(configuration, slave, command, targetPosition, speed, acceleration, deceleration, execute, actualPosition, statusBits, slot);
    }

    private static uint EncodeCommand(string command)
    {
        if (string.IsNullOrWhiteSpace(command))
        {
            return 0u;
        }

        var normalized = command.Trim().ToUpperInvariant();
        var bytes = new byte[4];
        var span = normalized.AsSpan();
        var length = Math.Min(4, span.Length);
        for (var i = 0; i < length; i++)
        {
            bytes[i] = (byte)span[i];
        }

        return BitConverter.ToUInt32(bytes, 0);
    }
}
