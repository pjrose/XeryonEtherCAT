
using System;
using XeryonEtherCAT.Core.Internal.Soem;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Event raised when drive status bits or position changes during command execution.
/// </summary>
public sealed class DriveStatusChangeEvent
{
    public DriveStatusChangeEvent(
        int slave,
        DateTimeOffset timestamp,
        SoemShim.DriveTxPDO currentStatus,
        SoemShim.DriveTxPDO previousStatus,
        uint changedBitsMask,
        string? activeCommand)
    {
        Slave = slave;
        Timestamp = timestamp;
        CurrentStatus = currentStatus;
        PreviousStatus = previousStatus;
        ChangedBitsMask = changedBitsMask;
        ActiveCommand = activeCommand;
    }

    public int Slave { get; }
    public DateTimeOffset Timestamp { get; }
    public SoemShim.DriveTxPDO CurrentStatus { get; }
    public SoemShim.DriveTxPDO PreviousStatus { get; }
    public uint ChangedBitsMask { get; }
    public string? ActiveCommand { get; }

    public int PositionChange => CurrentStatus.ActualPosition - PreviousStatus.ActualPosition;

    public override string ToString()
    {
        var changed = GetChangedFlags();
        var posChange = PositionChange;
        var cmd = string.IsNullOrEmpty(ActiveCommand) ? "none" : ActiveCommand;
        
        return $"[{Timestamp:HH:mm:ss.fff}] Slave {Slave} | Cmd: {cmd} | Pos: {CurrentStatus.ActualPosition} (Δ{posChange:+0;-#}) | Changed: {changed}";
    }

    private string GetChangedFlags()
    {
        if (ChangedBitsMask == 0)
            return "Position only";

        var flags = new System.Collections.Generic.List<string>();
        
        if ((ChangedBitsMask & (1u << 0)) != 0) flags.Add($"AmplifiersEnabled={CurrentStatus.AmplifiersEnabled}");
        if ((ChangedBitsMask & (1u << 1)) != 0) flags.Add($"EndStop={CurrentStatus.EndStop}");
        if ((ChangedBitsMask & (1u << 2)) != 0) flags.Add($"ThermalProtection1={CurrentStatus.ThermalProtection1}");
        if ((ChangedBitsMask & (1u << 3)) != 0) flags.Add($"ThermalProtection2={CurrentStatus.ThermalProtection2}");
        if ((ChangedBitsMask & (1u << 5)) != 0) flags.Add($"MotorOn={CurrentStatus.MotorOn}");
        if ((ChangedBitsMask & (1u << 6)) != 0) flags.Add($"ClosedLoop={CurrentStatus.ClosedLoop}");
        if ((ChangedBitsMask & (1u << 8)) != 0) flags.Add($"EncoderValid={CurrentStatus.EncoderValid}");
        if ((ChangedBitsMask & (1u << 9)) != 0) flags.Add($"SearchingIndex={CurrentStatus.SearchingIndex}");
        if ((ChangedBitsMask & (1u << 10)) != 0) flags.Add($"PositionReached={CurrentStatus.PositionReached}");
        if ((ChangedBitsMask & (1u << 12)) != 0) flags.Add($"EncoderError={CurrentStatus.EncoderError}");
        if ((ChangedBitsMask & (1u << 13)) != 0) flags.Add($"Scanning={CurrentStatus.Scanning}");
        if ((ChangedBitsMask & (1u << 14)) != 0) flags.Add($"LeftEndStop={CurrentStatus.LeftEndStop}");
        if ((ChangedBitsMask & (1u << 15)) != 0) flags.Add($"RightEndStop={CurrentStatus.RightEndStop}");
        if ((ChangedBitsMask & (1u << 16)) != 0) flags.Add($"ErrorLimit={CurrentStatus.ErrorLimit}");
        if ((ChangedBitsMask & (1u << 18)) != 0) flags.Add($"SafetyTimeout={CurrentStatus.SafetyTimeout}");
        if ((ChangedBitsMask & (1u << 19)) != 0) flags.Add($"ExecuteAck={CurrentStatus.ExecuteAck}");
        if ((ChangedBitsMask & (1u << 20)) != 0) flags.Add($"EmergencyStop={CurrentStatus.EmergencyStop}");
        if ((ChangedBitsMask & (1u << 21)) != 0) flags.Add($"PositionFail={CurrentStatus.PositionFail}");

        return string.Join(", ", flags);
    }
}