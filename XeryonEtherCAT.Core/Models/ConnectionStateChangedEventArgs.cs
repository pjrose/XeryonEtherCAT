using System;

namespace XeryonEtherCAT.Core.Models;

public sealed class ConnectionStateChangedEventArgs : EventArgs
{
    public ConnectionStateChangedEventArgs(ConnectionState newState, ConnectionState previousState, Exception? exception = null)
    {
        NewState = newState;
        PreviousState = previousState;
        Exception = exception;
        TimestampUtc = DateTimeOffset.UtcNow;
    }

    public ConnectionState NewState { get; }

    public ConnectionState PreviousState { get; }

    public Exception? Exception { get; }

    public DateTimeOffset TimestampUtc { get; }
}
