namespace XeryonEtherCAT.Core;

/// <summary>
/// Helper methods for converting between engineering units and device counts.
/// </summary>
public static class AxisUnitConverter
{
    public static int ToDeviceCounts(double engineeringUnits, AxisConfiguration configuration)
    {
        return (int)System.Math.Round(engineeringUnits * configuration.CountsPerUnit, System.MidpointRounding.AwayFromZero);
    }

    public static double ToEngineeringUnits(int deviceCounts, AxisConfiguration configuration)
    {
        return deviceCounts / configuration.CountsPerUnit;
    }
}
