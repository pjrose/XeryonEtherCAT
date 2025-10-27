using System;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Bit-mapped drive status flags reported by the Xeryon TX PDO.
/// </summary>
[Flags]
public enum DriveStatus : uint
{
    None = 0,
    AmplifiersEnabled = 1u << 0,
    EndStop = 1u << 1,
    ThermalProtection1 = 1u << 2,
    ThermalProtection2 = 1u << 3,
    ForceZero = 1u << 4,
    MotorOn = 1u << 5,
    ClosedLoop = 1u << 6,
    EncoderAtIndex = 1u << 7,
    EncoderValid = 1u << 8,
    SearchingIndex = 1u << 9,
    PositionReached = 1u << 10,
    ErrorCompensation = 1u << 11,
    EncoderError = 1u << 12,
    Scanning = 1u << 13,
    LeftEndStop = 1u << 14,
    RightEndStop = 1u << 15,
    ErrorLimit = 1u << 16,
    SearchingOptimalFrequency = 1u << 17,
    SafetyTimeout = 1u << 18,
    ExecuteAck = 1u << 19,
    EmergencyStop = 1u << 20,
    PositionFail = 1u << 21,
}
