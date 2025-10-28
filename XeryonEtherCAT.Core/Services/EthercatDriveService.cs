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

namespace XeryonEtherCAT.Core.Services;

/// <summary>
/// Production-grade EtherCAT drive orchestrator built around the soem_shim DLL.
/// </summary>
public sealed class EthercatDriveService : IEthercatDriveService
{
    private readonly EthercatDriveOptions _options;
    private readonly ILogger<EthercatDriveService> _logger;
    private readonly ISoemClient _soem;
    private readonly Channel<PendingCommand> _commandChannel;
    private readonly object _lifecycleGate = new();
    private readonly StringBuilder _errorBuffer = new(4096);
    private DriveErrorCode[] _lastFaults = Array.Empty<DriveErrorCode>();
    private DateTimeOffset[] _lastFaultTimes = Array.Empty<DateTimeOffset>();
    private readonly TimeSpan _faultRepeatInterval = TimeSpan.FromSeconds(5);


    private CancellationTokenSource? _ioCts;
    private Task? _ioTask;
    private IntPtr _handle = IntPtr.Zero;
    private string? _interface;
    private int _slaveCount;
    private bool _initialized;

    private SoemShim.DriveRxPDO[] _rxPdos = Array.Empty<SoemShim.DriveRxPDO>();
    private SoemShim.DriveTxPDO[] _txPdos = Array.Empty<SoemShim.DriveTxPDO>();
    private DriveStatus[] _lastStatuses = Array.Empty<DriveStatus>();
    private int[] _positions = Array.Empty<int>();
    private PendingCommand?[] _activeCommands = Array.Empty<PendingCommand?>();
    private SemaphoreSlim[] _axisLocks = Array.Empty<SemaphoreSlim>();
    private bool[] _stopLatch = Array.Empty<bool>();
    private SoemStatusSnapshot _snapshot = new(DateTimeOffset.UtcNow, new SoemHealthSnapshot(0, 0, 0, 0, 0, 0, 0), Array.Empty<DriveStatus>(), Array.Empty<int>(), TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero);
    private int _wkcStrikes;

    public EthercatDriveService(EthercatDriveOptions? options = null, ILogger<EthercatDriveService>? logger = null, ISoemClient? soemClient = null)
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
        var status = GetStatus().DriveStatuses.Length > axis ? GetStatus().DriveStatuses[axis] : DriveStatus.None;
        EnsureAxisReadyForMotion(slave, status, requireEncoder: true);
        var timeout = settleTimeout > TimeSpan.Zero ? settleTimeout : _options.DefaultSettleTimeout;
        var command = PendingCommand.CreateMotion(axis, "DPOS", targetPos, vel, acc, dec, timeout, CommandCompletion.PositionReached, requiresAck: true);
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
        var status = GetStatus().DriveStatuses.Length > axis ? GetStatus().DriveStatuses[axis] : DriveStatus.None;
        EnsureAxisReadyForMotion(slave, status, requireEncoder: false);
        var command = PendingCommand.CreateMotion(axis, "SCAN", direction, vel, acc, dec, TimeSpan.Zero, CommandCompletion.AckOnly, requiresAck: true);
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
        var status = GetStatus().DriveStatuses.Length > axis ? GetStatus().DriveStatuses[axis] : DriveStatus.None;
        EnsureAxisReadyForMotion(slave, status, requireEncoder: false);
        var timeout = settleTimeout > TimeSpan.Zero ? settleTimeout : _options.DefaultSettleTimeout;
        var command = PendingCommand.CreateMotion(axis, "INDX", direction, vel, acc, dec, timeout, CommandCompletion.Indexed, requiresAck: true);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
    }

    public async Task ResetAsync(int slave, CancellationToken ct)
    {
        EnsureInitialized();
        var axis = GetAxisIndex(slave);
        var command = PendingCommand.CreateControl(axis, "RSET", 0, TimeSpan.FromMilliseconds(100), CommandCompletion.AckOnly);
        await ExecuteCommandAsync(axis, command, ct).ConfigureAwait(false);
        _stopLatch[axis] = false;
    }

    public async Task EnableAsync(int slave, bool enable, CancellationToken ct)
    {
        EnsureInitialized();
        var axis = GetAxisIndex(slave);
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
        _lastStatuses = new DriveStatus[slaveCount];
        _positions = new int[slaveCount];
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
            "AllocateBuffers: slaveCount={SlaveCount}, rxPdos={RxPdos}, txPdos={TxPdos}, lastStatuses={LastStatuses}, positions={Positions}, activeCommands={ActiveCommands}, axisLocks={AxisLocks}, stopLatch={StopLatch}",
            slaveCount, _rxPdos.Length, _txPdos.Length, _lastStatuses.Length, _positions.Length, _activeCommands.Length, _axisLocks.Length, _stopLatch.Length);
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
        FillCommand(ref pdo, "NOP");
        return pdo;
    }

    private static void FillCommand(ref SoemShim.DriveRxPDO pdo, string keyword)
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

    private void EnsureAxisReadyForMotion(int slave, DriveStatus status, bool requireEncoder)
    {
        var axis = GetAxisIndex(slave);
        if (_stopLatch.Length > axis && _stopLatch[axis])
        {
            throw new InvalidOperationException($"Slave {slave} is latched by STOP. Issue ENBL=1 or RSET before motion.");
        }

        if (!status.HasFlag(DriveStatus.AmplifiersEnabled) || !status.HasFlag(DriveStatus.MotorOn))
        {
            throw new InvalidOperationException($"Slave {slave} is not enabled. Call EnableAsync before issuing motion commands.");
        }

        if (!status.HasFlag(DriveStatus.ClosedLoop))
        {
            throw new InvalidOperationException($"Slave {slave} is not in closed-loop mode.");
        }

        if (requireEncoder && !status.HasFlag(DriveStatus.EncoderValid))
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

            if (wkc >= 0)
            {
                ProcessStatuses(health, wkc);
            }
            else
            {
                _logger.LogWarning("Negative WKC {Wkc} detected.", wkc);
                HandleFaultyCycle(health, wkc, "Negative work counter");
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
            _logger.LogDebug("Staged {Command} for slave {Slave}.", command.Keyword, axis + 1);
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
                FillCommand(ref pdo, "NOP");
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
                    FillCommand(ref pdo, "NOP");
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
    _logger.LogDebug(
        "ProcessStatuses: rxPdos={RxPdos}, txPdos={TxPdos}, lastStatuses={LastStatuses}, positions={Positions}, activeCommands={ActiveCommands}, axisLocks={AxisLocks}, stopLatch={StopLatch}",
        _rxPdos.Length, _txPdos.Length, _lastStatuses.Length, _positions.Length, _activeCommands.Length, _axisLocks.Length, _stopLatch.Length);


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
                int slaveIndex = i + 1;
                var rc = _soem.ReadTxPdo(_handle, slaveIndex, out var tx);
                if (rc < 0)
                {
                    _logger.LogWarning("Failed to read TX PDO for slave {Slave}: {Result}.", i + 1, rc);
                    continue;
                }

                _txPdos[i] = tx;
                var status = DecodeStatus(tx);
                _positions[i] = tx.ActualPosition;
                _lastStatuses[i] = status;

                var command = _activeCommands[i];
                if (command is null)
                {
                    continue;
                }

                if (command.Cancelled)
                {
                    _activeCommands[i] = null;
                    continue;
                }

                if (!command.Acked && status.HasFlag(DriveStatus.ExecuteAck))
                {
                    command.MarkAcked();
                    _logger.LogDebug("Command {Command} acknowledged for slave {Slave}.", command.Keyword, i + 1);
                }


                var k = command.Keyword?.Trim().ToUpperInvariant();
                var isRecoveryCommand = (k == "RSET" || k == "ENBL" || k == "STOP" || k == "HALT" || k == "INDX" || k=="SCAN");


                // inside ProcessStatuses, replace the current TryDecodeError(...) handling with this block
                if (TryDecodeError(status, out var error))
                {
                    // Always notify the system about the fault (RaiseFault is throttled).
                    RaiseFault(i + 1, status, error, health);

                    // Allow recovery/control commands to proceed (do not fail them).
                    if (!isRecoveryCommand)
                    {
                        command.Fail(error);
                        _activeCommands[i] = null;
                        continue;
                    }

                    _logger.LogDebug("Recoverable fault present but allowing recovery command {Command} for slave {Slave}.", command.Keyword, i + 1);
                }
                else
                {
                    // No decoded error this cycle — clear last-fault tracking so a future fault will be raised again.
                    if (i >= 0 && i < _lastFaults.Length)
                    {
                        _lastFaults[i] = DriveErrorCode.None;
                        _lastFaultTimes[i] = DateTimeOffset.MinValue;
                    }
                }


                if (health.AlStatusCode != 0)
                {
                    var alError = new DriveError(DriveErrorCode.UnknownFault, $"AL status code {health.AlStatusCode}", "Inspect EtherCAT network and recover.");
                    command.Fail(alError);
                    RaiseFault(i + 1, status, alError, health);
                    _activeCommands[i] = null;
                    continue;
                }

                var result = command.Evaluate(status, tx.ActualPosition);
                if (result == CommandState.Completed)
                {
                    command.Complete();
                    _activeCommands[i] = null;
                }
                else if (result == CommandState.TimedOut)
                {
                    var timeoutError = new DriveError(DriveErrorCode.SafetyTimeout, $"Command {command.Keyword} timed out.", "Issue ENBL=1 or RSET, then retry with adjusted profile.");
                    command.Fail(timeoutError);
                    RaiseFault(i + 1, status, timeoutError, health);
                    _activeCommands[i] = null;
                }
            }
        }
        catch (Exception e)
        {
            _logger.LogError(e, null, null!);
;        }
    }

    // helper to classify recovery/control commands
    private static bool IsRecoveryCommand(PendingCommand? cmd)
    {
        if (cmd is null) return false;
        var k = cmd.Keyword?.Trim().ToUpperInvariant();
        return k == "RSET" || k == "ENBL" || k == "STOP" || k == "HALT" || k == "INDX" || k == "SCAN";
    }


    private void HandleFaultyCycle(SoemHealthSnapshot health, int wkc, string reason)
    {
        _wkcStrikes++;
        _logger.LogWarning("{Reason}: wkc={Wkc} expected={Expected} op={Op}", reason, health.LastWkc, health.GroupExpectedWkc, health.SlavesOperational);
        if (_wkcStrikes >= _options.WkcRecoveryThreshold)
        {
            _logger.LogError("WKC below expected for {Strikes} cycles. Attempting recovery.", _wkcStrikes);
            if (_soem.TryRecover(_handle, _options.RecoveryTimeoutMilliseconds) <= 0)
            {
                _logger.LogError("SOEM recovery failed. Reinitializing EtherCAT session.");
                Reinitialize();
            }
            _wkcStrikes = 0;
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
        var statuses = ArrayPool<DriveStatus>.Shared.Rent(_lastStatuses.Length);
        var positions = ArrayPool<int>.Shared.Rent(_positions.Length);
        try
        {
            Array.Copy(_lastStatuses, statuses, _lastStatuses.Length);
            Array.Copy(_positions, positions, _positions.Length);
            _snapshot = new SoemStatusSnapshot(DateTimeOffset.UtcNow, health, statuses[.._lastStatuses.Length].ToArray(), positions[.._positions.Length].ToArray(), cycleDuration, minCycle, maxCycle);
        }
        finally
        {
            ArrayPool<DriveStatus>.Shared.Return(statuses);
            ArrayPool<int>.Shared.Return(positions);
        }
    }

    private static DriveStatus DecodeStatus(in SoemShim.DriveTxPDO tx)
    {
        DriveStatus status = DriveStatus.None;

        if (tx.AmplifiersEnabled != 0) status |= DriveStatus.AmplifiersEnabled;
        if (tx.EndStop != 0) status |= DriveStatus.EndStop;
        if (tx.ThermalProtection1 != 0) status |= DriveStatus.ThermalProtection1;
        if (tx.ThermalProtection2 != 0) status |= DriveStatus.ThermalProtection2;
        if (tx.ForceZero != 0) status |= DriveStatus.ForceZero;
        if (tx.MotorOn != 0) status |= DriveStatus.MotorOn;
        if (tx.ClosedLoop != 0) status |= DriveStatus.ClosedLoop;
        if (tx.EncoderIndex != 0) status |= DriveStatus.EncoderAtIndex;
        if (tx.EncoderValid != 0) status |= DriveStatus.EncoderValid;
        if (tx.SearchingIndex != 0) status |= DriveStatus.SearchingIndex;
        if (tx.PositionReached != 0) status |= DriveStatus.PositionReached;
        if (tx.ErrorCompensation != 0) status |= DriveStatus.ErrorCompensation;
        if (tx.EncoderError != 0) status |= DriveStatus.EncoderError;
        if (tx.Scanning != 0) status |= DriveStatus.Scanning;
        if (tx.LeftEndStop != 0) status |= DriveStatus.LeftEndStop;
        if (tx.RightEndStop != 0) status |= DriveStatus.RightEndStop;
        if (tx.ErrorLimit != 0) status |= DriveStatus.ErrorLimit;
        if (tx.SearchingOptimalFrequency != 0) status |= DriveStatus.SearchingOptimalFrequency;
        if (tx.SafetyTimeout != 0) status |= DriveStatus.SafetyTimeout;
        if (tx.ExecuteAck != 0) status |= DriveStatus.ExecuteAck;
        if (tx.EmergencyStop != 0) status |= DriveStatus.EmergencyStop;
        if (tx.PositionFail != 0) status |= DriveStatus.PositionFail;

        return status;
    }

    private static bool TryDecodeError(DriveStatus status, out DriveError error)
    {
        if (status.HasFlag(DriveStatus.ErrorLimit))
        {
            error = new DriveError(DriveErrorCode.FollowError, "Following error exceeded limit.", "Reduce speed/acceleration; issue ENBL=1 to clear.");
            return true;
        }

        if (status.HasFlag(DriveStatus.PositionFail))
        {
            error = new DriveError(DriveErrorCode.PositionFail, "Position window not reached in time.", "Adjust PTO limits and retry after ENBL=1.");
            return true;
        }

        if (status.HasFlag(DriveStatus.SafetyTimeout))
        {
            error = new DriveError(DriveErrorCode.SafetyTimeout, "Safety timeout triggered.", "Issue ENBL=1 or RSET and review motion profile.");
            return true;
        }

        if (status.HasFlag(DriveStatus.EmergencyStop))
        {
            error = new DriveError(DriveErrorCode.EmergencyStop, "Emergency stop active.", "Investigate E-stop input; re-enable drive.");
            return true;
        }

        if (status.HasFlag(DriveStatus.EncoderError))
        {
            error = new DriveError(DriveErrorCode.EncoderError, "Encoder feedback invalid.", "Check encoder wiring; perform RSET then INDX.");
            return true;
        }

        if (status.HasFlag(DriveStatus.ThermalProtection1) || status.HasFlag(DriveStatus.ThermalProtection2))
        {
            error = new DriveError(DriveErrorCode.ThermalProtection, "Thermal protection active.", "Allow drive to cool; issue RSET once safe.");
            return true;
        }

        if (status.HasFlag(DriveStatus.EndStop) || status.HasFlag(DriveStatus.LeftEndStop) || status.HasFlag(DriveStatus.RightEndStop))
        {
            error = new DriveError(DriveErrorCode.EndStopHit, "End-stop detected during motion.", "Jog away from limit or reset limits before retry.");
            return true;
        }

        if (status.HasFlag(DriveStatus.ForceZero))
        {
            error = new DriveError(DriveErrorCode.ForceZero, "Force-zero active; output disabled.", "Issue ENBL=1 to clear before commanding motion.");
            return true;
        }

        if (status.HasFlag(DriveStatus.ErrorCompensation))
        {
            error = new DriveError(DriveErrorCode.ErrorCompensationFault, "Error compensation failure reported.", "Issue RSET then ENBL=1; review compensation tables.");
            return true;
        }

        error = new DriveError(DriveErrorCode.None, string.Empty, string.Empty);
        return false;
    }

    // replace existing RaiseFault with this throttled version
    private void RaiseFault(int slave, DriveStatus status, DriveError error, SoemHealthSnapshot health)
    {
        try
        {
            var idx = slave - 1;
            var now = DateTimeOffset.UtcNow;
            if (idx >= 0 && idx < _lastFaults.Length)
            {
                // Suppress identical, frequent repeats
                if (_lastFaults[idx] == error.Code && (now - _lastFaultTimes[idx]) < _faultRepeatInterval)
                {
                    _logger.LogDebug("Suppressing duplicate fault for slave {Slave} code={Code}.", slave, error.Code);
                    return;
                }

                _lastFaults[idx] = error.Code;
                _lastFaultTimes[idx] = now;
            }

            _logger.LogError("Slave {Slave} fault: {Error} status={Status:X}", slave, error, (uint)status);
            Faulted?.Invoke(this, new SoemFaultEvent(slave, status, error, health));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error while raising fault for slave {Slave}.", slave);
        }
    }

    private enum CommandCompletion
    {
        AckOnly,
        PositionReached,
        Indexed,
        Enabled,
        Disabled,
        Halt
    }

    private enum CommandState
    {
        Pending,
        Completed,
        TimedOut
    }

    private sealed class PendingCommand
    {
        private readonly TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private CancellationTokenSource? _cancellationSource;
        private readonly Stopwatch _stopwatch = new();
        private readonly CommandCompletion _completion;
        private readonly TimeSpan _timeout;

        private PendingCommand(int slaveIndex, string keyword, int parameter, int velocity, ushort acc, ushort dec, TimeSpan timeout, CommandCompletion completion, bool requiresAck)
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
        }

        public int SlaveIndex { get; }

        public string Keyword { get; }

        public int Parameter { get; }

        public int Velocity { get; }

        public ushort Acceleration { get; }

        public ushort Deceleration { get; }

        public bool RequiresAck { get; }

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

        //control completions don't require Acked
        public CommandState Evaluate(DriveStatus status, int actualPosition)
        {
            if (_timeout > TimeSpan.Zero && _stopwatch.Elapsed > _timeout)
            {
                return CommandState.TimedOut;
            }

            return _completion switch
            {
                CommandCompletion.AckOnly => Acked ? CommandState.Completed : CommandState.Pending,
                // motion/completion checks use status bits directly (don't require Acked)
                CommandCompletion.PositionReached => status.HasFlag(DriveStatus.PositionReached) ? CommandState.Completed : CommandState.Pending,
                CommandCompletion.Indexed => (status.HasFlag(DriveStatus.EncoderValid) && status.HasFlag(DriveStatus.PositionReached)) ? CommandState.Completed : CommandState.Pending,
                CommandCompletion.Enabled => (status.HasFlag(DriveStatus.AmplifiersEnabled) && status.HasFlag(DriveStatus.MotorOn)) ? CommandState.Completed : CommandState.Pending,
                CommandCompletion.Disabled => !status.HasFlag(DriveStatus.AmplifiersEnabled) ? CommandState.Completed : CommandState.Pending,
                CommandCompletion.Halt => !status.HasFlag(DriveStatus.Scanning) ? CommandState.Completed : CommandState.Pending,
                _ => CommandState.Pending,
            };
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

        public static PendingCommand CreateMotion(int slaveIndex, string keyword, int parameter, int velocity, ushort acc, ushort dec, TimeSpan timeout, CommandCompletion completion, bool requiresAck)
            => new(slaveIndex, keyword, parameter, velocity, acc, dec, timeout, completion, requiresAck);

        public static PendingCommand CreateControl(int slaveIndex, string keyword, int parameter, TimeSpan timeout, CommandCompletion completion)
            => new(slaveIndex, keyword, parameter, 0, 0, 0, timeout, completion, requiresAck: true);
    }
}
