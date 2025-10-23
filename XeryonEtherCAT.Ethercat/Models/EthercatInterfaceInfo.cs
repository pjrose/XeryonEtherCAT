namespace XeryonEtherCAT.Ethercat.Models;

/// <summary>
/// Represents a network interface that can host an EtherCAT master.
/// </summary>
/// <param name="InterfaceId">Identifier used by the master to open the adapter.</param>
/// <param name="DisplayName">Friendly name presented to end users.</param>
/// <param name="Description">Optional description reported by the master.</param>
/// <param name="Address">Optional address information for the adapter.</param>
public sealed record EthercatInterfaceInfo(
    string InterfaceId,
    string DisplayName,
    string? Description,
    string? Address)
{
    public override string ToString() => DisplayName;
}
