using System;
using Microsoft.Extensions.Logging;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class LogEntryViewModel
{
    public LogEntryViewModel(DateTimeOffset timestamp, LogLevel level, string category, string message, Exception? exception)
    {
        Timestamp = timestamp;
        Level = level;
        Category = category;
        Message = message;
        Exception = exception;
    }

    public DateTimeOffset Timestamp { get; }

    public LogLevel Level { get; }

    public string Category { get; }

    public string Message { get; }

    public Exception? Exception { get; }

    public override string ToString()
        => $"[{Timestamp:HH:mm:ss.fff}] {Level,-11} {Category}: {Message}{(Exception is null ? string.Empty : $"{Environment.NewLine}{Exception}")}";
}
