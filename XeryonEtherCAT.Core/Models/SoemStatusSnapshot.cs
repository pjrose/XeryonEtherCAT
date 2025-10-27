using System;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Snapshot of the most recent IO cycle state.
/// </summary>
public sealed class SoemStatusSnapshot
{
    public SoemStatusSnapshot(DateTimeOffset timestamp, SoemHealthSnapshot health, DriveStatus[] driveStatuses, int[] actualPositions, TimeSpan cycleTime, TimeSpan minCycle, TimeSpan maxCycle)
    {
        Timestamp = timestamp;
        Health = health;
        DriveStatuses = driveStatuses ?? Array.Empty<DriveStatus>();
        ActualPositions = actualPositions ?? Array.Empty<int>();
        CycleTime = cycleTime;
        MinCycleTime = minCycle;
        MaxCycleTime = maxCycle;
    }

    public DateTimeOffset Timestamp { get; }

    public SoemHealthSnapshot Health { get; }

    public DriveStatus[] DriveStatuses { get; }

    public int[] ActualPositions { get; }

    public TimeSpan CycleTime { get; }

    public TimeSpan MinCycleTime { get; }

    public TimeSpan MaxCycleTime { get; }
}
