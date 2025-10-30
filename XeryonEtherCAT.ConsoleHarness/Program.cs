using Microsoft.Extensions.Logging;
using XeryonEtherCAT.Core.Internal.Soem;
using XeryonEtherCAT.Core.Models;
using XeryonEtherCAT.Core.Options;
using XeryonEtherCAT.Core.Services;
using XeryonEtherCAT.Core.Utilities;
using XeryonEtherCAT.Integrations.Mqtt;

public class Program
{
    public static async Task Main(string[] args)
    {
        using var loggerFactory = LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Information);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
        });

        await using var harness = new Harness(loggerFactory);
        await harness.RunAsync();
    }
}

internal sealed class Harness : IAsyncDisposable
{
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private ILoggerFactory? _serviceLoggerFactory;


    private readonly EthercatDriveOptions _options = new();
    private readonly ISoemClient _soemClient;
    private readonly AsyncEventQueue<ConsoleMessage> _eventQueue;
    private readonly EthercatMqttBridgeOptions _mqttOptions = new();

    private EthercatDriveService? _service;
    private string? _interfaceName;
    private EthercatMqttBridge? _mqttBridge;

    public Harness(ILoggerFactory loggerFactory)
    {
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger("Harness");
        _soemClient = new SoemClient(loggerFactory.CreateLogger("SoemClient"));
        _eventQueue = new AsyncEventQueue<ConsoleMessage>(message =>
        {
            if (message.Color is { } color)
            {
                var original = Console.ForegroundColor;
                Console.ForegroundColor = color;
                Console.WriteLine(message.Message);
                Console.ForegroundColor = original;
            }
            else
            {
                Console.WriteLine(message.Message);
            }

            return ValueTask.CompletedTask;
        });
    }

    public async Task RunAsync()
    {
        var exit = false;
        while (!exit)
        {
            PrintMenu();
            Console.Write("Select option: ");
            var choice = (Console.ReadLine() ?? string.Empty).Trim();
            Console.WriteLine();

            try
            {
                switch (choice)
                {
                    case "1":
                        _soemClient.ListNetworkAdapterNames();
                        break;
                    case "2":
                        await InitializeAsync().ConfigureAwait(false);
                        break;
                    case "3":
                        await ShutdownAsync().ConfigureAwait(false);
                        break;
                    case "4":
                        await ShowStatusAsync().ConfigureAwait(false);
                        break;
                    case "5":
                        await RunSoakTestAsync().ConfigureAwait(false);
                        break;
                    case "6":
                        await ResetAndEnableAsync().ConfigureAwait(false);
                        break;
                    case "7":
                        await CableDisconnectDrillAsync().ConfigureAwait(false);
                        break;
                    case "8":
                        await SendRawCommandAsync().ConfigureAwait(false);
                        break;
                    case "9":
                        await DemonstrateCommandQueueAsync().ConfigureAwait(false);
                        break;
                    case "10":
                        await RunRecoveryWorkflowAsync().ConfigureAwait(false);
                        break;
                    case "11":
                        await ToggleMqttBridgeAsync().ConfigureAwait(false);
                        break;
                    case "0":
                        exit = true;
                        break;
                    default:
                        Console.WriteLine("Unknown selection. Please choose an item from the menu.");
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                Console.WriteLine("Operation canceled.");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Operation failed");
            }

            Console.WriteLine();
        }

        await ShutdownAsync().ConfigureAwait(false);
    }

    public async ValueTask DisposeAsync()
    {
        await ShutdownAsync().ConfigureAwait(false);
        _soemClient.Dispose();
        await _eventQueue.DisposeAsync().ConfigureAwait(false);
    }

    private void PrintMenu()
    {
        Console.WriteLine("==== Xeryon EtherCAT Service Harness ====");
        Console.WriteLine(" 1) List network adapters");
        Console.WriteLine(" 2) Initialize EtherCAT session");
        Console.WriteLine(" 3) Shutdown session");
        Console.WriteLine(" 4) Query slave devices / status snapshot");
        Console.WriteLine(" 5) Run process data soak test");
        Console.WriteLine(" 6) Issue Reset and Enable");
        Console.WriteLine(" 7) Cable disconnect / re-init drill");
        Console.WriteLine(" 8) Send raw command frame");
        Console.WriteLine(" 9) Demonstrate command queueing");
        Console.WriteLine("10) Reset / homing / recovery workflow");
        Console.WriteLine("11) Toggle MQTT bridge");
        Console.WriteLine(" 0) Exit");
    }

    private async Task InitializeAsync()
    {
        if (_service is not null)
        {
            Console.WriteLine($"Service already initialized on {_interfaceName}. Use option 3 to shut it down first.");
            return;
        }

        Console.Write("Network interface (e.g. eth0): ");
        var iface = (Console.ReadLine() ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(iface))
        {
            Console.WriteLine("Interface name is required.");
            return;
        }

        Console.Write("Cycle period in ms (default 2): ");
        if (double.TryParse(Console.ReadLine(), out var ms) && ms > 0)
        {
            _options.CyclePeriod = TimeSpan.FromMilliseconds(ms);
        }

        Console.Write("Enable verbose cycle trace logging? (y/N): ");
        var trace = (Console.ReadLine() ?? string.Empty).Trim();
        _options.EnableCycleTraceLogging = trace.Equals("y", StringComparison.OrdinalIgnoreCase);


        // Create a dedicated logger factory for the service so we can set Trace/Info per user's choice
        _serviceLoggerFactory = LoggerFactory.Create(builder =>
        {
            // Service gets Trace if user asked for verbose; otherwise Information
            builder.SetMinimumLevel(_options.EnableCycleTraceLogging ? LogLevel.Trace : LogLevel.Debug);
            builder.AddSimpleConsole(options =>
            {
                options.SingleLine = true;
                options.TimestampFormat = "HH:mm:ss.fff ";
            });
        });
        _service = new EthercatDriveService(_options, _serviceLoggerFactory.CreateLogger<EthercatDriveService>(), _soemClient);
        _service.Faulted += OnFaulted;
        _service.StatusChanged += OnStatusChanged;

        await _service.InitializeAsync(iface, CancellationToken.None).ConfigureAwait(false);
        _interfaceName = iface;
        var slaves = await _service.GetSlaveCountAsync().ConfigureAwait(false);
        Console.WriteLine($"Initialized on {iface} with {slaves} slave(s).");
    }

    private async Task ShutdownAsync()
    {
        if (_service is null)
        {
            return;
        }

        await _service.DisposeAsync().ConfigureAwait(false);
        _service.Faulted -= OnFaulted;
        _service.StatusChanged -= OnStatusChanged;
        _service = null;
        _interfaceName = null;
        Console.WriteLine("Service shut down.");

        if (_mqttBridge is not null)
        {
            await _mqttBridge.DisposeAsync().ConfigureAwait(false);
            _mqttBridge = null;
        }
    }

    private void OnFaulted(object? sender, SoemFaultEvent e)
    {
        var status = DriveStateFormatter.DriveTxPdoToHexString(e.Status);
        _logger.LogError("Fault reported for slave {Slave}: {Error}, txPDO: {rawHex}", e.Slave, e.Error, status);
        var message = $"Fault detected on slave {e.Slave}: {e.Error.Code} - {e.Error.Message} (tx={status})";
        _eventQueue.TryEnqueue(new ConsoleMessage(message, ConsoleColor.Red));
    }

    private void OnStatusChanged(object? sender, DriveStatusChangeEvent e)
    {
        _eventQueue.TryEnqueue(new ConsoleMessage(e.ToString(), ConsoleColor.DarkGray));
    }

    private readonly record struct ConsoleMessage(string Message, ConsoleColor? Color);

    private async Task ToggleMqttBridgeAsync()
    {
        if (_service is null)
        {
            Console.WriteLine("Initialize the EtherCAT service before starting the MQTT bridge.");
            return;
        }

        if (_mqttBridge is null)
        {
            Console.Write($"MQTT host (default {_mqttOptions.BrokerHost}): ");
            var hostInput = (Console.ReadLine() ?? string.Empty).Trim();
            if (!string.IsNullOrWhiteSpace(hostInput))
            {
                _mqttOptions.BrokerHost = hostInput;
            }

            Console.Write($"MQTT port (default {_mqttOptions.BrokerPort}): ");
            if (int.TryParse(Console.ReadLine(), out var port) && port > 0)
            {
                _mqttOptions.BrokerPort = port;
            }

            var logger = (_serviceLoggerFactory ?? _loggerFactory).CreateLogger<EthercatMqttBridge>();
            var bridge = new EthercatMqttBridge(_service, _mqttOptions, logger);

            try
            {
                await bridge.StartAsync(CancellationToken.None).ConfigureAwait(false);
                _mqttBridge = bridge;
                Console.WriteLine($"MQTT bridge connected to {_mqttOptions.BrokerHost}:{_mqttOptions.BrokerPort}.");
            }
            catch (Exception ex)
            {
                await bridge.DisposeAsync().ConfigureAwait(false);
                _logger.LogError(ex, "Failed to connect MQTT bridge");
            }
        }
        else
        {
            await _mqttBridge.StopAsync(CancellationToken.None).ConfigureAwait(false);
            await _mqttBridge.DisposeAsync().ConfigureAwait(false);
            _mqttBridge = null;
            Console.WriteLine("MQTT bridge disconnected.");
        }
    }

    private async Task ShowStatusAsync()
    {
        var service = RequireService();
        var count = await service.GetSlaveCountAsync().ConfigureAwait(false);
        var snapshot = service.GetStatus();

        Console.WriteLine($"Slaves: {count}, Operational: {snapshot.Health.SlavesOperational}, WKC: {snapshot.Health.LastWkc}/{snapshot.Health.GroupExpectedWkc}");
        Console.WriteLine($"IO bytes: out={snapshot.Health.BytesOut} in={snapshot.Health.BytesIn}");
        Console.WriteLine($"Cycle: last={snapshot.CycleTime.TotalMilliseconds:F2} ms min={snapshot.MinCycleTime.TotalMilliseconds:F2} ms max={snapshot.MaxCycleTime.TotalMilliseconds:F2} ms");

        for (var i = 0; i < snapshot.DriveStates.Length; i++)
        {
            var status = snapshot.DriveStates[i];
            var mask = DriveStateFormatter.ToBitMask(status);
            var hex = DriveStateFormatter.DriveTxPdoToHexString(status);
            Console.WriteLine($"Slave {i + 1}: {hex} [{DriveStateFormatter.Describe(status)}]");
        }
    }

    private async Task RunSoakTestAsync()
    {
        var service = RequireService();
        var count = await service.GetSlaveCountAsync().ConfigureAwait(false);
        Console.Write("Duration in minutes (default 1): ");
        var durationInput = Console.ReadLine();
        var duration = TimeSpan.FromMinutes(double.TryParse(durationInput, out var mins) && mins > 0 ? mins : 1);
        Console.WriteLine($"Running soak test for {duration.TotalMinutes:F1} minute(s). Press Ctrl+C to abort.");

        using var cts = new CancellationTokenSource();
        ConsoleCancelEventHandler? handler = null;
        handler = (_, e) =>
        {
            e.Cancel = true;
            cts.Cancel();
        };
        Console.CancelKeyPress += handler;

        var end = DateTimeOffset.UtcNow + duration;
        SoemStatusSnapshot? lastSnapshot = null;
        try
        {
            int cycle = 0;
            while (DateTimeOffset.UtcNow < end && !cts.IsCancellationRequested)
            {
                var snapshot = service.GetStatus();
                var health = snapshot.Health;
                var wkcHealthy = health.LastWkc >= health.GroupExpectedWkc;

                // Log full health snapshot every cycle
                Console.WriteLine($"[{DateTimeOffset.UtcNow:HH:mm:ss}] Cycle {cycle++}: WKC {health.LastWkc}/{health.GroupExpectedWkc} OP {health.SlavesOperational}/{count} | Bytes out={health.BytesOut} in={health.BytesIn} AL={health.AlStatusCode:X}");
                if (cycle == 1 || lastSnapshot == null ||
                    health.LastWkc != lastSnapshot.Health.LastWkc ||
                    health.GroupExpectedWkc != lastSnapshot.Health.GroupExpectedWkc ||
                    health.SlavesOperational != lastSnapshot.Health.SlavesOperational)
                {
                    Console.WriteLine($"  [DEBUG] Health: Found={health.SlavesFound} OP={health.SlavesOperational} WKC={health.LastWkc}/{health.GroupExpectedWkc} BytesOut={health.BytesOut} BytesIn={health.BytesIn} AL={health.AlStatusCode:X}");
                }

                for (var i = 0; i < snapshot.DriveStates.Length; i++)
                {
                    var status = snapshot.DriveStates[i];
                    var hexString = DriveStateFormatter.ToHexString(status);
                    Console.WriteLine($"  Slave {i + 1}: pos: {status.ActualPosition} status: {hexString}");
                }

                if (!wkcHealthy)
                {
                    Console.WriteLine("[WARN] Work counter below expected threshold. Ending soak test.");
                    break;
                }

                lastSnapshot = snapshot;
                await Task.Delay(TimeSpan.FromSeconds(1), cts.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            Console.WriteLine("[INFO] Soak test canceled by user.");
        }
        finally
        {
            if (handler is not null)
            {
                Console.CancelKeyPress -= handler;
            }
        }
    }

    private async Task ResetAndEnableAsync()
    {
        var service = RequireService();
        var count = await service.GetSlaveCountAsync().ConfigureAwait(false);
        var axis = ReadInt("Slave number", 1);
        Console.WriteLine($"Running ResetAsync on slave {axis}...");

        try
        {
            using var resetCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.ResetAsync(axis, resetCts.Token).ConfigureAwait(false);

            await ShowStatusAsync();
            Console.WriteLine("Reset complete, press enter to enable...");
            Console.ReadLine();

            using var enableCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await service.EnableAsync(axis, true, enableCts.Token).ConfigureAwait(false);

            using var indexCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await ShowStatusAsync();
            Console.WriteLine("Enabled, press enter to index...");
            Console.ReadLine();
            await service.IndexAsync(axis, 1, 10000, 10000, 10000, TimeSpan.FromSeconds(10), indexCts.Token).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Recovery command failed for slave {Slave}", axis);
        }

        Console.WriteLine("Recovery sequence complete. Observe status via option 4.");
    }

    private async Task CableDisconnectDrillAsync()
    {
        var service = RequireService();
        var count = await service.GetSlaveCountAsync().ConfigureAwait(false);
        Console.WriteLine("Disconnect the EtherCAT cable now, then press Enter to start monitoring.");
        Console.ReadLine();

        SoemStatusSnapshot snapshot;
        while (true)
        {
            snapshot = service.GetStatus();
            if (snapshot.Health.LastWkc < snapshot.Health.GroupExpectedWkc || snapshot.Health.SlavesOperational < count)
            {
                Console.WriteLine($"Link loss detected (WKC {snapshot.Health.LastWkc}/{snapshot.Health.GroupExpectedWkc}, OP {snapshot.Health.SlavesOperational}/{count}).");
                break;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(200)).ConfigureAwait(false);
        }

        Console.WriteLine("Reconnect the cable and press Enter once link is restored.");
        Console.ReadLine();

        Console.WriteLine("Waiting for service to re-initialize...");
        var timeout = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(30);
        while (DateTimeOffset.UtcNow < timeout)
        {
            snapshot = service.GetStatus();
            if (snapshot.Health.LastWkc >= snapshot.Health.GroupExpectedWkc && snapshot.Health.SlavesOperational == count)
            {
                Console.WriteLine("Link restored and process data healthy.");
                return;
            }

            await Task.Delay(TimeSpan.FromMilliseconds(500)).ConfigureAwait(false);
        }

        Console.WriteLine("Timed out waiting for clean re-initialization.");
    }

    private async Task SendRawCommandAsync()
    {
        var service = RequireService();
        var slave = ReadInt("Target slave", 1);
        Console.Write("Command keyword (4 ASCII chars): ");
        var keyword = (Console.ReadLine() ?? string.Empty).Trim();
        if (keyword.Length == 0)
        {
            Console.WriteLine("Command keyword is required.");
            return;
        }

        var parameter = ReadInt("Parameter", 0);
        var velocity = ReadInt("Velocity", 0);
        var acceleration = (ushort)Math.Clamp(ReadInt("Acceleration", 0), 0, ushort.MaxValue);
        var deceleration = (ushort)Math.Clamp(ReadInt("Deceleration", 0), 0, ushort.MaxValue);
        Console.Write("Require ACK? (Y/n): ");
        var ackInput = (Console.ReadLine() ?? string.Empty).Trim();
        var requiresAck = !ackInput.Equals("n", StringComparison.OrdinalIgnoreCase);
        var timeoutSeconds = ReadInt("Timeout seconds (0 = default)", 0);
        var timeout = timeoutSeconds > 0 ? TimeSpan.FromSeconds(timeoutSeconds) : (TimeSpan?)null;

        using var cts = CreateCancellation(timeout ?? TimeSpan.FromSeconds(15));
        await service.SendRawCommandAsync(slave, keyword, parameter, velocity, acceleration, deceleration, requiresAck, timeout, cts.Token).ConfigureAwait(false);
        Console.WriteLine("Command dispatched successfully.");
    }

    private async Task DemonstrateCommandQueueAsync()
    {
        var service = RequireService();
        var slave = ReadInt("Slave for queue demo", 1);
        var basePosition = ReadInt("Base position", 0);
        var step = ReadInt("Step delta", 10000);
        var velocity = ReadInt("Velocity", 50000);
        var acc = (ushort)Math.Clamp(ReadInt("Acceleration", 1000), 0, ushort.MaxValue);
        var dec = (ushort)Math.Clamp(ReadInt("Deceleration", 1000), 0, ushort.MaxValue);
        var moves = Math.Clamp(ReadInt("Number of queued moves", 3), 1, 10);
        Console.WriteLine("Queueing commands concurrently...");

        var tasks = new List<Task>();
        for (var i = 0; i < moves; i++)
        {
            var target = basePosition + (i * step);
            tasks.Add(Task.Run(async () =>
            {
                using var cts = CreateCancellation(TimeSpan.FromSeconds(30));
                await service.MoveAbsoluteAsync(slave, target, velocity, acc, dec, TimeSpan.FromSeconds(5), cts.Token).ConfigureAwait(false);
                Console.WriteLine($"Move to {target} completed.");
            }));
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);
        Console.WriteLine("Queue demonstration complete.");
    }

    private async Task RunRecoveryWorkflowAsync()
    {
        var service = RequireService();
        var slave = ReadInt("Slave to recover", 1);
        Console.WriteLine("Beginning reset -> enable -> homing sequence...");

        using var cts = CreateCancellation(TimeSpan.FromSeconds(60));
        await service.ResetAsync(slave, cts.Token).ConfigureAwait(false);
        await service.EnableAsync(slave, true, cts.Token).ConfigureAwait(false);
        await service.IndexAsync(slave, 1, ReadInt("Homing velocity", 25000), (ushort)Math.Clamp(ReadInt("Homing acceleration", 1000), 0, ushort.MaxValue), (ushort)Math.Clamp(ReadInt("Homing deceleration", 1000), 0, ushort.MaxValue), TimeSpan.FromSeconds(20), cts.Token).ConfigureAwait(false);
        Console.WriteLine("Homing complete. Optional absolute move follows.");

        if (AskYesNo("Execute absolute move? (y/N): "))
        {
            var position = ReadInt("Target position", 0);
            var velocity = ReadInt("Velocity", 50000);
            var acceleration = (ushort)Math.Clamp(ReadInt("Acceleration", 2000), 0, ushort.MaxValue);
            var deceleration = (ushort)Math.Clamp(ReadInt("Deceleration", 2000), 0, ushort.MaxValue);
            await service.MoveAbsoluteAsync(slave, position, velocity, acceleration, deceleration, TimeSpan.FromSeconds(10), cts.Token).ConfigureAwait(false);
            Console.WriteLine("Move complete.");
        }

        if (AskYesNo("Issue STOP latch? (y/N): "))
        {
            await service.StopAsync(slave, cts.Token).ConfigureAwait(false);
            Console.WriteLine("STOP latched. Use ENBL=1 to clear before motion.");
        }
    }

    private EthercatDriveService RequireService()
    {
        if (_service is null)
        {
            throw new InvalidOperationException("Service is not initialized. Use option 2 first.");
        }

        return _service;
    }

    private static CancellationTokenSource CreateCancellation(TimeSpan timeout)
        => timeout > TimeSpan.Zero ? new CancellationTokenSource(timeout) : new CancellationTokenSource();

    private static int ReadInt(string prompt, int defaultValue)
    {
        Console.Write($"{prompt} [{defaultValue}]: ");
        var input = Console.ReadLine();
        if (int.TryParse(input, out var value))
        {
            return value;
        }

        return defaultValue;
    }

    private static bool AskYesNo(string prompt)
    {
        Console.Write(prompt);
        var input = (Console.ReadLine() ?? string.Empty).Trim();
        return input.Equals("y", StringComparison.OrdinalIgnoreCase);
    }

}

