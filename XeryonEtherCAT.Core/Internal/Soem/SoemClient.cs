using Microsoft.Extensions.Logging;
using System;
using System.Runtime.CompilerServices;
using System.Text;

[assembly: InternalsVisibleTo("XeryonEtherCAT.ConsoleHarness")]

namespace XeryonEtherCAT.Core.Internal.Soem;

internal sealed class SoemClient : ISoemClient
{

    private readonly ILogger<SoemClient> _logger;

    public SoemClient(ILogger<SoemClient> logger)
    {
        _logger = logger;

        // Register native log callback
        SoemShim.soem_set_log_callback(NativeLogHandler);
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

        _logger.Log(logLevel, "[SOEM] {Message}", message);
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
        try
        {
            return SoemShim.soem_exchange_process_data(handle, null!, 0, null!, 0, timeoutUs);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Unexpected error occurred during process data exchange.");

            // Attempt recovery
            if (TryRecover(handle, timeoutUs / 1000) == 0)
            {
                _logger.LogInformation("Recovery successful after cable disconnection.");
                return -1; // Indicate recovery mode
            }
            else
            {
                _logger.LogError("Recovery failed. Manual intervention required.");
                throw; // Re-throw if recovery fails
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
