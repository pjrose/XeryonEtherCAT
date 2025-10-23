using XeryonEtherCAT.Core;

namespace XeryonEtherCAT.Ethercat.Models;

public sealed class AxisStatusChangedEventArgs : EventArgs
{
    public AxisStatusChangedEventArgs(AxisStatus status)
    {
        Status = status;
    }

    public AxisStatus Status { get; }
}
