namespace XeryonEtherCAT.Ethercat.Models;

public sealed class XeryonEthercatOptions
{
    /// <summary>
    /// Interval between process data updates.
    /// </summary>
    public TimeSpan PollingInterval { get; init; } = TimeSpan.FromMilliseconds(50);

    /// <summary>
    /// Size of the IO map in bytes. The default value is large enough to hold sixteen drives.
    /// </summary>
    public int IoMapSize { get; init; } = 1024;

    /// <summary>
    /// Maximum delay applied between automatic reconnection attempts.
    /// </summary>
    public TimeSpan ReconnectInterval { get; init; } = TimeSpan.FromSeconds(2);
}
