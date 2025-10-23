using System;
using System.Collections.Generic;

namespace XeryonEtherCAT.Core.Options;

public sealed class XeryonEtherCatOptions
{
    public string NetworkInterfaceName { get; set; } = string.Empty;

    public Dictionary<int, string> AxisNames { get; } = new();

    public TimeSpan CycleTime { get; set; } = TimeSpan.FromMilliseconds(20);

    public TimeSpan ReconnectDelay { get; set; } = TimeSpan.FromSeconds(2);

    public int MaximumAxes { get; set; } = 16;

    public uint VendorId { get; set; } = 0x0000004E;

    public uint ProductCode { get; set; } = 0x00000001;

    public uint Revision { get; set; } = 0x00000001;

    public bool AutoReconnect { get; set; } = true;

    public TimeSpan CommandQueueDrainTimeout { get; set; } = TimeSpan.FromSeconds(2);
}
