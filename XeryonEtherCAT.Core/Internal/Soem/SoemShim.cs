using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XeryonEtherCAT.Core.Internal.Soem;

public static class SoemShim
{
    [StructLayout(LayoutKind.Sequential)]
    public struct SoemHandle
    {
        public IntPtr Handle;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DriveRxPDO
    {
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
        public byte[] Command;
        public int Parameter;
        public int Velocity;
        public ushort Acceleration;
        public ushort Deceleration;
        public byte Execute;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    public struct DriveTxPDO
    {
        public int ActualPosition;
        public byte Status4_0;
        public byte Status5;
        public byte Status6;
        public byte Slot;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SoemHealth
    {
        public int slaves_found;
        public int group_expected_wkc;
        public int last_wkc;
        public int bytes_out;
        public int bytes_in;
        public int slaves_op;
        public int al_status_code;
    }

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern IntPtr soem_initialize([MarshalAs(UnmanagedType.LPStr)] string ifname);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern void soem_shutdown(IntPtr h);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_get_slave_count(IntPtr h);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_expected_rx_bytes();

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_expected_tx_bytes();

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_write_rxpdo(IntPtr h, int slaveIndex, ref DriveRxPDO inPdo);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_read_txpdo(IntPtr h, int slaveIndex, out DriveTxPDO outPdo);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_exchange_process_data(IntPtr h, byte[] outputs, int outputsLen, byte[] inputs, int inputsLen, int timeoutUs);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_get_health(IntPtr h, out SoemHealth health);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_try_recover(IntPtr h, int timeoutMs);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    internal static extern int soem_drain_error_list_r(IntPtr h, StringBuilder buf, int bufSz);
}
