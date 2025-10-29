using System;
using XeryonEtherCAT.Core.Internal.Soem;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Event payload describing a detected drive fault.
/// </summary>
public sealed class SoemFaultEvent : EventArgs
{
    public SoemFaultEvent(int slave, SoemShim.DriveTxPDO status, DriveError error, SoemHealthSnapshot health)
    {
        Slave = slave;
        Status = status;
        Error = error;
        Health = health;
    }

    public int Slave { get; }

    public SoemShim.DriveTxPDO Status { get; }

    public DriveError Error { get; }

    public SoemHealthSnapshot Health { get; }
}
