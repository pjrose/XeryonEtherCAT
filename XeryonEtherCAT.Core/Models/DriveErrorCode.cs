namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Canonical drive fault classifications.
/// </summary>
public enum DriveErrorCode
{
    None = 0,
    FollowError,
    PositionFail,
    SafetyTimeout,
    EmergencyStop,
    EncoderError,
    ThermalProtection,
    EndStopHit,
    ForceZero,
    ErrorCompensationFault,
    UnknownFault,
}
