# Xeryon EtherCAT Drive Service

This repository delivers a production-grade EtherCAT client library and a WPF dashboard that targets Xeryon drives via the `soem_shim` native library. The managed core orchestrates PDO exchange through SOEM, enforces the Xeryon motion procedures, and exposes a high-level asynchronous API for motion, health monitoring, and automated recovery.

## Solution layout

```
XeryonEtherCAT.sln
├── XeryonEtherCAT.Core
│   ├── Abstractions/IEthercatDriveService.cs          # Public surface for applications
│   ├── Extensions/ServiceCollectionExtensions.cs      # DI registration helper
│   ├── Internal/Soem/                                 # P/Invoke shim + simulation backend
│   ├── Models/                                        # DriveState helpers, DriveError, status snapshots
│   ├── Options/EthercatDriveOptions.cs                # Cycle timing and recovery settings
│   └── Services/EthercatDriveService.cs               # Core implementation with IO loop + command queue
├── XeryonEtherCAT.App                                 # WPF dashboard that exercises the service
│   ├── Commands/AsyncRelayCommand.cs
│   ├── MainWindow.xaml (+ .cs)
│   └── ViewModels/*                                   # Dashboard view model and drive status rows
└── native/soemshim                                    # C shim around SOEM (see native/README)
```

The managed service depends on `soemshim` to communicate with the EtherCAT fieldbus. When the native library is not present the library falls back to an in-memory `SimulatedSoemClient` so that the UI and unit tests can run without hardware.

## Building the shim

1. Build SOEM from [upstream](https://github.com/OpenEtherCATsociety/SOEM) or install the headers/libraries from your package manager.
2. Build the shim from `native/soemshim`:

   ```bash
   cd native/soemshim
   cmake -B build -DSOEM_ROOT="/path/to/SOEM"
   cmake --build build --config Release
   ```

3. Copy the resulting `soemshim` library next to your application or into a directory that is part of the platform probing path. The managed service loads it via `[DllImport("soemshim")]`.

## Running the desktop dashboard

```bash
dotnet build
dotnet run --project XeryonEtherCAT.App
```

The WPF app boots the `EthercatDriveService` against the in-process simulation backend, rendering a live dashboard with per-slave status, position feedback, and common motion controls (enable/disable, DPOS, SCAN, INDX, HALT, STOP, RSET). Once the native shim is deployed the same UI can drive real hardware by swapping the simulated client with the default SOEM client.

### Real-time telemetry & logging

* Drive status updates are now streamed directly from the `StatusChanged` event; the dashboard updates the position and status mask for each slave as soon as a PDO changes.
* A dedicated event pane captures the last 200 status or fault transitions in chronological order.
* All logs produced by the core services are replayed inside the UI and can optionally be mirrored to a secondary Windows console window by toggling **Mirror to console**. The mirror uses `AllocConsole`/`FreeConsole`, demonstrating how to surface diagnostics in a detachable window.
* Faults automatically surface in both the event pane and the log stream, helping with rapid recovery.

### MQTT bridge

Both the console harness and the dashboard can host an optional MQTT bridge that relays drive telemetry to external clients and accepts high-level commands. Configure the broker host/port in the UI or via the new console menu option.

Topics follow the pattern (default `TopicRoot` is `xeryon/ethercat`):

* Status snapshots – `xeryon/ethercat/slaves/<slaveId>/status`

  ```json
  {
    "slave": 1,
    "timestamp": "2024-05-04T18:32:10.123Z",
    "command": "DPOS",
    "position": 120000,
    "positionChange": 1024,
    "current": { "actualPosition": 120000, "amplifiersEnabled": 1, ... },
    "previous": { ... }
  }
  ```

* Faults – `xeryon/ethercat/slaves/<slaveId>/faults`

  ```json
  {
    "slave": 1,
    "timestamp": "2024-05-04T18:35:02.441Z",
    "error": { "code": "PositionFail", "message": "Position error", "recovery": "Reset drive" },
    "status": { "actualPosition": 120000, ... }
  }
  ```

* Command ingress – `xeryon/ethercat/slaves/<slaveId>/commands`

  ```json
  {
    "command": "moveAbsolute",
    "targetPosition": 250000,
    "velocity": 40000,
    "acceleration": 1500,
    "deceleration": 1500,
    "settleTimeoutSeconds": 5
  }
  ```

* Command acknowledgements – `xeryon/ethercat/slaves/<slaveId>/commands/ack`

  ```json
  {
    "slave": 1,
    "command": "moveAbsolute",
    "success": true,
    "timestamp": "2024-05-04T18:35:12.021Z"
  }
  ```

Supported command keywords:

* `enable` (`{"command":"enable","enable":true}`)
* `reset`
* `halt`
* `stop`
* `moveAbsolute`
* `jog` (`direction` -1/0/1 plus optional `velocity`, `acceleration`, `deceleration`)
* `index` (`direction` 0/1)

Each command is executed through the high-level service API and publishes an acknowledgement message regardless of success, including the error text when something fails.

Use the console harness' menu option **11) Toggle MQTT bridge** or the WPF UI controls to start/stop the bridge. Once connected all status/fault events are relayed while the application continues to operate normally.

## Working with `IEthercatDriveService`

```csharp
var options = new EthercatDriveOptions
{
    CyclePeriod = TimeSpan.FromMilliseconds(2)
};

await using IEthercatDriveService service = new EthercatDriveService(options);
await service.InitializeAsync("eth1", CancellationToken.None);

await service.EnableAsync(1, true, CancellationToken.None);
await service.MoveAbsoluteAsync(1, targetPos: 120_000, vel: 40_000, acc: 1200, dec: 1200,
    settleTimeout: TimeSpan.FromSeconds(5), CancellationToken.None);
```

The service offers:

* A dedicated IO loop that exchanges PDOs via `soem_exchange_process_data` at a configurable cycle time (1–2 ms by default).
* A per-slave command queue that stages `DriveRxPDO` frames before the next exchange and observes the Execute/Ack handshake.
* Strongly typed motion helpers (`MoveAbsoluteAsync`, `JogAsync`, `IndexAsync`, `EnableAsync`, `HaltAsync`, `StopAsync`, `ResetAsync`) that implement the Xeryon PDF procedures, including settle timeouts and status-bit validation.
* Robust WKC validation, link-loss detection, automated recovery (`soem_try_recover` + re-initialisation), and draining of the SOEM error sink into structured logs.
* Rich telemetry via `SoemStatusSnapshot` plus a `Faulted` event that surfaces decoded `DriveError` classifications and recommended recovery actions.

For dependency injection scenarios call `services.AddEthercatDriveService()` and resolve `IEthercatDriveService` from the container.

## Native interop contract

The P/Invoke layer binds directly to the `soem_shim` exports:

```csharp
[DllImport("soemshim")]
internal static extern IntPtr soem_initialize(string ifname);
[DllImport("soemshim")]
internal static extern int soem_write_rxpdo(IntPtr h, int slaveIndex, ref DriveRxPDO pdo);
// ... see Internal/Soem/SoemShim.cs for the full list
```

`DriveRxPDO` and `DriveTxPDO` mirror the PDO layout defined by Xeryon. The managed service always populates the 32-byte command field with ASCII keywords (`DPOS`, `SCAN`, `INDX`, `ENBL`, `RSET`, `HALT`, `STOP`) padded with NUL characters.

## Health monitoring & recovery

During every IO cycle the service:

* Calls `soem_get_health` to capture `group_expected_wkc`, `last_wkc`, slave count, and AL status codes.
* Marks the cycle as degraded when the WKC drops below the expected value and attempts recovery once a strike threshold is exceeded.
* Drains the SOEM error list via `soem_drain_error_list_r` and logs the result.
* Decodes the TX PDO status bits into friendly `DriveStateFormatter` helpers and maps error conditions to the high-level `DriveErrorCode` enumeration (FollowError, SafetyTimeout, PositionFail, E-Stop, EncoderError, ThermalProtection, EndStopHit, ForceZero, ErrorCompensationFault, UnknownFault).

Faults raise a `SoemFaultEvent` that contains the offending slave, the raw status bits, the decoded error, and the last health snapshot—callers can react by issuing `ResetAsync`/`EnableAsync` or by adjusting motion profiles.

## Simulation backend

`SimulatedSoemClient` implements `ISoemClient` and mimics the SOEM shim so that command packing, status decoding, and the dashboard can be exercised without real hardware. It understands the same command keywords and toggles the ExecuteAck/PositionReached bits to emulate drive behaviour.

Switch between the simulation and the native shim by supplying a different `ISoemClient` instance to `EthercatDriveService`.

## Lean Linux build of `soemshim`

The `native/soemshim-linux` folder contains a trimmed CMake build that targets lightweight Docker containers. It depends on SOEM headers/libraries plus `libpcap` (the Linux counterpart to Npcap). See the included README for dependency notes, build commands, and a sample Dockerfile snippet.

## License

This project is distributed under the MIT license. The SOEM project retains its own licensing terms—consult the upstream repository when redistributing the shim or bundling SOEM binaries.
