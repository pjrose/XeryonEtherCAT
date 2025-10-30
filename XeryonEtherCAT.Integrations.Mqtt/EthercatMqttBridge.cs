using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Utilities;

namespace XeryonEtherCAT.Integrations.Mqtt;

public sealed class EthercatMqttBridge : IAsyncDisposable
{
    private readonly IEthercatDriveService _service;
    private readonly EthercatMqttBridgeOptions _options;
    private readonly ILogger<EthercatMqttBridge> _logger;
    private readonly IMqttClient _client;
    private readonly AsyncEventQueue<DriveStatusChangeEvent> _statusQueue;
    private readonly AsyncEventQueue<SoemFaultEvent> _faultQueue;
    private readonly AsyncEventQueue<CommandRequest> _commandQueue;
    private readonly JsonSerializerOptions _jsonOptions;
    private readonly CancellationTokenSource _cts = new();
    private bool _started;

    public EthercatMqttBridge(IEthercatDriveService service, EthercatMqttBridgeOptions options, ILogger<EthercatMqttBridge> logger)
    {
        _service = service ?? throw new ArgumentNullException(nameof(service));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += OnApplicationMessageReceivedAsync;

        _statusQueue = new AsyncEventQueue<DriveStatusChangeEvent>(PublishStatusAsync);
        _faultQueue = new AsyncEventQueue<SoemFaultEvent>(PublishFaultAsync);
        _commandQueue = new AsyncEventQueue<CommandRequest>(ProcessCommandAsync);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        if (_started)
        {
            return;
        }

        var options = new MqttClientOptionsBuilder()
            .WithClientId(_options.ClientId)
            .WithTcpServer(_options.BrokerHost, _options.BrokerPort)
            .Build();

        await _client.ConnectAsync(options, ct).ConfigureAwait(false);
        await _client.SubscribeAsync(new MqttTopicFilterBuilder()
            .WithTopic($"{_options.TopicRoot}/slaves/+/commands")
            .Build(), ct).ConfigureAwait(false);

        _service.StatusChanged += OnStatusChanged;
        _service.Faulted += OnFaulted;
        _started = true;
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (!_started)
        {
            return;
        }

        _service.StatusChanged -= OnStatusChanged;
        _service.Faulted -= OnFaulted;
        _started = false;

        if (_client.IsConnected)
        {
            await _client.UnsubscribeAsync($"{_options.TopicRoot}/slaves/+/commands").ConfigureAwait(false);
            await _client.DisconnectAsync().ConfigureAwait(false);
        }
    }

    private void OnStatusChanged(object? sender, DriveStatusChangeEvent e)
    {
        _statusQueue.TryEnqueue(e);
    }

    private void OnFaulted(object? sender, SoemFaultEvent e)
    {
        _faultQueue.TryEnqueue(e);
    }

    private ValueTask PublishStatusAsync(DriveStatusChangeEvent change)
    {
        if (!_client.IsConnected)
        {
            return ValueTask.CompletedTask;
        }

        var topic = $"{_options.TopicRoot}/slaves/{change.Slave}/status";
        var payload = JsonSerializer.Serialize(new
        {
            change.Slave,
            timestamp = change.Timestamp,
            command = change.ActiveCommand,
            changedBits = change.ChangedBitsMask,
            position = change.CurrentStatus.ActualPosition,
            positionChange = change.PositionChange,
            current = ToDto(change.CurrentStatus),
            previous = ToDto(change.PreviousStatus)
        }, _jsonOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(_options.RetainStatusMessages)
            .Build();

        _logger.LogDebug("Publishing status for slave {Slave} to {Topic}.", change.Slave, topic);
        return new ValueTask(_client.PublishAsync(message, _cts.Token));
    }

    private ValueTask PublishFaultAsync(SoemFaultEvent fault)
    {
        if (!_client.IsConnected)
        {
            return ValueTask.CompletedTask;
        }

        var topic = $"{_options.TopicRoot}/slaves/{fault.Slave}/faults";
        var payload = JsonSerializer.Serialize(new
        {
            fault.Slave,
            timestamp = DateTimeOffset.UtcNow,
            error = new
            {
                fault.Error.Code,
                Message = fault.Error.Message,
                Recovery = fault.Error.RecoveryAction
            },
            status = ToDto(fault.Status)
        }, _jsonOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        _logger.LogWarning("Publishing fault for slave {Slave} to {Topic}.", fault.Slave, topic);
        return new ValueTask(_client.PublishAsync(message, _cts.Token));
    }

    private ValueTask ProcessCommandAsync(CommandRequest request)
    {
        if (!_client.IsConnected)
        {
            return ValueTask.CompletedTask;
        }

        return new ValueTask(ExecuteCommandAsync(request));
    }

    private async Task ExecuteCommandAsync(CommandRequest request)
    {
        var payload = request.Payload;
        var slave = request.Slave;

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
            var timeoutWindow = _options.CommandTimeout;
            switch (payload.Command.ToLowerInvariant())
            {
                case "enable":
                    if (payload.Enable is not bool enableValue)
                    {
                        throw new InvalidOperationException("Enable command requires 'enable' boolean field.");
                    }

                    timeoutCts.CancelAfter(timeoutWindow);
                    await _service.EnableAsync(slave, enableValue, timeoutCts.Token).ConfigureAwait(false);
                    break;
                case "reset":
                    timeoutCts.CancelAfter(timeoutWindow);
                    await _service.ResetAsync(slave, timeoutCts.Token).ConfigureAwait(false);
                    break;
                case "halt":
                    timeoutCts.CancelAfter(timeoutWindow);
                    await _service.HaltAsync(slave, timeoutCts.Token).ConfigureAwait(false);
                    break;
                case "stop":
                    timeoutCts.CancelAfter(timeoutWindow);
                    await _service.StopAsync(slave, timeoutCts.Token).ConfigureAwait(false);
                    break;
                case "moveabsolute":
                    if (payload.TargetPosition is not int target)
                    {
                        throw new InvalidOperationException("moveAbsolute requires 'targetPosition'.");
                    }

                    var vel = payload.Velocity ?? 30000;
                    var acc = payload.Acceleration ?? 1000;
                    var dec = payload.Deceleration ?? acc;
                    var settle = payload.SettleTimeoutSeconds.HasValue
                        ? TimeSpan.FromSeconds(payload.SettleTimeoutSeconds.Value)
                        : _options.CommandTimeout;
                    timeoutWindow = settle > timeoutWindow ? settle : timeoutWindow;
                    timeoutCts.CancelAfter(timeoutWindow);
                    await _service.MoveAbsoluteAsync(slave, target, vel, acc, dec, settle, timeoutCts.Token).ConfigureAwait(false);
                    break;
                case "jog":
                    if (payload.Direction is not int dir || Math.Abs(dir) > 1)
                    {
                        throw new InvalidOperationException("jog requires 'direction' -1, 0 or 1.");
                    }

                    timeoutCts.CancelAfter(timeoutWindow);
                    await _service.JogAsync(slave, dir, payload.Velocity ?? 10000, payload.Acceleration ?? 800, payload.Deceleration ?? 800, timeoutCts.Token).ConfigureAwait(false);
                    break;
                case "index":
                    if (payload.Direction is not int indexDirection || indexDirection is < 0 or > 1)
                    {
                        throw new InvalidOperationException("index requires 'direction' 0 or 1.");
                    }

                    var indexSettle = payload.SettleTimeoutSeconds.HasValue
                        ? TimeSpan.FromSeconds(payload.SettleTimeoutSeconds.Value)
                        : _options.CommandTimeout;
                    timeoutWindow = indexSettle > timeoutWindow ? indexSettle : timeoutWindow;
                    timeoutCts.CancelAfter(timeoutWindow);
                    await _service.IndexAsync(slave, indexDirection, payload.Velocity ?? 12000, payload.Acceleration ?? 1000, payload.Deceleration ?? 1000, indexSettle, timeoutCts.Token).ConfigureAwait(false);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown command '{payload.Command}'.");
            }

            await PublishAckAsync(slave, payload.Command, success: true, null).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to execute MQTT command {Command} for slave {Slave}.", payload.Command, slave);
            await PublishAckAsync(slave, payload.Command, success: false, ex.Message).ConfigureAwait(false);
        }
    }

    private async Task PublishAckAsync(int slave, string command, bool success, string? error)
    {
        if (!_client.IsConnected)
        {
            return;
        }

        var topic = $"{_options.TopicRoot}/slaves/{slave}/commands/ack";
        var payload = JsonSerializer.Serialize(new
        {
            slave,
            command,
            success,
            error,
            timestamp = DateTimeOffset.UtcNow
        }, _jsonOptions);

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(payload)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.PublishAsync(message, _cts.Token).ConfigureAwait(false);
    }

    private Task OnApplicationMessageReceivedAsync(MqttApplicationMessageReceivedEventArgs args)
    {
        try
        {
            if (!TryParseSlave(args.ApplicationMessage.Topic, out var slave))
            {
                return Task.CompletedTask;
            }

            var payload = JsonSerializer.Deserialize<MqttCommandPayload>(args.ApplicationMessage.PayloadSegment, _jsonOptions);
            if (payload is null || string.IsNullOrWhiteSpace(payload.Command))
            {
                _logger.LogWarning("Received MQTT command with empty payload on {Topic}.", args.ApplicationMessage.Topic);
                return Task.CompletedTask;
            }

            _logger.LogInformation("MQTT command {Command} received for slave {Slave}.", payload.Command, slave);
            if (!_commandQueue.TryEnqueue(new CommandRequest(slave, payload)))
            {
                _logger.LogWarning("Command queue is full, dropping command {Command} for slave {Slave}.", payload.Command, slave);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to process MQTT command message on topic {Topic}.", args.ApplicationMessage.Topic);
        }

        return Task.CompletedTask;
    }

    private bool TryParseSlave(string topic, out int slave)
    {
        slave = 0;
        var normalized = topic.Trim('/');
        var segments = normalized.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var rootSegments = _options.TopicRoot.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);

        if (segments.Length < rootSegments.Length + 3)
        {
            return false;
        }

        if (!rootSegments.SequenceEqual(segments.Take(rootSegments.Length), StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!segments[rootSegments.Length].Equals("slaves", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return int.TryParse(segments[rootSegments.Length + 1], out slave);
    }

    private static object ToDto(in SoemShim.DriveTxPDO status)
        => new
        {
            status.ActualPosition,
            status.AmplifiersEnabled,
            status.EndStop,
            status.ThermalProtection1,
            status.ThermalProtection2,
            status.ForceZero,
            status.MotorOn,
            status.ClosedLoop,
            status.EncoderIndex,
            status.EncoderValid,
            status.SearchingIndex,
            status.PositionReached,
            status.ErrorCompensation,
            status.EncoderError,
            status.Scanning,
            status.LeftEndStop,
            status.RightEndStop,
            status.ErrorLimit,
            status.SearchingOptimalFrequency,
            status.SafetyTimeout,
            status.ExecuteAck,
            status.EmergencyStop,
            status.PositionFail,
            status.Slot
        };

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        await _statusQueue.DisposeAsync().ConfigureAwait(false);
        await _faultQueue.DisposeAsync().ConfigureAwait(false);
        await _commandQueue.DisposeAsync().ConfigureAwait(false);
        _client.Dispose();
        _cts.Dispose();
    }

    private readonly record struct CommandRequest(int Slave, MqttCommandPayload Payload);
}
