using System;
using System.Runtime.InteropServices;

namespace XeryonEtherCAT.Core.Internal.Soem;

internal static class SoemShimNative
{
    private const string LibraryName = "soemshim";

    [DllImport(LibraryName, EntryPoint = "soem_initialize", CallingConvention = CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    public static extern IntPtr Initialize(string interfaceName);

    [DllImport(LibraryName, EntryPoint = "soem_shutdown", CallingConvention = CallingConvention.Cdecl)]
    public static extern void Shutdown(IntPtr handle);

    [DllImport(LibraryName, EntryPoint = "soem_get_slave_count", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetSlaveCount(IntPtr handle);

    [DllImport(LibraryName, EntryPoint = "soem_get_process_sizes", CallingConvention = CallingConvention.Cdecl)]
    public static extern int GetProcessSizes(IntPtr handle, out int outputs, out int inputs);

    [DllImport(LibraryName, EntryPoint = "soem_scan_slaves", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ScanSlaves(IntPtr handle, [Out] SoemSlaveInfo[] buffer, int maxCount);

    [DllImport(LibraryName, EntryPoint = "soem_exchange_process_data", CallingConvention = CallingConvention.Cdecl)]
    public static extern int ExchangeProcessData(IntPtr handle, byte[] outputs, int outputsLength, byte[] inputs, int inputsLength, int timeoutMicroseconds);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Ansi)]
    public struct SoemSlaveInfo
    {
        public int Position;
        public uint VendorId;
        public uint ProductCode;
        public uint Revision;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string Name;
    }
}
