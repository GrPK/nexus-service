using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmDataSensorsTests
{
    [Theory]
    [InlineData("GpuNvidia", "GPU Memory Used", "MiB")]
    [InlineData("GpuAmd", "D3D Shared Memory Total", "MiB")]
    [InlineData("GpuIntel", "D3D Dedicated Memory Used", "MiB")]
    [InlineData("Storage", "Total Space", "DecimalGb")]
    [InlineData("Storage", "Free Space", "DecimalGb")]
    [InlineData("Storage", "Data Read", "GiB")]
    [InlineData("Memory", "Memory Used", "GiB")]
    [InlineData("Network", "Data Downloaded", "GiB")]
    public void UnitFor_KeepsEachSensorsPreBytesUnit(string hardwareType, string name, string expected)
    {
        Assert.Equal(Enum.Parse<LegacyDataUnit>(expected), LhmDataSensors.UnitFor(hardwareType, name));
    }

    [Theory]
    [InlineData(8589934592f, "MiB", 8192f)]
    [InlineData(8589934592f, "GiB", 8f)]
    [InlineData(0f, "MiB", 0f)]
    [InlineData(0f, "GiB", 0f)]
    public void ToLegacyValue_ScalesBytesToMbOrGb(float bytes, string unit, float expected)
    {
        Assert.Equal(expected, LhmDataSensors.ToLegacyValue(bytes, Enum.Parse<LegacyDataUnit>(unit)));
    }

    [Fact]
    public void ToLegacyValue_StorageSpaceIsDecimalGb()
    {
        // Storage capacity reads in decimal gigabytes, not GiB.
        Assert.Equal(2000.3989f, LhmDataSensors.ToLegacyValue(2000398934016f, LegacyDataUnit.DecimalGb), 3);
    }

    [Theory]
    [InlineData("MiB", "SmallData", "MB")]
    [InlineData("GiB", "Data", "GB")]
    [InlineData("DecimalGb", "Data", "GB")]
    public void LegacyTypeAndUnits_MatchWireContract(string unit, string type, string units)
    {
        Assert.Equal(type, LhmDataSensors.ToLegacyType(Enum.Parse<LegacyDataUnit>(unit)));
        Assert.Equal(units, LhmDataSensors.ToLegacyUnits(Enum.Parse<LegacyDataUnit>(unit)));
    }

    [Theory]
    [InlineData(8192f, "MiB", "8192 MB")]
    [InlineData(7.5f, "GiB", "7.50 GB")]
    [InlineData(2000.3989f, "DecimalGb", "2000.40 GB")]
    public void Format_MatchesLegacyFormatting(float value, string unit, string expected)
    {
        Assert.Equal(expected, LhmDataSensors.Format(value, Enum.Parse<LegacyDataUnit>(unit)).Replace(',', '.'));
    }

    [Theory]
    [InlineData("/gpu-nvidia/0/data/1", "GpuNvidia", "GPU Memory Used", "/gpu-nvidia/0/smalldata/1")]
    [InlineData("/gpu-amd/0/data/12", "GpuAmd", "D3D Shared Memory Total", "/gpu-amd/0/smalldata/12")]
    [InlineData("/gpu-intel-dg/0/data/0", "GpuIntel", "GPU Memory Used", "/gpu-intel-dg/0/smalldata/0")]
    [InlineData("/ram/data/0", "Memory", "Memory Used", "/ram/data/0")]
    [InlineData("/nvme/0/data/32", "Storage", "Total Space", "/nvme/0/data/32")]
    [InlineData("/nic/%7BABC%7D/data/2", "Network", "Data Downloaded", "/nic/%7BABC%7D/data/2")]
    public void ToLegacyId_RestoresSmallDataSegmentForGpuMemoryOnly(string id, string hardwareType, string name, string expected)
    {
        Assert.Equal(expected, LhmDataSensors.ToLegacyId(id, LhmDataSensors.UnitFor(hardwareType, name)));
    }

    [Fact]
    public void BytesToMbAndGb_AreExactPowersOfTwo()
    {
        Assert.Equal(1f, LhmDataSensors.BytesToMb(1048576f));
        Assert.Equal(1f, LhmDataSensors.BytesToGb(1073741824f));
    }
}
