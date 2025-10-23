using System;
using System.Buffers.Binary;
using System.Text;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Represents the cyclic output process data sent to a Xeryon drive.
/// </summary>
public readonly record struct XeryonCommandFrame(
    string Command,
    int TargetPosition,
    uint Speed,
    ushort Acceleration,
    ushort Deceleration,
    byte ExecuteFlag)
{
    public static readonly XeryonCommandFrame Empty = new("NONE", 0, 0, 0, 0, 0);

    /// <summary>
    /// Writes the command frame to the supplied process data span using the PDO layout from the ESI file.
    /// </summary>
    public void WriteTo(Span<byte> processData)
    {
        if (processData.Length < 20)
        {
            throw new ArgumentException("Process data span too small", nameof(processData));
        }

        Span<byte> commandBuffer = stackalloc byte[4];
        Encoding.ASCII.GetBytes(Command.AsSpan(0, Math.Min(Command.Length, 4)), commandBuffer);
        commandBuffer.CopyTo(processData.Slice(0, 4));

        BinaryPrimitives.WriteInt32LittleEndian(processData.Slice(4, 4), TargetPosition);
        BinaryPrimitives.WriteUInt32LittleEndian(processData.Slice(8, 4), Speed);
        BinaryPrimitives.WriteUInt16LittleEndian(processData.Slice(12, 2), Acceleration);
        BinaryPrimitives.WriteUInt16LittleEndian(processData.Slice(14, 2), Deceleration);
        processData[16] = ExecuteFlag;
        processData[17] = 0;
        processData[18] = 0;
        processData[19] = 0;
    }
}
