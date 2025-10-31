using Microsoft.Extensions.Logging;
using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

[assembly: InternalsVisibleTo("XeryonEtherCAT.ConsoleHarness")]

namespace XeryonEtherCAT.Core.Internal.Soem;

internal sealed class SoemClient : ISoemClient
{

    private readonly ILogger _logger;

    // Keep a strong reference to prevent GC collection
    private readonly SoemShim.SoemLogCallback _logCallback;

    public SoemClient(ILogger logger)
    {
        _logger = logger;

        // Create delegate and keep reference
        _logCallback = NativeLogHandler;

        // Register native log callback
        SoemShim.soem_set_log_callback(_logCallback);
    }

    private void NativeLogHandler(SoemShim.SoemLogLevel level, string message)
    {
        var logLevel = level switch
        {
            SoemShim.SoemLogLevel.SOEM_LOG_INFO => LogLevel.Information,
            SoemShim.SoemLogLevel.SOEM_LOG_WARN => LogLevel.Warning,
            SoemShim.SoemLogLevel.SOEM_LOG_ERR => LogLevel.Error,
            _ => LogLevel.Debug
        };

        _logger.Log(logLevel, "{Message}", message);
    }

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
    {
        if (handle == IntPtr.Zero)
        {
            _logger.LogWarning("ExchangeProcessData called with invalid handle.");
            return -1;
        }
        try
        {
            /*
            Note: passing null for the outputs/ inputs buffers is safe and intentional in this architecture.
               
            Outputs (RxPDO):
                •	If outputs == NULL → skips memset/ memcpy, leaves g->outputs as-is (already filled by soem_write_rxpdo).
                •	Calls ecx_send_processdata(...) which uses the internal g->outputs buffer.
            Inputs (TxPDO):
                •	If inputs == NULL → skips the memcpy(inputs, g->inputs, ...).
                •	The data is still received into the internal g->inputs buffer and can be read later via soem_read_txpdo.
            */
            return SoemShim.soem_exchange_process_data(handle, null!, 0, null!, 0, timeoutUs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred during process data exchange.");

            // Attempt recovery
            if (TryRecover(handle, timeoutUs / 1000) == 0)
            {
                _logger.LogInformation("Recovery successful after cable disconnection.");
                return -1;
            }
            else
            {               
                throw;
            }
        }
    }

    public int GetHealth(IntPtr handle, out SoemShim.SoemHealth health)
        => SoemShim.soem_get_health(handle, out health);

    public int TryRecover(IntPtr handle, int timeoutMs)
        => SoemShim.soem_try_recover(handle, timeoutMs);

    public int ListNetworkAdapterNames()
        => SoemShim.soem_get_network_adapters();

    public string DrainErrorList(IntPtr handle, StringBuilder? buffer = null)
    {
        buffer ??= new StringBuilder(4096);
        buffer.Clear();
        var rc = SoemShim.soem_drain_error_list(handle, buffer, buffer.Capacity);
        return rc == 0 ? string.Empty : buffer.ToString();
    }

    public void Dispose()
    {
    }
}
