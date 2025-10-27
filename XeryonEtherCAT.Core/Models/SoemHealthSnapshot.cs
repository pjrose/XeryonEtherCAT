using System;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Immutable representation of the SOEM health metrics.
/// </summary>
public readonly struct SoemHealthSnapshot
{
    public SoemHealthSnapshot(int slavesFound, int groupExpectedWkc, int lastWkc, int bytesOut, int bytesIn, int slavesOperational, int alStatusCode)
    {
        SlavesFound = slavesFound;
        GroupExpectedWkc = groupExpectedWkc;
        LastWkc = lastWkc;
        BytesOut = bytesOut;
        BytesIn = bytesIn;
        SlavesOperational = slavesOperational;
        AlStatusCode = alStatusCode;
    }

    public int SlavesFound { get; }

    public int GroupExpectedWkc { get; }

    public int LastWkc { get; }

    public int BytesOut { get; }

    public int BytesIn { get; }

    public int SlavesOperational { get; }

    public int AlStatusCode { get; }
}
