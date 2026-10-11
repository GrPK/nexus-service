namespace Nexus.Service.Sensors;

/// <summary>The unit each LHM Data sensor reported before LHM switched them to bytes; the wire contract keeps it.</summary>
internal enum LegacyDataUnit
{
    GiB,
    /// <summary>GPU memory, exposed as type "SmallData" with "/smalldata/" ids.</summary>
    MiB,
    /// <summary>Storage Total/Free Space, in decimal gigabytes.</summary>
    DecimalGb,
}

/// <summary>
/// Maps LHM's byte-valued Data sensors back to the legacy wire contract. Kept
/// in its own file with no LibreHardwareMonitor types so it compiles and tests
/// on every platform.
/// </summary>
internal static class LhmDataSensors
{
    private const float BytesPerMb = 1024f * 1024f;
    private const float BytesPerGb = 1024f * 1024f * 1024f;
    private const float BytesPerDecimalGb = 1e9f;
    private const string DataSegment = "/data/";
    private const string SmallDataSegment = "/smalldata/";

    /// <summary><paramref name="hardwareType"/> is LHM's HardwareType name.</summary>
    internal static LegacyDataUnit UnitFor(string hardwareType, string sensorName) => hardwareType switch
    {
        "GpuNvidia" or "GpuAmd" or "GpuIntel" => LegacyDataUnit.MiB,
        "Storage" when sensorName is "Total Space" or "Free Space" => LegacyDataUnit.DecimalGb,
        _ => LegacyDataUnit.GiB,
    };

    internal static float BytesToMb(float bytes) => bytes / BytesPerMb;

    internal static float BytesToGb(float bytes) => bytes / BytesPerGb;

    internal static float ToLegacyValue(float bytes, LegacyDataUnit unit) => unit switch
    {
        LegacyDataUnit.MiB => BytesToMb(bytes),
        LegacyDataUnit.DecimalGb => bytes / BytesPerDecimalGb,
        _ => BytesToGb(bytes),
    };

    internal static string ToLegacyType(LegacyDataUnit unit) => unit == LegacyDataUnit.MiB ? "SmallData" : "Data";

    internal static string ToLegacyUnits(LegacyDataUnit unit) => unit == LegacyDataUnit.MiB ? "MB" : "GB";

    internal static string Format(float legacyValue, LegacyDataUnit unit) =>
        unit == LegacyDataUnit.MiB ? $"{legacyValue:F0} MB" : $"{legacyValue:F2} GB";

    /// <summary>Restores the pre-bytes sensor id: LHM embeds the lowercased sensor type in it.</summary>
    internal static string ToLegacyId(string sensorId, LegacyDataUnit unit)
    {
        if (unit != LegacyDataUnit.MiB) return sensorId;
        var at = sensorId.LastIndexOf(DataSegment, StringComparison.Ordinal);
        return at < 0
            ? sensorId
            : string.Concat(sensorId.AsSpan(0, at), SmallDataSegment, sensorId.AsSpan(at + DataSegment.Length));
    }
}
