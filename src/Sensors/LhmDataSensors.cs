namespace Nexus.Service.Sensors;

/// <summary>
/// Maps LHM's byte-valued Data sensors back to the legacy wire contract:
/// GPU memory sensors stay type "SmallData" in MB with "/smalldata/" ids,
/// every other Data sensor stays "Data" in GB. Kept in its own file with no
/// LibreHardwareMonitor types so it compiles and tests on every platform.
/// </summary>
internal static class LhmDataSensors
{
    private const float BytesPerMb = 1024f * 1024f;
    private const float BytesPerGb = 1024f * 1024f * 1024f;
    private const string DataSegment = "/data/";
    private const string SmallDataSegment = "/smalldata/";

    internal static float BytesToMb(float bytes) => bytes / BytesPerMb;

    internal static float BytesToGb(float bytes) => bytes / BytesPerGb;

    internal static float ToLegacyValue(float bytes, bool smallData) =>
        smallData ? BytesToMb(bytes) : BytesToGb(bytes);

    internal static string ToLegacyType(bool smallData) => smallData ? "SmallData" : "Data";

    internal static string ToLegacyUnits(bool smallData) => smallData ? "MB" : "GB";

    internal static string Format(float legacyValue, bool smallData) =>
        smallData ? $"{legacyValue:F0} MB" : $"{legacyValue:F2} GB";

    /// <summary>Restores the pre-bytes sensor id: LHM embeds the lowercased sensor type in it.</summary>
    internal static string ToLegacyId(string sensorId, bool smallData)
    {
        if (!smallData) return sensorId;
        var at = sensorId.LastIndexOf(DataSegment, StringComparison.Ordinal);
        return at < 0
            ? sensorId
            : string.Concat(sensorId.AsSpan(0, at), SmallDataSegment, sensorId.AsSpan(at + DataSegment.Length));
    }
}
