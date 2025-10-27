using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using XeryonEtherCAT.Core.Models;

namespace XeryonEtherCAT.Core.Abstractions;

/// <summary>
/// Represents a lightweight EtherCAT master that is able to exchange process data with Xeryon drives.
/// </summary>
public interface IEtherCatMaster : IAsyncDisposable
{
    /// <summary>
    /// Gets the current connection state of the master.
    /// </summary>
    ConnectionState ConnectionState { get; }

    /// <summary>
    /// Raised whenever the connection state changes.
    /// </summary>
    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    /// <summary>
    /// Connects to the given network interface and performs a scan for slaves.
    /// </summary>
    /// <param name="networkInterfaceName">The friendly name or system name of the network interface.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The list of discovered slaves in the order they are addressed on the bus.</returns>
    Task<IReadOnlyList<EtherCatSlaveInfo>> ConnectAsync(string networkInterfaceName, CancellationToken cancellationToken);

    /// <summary>
    /// Exchanges process data with the slaves. The master is responsible for copying the outgoing command frame to the
    /// process data image and reading the resulting status frame.
    /// </summary>
    /// <param name="commands">Command frame for each addressed axis.</param>
    /// <param name="statuses">Buffer that receives the status frames for each axis.</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    Task ExchangeProcessDataAsync(ReadOnlySpan<XeryonCommandFrame> commands, Span<XeryonStatusFrame> statuses, CancellationToken cancellationToken);

    /// <summary>
    /// Disconnects from the bus and releases any resources held by the master.
    /// </summary>
    Task DisconnectAsync(CancellationToken cancellationToken);
}
