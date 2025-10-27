using System;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Represents a decoded drive error condition.
/// </summary>
public sealed class DriveError
{
    public DriveError(DriveErrorCode code, string message, string recoveryAction)
    {
        Code = code;
        Message = message ?? throw new ArgumentNullException(nameof(message));
        RecoveryAction = recoveryAction ?? throw new ArgumentNullException(nameof(recoveryAction));
    }

    public DriveErrorCode Code { get; }

    public string Message { get; }

    public string RecoveryAction { get; }

    public override string ToString() => $"{Code}: {Message} (Recovery: {RecoveryAction})";
}
