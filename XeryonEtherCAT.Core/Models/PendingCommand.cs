using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using XeryonEtherCAT.Core.Internal.Soem;

namespace XeryonEtherCAT.Core.Models;

internal enum CommandCompletion
{
    AckOnly,
    PositionReached,
    Indexed,
    Enabled,
    Disabled,
    Halt,
    AckWithTimeout
}

internal enum CommandState
{
    Pending,
    Completed,
    TimedOut
}

internal sealed class PendingCommand
{
    private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _cancellationSource;
    private readonly Stopwatch _stopwatch = new();
    private readonly CommandCompletion _completion;
    private readonly TimeSpan _timeout;
    private readonly ILogger? _logger;

    // Edge detection state
    private bool _previousPositionReached;
    private bool _previousMotorOn;
    private bool _edgeDetectionInitialized;

    private PendingCommand(int slaveIndex, string keyword, int parameter, int velocity, ushort acc, ushort dec, TimeSpan timeout, CommandCompletion completion, bool requiresAck, ILogger? logger = null)
    {
        SlaveIndex = slaveIndex;
        Keyword = keyword;
        Parameter = parameter;
        Velocity = velocity;
        Acceleration = acc;
        Deceleration = dec;
        RequiresAck = requiresAck;
        _completion = completion;
        _timeout = timeout;
        _logger = logger;
    }

    public int SlaveIndex { get; }

    public string Keyword { get; }

    public int Parameter { get; }

    public int Velocity { get; }

    public ushort Acceleration { get; }

    public ushort Deceleration { get; }

    public bool RequiresAck { get; }

    public TimeSpan Timeout => _timeout;

    public bool Acked { get; private set; }

    public Task Task => _tcs.Task;

    public bool Cancelled { get; private set; }

    public void AttachCancellation(CancellationTokenSource source, Action onCancelled)
    {
        _cancellationSource = source;
        source.Token.Register(() =>
        {
            Cancelled = true;
            onCancelled();
            _tcs.TrySetCanceled(source.Token);
        });
    }

    public void Start()
    {
        _stopwatch.Restart();
        Acked = false;
        _edgeDetectionInitialized = false;
    }

    public void MarkAcked()
    {
        Acked = true;
    }

    public void Apply(ref SoemShim.DriveRxPDO pdo)
    {
        FillCommand(ref pdo, Keyword);
        pdo.Parameter = Parameter;
        pdo.Velocity = Velocity;
        pdo.Acceleration = Acceleration;
        pdo.Deceleration = Deceleration;
        pdo.Execute = (byte)(Acked && RequiresAck ? 0 : 1);
    }

    public CommandState Evaluate(SoemShim.DriveTxPDO status)
    {
        // Special handling for AckWithTimeout - needs both ACK and full timeout duration
        if (_completion == CommandCompletion.AckWithTimeout)
        {
            if (!Acked)
            {
                // Still waiting for ACK
                if (_timeout > TimeSpan.Zero && _stopwatch.Elapsed > _timeout)
                {
                    return CommandState.TimedOut; // Timed out without ACK
                }
                return CommandState.Pending;
            }

            // ACK received - now wait for timeout to complete
            if (_timeout > TimeSpan.Zero && _stopwatch.Elapsed >= _timeout)
            {
                return CommandState.Completed; // ACK + timeout both satisfied
            }
            return CommandState.Pending;
        }

        // General timeout check for all other completion types
        if (_timeout > TimeSpan.Zero && _stopwatch.Elapsed > _timeout)
        {
            return CommandState.TimedOut;
        }

        return _completion switch
        {
            CommandCompletion.AckOnly => Acked ? CommandState.Completed : CommandState.Pending,
            CommandCompletion.PositionReached => EvaluatePositionReached(status),
            CommandCompletion.Indexed => (status.EncoderValid != 0 && status.PositionReached != 0) ? CommandState.Completed : CommandState.Pending,
            CommandCompletion.Enabled => (status.AmplifiersEnabled != 0 && status.MotorOn != 0) ? CommandState.Completed : CommandState.Pending,
            CommandCompletion.Disabled => status.AmplifiersEnabled == 0 ? CommandState.Completed : CommandState.Pending,
            CommandCompletion.Halt => status.Scanning == 0 ? CommandState.Completed : CommandState.Pending,
            _ => CommandState.Pending,
        };
    }

    private CommandState EvaluatePositionReached(SoemShim.DriveTxPDO status)
    {
        var currentPositionReached = status.PositionReached != 0;
        var currentMotorOn = status.MotorOn != 0;

        // Initialize edge detection on first call
        if (!_edgeDetectionInitialized)
        {
            _previousPositionReached = currentPositionReached;
            _previousMotorOn = currentMotorOn;
            _edgeDetectionInitialized = true;
            return CommandState.Pending;
        }

        // Detect rising edge of PositionReached (0 -> 1)
        var positionReachedRisingEdge = !_previousPositionReached && currentPositionReached;

        // Detect falling edge of MotorOn (1 -> 0)
        var motorOnFallingEdge = _previousMotorOn && !currentMotorOn;

        // Check if actual position matches target (for DPOS commands)
        var positionMatches = Keyword == "DPOS" && status.ActualPosition == Parameter;

        // Update state for next cycle
        _previousPositionReached = currentPositionReached;
        _previousMotorOn = currentMotorOn;

        // Complete on any of these conditions:
        // 1. Rising edge of PositionReached
        // 2. Falling edge of MotorOn (motion complete)
        // 3. Position matches target (already at position)
        if (positionReachedRisingEdge || motorOnFallingEdge || positionMatches)
        {
            var reason = positionReachedRisingEdge ? "PositionReached rising edge" :
                        motorOnFallingEdge ? "MotorOn falling edge" :
                        "Position matches target";
            _logger?.LogDebug("[Slave {SlaveIndex}] Move completed: {Reason} (Keyword: {Keyword}, Target: {Target}, Actual: {Actual})", 
                SlaveIndex, reason, Keyword, Parameter, status.ActualPosition);
            return CommandState.Completed;
        }

        return CommandState.Pending;
    }

    public void Complete()
    {
        _tcs.TrySetResult();
        _cancellationSource?.Dispose();
    }

    public void Fail(DriveError error)
    {
        if (error.Code == DriveErrorCode.None)
        {
            _tcs.TrySetException(new InvalidOperationException("Unknown drive fault."));
        }
        else
        {
            _tcs.TrySetException(new InvalidOperationException(error.ToString()));
        }
        _cancellationSource?.Dispose();
    }

    public static PendingCommand CreateMotion(int slaveIndex, string keyword, int parameter, int velocity, ushort acc, ushort dec, TimeSpan timeout, CommandCompletion completion, bool requiresAck, ILogger? logger = null)
        => new(slaveIndex, keyword, parameter, velocity, acc, dec, timeout, completion, requiresAck, logger);

    public static PendingCommand CreateControl(int slaveIndex, string keyword, int parameter, TimeSpan timeout, CommandCompletion completion, ILogger? logger = null)
        => new(slaveIndex, keyword, parameter, 0, 0, 0, timeout, completion, requiresAck: true, logger);

    public static void FillCommand(ref SoemShim.DriveRxPDO pdo, string keyword)
    {
        if (pdo.Command == null || pdo.Command.Length != 32)
        {
            pdo.Command = new byte[32];
        }

        Array.Clear(pdo.Command, 0, 32);
        var ascii = Encoding.ASCII.GetBytes(keyword);
        var length = Math.Min(ascii.Length, 32);
        Array.Copy(ascii, 0, pdo.Command, 0, length);
    }
}
