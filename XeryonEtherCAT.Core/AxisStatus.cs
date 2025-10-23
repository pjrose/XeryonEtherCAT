namespace XeryonEtherCAT.Core;

/// <summary>
/// Snapshot of the status information that is periodically reported by the drive.
/// </summary>
public sealed record AxisStatus(
    string AxisName,
    int AxisIndex,
    int ActualPosition,
    AxisStatusFlags Flags,
    byte Slot,
    System.DateTime TimestampUtc)
{
    /// <summary>
    /// Gets a textual representation that is useful for logging and diagnostics.
    /// </summary>
    public override string ToString()
        => $"Axis {AxisName}#{AxisIndex}: position={ActualPosition}, flags={Flags}, slot={Slot}";
}
