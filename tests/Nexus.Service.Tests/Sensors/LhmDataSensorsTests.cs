using Nexus.Service.Sensors;

namespace Nexus.Service.Tests.Sensors;

public class LhmDataSensorsTests
{
    [Theory]
    [InlineData(8589934592f, true, 8192f)]
    [InlineData(8589934592f, false, 8f)]
    [InlineData(0f, true, 0f)]
    [InlineData(0f, false, 0f)]
    public void ToLegacyValue_ScalesBytesToMbOrGb(float bytes, bool smallData, float expected)
    {
        Assert.Equal(expected, LhmDataSensors.ToLegacyValue(bytes, smallData));
    }

    [Theory]
    [InlineData(true, "SmallData", "MB")]
    [InlineData(false, "Data", "GB")]
    public void LegacyTypeAndUnits_MatchWireContract(bool smallData, string type, string units)
    {
        Assert.Equal(type, LhmDataSensors.ToLegacyType(smallData));
        Assert.Equal(units, LhmDataSensors.ToLegacyUnits(smallData));
    }

    [Theory]
    [InlineData(8192f, true, "8192 MB")]
    [InlineData(7.5f, false, "7.50 GB")]
    public void Format_MatchesLegacyFormatting(float value, bool smallData, string expected)
    {
        Assert.Equal(expected, LhmDataSensors.Format(value, smallData).Replace(',', '.'));
    }

    [Theory]
    [InlineData("/gpu-nvidia/0/data/1", true, "/gpu-nvidia/0/smalldata/1")]
    [InlineData("/gpu-amd/0/data/12", true, "/gpu-amd/0/smalldata/12")]
    [InlineData("/gpu-intel-dg/0/data/0", true, "/gpu-intel-dg/0/smalldata/0")]
    [InlineData("/ram/data/0", false, "/ram/data/0")]
    [InlineData("/nic/%7BABC%7D/data/2", false, "/nic/%7BABC%7D/data/2")]
    [InlineData("/gpu-nvidia/0/load/0", true, "/gpu-nvidia/0/load/0")]
    public void ToLegacyId_RestoresSmallDataSegmentOnly(string id, bool smallData, string expected)
    {
        Assert.Equal(expected, LhmDataSensors.ToLegacyId(id, smallData));
    }

    [Fact]
    public void BytesToMbAndGb_AreExactPowersOfTwo()
    {
        Assert.Equal(1f, LhmDataSensors.BytesToMb(1048576f));
        Assert.Equal(1f, LhmDataSensors.BytesToGb(1073741824f));
    }
}
