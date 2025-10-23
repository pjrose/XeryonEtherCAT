namespace XeryonEtherCAT.Core;

/// <summary>
/// Represents the state of the EtherCAT communication channel.
/// </summary>
public enum ConnectionState
{
    Disconnected,
    Connecting,
    Connected,
    Reconnecting,
    Faulted
}
