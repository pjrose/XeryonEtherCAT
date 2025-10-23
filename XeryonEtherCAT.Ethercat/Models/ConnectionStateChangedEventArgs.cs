using XeryonEtherCAT.Core;

namespace XeryonEtherCAT.Ethercat.Models;

public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState previous, ConnectionState current, Exception? exception = null)
    {
        PreviousState = previous;
        CurrentState = current;
        Exception = exception;
    }

    public ConnectionState PreviousState { get; }

    public ConnectionState CurrentState { get; }

    public Exception? Exception { get; }
}
