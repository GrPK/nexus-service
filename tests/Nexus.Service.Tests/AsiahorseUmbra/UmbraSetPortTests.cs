using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Nexus.Service.Routes;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

public class UmbraSetPortTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    private static (UmbraHub hub, UmbraDeviceFake fake) Connected()
    {
        var fake = new UmbraDeviceFake();
        fake.Ports[2][0] = 6;
        var hub = new UmbraHub();
        hub.Attach(fake);
        Assert.True(hub.Connect());
        fake.Writes.Clear();
        return (hub, fake);
    }

    private static async Task WaitUntilRestarting(UmbraHub hub)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!hub.IsRestarting && DateTime.UtcNow < deadline)
        {
            await Task.Delay(5);
        }
        Assert.True(hub.IsRestarting);
    }

    private static UmbraDeviceFake Reconnect(UmbraHub hub, int port, int liveCount)
    {
        var replacement = new UmbraDeviceFake();
        replacement.Ports[port][0] = liveCount;
        hub.Detach();
        hub.Attach(replacement);
        Assert.True(hub.Connect());
        replacement.Writes.Clear();
        return replacement;
    }

    [Fact]
    public async Task A_count_writes_the_override_then_restarts_and_rereads_the_ports()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            var task = AsiahorseUmbraRouteLogic.SetPortAsync(hub, 2, new UmbraPortRequest { LedCount = 10 }, Timeout, CancellationToken.None);
            await WaitUntilRestarting(hub);

            Assert.Equal(new byte[] { 0x02, 0xFC }, fake.Writes.Select(w => w[4]).ToArray());
            Assert.Equal(new byte[] { 2, 0, 10 }, fake.Writes[0][5..8]);

            var replacement = Reconnect(hub, 2, 10);
            var (status, error) = await task;

            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Null(error);
            Assert.Equal(new byte[] { 0x01 }, replacement.Writes.Select(w => w[4]).ToArray());
            Assert.Equal(10, hub.Snapshot.Ports[2].LedCount);
        }
    }

    [Fact]
    public async Task An_ignored_count_is_cleared_without_another_restart()
    {
        var (hub, _) = Connected();
        using (hub)
        {
            var task = AsiahorseUmbraRouteLogic.SetPortAsync(hub, 2, new UmbraPortRequest { LedCount = 3 }, Timeout, CancellationToken.None);
            await WaitUntilRestarting(hub);

            var replacement = Reconnect(hub, 2, 6);
            var (status, _) = await task;

            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.Equal(new byte[] { 0x01, 0x02, 0x01 }, replacement.Writes.Select(w => w[4]).ToArray());
            Assert.Equal(new byte[] { 2, 0, 0 }, replacement.Writes[1][5..8]);
            Assert.DoesNotContain(replacement.Writes, w => w[4] == 0xFC);
        }
    }

    [Fact]
    public async Task An_accepted_count_is_not_cleared()
    {
        var (hub, _) = Connected();
        using (hub)
        {
            var task = AsiahorseUmbraRouteLogic.SetPortAsync(hub, 2, new UmbraPortRequest { LedCount = 10 }, Timeout, CancellationToken.None);
            await WaitUntilRestarting(hub);
            var replacement = Reconnect(hub, 2, 10);
            await task;

            Assert.DoesNotContain(replacement.Writes, w => w[4] == 0x02);
        }
    }

    [Fact]
    public async Task A_second_restart_is_a_conflict_and_writes_nothing()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            var first = hub.RestartAsync(Timeout, CancellationToken.None);
            fake.Writes.Clear();

            var (status, _) = await AsiahorseUmbraRouteLogic.SetPortAsync(hub, 2, new UmbraPortRequest { LedCount = 10 }, Timeout, CancellationToken.None);

            Assert.Equal(StatusCodes.Status409Conflict, status);
            Assert.Empty(fake.Writes);
            Reconnect(hub, 2, 6);
            await first;
        }
    }

    [Fact]
    public async Task A_device_that_does_not_return_is_a_gateway_timeout_and_a_retry_is_not_busy()
    {
        var (hub, _) = Connected();
        using (hub)
        {
            var (status, _) = await AsiahorseUmbraRouteLogic.SetPortAsync(hub, 2, new UmbraPortRequest { LedCount = 10 }, TimeSpan.FromMilliseconds(50), CancellationToken.None);

            Assert.Equal(StatusCodes.Status504GatewayTimeout, status);
            Assert.False(hub.IsRestarting);
            Assert.NotEqual(UmbraRestartResult.Busy, await hub.RestartAsync(TimeSpan.FromMilliseconds(50), CancellationToken.None));
        }
    }

    [Fact]
    public async Task A_count_combined_with_another_field_is_rejected_before_any_write()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            var (status, error) = await AsiahorseUmbraRouteLogic.SetPortAsync(hub, 2, new UmbraPortRequest { LedCount = 10, Order = 3 }, Timeout, CancellationToken.None);

            Assert.Equal(StatusCodes.Status400BadRequest, status);
            Assert.NotNull(error);
            Assert.Empty(fake.Writes);
        }
    }

    [Fact]
    public async Task Direction_and_order_apply_immediately_without_a_restart()
    {
        var (hub, fake) = Connected();
        using (hub)
        {
            var (status, _) = await AsiahorseUmbraRouteLogic.SetPortAsync(hub, 2, new UmbraPortRequest { Direction = "reverse", Order = 4 }, Timeout, CancellationToken.None);

            Assert.Equal(StatusCodes.Status200OK, status);
            Assert.DoesNotContain(fake.Writes, w => w[4] == 0xFC);
            Assert.Equal(new byte[] { 0x02, 0x01, 0x02, 0x01 }, fake.Writes.Select(w => w[4]).ToArray());
        }
    }
}
