using System;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Internal.Soem;

namespace XeryonEtherCAT.App.ViewModels;

public sealed class DriveEventViewModel
{
    public DriveEventViewModel(DriveStatusChangeEvent change)
    {
        Slave = change.Slave;
        Timestamp = change.Timestamp;
        Keyword = change.ActiveCommand ?? string.Empty;
        Current = change.CurrentStatus;
        Previous = change.PreviousStatus;
        ChangedMask = change.ChangedBitsMask;
    }

    public int Slave { get; }

    public DateTimeOffset Timestamp { get; }

    public string Keyword { get; }

    public SoemShim.DriveTxPDO Current { get; }

    public SoemShim.DriveTxPDO Previous { get; }

    public uint ChangedMask { get; }

    public override string ToString()
    {
        var mask = Convert.ToString(ChangedMask, 2).PadLeft(32, '0');
        return $"[{Timestamp:HH:mm:ss.fff}] Slave {Slave} {Keyword} -> Pos {Current.ActualPosition} (Δ={Current.ActualPosition - Previous.ActualPosition}) Mask {mask}";
    }
}
