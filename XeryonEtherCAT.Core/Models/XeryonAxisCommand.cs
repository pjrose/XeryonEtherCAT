namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// High level command envelope used by the service.
/// </summary>
public sealed record XeryonAxisCommand(
    string Command,
    int TargetPosition = 0,
    uint Speed = 0,
    ushort Acceleration = 0,
    ushort Deceleration = 0,
    byte ExecuteFlag = 1,
    bool ClearAfterSend = true)
{
    public static XeryonAxisCommand None { get; } = new("NONE", 0, 0, 0, 0, 0, false);

    public static XeryonAxisCommand Stop() => new("STOP", 0, 0, 0, 0, 1);

    public static XeryonAxisCommand Reset() => new("RSET", 0, 0, 0, 0, 1);

    public static XeryonAxisCommand EnableMotor() => new("MENA", 0, 0, 0, 0, 1);

    public static XeryonAxisCommand DisableMotor() => new("MDIS", 0, 0, 0, 0, 1);

    public static XeryonAxisCommand MoveTo(int targetPosition, uint speed, ushort acceleration, ushort deceleration)
        => new("DPOS", targetPosition, speed, acceleration, deceleration, 1);

    public static XeryonAxisCommand Step(int delta, uint speed, ushort acceleration, ushort deceleration)
        => new("STEP", delta, speed, acceleration, deceleration, 1);

    public static XeryonAxisCommand StartScan(int direction)
        => new("SCAN", direction, 0, 0, 0, 1, false);

    public static XeryonAxisCommand StopScan()
        => new("SCAN", 0, 0, 0, 0, 0);

    public XeryonCommandFrame ToCommandFrame()
        => new(Command, TargetPosition, Speed, Acceleration, Deceleration, ExecuteFlag);
}
