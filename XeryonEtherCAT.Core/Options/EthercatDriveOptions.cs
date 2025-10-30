using System;

namespace XeryonEtherCAT.Core.Options;

/// <summary>
/// Configurable runtime parameters for the EtherCAT drive service.
/// </summary>
public sealed class EthercatDriveOptions
{
    /// <summary>
    /// Target period of the IO loop.
    /// </summary>
    public TimeSpan CyclePeriod { get; set; } = TimeSpan.FromMilliseconds(2);

    /// <summary>
    /// Timeout (in microseconds) passed to <c>soem_exchange_process_data</c>.
    /// </summary>
    public int ExchangeTimeoutMicroseconds { get; set; } = 100000;

    /// <summary>
    /// Maximum consecutive low-WKC cycles tolerated before attempting recovery.
    /// </summary>
    public int WkcRecoveryThreshold { get; set; } = 3;

    /// <summary>
    /// Timeout passed to <c>soem_try_recover</c> in milliseconds.
    /// </summary>
    public int RecoveryTimeoutMilliseconds { get; set; } = 500;

    /// <summary>
    /// Delay between recovery retries when a hard re-initialization is required.
    /// </summary>
    public TimeSpan ReinitializationDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>
    /// Maximum amount of commands queued per slave.
    /// </summary>
    public int CommandQueueCapacity { get; set; } = 64;

    /// <summary>
    /// Default timeout when waiting for position settle operations.
    /// </summary>
    public TimeSpan DefaultSettleTimeout { get; set; } = TimeSpan.FromSeconds(10);

    /// <summary>
    /// Enables verbose per-cycle tracing.
    /// </summary>
    public bool EnableCycleTraceLogging { get; set; } = false;
}
