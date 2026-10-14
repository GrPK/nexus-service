using System;
using System.IO;
using Nexus.Service.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

public sealed class AdlxInitGuardTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"adlx-guard-test-{Guid.NewGuid():N}");

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void A_first_start_is_not_latched() =>
        Assert.False(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));

    [Fact]
    public void An_init_that_returned_leaves_no_latch()
    {
        AdlxInitGuard.Arm(_dir, "1.5.0.124@157");
        AdlxInitGuard.Disarm(_dir);

        Assert.False(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
    }

    [Fact]
    public void A_run_that_died_inside_init_latches_every_later_start_with_the_same_key()
    {
        AdlxInitGuard.Arm(_dir, "1.5.0.124@157");

        Assert.True(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
        Assert.True(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
    }

    [Theory]
    [InlineData("1.5.0.124@158")] // reboot
    [InlineData("1.6.0.10@157")]  // driver update
    public void A_new_key_clears_the_latch(string next)
    {
        AdlxInitGuard.Arm(_dir, "1.5.0.124@157");
        Assert.True(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));

        Assert.False(AdlxInitGuard.Latched(_dir, next));
        Assert.False(AdlxInitGuard.Latched(_dir, "1.5.0.124@157"));
    }
}
