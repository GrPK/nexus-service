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

    [Theory]
    [InlineData("software", true)]
    [InlineData(" Software ", true)]
    [InlineData("auto", false)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsSoftwareChoice_matches_only_the_software_value(string? renderGpu, bool expected)
    {
        Assert.Equal(expected, GpuBackend.IsSoftwareChoice(renderGpu));
    }

    [Theory]
    [InlineData(null, true, true)]
    [InlineData("angle", true, true)]
    [InlineData("wgl", true, false)]
    [InlineData("glfw", true, false)]
    [InlineData(null, false, false)]
    [InlineData("angle", false, false)]
    public void SoftwareRenderingAvailable_needs_the_angle_backend(
        string? backend, bool angleAvailable, bool expected)
    {
        Assert.Equal(expected, GpuBackend.SoftwareRenderingAvailable(backend, angleAvailable));
    }
}
