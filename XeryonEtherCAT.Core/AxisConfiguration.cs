namespace XeryonEtherCAT.Core;

/// <summary>
/// Describes how a Xeryon axis should be exposed to higher level applications.
/// </summary>
public sealed record AxisConfiguration
{
    public AxisConfiguration(int axisIndex, string name)
    {
        AxisIndex = axisIndex;
        Name = name;
    }

    /// <summary>
    /// EtherCAT slave address (1-based).
    /// </summary>
    public int AxisIndex { get; init; }

    /// <summary>
    /// Friendly name that is shown in user interfaces.
    /// </summary>
    public string Name { get; init; }

    /// <summary>
    /// Number of device counts that represent a single engineering unit.
    /// Defaults to 1, which means that values are interpreted as raw counts.
    /// </summary>
    public double CountsPerUnit { get; init; } = 1.0;

    /// <summary>
    /// Default motion speed expressed in device counts per second.
    /// </summary>
    public double DefaultSpeed { get; init; } = 50_000;

    /// <summary>
    /// Default acceleration (counts per second squared).
    /// </summary>
    public double DefaultAcceleration { get; init; } = 1_000_000;

    /// <summary>
    /// Default deceleration (counts per second squared).
    /// </summary>
    public double DefaultDeceleration { get; init; } = 1_000_000;

    /// <summary>
    /// When <c>true</c>, the service will limit the automatic reconnection attempts for the axis.
    /// </summary>
    public bool DisableAutoReconnect { get; init; }
}
