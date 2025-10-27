# Xeryon EtherCAT Test Utility

This repository contains a multi-axis control service and a desktop test harness for Xeryon piezo stages that communicate over EtherCAT. The solution is organised in two projects:

- `XeryonEtherCAT.Core` – a reusable library that wraps a SOEM-based EtherCAT master, exposes fault-tolerant services for discovering drives, managing connections, queueing motion commands, and tracking drive state.
- `XeryonEtherCAT.App` – an Avalonia UI that demonstrates the service with a four-axis dashboard inspired by the serial tooling layout. The UI runs against a simulation master by default so that it can be exercised without hardware.

The EtherCAT interaction is handled through the open-source [Simple Open EtherCAT Master (SOEM)](https://github.com/OpenEtherCATsociety/SOEM) project. A lightweight native shim (`native/soemshim`) exposes the small subset of SOEM functionality that is required by the C# service. When the shim is not available, the managed service automatically falls back to an in-memory simulator so that the rest of the stack remains testable.

## Solution layout

```
XeryonEtherCAT.sln
├── XeryonEtherCAT.Core
│   ├── Abstractions/IEtherCatMaster.cs
│   ├── Internal/Simulation/SimulatedEtherCatMaster.cs
│   ├── Internal/Soem/SoemEtherCatMaster.cs
│   ├── Models/… (axis state, PDO frames, connection events)
│   ├── Options/XeryonEtherCatOptions.cs
│   └── Services/XeryonEtherCatService.cs
├── XeryonEtherCAT.App
│   ├── App.axaml / App.axaml.cs
│   ├── MainWindow.axaml / MainWindow.axaml.cs
│   ├── ViewModels/… (MVVM wrappers around the core service)
│   └── Views/AxisControl.axaml (Axis control surface)
└── native/soemshim
    ├── soem_shim.c (minimal C wrapper around SOEM)
    └── CMakeLists.txt (build instructions for the shim)
```

## Building the SOEM shim

1. Install the SOEM headers and libraries from the upstream project (or build them from source).
2. Build the shim library in `native/soemshim`:

   ```bash
   cd native/soemshim
   cmake -B build -DSOEM_ROOT="/path/to/SOEM"
   cmake --build build --config Release
   ```

   The build produces `libsoemshim.so` (Linux), `libsoemshim.dylib` (macOS), or `soemshim.dll` (Windows).
3. Place the resulting library on the native probing path of your application (for example next to the executable or in `/usr/local/lib`).

When the shim cannot be located at runtime, the managed code falls back to the simulator so that the UI remains usable. To talk to real hardware, make sure the shim is available and configure the `NetworkInterfaceName` in `XeryonEtherCatOptions` to match the NIC that is wired into the EtherCAT network.

## Running the sample UI

1. Restore dependencies and build the solution:

   ```bash
   dotnet build
   ```

2. Launch the Avalonia desktop app:

   ```bash
   dotnet run --project XeryonEtherCAT.App
   ```

   The dashboard displays four axes, mirroring the order specified in the ESI. Each axis card exposes:

   - Speed control, with Stop/Reset buttons.
   - Step controls for incremental motion.
   - Absolute target entry and “Go To Zero” helper.
   - Live status flags and absolute encoder position.

   When running against real hardware the UI receives live position feedback and status bits via SOEM. In simulation mode the same experience is driven by the managed `SimulatedEtherCatMaster`.

## Working with `XeryonEtherCatService`

`XeryonEtherCatService` encapsulates the EtherCAT session lifecycle:

- Discover drives on a NIC and build a nameable axis catalogue.
- Maintain a cyclic task that exchanges PDOs at the requested cycle time.
- Queue commands per axis and translate them into the PDO layout defined in the ESI.
- Surface `AxisStatusChanged` and `ConnectionStateChanged` events so that higher-level applications can react to status bits, position feedback, timeouts, and reconnects.

A minimal usage example:

```csharp
var options = new XeryonEtherCatOptions
{
    NetworkInterfaceName = "eth1",
    CycleTime = TimeSpan.FromMilliseconds(20)
};
options.AxisNames[1] = "X";
options.AxisNames[2] = "Y";

await using var service = new XeryonEtherCatService(options);
await service.StartAsync(CancellationToken.None);

await service.EnqueueCommandAsync(1, XeryonAxisCommand.MoveTo(100_000, 2_000, 200, 200), CancellationToken.None);
```

The service automatically reconnects after interruptions when `AutoReconnect` is enabled. Axis commands can be queued by number or by the configured display name. Status updates, including the full set of flags listed in the ESI, are exposed through `XeryonAxis.Status`.

## Next steps

- Replace the simulator instance in `MainWindowViewModel` with the real `SoemEtherCatMaster` once the shim is deployed and the machine is connected to the EtherCAT network.
- Extend `AxisViewModel` to show additional telemetry (slots, encoder validity, end-stop states) or to execute more advanced motion sequences.
- Integrate the core service into automation workflows by consuming the events and command helpers exposed in `XeryonEtherCAT.Core`.
