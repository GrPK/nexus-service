using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GpuBackendTests
{
    [Theory]
    [InlineData(null, true, "angle")]
    [InlineData("", true, "angle")]
    [InlineData("angle", true, "angle")]
    [InlineData("ANGLE", true, "angle")]
    [InlineData("wgl", true, "wgl")]
    [InlineData("glfw", true, "glfw")]
    [InlineData("nonsense", true, "angle")]
    [InlineData(null, false, "wgl")]
    [InlineData("angle", false, "wgl")]
    [InlineData("glfw", false, "glfw")]
    public void Resolve_honors_explicit_fallbacks_and_defaults_to_angle_only_where_it_ships(
        string? requested, bool angleAvailable, string expected)
    {
        Assert.Equal(expected, GpuBackend.Resolve(requested, angleAvailable));
    }
}
