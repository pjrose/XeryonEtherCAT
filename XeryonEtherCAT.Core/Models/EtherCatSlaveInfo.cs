namespace XeryonEtherCAT.Core.Models;

/// <summary>
/// Information about an EtherCAT slave discovered on the network.
/// </summary>
public sealed record EtherCatSlaveInfo(
    int Position,
    uint VendorId,
    uint ProductCode,
    uint Revision,
    string Name);
