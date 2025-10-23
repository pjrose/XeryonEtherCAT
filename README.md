# Xeryon EtherCAT test utility

This repository contains a multi-axis motion test harness for Xeryon EtherCAT drives. It is composed of three .NET 8 projects:

* **XeryonEtherCAT.Core** – shared domain types such as axis configuration, commands, and status flags.
* **XeryonEtherCAT.Ethercat** – a high level service that wraps the open-source [EC_Net](https://www.nuget.org/packages/EC_Net/) master (MIT licensed) to discover adapters, manage connectivity, and expose strongly-typed axis operations.
* **XeryonEtherCAT.App** – a Windows Presentation Foundation (WPF) desktop client that allows an operator to drive up to sixteen axes while monitoring connection health.

## Building

The solution targets .NET 8.0. From the repository root run:

```bash
dotnet build
```

## Running the GUI

The WPF client lives in `XeryonEtherCAT.App` and must be launched on Windows:

```bash
dotnet run --project XeryonEtherCAT.App
```

On startup the app enumerates available NICs, allows selection of the EtherCAT adapter, and displays four axis cards. Each card mirrors the per-axis drive controls that were provided in the design brief – including editable speed/step/target values, motion buttons, and live status feedback.

## EtherCAT service

`XeryonEthercatService` exposes a simple API that can be consumed by other applications:

```csharp
var service = new XeryonEthercatService();
var axes = new[]
{
    new AxisConfiguration(1, "Drive #1"),
    new AxisConfiguration(2, "Drive #2"),
    new AxisConfiguration(3, "Drive #3"),
    new AxisConfiguration(4, "Drive #4"),
};

await service.ConnectAsync("eth0", axes);
await service.SendCommandAsync("Drive #1", new AxisCommand("MOVE", targetPosition: 100_000));
```

The service automatically polls process data, publishes connection and axis status events, and performs limited self-recovery when link loss is detected.

## License

All original code in this repository is released under the MIT license. The EC_Net package that provides the underlying EtherCAT bindings is also MIT licensed.
