using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using XeryonEtherCAT.Core.Internal.Soem;

namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Helper utilities for interpreting <see cref="SoemShim.DriveTxPDO"/> values.
/// </summary>
public static class DriveStateFormatter
{
    /// <summary>
    /// Converts the drive status bits into a packed mask for logging/debugging.
    /// </summary>
    public static uint ToBitMask(in SoemShim.DriveTxPDO status)
    {
        uint value = 0;
        if (status.AmplifiersEnabled != 0) value |= 1u << 0;
        if (status.EndStop != 0) value |= 1u << 1;
        if (status.ThermalProtection1 != 0) value |= 1u << 2;
        if (status.ThermalProtection2 != 0) value |= 1u << 3;
        if (status.ForceZero != 0) value |= 1u << 4;
        if (status.MotorOn != 0) value |= 1u << 5;
        if (status.ClosedLoop != 0) value |= 1u << 6;
        if (status.EncoderIndex != 0) value |= 1u << 7;
        if (status.EncoderValid != 0) value |= 1u << 8;
        if (status.SearchingIndex != 0) value |= 1u << 9;
        if (status.PositionReached != 0) value |= 1u << 10;
        if (status.ErrorCompensation != 0) value |= 1u << 11;
        if (status.EncoderError != 0) value |= 1u << 12;
        if (status.Scanning != 0) value |= 1u << 13;
        if (status.LeftEndStop != 0) value |= 1u << 14;
        if (status.RightEndStop != 0) value |= 1u << 15;
        if (status.ErrorLimit != 0) value |= 1u << 16;
        if (status.SearchingOptimalFrequency != 0) value |= 1u << 17;
        if (status.SafetyTimeout != 0) value |= 1u << 18;
        if (status.ExecuteAck != 0) value |= 1u << 19;
        if (status.EmergencyStop != 0) value |= 1u << 20;
        if (status.PositionFail != 0) value |= 1u << 21;
        return value;
    }

    public static string ToHexString(in SoemShim.DriveTxPDO status)
    {
        var mask = ToBitMask(status);
        return FormatAs3ByteHex(mask);
    }

    // Helper to format a 24-bit mask as "AA BB CC"
    private static string FormatAs3ByteHex(uint value)
    {
        // Ensure only the low 24 bits are used
        var masked = value & 0xFFFFFF;
        // Extract bytes in big-endian order
        var b1 = (masked >> 16) & 0xFF;
        var b2 = (masked >> 8) & 0xFF;
        var b3 = masked & 0xFF;
        // Format each as two hex digits and join with spaces
        return $"{b1:X2} {b2:X2} {b3:X2}";
    }

    /// <summary>
    /// Builds a short textual description summarizing the active drive flags.
    /// Dynamically inspects the fields of <see cref="SoemShim.DriveTxPDO"/>,
    /// excluding 'ActualPosition' and 'Slot', and appends any non-zero fields by name.
    /// </summary>
    public static string Describe(in SoemShim.DriveTxPDO status)
    {
        var parts = new List<string>();
        var type = typeof(SoemShim.DriveTxPDO);
        var fields = type.GetFields(BindingFlags.Instance | BindingFlags.Public);

        foreach (var f in fields)
        {
            // skip actual position and slot
            if (f.Name == nameof(SoemShim.DriveTxPDO.ActualPosition) || f.Name == nameof(SoemShim.DriveTxPDO.Slot))
            {
                continue;
            }

            var val = f.GetValue(status);
            if (val is null) continue;

            bool nonZero = val switch
            {
                byte b => b != 0,
                int i => i != 0,
                ushort us => us != 0,
                _ => !Equals(val, f.FieldType.IsValueType ? System.Activator.CreateInstance(f.FieldType) : null)
            };

            if (nonZero)
            {
                parts.Add(f.Name);
            }
        }

        return parts.Count == 0 ? "Idle" : string.Join(", ", parts);
    }

    // ---- New formatting helpers moved from SoemShim ----

    /// <summary>
    /// Reconstruct the 8 raw bytes from a TX PDO:
    /// [0..3] = ActualPosition (little-endian), [4]=flags, [5]=flags, [6]=flags, [7]=slot
    /// </summary>
    public static byte[] DriveTxPdoToBytes(in SoemShim.DriveTxPDO pdo)
    {
        var result = new byte[8];

        var posBytes = System.BitConverter.GetBytes(pdo.ActualPosition);
        if (!System.BitConverter.IsLittleEndian)
        {
            System.Array.Reverse(posBytes);
        }
        System.Array.Copy(posBytes, 0, result, 0, 4);

        byte b4 = 0;
        b4 |= (byte)((pdo.AmplifiersEnabled & 0x1) << 0);
        b4 |= (byte)((pdo.EndStop & 0x1) << 1);
        b4 |= (byte)((pdo.ThermalProtection1 & 0x1) << 2);
        b4 |= (byte)((pdo.ThermalProtection2 & 0x1) << 3);
        b4 |= (byte)((pdo.ForceZero & 0x1) << 4);
        b4 |= (byte)((pdo.MotorOn & 0x1) << 5);
        b4 |= (byte)((pdo.ClosedLoop & 0x1) << 6);
        b4 |= (byte)((pdo.EncoderIndex & 0x1) << 7);
        result[4] = b4;

        byte b5 = 0;
        b5 |= (byte)((pdo.EncoderValid & 0x1) << 0);
        b5 |= (byte)((pdo.SearchingIndex & 0x1) << 1);
        b5 |= (byte)((pdo.PositionReached & 0x1) << 2);
        b5 |= (byte)((pdo.ErrorCompensation & 0x1) << 3);
        b5 |= (byte)((pdo.EncoderError & 0x1) << 4);
        b5 |= (byte)((pdo.Scanning & 0x1) << 5);
        b5 |= (byte)((pdo.LeftEndStop & 0x1) << 6);
        b5 |= (byte)((pdo.RightEndStop & 0x1) << 7);
        result[5] = b5;

        byte b6 = 0;
        b6 |= (byte)((pdo.ErrorLimit & 0x1) << 0);
        b6 |= (byte)((pdo.SearchingOptimalFrequency & 0x1) << 1);
        b6 |= (byte)((pdo.SafetyTimeout & 0x1) << 2);
        b6 |= (byte)((pdo.ExecuteAck & 0x1) << 3);
        b6 |= (byte)((pdo.EmergencyStop & 0x1) << 4);
        b6 |= (byte)((pdo.PositionFail & 0x1) << 5);
        result[6] = b6;

        result[7] = pdo.Slot;

        return result;
    }

    /// <summary>
    /// Format TX PDO as: "pos: XX XX XX XX, status: XX XX XX, slot: XX"
    /// - position shown MSB-first (human order)
    /// - status bytes shown most-significant first (byte6, byte5, byte4)
    /// - slot displayed 1-based (device reports 0 for slot 1)
    /// </summary>
    public static string DriveTxPdoToHexString(in SoemShim.DriveTxPDO pdo, string separator = " ", bool prefixPerByte = false)
    {
        var bytes = DriveTxPdoToBytes(pdo);

        // Position display: big-endian bytes[3],bytes[2],bytes[1],bytes[0]
        var pos = new byte[] { bytes[3], bytes[2], bytes[1], bytes[0] };

        // Status display: most significant status byte first (byte6, byte5, byte4)
        var status = new byte[] { bytes[6], bytes[5], bytes[4] };

        var slot = (byte)(bytes[7] + 1);

        string fmt(byte b) => prefixPerByte ? "0x" + b.ToString("X2") : b.ToString("X2");

        var posStr = string.Join(separator, pos.Select(fmt));
        var statusStr = string.Join(separator, status.Select(fmt));
        var slotStr = fmt(slot);

        return $"pos: {posStr} ({pdo.ActualPosition}), status: {statusStr}, slot: {slotStr}";
    }
}
