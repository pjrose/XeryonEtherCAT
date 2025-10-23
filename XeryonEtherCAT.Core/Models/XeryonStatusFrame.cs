using System;
using System.Buffers.Binary;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Represents the cyclic input process data coming from a Xeryon drive.
/// </summary>
public readonly record struct XeryonStatusFrame(
    int ActualPosition,
    AxisStatusFlags Status,
    byte Slot)
{
    public static XeryonStatusFrame FromProcessData(ReadOnlySpan<byte> processData)
    {
        if (processData.Length < 24)
        {
            throw new ArgumentException("Process data span too small", nameof(processData));
        }

        var actualPosition = BinaryPrimitives.ReadInt32LittleEndian(processData.Slice(0, 4));
        var statusBits = BinaryPrimitives.ReadUInt32LittleEndian(processData.Slice(4, 4));
        var slot = processData[23];
        return new XeryonStatusFrame(actualPosition, (AxisStatusFlags)statusBits, slot);
    }
}
