using XeryonEtherCAT.Core;
using XeryonEtherCAT.Ethercat.Models;

namespace XeryonEtherCAT.Ethercat.Interfaces;

/// <summary>
/// Facade that manages the lifetime of the EtherCAT master and exposes high level operations.
/// </summary>
public interface IXeryonEthercatService : System.IDisposable
{
    ConnectionState ConnectionState { get; }

    IReadOnlyDictionary<string, AxisConfiguration> AxisConfigurations { get; }

    event EventHandler<ConnectionStateChangedEventArgs>? ConnectionStateChanged;

    event EventHandler<AxisStatusChangedEventArgs>? AxisStatusChanged;

    Task<IReadOnlyList<EthercatInterfaceInfo>> GetAvailableInterfacesAsync(CancellationToken cancellationToken = default);

    Task ConnectAsync(string interfaceName, IEnumerable<AxisConfiguration> axes, CancellationToken cancellationToken = default);

    Task DisconnectAsync(CancellationToken cancellationToken = default);

    Task SendCommandAsync(string axisName, AxisCommand command, CancellationToken cancellationToken = default);

    Task StopAxisAsync(string axisName, CancellationToken cancellationToken = default);

    Task ResetAxisAsync(string axisName, CancellationToken cancellationToken = default);

    AxisStatus? GetCachedStatus(string axisName);
}
