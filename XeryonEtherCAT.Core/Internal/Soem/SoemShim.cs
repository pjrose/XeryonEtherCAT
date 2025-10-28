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
        public byte AmplifiersEnabled;
        public byte EndStop;
        public byte ThermalProtection1;
        public byte ThermalProtection2;
        public byte ForceZero;
        public byte MotorOn;
        public byte ClosedLoop;
        public byte EncoderIndex;
        public byte EncoderValid;
        public byte SearchingIndex;
        public byte PositionReached;
        public byte ErrorCompensation;
        public byte EncoderError;
        public byte Scanning;
        public byte LeftEndStop;
        public byte RightEndStop;
        public byte ErrorLimit;
        public byte SearchingOptimalFrequency;
        public byte SafetyTimeout;
        public byte ExecuteAck;
        public byte EmergencyStop;
        public byte PositionFail;
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



    public enum SoemLogLevel : int
    {
        SOEM_LOG_INFO = 0,
        SOEM_LOG_WARN = 1,
        SOEM_LOG_ERR = 2
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    public delegate void SoemLogCallback(SoemLogLevel level, [MarshalAs(UnmanagedType.LPStr)] string message);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    public static extern void soem_set_log_callback(SoemLogCallback callback);

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    public static extern int soem_drain_error_list(IntPtr handle, [MarshalAs(UnmanagedType.LPStr)] StringBuilder buffer, int bufferSize);

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

    [DllImport("soemshim", CallingConvention = CallingConvention.Cdecl)]
    internal static extern int soem_get_network_adapters();
}
