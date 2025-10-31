using System;
using System.Diagnostics;

namespace XeryonEtherCAT.Core.Utilities;

/// <summary>
/// Provides a monotonic clock for correlating telemetry across processes.
/// </summary>
public static class TelemetrySync
{
    /// <summary>
    /// Gets the frequency of the monotonic clock ticks per second.
    /// </summary>
    public static long TimestampFrequency => Stopwatch.Frequency;

    /// <summary>
    /// Gets the current monotonic timestamp in high-resolution ticks.
    /// </summary>
    public static long GetTimestampTicks() => Stopwatch.GetTimestamp();

    /// <summary>
    /// Converts high-resolution ticks into a <see cref="TimeSpan"/>.
    /// </summary>
    public static TimeSpan ToTimeSpan(long ticks) =>
        TimeSpan.FromSeconds(ticks / (double)Stopwatch.Frequency);

    /// <summary>
    /// Converts high-resolution ticks into seconds.
    /// </summary>
    public static double ToSeconds(long ticks) => ticks / (double)Stopwatch.Frequency;
}
