using System;
using Microsoft.Extensions.Logging;
using XeryonEtherCAT.App.ViewModels;

namespace XeryonEtherCAT.App.Logging;

public sealed class RelayLoggerProvider : ILoggerProvider
{
    private readonly Action<LogEntryViewModel> _sink;
    private readonly string? _categoryPrefix;

    public RelayLoggerProvider(Action<LogEntryViewModel> sink, string? categoryPrefix = null)
    {
        _sink = sink ?? throw new ArgumentNullException(nameof(sink));
        _categoryPrefix = categoryPrefix;
    }

    public ILogger CreateLogger(string categoryName)
        => new RelayLogger(_sink, string.IsNullOrWhiteSpace(_categoryPrefix) ? categoryName : $"{_categoryPrefix}.{categoryName}");

    public void Dispose()
    {
    }

    private sealed class RelayLogger : ILogger
    {
        private readonly Action<LogEntryViewModel> _sink;
        private readonly string _category;

        public RelayLogger(Action<LogEntryViewModel> sink, string category)
        {
            _sink = sink;
            _category = category;
        }

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (formatter is null)
            {
                throw new ArgumentNullException(nameof(formatter));
            }

            var message = formatter(state, exception);
            var entry = new LogEntryViewModel(DateTimeOffset.UtcNow, logLevel, _category, message, exception);
            _sink(entry);
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
