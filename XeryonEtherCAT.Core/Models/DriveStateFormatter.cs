using System.Collections.Generic;
using XeryonEtherCAT.Core.Internal.Soem;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Helper utilities for interpreting <see cref="SoemShim.DriveTxPDO"/> values.
/// </summary>
public static class DriveStateFormatter
{
    /// <summary>
    /// Converts the drive status bits into a packed mask for logging/debugging.
    /// </summary>
    public static uint ToBitMask(in SoemShim.DriveTxPDO status)
    {
        uint value = 0;
        if (status.AmplifiersEnabled != 0) value |= 1u << 0;
        if (status.EndStop != 0) value |= 1u << 1;
        if (status.ThermalProtection1 != 0) value |= 1u << 2;
        if (status.ThermalProtection2 != 0) value |= 1u << 3;
        if (status.ForceZero != 0) value |= 1u << 4;
        if (status.MotorOn != 0) value |= 1u << 5;
        if (status.ClosedLoop != 0) value |= 1u << 6;
        if (status.EncoderIndex != 0) value |= 1u << 7;
        if (status.EncoderValid != 0) value |= 1u << 8;
        if (status.SearchingIndex != 0) value |= 1u << 9;
        if (status.PositionReached != 0) value |= 1u << 10;
        if (status.ErrorCompensation != 0) value |= 1u << 11;
        if (status.EncoderError != 0) value |= 1u << 12;
        if (status.Scanning != 0) value |= 1u << 13;
        if (status.LeftEndStop != 0) value |= 1u << 14;
        if (status.RightEndStop != 0) value |= 1u << 15;
        if (status.ErrorLimit != 0) value |= 1u << 16;
        if (status.SearchingOptimalFrequency != 0) value |= 1u << 17;
        if (status.SafetyTimeout != 0) value |= 1u << 18;
        if (status.ExecuteAck != 0) value |= 1u << 19;
        if (status.EmergencyStop != 0) value |= 1u << 20;
        if (status.PositionFail != 0) value |= 1u << 21;
        return value;
    }

    /// <summary>
    /// Builds a short textual description summarizing the active drive flags.
    /// </summary>
    public static string Describe(in SoemShim.DriveTxPDO status)
    {
        var parts = new List<string>();
        if (status.AmplifiersEnabled != 0) parts.Add("Enabled");
        if (status.MotorOn != 0) parts.Add("MotorOn");
        if (status.ClosedLoop != 0) parts.Add("ClosedLoop");
        if (status.PositionReached != 0) parts.Add("InPos");
        if (status.Scanning != 0) parts.Add("Jogging");
        if (status.ExecuteAck != 0) parts.Add("Ack");
        if (status.ErrorLimit != 0) parts.Add("FollowErr");
        if (status.SafetyTimeout != 0) parts.Add("Timeout");
        if (status.PositionFail != 0) parts.Add("PositionFail");
        if (status.EmergencyStop != 0) parts.Add("E-Stop");
        if (status.ForceZero != 0) parts.Add("ForceZero");

        return parts.Count == 0 ? "Idle" : string.Join(", ", parts);
    }
}
