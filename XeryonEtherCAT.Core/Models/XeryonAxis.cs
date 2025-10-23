using System;
using System.Threading;
using System.Threading.Channels;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Represents a single Xeryon axis on the EtherCAT network.
/// </summary>
public sealed class XeryonAxis
{
    private readonly Channel<XeryonAxisCommand> _commandQueue = Channel.CreateUnbounded<XeryonAxisCommand>(new UnboundedChannelOptions
    {
        AllowSynchronousContinuations = false,
        SingleReader = true,
        SingleWriter = false
    });

    private readonly object _nameGate = new();
    private string _name;
    private int _statusBits;
    private int _actualPosition;
    private int _slot;
    private XeryonAxisCommand _latchedCommand = XeryonAxisCommand.None;

    internal XeryonAxis(int axisNumber, int slavePosition, uint productCode, uint revision, string defaultName)
    {
        AxisNumber = axisNumber;
        SlavePosition = slavePosition;
        ProductCode = productCode;
        Revision = revision;
        _name = defaultName;
    }

    public int AxisNumber { get; }

    public int SlavePosition { get; }

    public uint ProductCode { get; }

    public uint Revision { get; }

    public string Name
    {
        get
        {
            lock (_nameGate)
            {
                return _name;
            }
        }
        set
        {
            lock (_nameGate)
            {
                _name = value;
            }
        }
    }

    public AxisStatusFlags Status => (AxisStatusFlags)Volatile.Read(ref _statusBits);

    public int ActualPosition => Volatile.Read(ref _actualPosition);

    public int Slot => Volatile.Read(ref _slot);

    public event EventHandler<AxisStatusChangedEventArgs>? StatusChanged;

    internal XeryonCommandFrame GetNextCommandFrame()
    {
        if (_commandQueue.Reader.TryRead(out var command))
        {
            if (command.ClearAfterSend)
            {
                _latchedCommand = XeryonAxisCommand.None;
            }
            else
            {
                _latchedCommand = command;
            }

            return command.ToCommandFrame();
        }

        return _latchedCommand.ToCommandFrame();
    }

    internal async ValueTask EnqueueCommandAsync(XeryonAxisCommand command, CancellationToken cancellationToken)
    {
        await _commandQueue.Writer.WriteAsync(command, cancellationToken).ConfigureAwait(false);
    }

    internal void UpdateStatus(XeryonStatusFrame frame)
    {
        var previousStatus = (AxisStatusFlags)Interlocked.Exchange(ref _statusBits, (int)frame.Status);
        Interlocked.Exchange(ref _actualPosition, frame.ActualPosition);
        Interlocked.Exchange(ref _slot, frame.Slot);

        if (previousStatus != frame.Status)
        {
            StatusChanged?.Invoke(this, new AxisStatusChangedEventArgs(this, frame.Status, previousStatus));
        }
    }
}
