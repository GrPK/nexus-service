using Nexus.Service.Routes;

namespace Nexus.Service.Tests.Lighting.CustomEffects;

public class CustomEffectTelemetryTests
{
    [Theory]
    [InlineData("animate", "custom:abc123")]
    [InlineData("animate", " Custom:ABC123 ")]
    [InlineData("static", "custom:abc123")]
    public void A_custom_key_reports_as_custom_and_never_leaks_the_id(string mode, string effect)
    {
        var (resolvedMode, slug) = LightingRoutes.ResolveEffect(mode, effect);

        Assert.Equal(mode, resolvedMode);
        Assert.Equal("custom", slug);
    }

    [Fact]
    public void Built_in_keys_still_report_their_own_slug()
    {
        Assert.Equal(("animate", "plasma"), LightingRoutes.ResolveEffect("animate", "plasma"));
    }
}
