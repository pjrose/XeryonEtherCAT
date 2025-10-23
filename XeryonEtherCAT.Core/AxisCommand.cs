namespace XeryonEtherCAT.Core;

/// <summary>
/// Represents a single command that should be written to the drive process data image.
/// </summary>
public sealed record AxisCommand
{
    public AxisCommand(
        string command,
        int? targetPosition = null,
        uint? speed = null,
        ushort? acceleration = null,
        ushort? deceleration = null,
        bool execute = true)
    {
        Command = command;
        TargetPosition = targetPosition;
        Speed = speed;
        Acceleration = acceleration;
        Deceleration = deceleration;
        Execute = execute;
    }

    /// <summary>
    /// Four-character command identifier. When shorter than four characters the value is padded with null bytes.
    /// </summary>
    public string Command { get; }

    /// <summary>
    /// Optional target position in device counts.
    /// </summary>
    public int? TargetPosition { get; }

    /// <summary>
    /// Optional speed in device counts per second.
    /// </summary>
    public uint? Speed { get; }

    /// <summary>
    /// Optional acceleration in device counts per second squared.
    /// </summary>
    public ushort? Acceleration { get; }

    /// <summary>
    /// Optional deceleration in device counts per second squared.
    /// </summary>
    public ushort? Deceleration { get; }

    /// <summary>
    /// Controls the state of the Execute flag in the PDO image.
    /// </summary>
    public bool Execute { get; }
}
