using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.Routes;

public sealed class OverlayRoutesSizeTests
{
    [Theory]
    [InlineData("4x1")]
    [InlineData("4x2")]
    [InlineData("2x4")]
    public void NormalizeSize_KeepsKnownSizes(string size)
    {
        Assert.Equal(size, OverlayRoutes.NormalizeSize(size));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("3x3")]
    public void NormalizeSize_FallsBackToTwoByTwo(string? size)
    {
        Assert.Equal("2x2", OverlayRoutes.NormalizeSize(size));
    }
}
