using System;
using System.Threading;
using System.Threading.Tasks;
using XeryonEtherCAT.Core.Models;

namespace XeryonEtherCAT.Core.Abstractions;

/// <summary>
/// High-level EtherCAT drive orchestration service.
/// </summary>
public interface IEthercatDriveService : IAsyncDisposable
{
    /// <summary>
    /// Initializes the EtherCAT network and starts the IO loop.
    /// </summary>
    /// <param name="iface">The network interface name passed to the SOEM shim.</param>
    /// <param name="ct">Cancellation token.</param>
    Task InitializeAsync(string iface, CancellationToken ct);

    /// <summary>
    /// Gets the current slave count.
    /// </summary>
    Task<int> GetSlaveCountAsync();

    /// <summary>
    /// Executes a DPOS motion command.
    /// </summary>
    Task MoveAbsoluteAsync(int slave, int targetPos, int vel, ushort acc, ushort dec, TimeSpan settleTimeout, CancellationToken ct);

    /// <summary>
    /// Executes a SCAN (jog) command.
    /// </summary>
    Task JogAsync(int slave, int direction, int vel, ushort acc, ushort dec, CancellationToken ct);

    /// <summary>
    /// Executes an INDX command.
    /// </summary>
    Task IndexAsync(int slave, int direction, int vel, ushort acc, ushort dec, TimeSpan settleTimeout, CancellationToken ct);

    /// <summary>
    /// Executes a RSET command.
    /// </summary>
    Task ResetAsync(int slave, CancellationToken ct);

    /// <summary>
    /// Executes an ENBL command.
    /// </summary>
    Task EnableAsync(int slave, bool enable, CancellationToken ct);

    /// <summary>
    /// Executes a HALT command.
    /// </summary>
    Task HaltAsync(int slave, CancellationToken ct);

    /// <summary>
    /// Executes a STOP command.
    /// </summary>
    Task StopAsync(int slave, CancellationToken ct);

    /// <summary>
    /// Retrieves the latest SOEM health and per-drive status snapshot.
    /// </summary>
    SoemStatusSnapshot GetStatus();

    /// <summary>
    /// Raised when a drive enters a faulted state.
    /// </summary>
    event EventHandler<SoemFaultEvent> Faulted;
}
