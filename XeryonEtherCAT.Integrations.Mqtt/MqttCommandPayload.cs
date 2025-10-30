using System.Text.Json.Serialization;

namespace XeryonEtherCAT.Integrations.Mqtt;

public sealed class MqttCommandPayload
{
    [JsonPropertyName("command")]
    public string Command { get; set; } = string.Empty;

    [JsonPropertyName("enable")]
    public bool? Enable { get; set; }

    [JsonPropertyName("targetPosition")]
    public int? TargetPosition { get; set; }

    [JsonPropertyName("velocity")]
    public int? Velocity { get; set; }

    [JsonPropertyName("acceleration")]
    public ushort? Acceleration { get; set; }

    [JsonPropertyName("deceleration")]
    public ushort? Deceleration { get; set; }

    [JsonPropertyName("direction")]
    public int? Direction { get; set; }

    [JsonPropertyName("settleTimeoutSeconds")]
    public double? SettleTimeoutSeconds { get; set; }
}
