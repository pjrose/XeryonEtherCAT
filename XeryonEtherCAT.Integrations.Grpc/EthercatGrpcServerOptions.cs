namespace XeryonEtherCAT.Integrations.Grpc;

public sealed class EthercatGrpcServerOptions
{
    public string Host { get; set; } = "0.0.0.0";

    public int Port { get; set; } = 50051;

    /// <summary>
    /// Maximum number of telemetry events buffered per subscriber.
    /// </summary>
    public int TelemetryBufferSize { get; set; } = 512;
}
