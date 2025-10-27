using System;
using System.Text;

namespace XeryonEtherCAT.Core.Internal.Soem;

internal sealed class SoemClient : ISoemClient
{

    public IntPtr Initialize(string iface)
        => SoemShim.soem_initialize(iface);

    public void Shutdown(IntPtr handle)
        => SoemShim.soem_shutdown(handle);

    public int GetSlaveCount(IntPtr handle)
        => SoemShim.soem_get_slave_count(handle);

    public int GetExpectedRxBytes()
        => SoemShim.soem_expected_rx_bytes();

    public int GetExpectedTxBytes()
        => SoemShim.soem_expected_tx_bytes();

    public int WriteRxPdo(IntPtr handle, int slaveIndex, ref SoemShim.DriveRxPDO pdo)
        => SoemShim.soem_write_rxpdo(handle, slaveIndex, ref pdo);

    public int ReadTxPdo(IntPtr handle, int slaveIndex, out SoemShim.DriveTxPDO pdo)
        => SoemShim.soem_read_txpdo(handle, slaveIndex, out pdo);

    public int ExchangeProcessData(IntPtr handle, int timeoutUs)
        => SoemShim.soem_exchange_process_data(handle, null!, 0, null!, 0, timeoutUs);

    public int GetHealth(IntPtr handle, out SoemShim.SoemHealth health)
        => SoemShim.soem_get_health(handle, out health);

    public int TryRecover(IntPtr handle, int timeoutMs)
        => SoemShim.soem_try_recover(handle, timeoutMs);

    public string DrainErrorList(IntPtr handle, StringBuilder? buffer = null)
    {
        buffer ??= new StringBuilder(4096);
        buffer.Clear();
        var rc = SoemShim.soem_drain_error_list_r(handle, buffer, buffer.Capacity);
        return rc == 0 ? string.Empty : buffer.ToString();
    }

    public void Dispose()
    {
    }
}
