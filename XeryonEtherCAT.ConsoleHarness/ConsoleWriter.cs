using System.Threading.Channels;

namespace XeryonEtherCAT.ConsoleHarness;

public sealed class ConsoleWriter : IAsyncDisposable
{
    private readonly Channel<string> _messageChannel;
    private readonly CancellationTokenSource _cts;
    private readonly Task _writerTask;

    public ConsoleWriter()
    {
        _messageChannel = Channel.CreateUnbounded<string>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false
        });
        _cts = new CancellationTokenSource();
        _writerTask = Task.Run(() => RunWriterLoopAsync(_cts.Token));
    }

    public void WriteLine(string message = "")
    {
        _messageChannel.Writer.TryWrite(message);
    }

    public async Task FlushAsync()
    {
        // Write a sentinel to ensure all previous messages are processed
        var tcs = new TaskCompletionSource();
        await _messageChannel.Writer.WriteAsync(string.Empty);
        await Task.Delay(50); // Give writer time to catch up
    }

    private async Task RunWriterLoopAsync(CancellationToken ct)
    {
        await foreach (var message in _messageChannel.Reader.ReadAllAsync(ct))
        {
            if (!string.IsNullOrEmpty(message))
            {
                Console.WriteLine(message);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _messageChannel.Writer.Complete();
        _cts.Cancel();
        try
        {
            await _writerTask;
        }
        catch (OperationCanceledException)
        {
        }
        _cts.Dispose();
    }
}