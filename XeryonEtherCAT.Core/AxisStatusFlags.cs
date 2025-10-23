namespace XeryonEtherCAT.Core;

/// <summary>
/// Bit field describing the live status reported by a Xeryon drive.
/// The layout mirrors the status bits that are transmitted by the EtherCAT PDO (#x1A00).
/// </summary>
[System.Flags]
public enum AxisStatusFlags
{
    None = 0,
    AmplifiersEnabled = 1 << 0,
    EndStop = 1 << 1,
    ThermalProtection1 = 1 << 2,
    ThermalProtection2 = 1 << 3,
    ForceZero = 1 << 4,
    MotorOn = 1 << 5,
    ClosedLoop = 1 << 6,
    EncoderAtIndex = 1 << 7,
    EncoderValid = 1 << 8,
    SearchingIndex = 1 << 9,
    PositionReached = 1 << 10,
    ErrorCompensation = 1 << 11,
    EncoderError = 1 << 12,
    Scanning = 1 << 13,
    LeftEndStop = 1 << 14,
    RightEndStop = 1 << 15,
    ErrorLimit = 1 << 16,
    SearchingOptimalFrequency = 1 << 17,
    SafetyTimeoutTriggered = 1 << 18,
    ExecuteAcknowledged = 1 << 19,
    EmergencyStop = 1 << 20,
    PositionFail = 1 << 21
}
