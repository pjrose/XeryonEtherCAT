using System;
using XeryonEtherCAT.Core.Internal.Soem;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Snapshot of the most recent IO cycle state.
/// </summary>
public sealed class SoemStatusSnapshot
{
    public SoemStatusSnapshot(DateTimeOffset timestamp, SoemHealthSnapshot health, SoemShim.DriveTxPDO[] drives, TimeSpan cycleTime, TimeSpan minCycle, TimeSpan maxCycle)
    {
        Timestamp = timestamp;
        Health = health;
        DriveStates = drives ?? Array.Empty<SoemShim.DriveTxPDO>();
        ActualPositions = new int[DriveStates.Length];
        for (var i = 0; i < DriveStates.Length; i++)
        {
            ActualPositions[i] = DriveStates[i].ActualPosition;
        }

        CycleTime = cycleTime;
        MinCycleTime = minCycle;
        MaxCycleTime = maxCycle;
    }

    public DateTimeOffset Timestamp { get; }

    public SoemHealthSnapshot Health { get; }

    public SoemShim.DriveTxPDO[] DriveStates { get; }

    public int[] ActualPositions { get; }

    public TimeSpan CycleTime { get; }

    public TimeSpan MinCycleTime { get; }

    public TimeSpan MaxCycleTime { get; }
}
