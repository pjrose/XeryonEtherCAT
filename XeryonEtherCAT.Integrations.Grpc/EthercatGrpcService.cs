using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Grpc.Core;
using Microsoft.Extensions.Logging;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Utilities;

namespace XeryonEtherCAT.Integrations.Grpc;

internal sealed class EthercatGrpcService : EthercatControl.EthercatControlBase
{
    private readonly IEthercatDriveService _driveService;
    private readonly EthercatGrpcServerOptions _options;
    private readonly ILogger<EthercatGrpcService> _logger;

    public EthercatGrpcService(
        IEthercatDriveService driveService,
        EthercatGrpcServerOptions options,
        ILogger<EthercatGrpcService> logger)
    {
        _driveService = driveService ?? throw new ArgumentNullException(nameof(driveService));
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task SubscribeTelemetry(
        TelemetrySubscriptionRequest request,
        IServerStreamWriter<TelemetryFrame> responseStream,
        ServerCallContext context)
    {
        var bufferSize = Math.Max(1, _options.TelemetryBufferSize);
        var channel = System.Threading.Channels.Channel.CreateBounded<DriveStatusChangeEvent>(new BoundedChannelOptions(bufferSize)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

        HashSet<int>? filter = null;
        if (request?.Slaves?.Count > 0)
        {
            filter = request.Slaves.ToHashSet();
        }

        void Handler(object? sender, DriveStatusChangeEvent change)
        {
            if (filter is not null && !filter.Contains(change.Slave))
            {
                return;
            }

            if (!channel.Writer.TryWrite(change))
            {
                _logger.LogWarning("Telemetry buffer full, dropping frame for slave {Slave}.", change.Slave);
            }
        }

        _driveService.StatusChanged += Handler;
        _logger.LogInformation(
            "Telemetry subscription started from {Peer}. Filter={Filter} Buffer={Buffer}.",
            context.Peer,
            filter is null ? "all" : string.Join(',', filter.OrderBy(s => s)),
            bufferSize);

        try
        {
            await foreach (var change in channel.Reader.ReadAllAsync(context.CancellationToken).ConfigureAwait(false))
            {
                var frame = MapTelemetry(change);
                await responseStream.WriteAsync(frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Telemetry subscription cancelled for {Peer}.", context.Peer);
        }
        finally
        {
            _driveService.StatusChanged -= Handler;
            channel.Writer.TryComplete();
            _logger.LogInformation("Telemetry subscription ended for {Peer}.", context.Peer);
        }
    }

    public override Task<CommandReply> MoveAbsolute(MoveAbsoluteRequest request, ServerCallContext context) =>
        ExecuteCommandAsync(
            context,
            request,
            nameof(MoveAbsolute),
            ct => _driveService.MoveAbsoluteAsync(
                request.Slave,
                request.TargetPosition,
                request.Velocity,
                EnsureUShort(request.Acceleration, nameof(request.Acceleration)),
                EnsureUShort(request.Deceleration, nameof(request.Deceleration)),
                TimeSpan.FromSeconds(EnsureNonNegative(request.SettleTimeoutSeconds, nameof(request.SettleTimeoutSeconds))),
                ct));

    public override Task<CommandReply> Jog(JogRequest request, ServerCallContext context) =>
        ExecuteCommandAsync(
            context,
            request,
            nameof(Jog),
            ct => _driveService.JogAsync(
                request.Slave,
                request.Direction,
                request.Velocity,
                EnsureUShort(request.Acceleration, nameof(request.Acceleration)),
                EnsureUShort(request.Deceleration, nameof(request.Deceleration)),
                ct));

    public override Task<CommandReply> Index(IndexRequest request, ServerCallContext context) =>
        ExecuteCommandAsync(
            context,
            request,
            nameof(Index),
            ct => _driveService.IndexAsync(
                request.Slave,
                request.Direction,
                request.Velocity,
                EnsureUShort(request.Acceleration, nameof(request.Acceleration)),
                EnsureUShort(request.Deceleration, nameof(request.Deceleration)),
                TimeSpan.FromSeconds(EnsureNonNegative(request.SettleTimeoutSeconds, nameof(request.SettleTimeoutSeconds))),
                ct));

    public override Task<CommandReply> Reset(DriveSelectionRequest request, ServerCallContext context) =>
        ExecuteCommandAsync(
            context,
            request,
            nameof(Reset),
            ct => _driveService.ResetAsync(request.Slave, ct));

    public override Task<CommandReply> Enable(EnableRequest request, ServerCallContext context) =>
        ExecuteCommandAsync(
            context,
            request,
            nameof(Enable),
            ct => _driveService.EnableAsync(request.Slave, request.Enable, ct));

    public override Task<CommandReply> Halt(DriveSelectionRequest request, ServerCallContext context) =>
        ExecuteCommandAsync(
            context,
            request,
            nameof(Halt),
            ct => _driveService.HaltAsync(request.Slave, ct));

    public override Task<CommandReply> Stop(DriveSelectionRequest request, ServerCallContext context) =>
        ExecuteCommandAsync(
            context,
            request,
            nameof(Stop),
            ct => _driveService.StopAsync(request.Slave, ct));

    private async Task<CommandReply> ExecuteCommandAsync<TRequest>(
        ServerCallContext context,
        TRequest request,
        string commandName,
        Func<CancellationToken, Task> command)
    {
        _logger.LogInformation("{Command} requested by {Peer}: {@Request}.", commandName, context.Peer, request);

        try
        {
            await command(context.CancellationToken).ConfigureAwait(false);
            return new CommandReply
            {
                Accepted = true,
                Message = "Command completed"
            };
        }
        catch (OperationCanceledException ex)
        {
            _logger.LogInformation(ex, "{Command} cancelled for {Peer}.", commandName, context.Peer);
            throw new RpcException(new Status(StatusCode.Cancelled, $"{commandName} cancelled."));
        }
        catch (ArgumentOutOfRangeException ex)
        {
            _logger.LogWarning(ex, "{Command} invalid argument for {Peer}.", commandName, context.Peer);
            throw new RpcException(new Status(StatusCode.OutOfRange, ex.Message));
        }
        catch (ArgumentException ex)
        {
            _logger.LogWarning(ex, "{Command} invalid argument for {Peer}.", commandName, context.Peer);
            throw new RpcException(new Status(StatusCode.InvalidArgument, ex.Message));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "{Command} failed for {Peer}.", commandName, context.Peer);
            throw new RpcException(new Status(StatusCode.Internal, "Command execution failed."));
        }
    }

    private static ushort EnsureUShort(uint value, string name)
    {
        if (value > ushort.MaxValue)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value exceeds 16-bit range.");
        }

        return (ushort)value;
    }

    private static double EnsureNonNegative(double value, string name)
    {
        if (double.IsNaN(value) || value < 0)
        {
            throw new ArgumentOutOfRangeException(name, value, "Value must be non-negative.");
        }

        return value;
    }

    private static TelemetryFrame MapTelemetry(DriveStatusChangeEvent change) => new()
    {
        Slave = change.Slave,
        TimestampUtcTicks = change.Timestamp.UtcTicks,
        MonotonicTicks = change.MonotonicTimestampTicks,
        MonotonicFrequency = TelemetrySync.TimestampFrequency,
        ActiveCommand = change.ActiveCommand ?? string.Empty,
        ChangedBits = change.ChangedBitsMask,
        Current = MapStatus(change.CurrentStatus),
        Previous = MapStatus(change.PreviousStatus),
        Sequence = change.Sequence > 0 ? (ulong)change.Sequence : 0
    };

    private static DriveStatusSnapshot MapStatus(SoemShim.DriveTxPDO status) => new()
    {
        ActualPosition = status.ActualPosition,
        AmplifiersEnabled = status.AmplifiersEnabled != 0,
        EndStop = status.EndStop != 0,
        ThermalProtection1 = status.ThermalProtection1 != 0,
        ThermalProtection2 = status.ThermalProtection2 != 0,
        ForceZero = status.ForceZero != 0,
        MotorOn = status.MotorOn != 0,
        ClosedLoop = status.ClosedLoop != 0,
        EncoderIndex = status.EncoderIndex != 0,
        EncoderValid = status.EncoderValid != 0,
        SearchingIndex = status.SearchingIndex != 0,
        PositionReached = status.PositionReached != 0,
        ErrorCompensation = status.ErrorCompensation != 0,
        EncoderError = status.EncoderError != 0,
        Scanning = status.Scanning != 0,
        LeftEndStop = status.LeftEndStop != 0,
        RightEndStop = status.RightEndStop != 0,
        ErrorLimit = status.ErrorLimit != 0,
        SearchingOptimalFrequency = status.SearchingOptimalFrequency != 0,
        SafetyTimeout = status.SafetyTimeout != 0,
        ExecuteAck = status.ExecuteAck != 0,
        EmergencyStop = status.EmergencyStop != 0,
        PositionFail = status.PositionFail != 0,
        Slot = status.Slot
    };
}
