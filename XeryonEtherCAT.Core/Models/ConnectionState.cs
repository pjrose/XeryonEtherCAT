namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Represents the health of the EtherCAT connection.
/// </summary>
public enum ConnectionState
{
    /// <summary>
    /// The master is not connected to the EtherCAT network.
    /// </summary>
    Disconnected,

    /// <summary>
    /// The master is attempting to (re)connect, or the last IO cycle failed.
    /// </summary>
    Degraded,

    /// <summary>
    /// The master is connected and process data exchange is running nominally.
    /// </summary>
    Operational
}
