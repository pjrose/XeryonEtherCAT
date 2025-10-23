using System;

namespace XeryonEtherCAT.Core.Models;

public sealed class AxisStatusChangedEventArgs : EventArgs
{
    public AxisStatusChangedEventArgs(XeryonAxis axis, AxisStatusFlags newStatus, AxisStatusFlags previousStatus)
    {
        Axis = axis;
        NewStatus = newStatus;
        PreviousStatus = previousStatus;
        TimestampUtc = DateTimeOffset.UtcNow;
    }

    public XeryonAxis Axis { get; }

    public AxisStatusFlags NewStatus { get; }

    public AxisStatusFlags PreviousStatus { get; }

    public DateTimeOffset TimestampUtc { get; }
}
