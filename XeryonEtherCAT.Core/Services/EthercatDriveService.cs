using System;
using System.Buffers;
using System.Diagnostics;
using System.Text;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using XeryonEtherCAT.Core.Abstractions;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Options;
using XeryonEtherCAT.Core.Utilities;

namespace XeryonEtherCAT.Core.Services;

/// <summary>
/// Production-grade EtherCAT drive orchestrator built around the soem_shim DLL.
/// </summary>
public sealed class EthercatDriveService : IEthercatDriveService
{
    private readonly EthercatDriveOptions _options;
    private readonly ILogger _logger;
    private readonly ISoemClient _soem;
    private readonly Channel<PendingCommand> _commandChannel;
    private readonly object _lifecycleGate = new();
    private readonly StringBuilder _errorBuffer = new(4096);
    private DriveErrorCode[] _lastFaults = Array.Empty<DriveErrorCode>();
    private DateTimeOffset[] _lastFaultTimes = Array.Empty<DateTimeOffset>();
    private readonly TimeSpan _faultRepeatInterval = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Raised when drive status bits or position changes during command execution.
    /// </summary>
    public event EventHandler<DriveStatusChangeEvent>? StatusChanged;

    private CancellationTokenSource? _ioCts;
    private Task? _ioTask;
    private IntPtr _handle = IntPtr.Zero;
    private string? _interface;
    private int _slaveCount;
    private bool _initialized;

    private SoemShim.DriveRxPDO[] _rxPdos = Array.Empty<SoemShim.DriveRxPDO>();
    private SoemShim.DriveTxPDO[] _txPdos = Array.Empty<SoemShim.DriveTxPDO>();
    private SoemShim.DriveTxPDO[] _previousTxPdos = Array.Empty<SoemShim.DriveTxPDO>();
    private PendingCommand?[] _activeCommands = Array.Empty<PendingCommand?>();
    private SemaphoreSlim[] _axisLocks = Array.Empty<SemaphoreSlim>();
    private bool[] _stopLatch = Array.Empty<bool>();
    private SoemStatusSnapshot _snapshot = new(DateTimeOffset.UtcNow, new SoemHealthSnapshot(0, 0, 0, 0, 0, 0, 0), Array.Empty<SoemShim.DriveTxPDO>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    private int _wkcStrikes;
    private long _telemetrySequence;

    public EthercatDriveService(EthercatDriveOptions? options = null, ILogger? logger = null, ISoemClient? soemClient = null)
    {
        _options = options ?? new EthercatDriveOptions();
        _logger = logger ?? NullLogger<EthercatDriveService>.Instance;
        
        _soem = soemClient ?? new SoemClient(NullLogger<SoemClient>.Instance);
        _commandChannel = Channel.CreateUnbounded<PendingCommand>(new UnboundedChannelOptions
        {
            SingleReader = true,
            AllowSynchronousContinuations = false
        });
    }

    public EthercatDriveService(IOptions<EthercatDriveOptions> options, ILogger<EthercatDriveService> logger, ISoemClient soemClient)
        : this(options?.Value ?? throw new ArgumentNullException(nameof(options)), logger, soemClient)
    {
    }

    public event EventHandler<SoemFaultEvent>? Faulted;

    public Task InitializeAsync(string iface, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        lock (_lifecycleGate)
        {
            if (_initialized)
            {
                throw new InvalidOperationException("EtherCAT drive service is already initialized.");
            }

            _interface = iface ?? throw new ArgumentNullException(nameof(iface));
        }

        _handle = _soem.Initialize(iface);
        if (_handle == IntPtr.Zero)
        {
            throw new InvalidOperationException("Unable to initialize soem_shim. Ensure the native library is accessible.");
        }

        _slaveCount = _soem.GetSlaveCount(_handle);
        if (_slaveCount <= 0)
        {
            _soem.Shutdown(_handle);
            _handle = IntPtr.Zero;
            throw new InvalidOperationException("SOEM reported zero slaves. Ensure the EtherCAT network is reachable.");
        }

        AllocateBuffers(_slaveCount);
        _ioCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _ioTask = Task.Run(() => RunIoLoopAsync(_ioCts.Token), CancellationToken.None);

        lock (_lifecycleGate)
        {
            _initialized = true;
        }

        return Task.CompletedTask;
    }

    public Task<int> GetSlaveCountAsync()
    {
        EnsureInitialized();
        return Task.FromResult(_slaveCount);
    }

    public async Task MoveAbsoluteAsync(int slave, int targetPos, int vel, ushort acc, ushort dec, TimeSpan settleTimeout, CancellationToken ct)
    {
        EnsureInitialized();
        var axis = GetAxisIndex(slave);
        var snapshot = GetStatus();
        var status = snapshot.DriveStates.Length > axis ? snapshot.DriveStates[axis] : default;
        EnsureAxisReadyForMotion(slave, status, requireEncoder: true);
        var timeout = settleTimeout > TimeSpan.Zero ? settleTimeout : _options.DefaultSettleTimeout;
        var command = PendingCommand.CreateMotion(axis, "DPOS", targetPos, vel, acc, dec, timeout, CommandCompletion.PositionReached, requiresAck: true, _logger);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
    }

    public async Task JogAsync(int slave, int direction, int vel, ushort acc, ushort dec, CancellationToken ct)
    {
        EnsureInitialized();
        if (direction is < -1 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "Direction must be -1, 0 or 1.");
        }

        var axis = GetAxisIndex(slave);
        var snapshot = GetStatus();
        var status = snapshot.DriveStates.Length > axis ? snapshot.DriveStates[axis] : default;
        EnsureAxisReadyForMotion(slave, status, requireEncoder: false);
        var command = PendingCommand.CreateMotion(axis, "SCAN", direction, vel, acc, dec, TimeSpan.Zero, CommandCompletion.AckOnly, requiresAck: true, _logger);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
    }

    public async Task IndexAsync(int slave, int direction, int vel, ushort acc, ushort dec, TimeSpan settleTimeout, CancellationToken ct)
    {
        EnsureInitialized();
        if (direction is < 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(direction), "Index direction must be 0 or 1.");
        }

        var axis = GetAxisIndex(slave);
        var snapshot = GetStatus();
        var status = snapshot.DriveStates.Length > axis ? snapshot.DriveStates[axis] : default;

        // If encoder is already valid, return immediately (idempotent behavior)
        if (status.EncoderValid != 0)
        {
            _logger.LogDebug("Slave {Slave} encoder is already valid, skipping indexing command.", slave);
            return;
        }

        EnsureAxisReadyForMotion(slave, status, requireEncoder: false);
        var timeout = settleTimeout > TimeSpan.Zero ? settleTimeout : _options.DefaultSettleTimeout;
        var command = PendingCommand.CreateMotion(axis, "INDX", direction, vel, acc, dec, timeout, CommandCompletion.Indexed, requiresAck: true, _logger);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
    }

    public async Task ResetAsync(int slave, CancellationToken ct)
    {
        EnsureInitialized();
        var axis = GetAxisIndex(slave);
        var command = PendingCommand.CreateControl(axis, "RSET", 0, TimeSpan.FromMilliseconds(1000), CommandCompletion.AckWithTimeout);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
        _stopLatch[axis] = false;
    }

    public async Task EnableAsync(int slave, bool enable, CancellationToken ct)
    {
        EnsureInitialized();
        var axis = GetAxisIndex(slave);

        // Check current state
        var snapshot = GetStatus();
        var status = snapshot.DriveStates.Length > axis ? snapshot.DriveStates[axis] : default;

        // If already in target state, return immediately (idempotent behavior)
        if (enable && status.AmplifiersEnabled != 0)
        {
            _logger.LogDebug("Slave {Slave} is already enabled, skipping command.", slave);
            return;
        }

        if (!enable && status.AmplifiersEnabled == 0)
        {
            _logger.LogDebug("Slave {Slave} is already disabled, skipping command.", slave);
            return;
        }

        var completion = enable ? CommandCompletion.Enabled : CommandCompletion.Disabled;
        var command = PendingCommand.CreateControl(axis, "ENBL", enable ? 1 : 0, TimeSpan.FromMilliseconds(500), completion);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
        if (enable)
        {
            _stopLatch[axis] = false;
        }
    }

    public async Task HaltAsync(int slave, CancellationToken ct)
    {
        EnsureInitialized();
        var axis = GetAxisIndex(slave);
        var command = PendingCommand.CreateControl(axis, "HALT", 0, TimeSpan.FromSeconds(2), CommandCompletion.Halt);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
    }

    public async Task StopAsync(int slave, CancellationToken ct)
    {
        EnsureInitialized();
        var axis = GetAxisIndex(slave);
        var command = PendingCommand.CreateControl(axis, "STOP", 0, TimeSpan.FromSeconds(2), CommandCompletion.AckOnly);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
        _stopLatch[axis] = true;
    }

    public async Task SendRawCommandAsync(
        int slave,
        string keyword,
        int parameter,
        int velocity = 0,
        ushort acceleration = 0,
        ushort deceleration = 0,
        bool requiresAck = true,
        TimeSpan? timeout = null,
        CancellationToken ct = default)
    {
        EnsureInitialized();
        if (string.IsNullOrWhiteSpace(keyword))
        {
            throw new ArgumentException("Command keyword must be provided.", nameof(keyword));
        }

        keyword = keyword.Trim().ToUpperInvariant();
        if (keyword.Length > 32)
        {
            throw new ArgumentOutOfRangeException(nameof(keyword), "Command keyword must be 32 characters or fewer.");
        }

        var axis = GetAxisIndex(slave);
        var command = PendingCommand.CreateMotion(
            axis,
            keyword,
            parameter,
            velocity,
            acceleration,
            deceleration,
            timeout ?? TimeSpan.Zero,
            CommandCompletion.AckOnly,
            requiresAck);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
    }

    public SoemStatusSnapshot GetStatus()
        => _snapshot;

    public async ValueTask DisposeAsync()
    {
        Task? ioTask;
        CancellationTokenSource? cts;

        lock (_lifecycleGate)
        {
            if (!_initialized)
            {
                _soem.Dispose();
                return;
            }

            ioTask = _ioTask;
            cts = _ioCts;
            _initialized = false;
        }

        if (cts is not null)
        {
            cts.Cancel();
        }

        if (ioTask is not null)
        {
            try
            {
                await ioTask.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (_handle != IntPtr.Zero)
        {
            _soem.Shutdown(_handle);
            _handle = IntPtr.Zero;
        }

        _soem.Dispose();
    }

    private async Task ExecuteCommandAsync(int axisIndex, PendingCommand command, CancellationToken ct)
    {
        var axisLock = _axisLocks[axisIndex];
        await axisLock.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            command.AttachCancellation(linkedCts, () =>
            {
                _activeCommands[axisIndex] = null;
            });
            await _commandChannel.Writer.WriteAsync(command, ct).ConfigureAwait(false);
            await command.Task.ConfigureAwait(false);
        }
        finally
        {
            axisLock.Release();
        }
    }

    private void AllocateBuffers(int slaveCount)
    {
        _rxPdos = new SoemShim.DriveRxPDO[slaveCount];
        _txPdos = new SoemShim.DriveTxPDO[slaveCount];
        _previousTxPdos = new SoemShim.DriveTxPDO[slaveCount]; // Add this
        _activeCommands = new PendingCommand?[slaveCount];
        _axisLocks = new SemaphoreSlim[slaveCount];
        _stopLatch = new bool[slaveCount];

        for (var i = 0; i < slaveCount; i++)
        {
            _rxPdos[i] = CreateNopPdo();
            _axisLocks[i] = new SemaphoreSlim(1, 1);
            _stopLatch[i] = false;
        }

        _lastFaults = new DriveErrorCode[slaveCount];
        _lastFaultTimes = new DateTimeOffset[slaveCount];
        for (var i = 0; i < slaveCount; i++)
        {
            _lastFaults[i] = DriveErrorCode.None;
            _lastFaultTimes[i] = DateTimeOffset.MinValue;
        }

        _logger.LogInformation(
            "AllocateBuffers: slaveCount={SlaveCount}, rxPdos={RxPdos}, txPdos={TxPdos}, activeCommands={ActiveCommands}, axisLocks={AxisLocks}, stopLatch={StopLatch}",
            slaveCount, _rxPdos.Length, _txPdos.Length, _activeCommands.Length, _axisLocks.Length, _stopLatch.Length);
    }

    private static SoemShim.DriveRxPDO CreateNopPdo()
    {
        var pdo = new SoemShim.DriveRxPDO
        {
            Command = new byte[32],
            Parameter = 0,
            Velocity = 0,
            Acceleration = 0,
            Deceleration = 0,
            Execute = 0
        };
        PendingCommand.FillCommand(ref pdo, "NOP");
        return pdo;
    }

   

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("EtherCAT drive service has not been initialized.");
        }
    }

    private static int GetAxisIndex(int slave)
    {
        if (slave <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(slave), "Slave index must be >= 1.");
        }

        return slave - 1;
    }

    private void EnsureAxisReadyForMotion(int slave, SoemShim.DriveTxPDO status, bool requireEncoder)
    {
        var axis = GetAxisIndex(slave);
        if (_stopLatch.Length > axis && _stopLatch[axis])
        {
            throw new InvalidOperationException($"Slave {slave} is latched by STOP. Issue ENBL=1 or RSET before motion.");
        }

        if (status.AmplifiersEnabled == 0)
        {
            throw new InvalidOperationException($"Slave {slave} is not enabled. Call EnableAsync before issuing motion commands.");
        }

        if (requireEncoder && status.EncoderValid == 0)
        {
            throw new InvalidOperationException($"Slave {slave} encoder is not referenced. Run IndexAsync before absolute motion.");
        }
    }

    private async Task RunIoLoopAsync(CancellationToken ct)
    {
        var timerPeriod = _options.CyclePeriod > TimeSpan.Zero ? _options.CyclePeriod : TimeSpan.FromMilliseconds(2);
        using var timer = new PeriodicTimer(timerPeriod);
        var minCycle = TimeSpan.MaxValue;
        var maxCycle = TimeSpan.Zero;
        var lastCycle = TimeSpan.Zero;

        while (!ct.IsCancellationRequested)
        {
            var cycleStart = Stopwatch.GetTimestamp();

            ProcessIncomingCommands();
            StageOutputs();

            var wkc = _soem.ExchangeProcessData(_handle, _options.ExchangeTimeoutMicroseconds);
            var health = ReadHealth();

            // Handle different error codes from SOEM
            if (wkc >= 0)
            {
                // Success - process normally
                _fatalErrorCount = 0;
                ProcessStatuses(health, wkc);
            }
            else if (wkc == SoemErrorCodes.SOEM_ERR_WKC_LOW)
            {
                // Recoverable: working counter low
                _logger.LogWarning("Working counter low: {Description}", SoemErrorCodes.GetErrorDescription(wkc));
                _fatalErrorCount = 0;
                ProcessStatuses(health, wkc);
            }
            else if (SoemErrorCodes.IsFatalError(wkc))
            {
                // Fatal errors: bad args, send fail, recv fail
                _fatalErrorCount++;
                _logger.LogError("Fatal EtherCAT error (#{Count}): {Code} - {Description}",
                    _fatalErrorCount, wkc, SoemErrorCodes.GetErrorDescription(wkc));

                // Attempt immediate recovery for fatal errors
                if (_fatalErrorCount >= 3)
                {
                    _logger.LogCritical("Too many consecutive fatal errors ({Count}). Force reinitializing.", _fatalErrorCount);
                    Reinitialize();
                    _fatalErrorCount = 0;
                    _wkcStrikes = 0;
                }
                else
                {
                    HandleFaultyCycle(health, wkc, $"Fatal communication error: {SoemErrorCodes.GetErrorDescription(wkc)}");
                }
            }
            else
            {
                // Unknown error code
                _logger.LogError("Unknown SOEM error code: {Wkc} - {Description}", wkc, SoemErrorCodes.GetErrorDescription(wkc));
                HandleFaultyCycle(health, wkc, $"Unknown error code: {wkc}");
            }

            DrainErrorSink();

            lastCycle = Stopwatch.GetElapsedTime(cycleStart);
            if (lastCycle < minCycle)
            {
                minCycle = lastCycle;
            }

            if (lastCycle > maxCycle)
            {
                maxCycle = lastCycle;
            }

            PublishSnapshot(health, lastCycle, minCycle, maxCycle);

            if (_options.EnableCycleTraceLogging)
            {
                _logger.LogTrace("Cycle complete: wkc={Wkc} expected={Expected} op={Op} duration={Duration} min={Min} max={Max}", health.LastWkc, health.GroupExpectedWkc, health.SlavesOperational, lastCycle.TotalMilliseconds, minCycle.TotalMilliseconds, maxCycle.TotalMilliseconds);
            }

            try
            {
                await timer.WaitForNextTickAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private void ProcessIncomingCommands()
    {
        while (_commandChannel.Reader.TryRead(out var command))
        {
            if (command == null)
            {
                continue;
            }

            if (command.Cancelled)
            {
                continue;
            }

            var axis = command.SlaveIndex;
            if (axis < 0 || axis >= _activeCommands.Length)
            {
                command.Fail(new DriveError(DriveErrorCode.UnknownFault, "Invalid slave index.", "Verify command arguments."));
                continue;
            }

            if (_activeCommands[axis] is not null)
            {
                command.Fail(new DriveError(DriveErrorCode.UnknownFault, "Command already in-flight for slave.", "Wait for active command to complete."));
                continue;
            }

            _activeCommands[axis] = command;
            command.Start();
            _logger.LogDebug("Staged {Command}={Parameter} for slave {Slave}.", command.Keyword, command.Parameter, axis + 1);
        }
    }

    private void StageOutputs()
    {
        for (var i = 0; i < _rxPdos.Length; i++)
        {
            ref var pdo = ref _rxPdos[i];
            var command = _activeCommands[i];
            if (command is null)
            {
                pdo.Execute = 0;
                PendingCommand.FillCommand(ref pdo, "NOP");
                pdo.Parameter = 0;
                pdo.Velocity = 0;
                pdo.Acceleration = 0;
                pdo.Deceleration = 0;
            }
            else
            {
                if (command.Cancelled)
                {
                    _activeCommands[i] = null;
                    pdo.Execute = 0;
                    PendingCommand.FillCommand(ref pdo, "NOP");
                    pdo.Parameter = 0;
                    pdo.Velocity = 0;
                    pdo.Acceleration = 0;
                    pdo.Deceleration = 0;
                }
                else
                {
                    command.Apply(ref pdo);
                }
            }

            var rc = _soem.WriteRxPdo(_handle, i + 1, ref pdo);
            if (rc < 0)
            {
                _logger.LogWarning("Failed to write RX PDO for slave {Slave}: {Result}.", i + 1, rc);
            }
        }
    }

    private SoemHealthSnapshot ReadHealth()
    {
        if (_soem.GetHealth(_handle, out var health) != 0)
        {
            return new SoemHealthSnapshot(health.slaves_found, health.group_expected_wkc, health.last_wkc, health.bytes_out, health.bytes_in, health.slaves_op, health.al_status_code);
        }

        return new SoemHealthSnapshot(0, 0, 0, 0, 0, 0, 0);
    }

    private void ProcessStatuses(SoemHealthSnapshot health, int wkc)
    {
        _logger.LogTrace(
            "ProcessStatuses: rxPdos={RxPdos}, txPdos={TxPdos}, activeCommands={ActiveCommands}, axisLocks={AxisLocks}, stopLatch={StopLatch}",
            _rxPdos.Length, _txPdos.Length, _activeCommands.Length, _axisLocks.Length, _stopLatch.Length);

        if (health.LastWkc < health.GroupExpectedWkc)
        {
            HandleFaultyCycle(health, wkc, "Work counter below expected");
        }
        else
        {
            _wkcStrikes = 0;
        }

        try
        {
            for (var i = 0; i < _txPdos.Length; i++)
            {
                var slaveIndex = i + 1;
                var rc = _soem.ReadTxPdo(_handle, slaveIndex, out var tx);
                if (rc < 0)
                {
                    _logger.LogWarning("Failed to read TX PDO for slave {Slave}: {Result}.", slaveIndex, rc);
                    continue;
                }

                // Detect changes
                var previous = _previousTxPdos[i];
                var currentMask = DriveStateFormatter.ToBitMask(tx);
                var previousMask = DriveStateFormatter.ToBitMask(previous);
                var changedMask = currentMask ^ previousMask;
                var positionChanged = tx.ActualPosition != previous.ActualPosition;

                // Update stored state
                _previousTxPdos[i] = tx;
                _txPdos[i] = tx;
                
                var command = _activeCommands[i];
                // Raise status change event if anything changed during command execution
                if ((changedMask != 0 || positionChanged) && (command != null))
                {
                    var timestamp = DateTimeOffset.UtcNow;
                    var monotonicTicks = TelemetrySync.GetTimestampTicks();
                    var sequence = Interlocked.Increment(ref _telemetrySequence);
                    var statusEvent = new DriveStatusChangeEvent(
                        slaveIndex,
                        timestamp,
                        tx,
                        previous,
                        changedMask,
                        command?.Keyword,
                        monotonicTicks,
                        sequence);

                    // Log the change with millisecond precision
                    //_logger.LogDebug("{StatusChange}", statusEvent.ToString());
                    
                    // Raise event
                    StatusChanged?.Invoke(this, statusEvent);
                }

                if (command is null)
                {
                    continue;
                }

                if (command.Cancelled)
                {
                    _activeCommands[i] = null;
                    continue;
                }

                if (!command.Acked && tx.ExecuteAck != 0)
                {
                    command.MarkAcked();
                    _logger.LogDebug("[{Timestamp:HH:mm:ss.fff}] Command {Command}={Param} acknowledged for slave {Slave}.", 
                        DateTimeOffset.UtcNow, command.Keyword, command.Parameter, slaveIndex);
                }

                if (TryDecodeError(tx, out var error))
                {
                    RaiseFault(slaveIndex, tx, error, health);
                }
                else if (i >= 0 && i < _lastFaults.Length)
                {
                    _lastFaults[i] = DriveErrorCode.None;
                    _lastFaultTimes[i] = DateTimeOffset.MinValue;
                }

                if (health.AlStatusCode != 0)
                {
                    var alError = new DriveError(DriveErrorCode.UnknownFault, $"AL status code {health.AlStatusCode}", "Inspect EtherCAT network and recover.");
                    command.Fail(alError);
                    RaiseFault(slaveIndex, tx, alError, health);
                    _activeCommands[i] = null;
                    continue;
                }

                var result = command.Evaluate(tx);
                switch (result)
                {
                    case CommandState.Completed:
                        _logger.LogDebug("[{Timestamp:HH:mm:ss.fff}] Command {Command}={Parameter} completed for slave {Slave}.", 
                            DateTimeOffset.UtcNow, command.Keyword, command.Parameter, slaveIndex);
                        command.Complete();
                        _activeCommands[i] = null;
                        break;
                    case CommandState.TimedOut:
                        var timeoutError = new DriveError(DriveErrorCode.SafetyTimeout, $"Command {command.Keyword} timed out after {command.Timeout.TotalSeconds:F2} seconds.", "Issue ENBL=1 or RSET, then retry with adjusted profile.");
                        command.Fail(timeoutError);
                        RaiseFault(slaveIndex, tx, timeoutError, health);
                        _activeCommands[i] = null;
                        break;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while processing drive statuses.");
        }
    }

    // helper to classify recovery/control commands
    private void HandleFaultyCycle(SoemHealthSnapshot health, int wkc, string reason)
    {
        _wkcStrikes++;
        _logger.LogWarning("{Reason}: wkc={Wkc} expected={Expected} op={Op} strikes={Strikes}",
            reason, health.LastWkc, health.GroupExpectedWkc, health.SlavesOperational, _wkcStrikes);

        if (_wkcStrikes >= _options.WkcRecoveryThreshold)
        {
            _logger.LogError("WKC below expected for {Strikes} cycles. Attempting recovery.", _wkcStrikes);

            var recoveryResult = _soem.TryRecover(_handle, _options.RecoveryTimeoutMilliseconds);
            if (recoveryResult > 0)
            {
                // Recovery succeeded - give slaves time to settle
                Thread.Sleep(20); // 20ms post-recovery settling time

                _logger.LogInformation("Recovery successful, resetting strike counter.");
                _wkcStrikes = 0;
            }
            else
            {
                _logger.LogError("SOEM recovery failed. Reinitializing EtherCAT session.");
                Reinitialize();
                _wkcStrikes = 0;
            }
        }
    }
    private void Reinitialize()
    {
        foreach (var command in _activeCommands)
        {
            command?.Fail(new DriveError(DriveErrorCode.SafetyTimeout, "IO loop restarted.", "Re-issue motion command after recovery."));
        }

        Array.Clear(_activeCommands, 0, _activeCommands.Length);
        if (_handle != IntPtr.Zero)
        {
            _soem.Shutdown(_handle);
            _handle = IntPtr.Zero;
        }

        if (_interface is null)
        {
            return;
        }

        if (_options.ReinitializationDelay > TimeSpan.Zero)
        {
            Thread.Sleep(_options.ReinitializationDelay);
        }

        _handle = _soem.Initialize(_interface);
        if (_handle == IntPtr.Zero)
        {
            _logger.LogCritical("Failed to reinitialize SOEM after recovery attempt.");
            return;
        }

        var count = _soem.GetSlaveCount(_handle);
        if (count != _slaveCount)
        {
            _logger.LogWarning("Slave count changed after recovery {Old}->{New}.", _slaveCount, count);
            _slaveCount = count;
            AllocateBuffers(_slaveCount);
        }
    }

    private void DrainErrorSink()
    {
        var errors = _soem.DrainErrorList(_handle, _errorBuffer);
        if (!string.IsNullOrWhiteSpace(errors))
        {
            _logger.LogError("SOEM: {Errors}", errors);
        }
    }

    private void PublishSnapshot(SoemHealthSnapshot health, TimeSpan cycleDuration, TimeSpan minCycle, TimeSpan maxCycle)
    {
        var drives = ArrayPool<SoemShim.DriveTxPDO>.Shared.Rent(_txPdos.Length);
        try
        {
            Array.Copy(_txPdos, drives, _txPdos.Length);
            _snapshot = new SoemStatusSnapshot(DateTimeOffset.UtcNow, health, drives[.._txPdos.Length].ToArray(), cycleDuration, minCycle, maxCycle);
        }
        finally
        {
            ArrayPool<SoemShim.DriveTxPDO>.Shared.Return(drives);
        }
    }

    private static bool TryDecodeError(SoemShim.DriveTxPDO status, out DriveError error)
    {
        if (status.ThermalProtection1 != 0)
        {
            error = new DriveError(DriveErrorCode.ThermalProtection, "Thermal overload of the piezo amplifier 1.", "Allow drive to cool; issue ENBL=1 or RSET.");
            return true;
        }

        if (status.ThermalProtection2 != 0)
        {
            error = new DriveError(DriveErrorCode.ThermalProtection, "Thermal overload of the piezo amplifier 2.", "Allow drive to cool; issue ENBL=1 or RSET.");
            return true;
        }

        if (status.EncoderError != 0)
        {
            error = new DriveError(DriveErrorCode.EncoderError, "Encoder read error.", "Avoid touching the encoder strip. Check encoder wiring; perform RSET then INDX.");
            return true;
        }

        if (status.ErrorLimit != 0)
        {
            error = new DriveError(DriveErrorCode.FollowError, "Following error has reached the limit set by ELIM. This can indicate a collision, or the motor not strong enough to produce the acceleration and speed required by the trajectory.", "Reduce speed/acceleration; issue ENBL=1 to clear. Increase ELIM or disable by setting ELIM=0");
            return true;
        }

        if (status.SafetyTimeout != 0)
        {
            error = new DriveError(DriveErrorCode.SafetyTimeout, "Motor was on for a time longer that the value set by TOU2", "Perform RSET or ENBL=1. Issue STOP or HALT to avoid. TOU2=0 disables this timeout. ");
            return true;
        }

        if (status.EmergencyStop != 0)
        {
            error = new DriveError(DriveErrorCode.EmergencyStop, "The STOP command was issued." , "Set ENBL=1 or RSET. Use HALT to avoid this error.");
            return true;
        }

        if (status.PositionFail != 0)
        {
            error = new DriveError(DriveErrorCode.PositionFail, "The actuator did not settle at the target position within the specified time (TOU3).", "Increase TOU3, PTOL or PTO2. Issue ENBL=1 or RSET");
            return true;
        }
           
        if (status.EndStop != 0 & status.LeftEndStop != 0)
        {
            error = new DriveError(DriveErrorCode.EndStopHit, "Left End-stop detected.", "Jog away from the left limit.");
            return true;
        }
        
        if (status.EndStop != 0 & status.RightEndStop != 0 )
        {
            error = new DriveError(DriveErrorCode.EndStopHit, "Right End-stop detected.", "Jog away from the right limit.");
            return true;
        }  

        error = new DriveError(DriveErrorCode.None, string.Empty, string.Empty);
        return false;
    }

    private void RaiseFault(int slave, SoemShim.DriveTxPDO status, DriveError error, SoemHealthSnapshot health)
    {
        try
        {
            var idx = slave - 1;
            var now = DateTimeOffset.UtcNow;
            if (idx >= 0 && idx < _lastFaults.Length)
            {
                if (_lastFaults[idx] == error.Code && (now - _lastFaultTimes[idx]) < _faultRepeatInterval)
                {
                    _logger.LogDebug("Suppressing duplicate fault for slave {Slave} code={Code}.", slave, error.Code);
                    return;
                }

                _lastFaults[idx] = error.Code;
                _lastFaultTimes[idx] = now;
            }

            _logger.LogError("Slave {Slave} fault: {Error} status: {status}", slave, error, DriveStateFormatter.ToHexString(status));
            Faulted?.Invoke(this, new SoemFaultEvent(slave, status, error, health));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while raising fault for slave {Slave}.", slave);
        }
    }

    
}
