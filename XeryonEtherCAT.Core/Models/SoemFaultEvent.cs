using System;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Event payload describing a detected drive fault.
/// </summary>
public sealed class SoemFaultEvent : EventArgs
{
    public SoemFaultEvent(int slave, DriveStatus status, DriveError error, SoemHealthSnapshot health)
    {
        Slave = slave;
        Status = status;
        Error = error;
        Health = health;
    }

    public int Slave { get; }

    public DriveStatus Status { get; }

    public DriveError Error { get; }

    public SoemHealthSnapshot Health { get; }
}
