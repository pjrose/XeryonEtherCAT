using System;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace XeryonEtherCAT.Core.Utilities;

/// <summary>
/// Thread-safe asynchronous event queue that guarantees ordered delivery of messages.
/// Useful when events are fired from background threads and the consumer needs to
/// serialize processing (e.g. writing to console/UI or relaying to external systems).
/// </summary>
public sealed class AsyncEventQueue<T> : IAsyncDisposable
{
    private readonly Channel<T> _channel;
    private readonly CancellationTokenSource _cts = new();
    private readonly Func<T, ValueTask> _handler;
    private readonly Task _pump;

    public AsyncEventQueue(Func<T, ValueTask> handler, bool singleWriter = false)
    {
        _handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _channel = Channel.CreateUnbounded<T>(new UnboundedChannelOptions
        {
            AllowSynchronousContinuations = false,
            SingleReader = true,
            SingleWriter = singleWriter
        });

        _pump = Task.Run(ProcessAsync);
    }

    /// <summary>
    /// Attempts to enqueue a message without blocking.
    /// </summary>
    public bool TryEnqueue(T message) => _channel.Writer.TryWrite(message);

    /// <summary>
    /// Enqueues a message, throwing if the queue has been completed.
    /// </summary>
    public void Enqueue(T message)
    {
        if (!_channel.Writer.TryWrite(message))
        {
            throw new InvalidOperationException("AsyncEventQueue has been completed.");
        }
    }

    private async Task ProcessAsync()
    {
        try
        {
            await foreach (var item in _channel.Reader.ReadAllAsync(_cts.Token).ConfigureAwait(false))
            {
                await _handler(item).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Swallow cancellation when disposing.
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        _channel.Writer.TryComplete();
        try
        {
            await _pump.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            _cts.Dispose();
        }
    }
}
