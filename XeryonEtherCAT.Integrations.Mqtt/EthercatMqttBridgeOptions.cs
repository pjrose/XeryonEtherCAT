using System;

namespace XeryonEtherCAT.Integrations.Mqtt;

public sealed class EthercatMqttBridgeOptions
{
    public string BrokerHost { get; set; } = "localhost";

    public int BrokerPort { get; set; } = 1883;

    public string ClientId { get; set; } = $"xeryon-bridge-{Environment.MachineName}";

    public string TopicRoot { get; set; } = "xeryon/ethercat";

    public bool RetainStatusMessages { get; set; } = true;

    public TimeSpan CommandTimeout { get; set; } = TimeSpan.FromSeconds(5);
}
