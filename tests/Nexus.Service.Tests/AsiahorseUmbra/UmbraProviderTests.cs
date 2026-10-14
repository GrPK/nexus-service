using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Lighting;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Peripherals.AsiahorseUmbra;
using Xunit;

namespace Nexus.Service.Tests.AsiahorseUmbra;

public class UmbraProviderTests
{
    private readonly InMemoryConfigStore _store = new();
    private readonly UmbraHub _hub = new();
    private readonly UmbraDeviceFake _fake = new();

    private void Connect(params (int port, int leds)[] ports)
    {
        foreach (var (port, leds) in ports)
        {
            _fake.Ports[port][0] = leds;
        }
        _hub.Attach(_fake);
        Assert.True(_hub.Connect());
    }

    // ── Lighting zones ──

    [Fact]
    public void Nexus_source_publishes_one_zone_per_port_with_leds()
    {
        Connect((0, 8), (4, 12), (9, 6));
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);

        var devices = provider.GetAll().Devices;

        Assert.Equal(new[] { "asiahorse-umbra:port:1", "asiahorse-umbra:port:5", "asiahorse-umbra:port:10" }, devices.Select(d => d.Id));
        Assert.Equal(new[] { 8, 12, 6 }, devices.Select(d => d.LedCount));
        Assert.All(devices, d => Assert.False(d.ZoneResizable));
        Assert.All(devices, d => Assert.Equal("asiahorse-umbra", d.ParentDeviceId));
        Assert.Empty(provider.GetAll().DevicePageDevices);
    }

    [Theory]
    [InlineData("firmware")]
    [InlineData("argbInput")]
    public void Other_sources_publish_no_zones_structures_or_frames(string source)
    {
        Connect((0, 8));
        _store.Update(s => s.Devices.AsiahorseUmbra.Source = source);
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);

        Assert.Empty(provider.GetAll().Devices);
        Assert.Empty(provider.GetStructures());
        Assert.Empty(provider.BuildFrames(0));
        var entry = Assert.Single(provider.GetAll().DevicePageDevices);
        Assert.Equal("asiahorse-umbra", entry.HandlerId);
        Assert.Equal(source, entry.Reason);
    }

    [Fact]
    public void Switching_between_device_page_sources_raises_DevicesChanged()
    {
        Connect((0, 8));
        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "firmware");
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);
        var changed = 0;
        provider.DevicesChanged += () => changed++;
        provider.OnHubStateUpdated();
        changed = 0;

        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "argbInput");
        provider.OnHubStateUpdated();

        Assert.Equal(1, changed);
    }

    [Fact]
    public void A_disconnected_device_publishes_nothing()
    {
        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "firmware");
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);

        Assert.Empty(provider.GetAll().Devices);
        Assert.Empty(provider.GetAll().DevicePageDevices);
        Assert.Empty(provider.GetStructures());
        Assert.Empty(provider.BuildFrames(0));
    }

    [Fact]
    public void Frames_follow_the_device_reported_counts_in_port_order()
    {
        Connect((2, 20), (7, 30));
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);

        var frames = provider.BuildFrames(5);

        Assert.Equal(new[] { "asiahorse-umbra:port:3", "asiahorse-umbra:port:8" }, frames.Select(f => f.Id));
        Assert.Equal(new[] { 20, 30 }, frames.Select(f => f.LedCount));
        Assert.Equal(new[] { 5, 6 }, frames.Select(f => f.Index));
    }

    [Fact]
    public void A_reconnect_reuses_the_frames_that_already_hold_rendered_colors()
    {
        Connect((0, 8));
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);
        var before = Assert.Single(provider.BuildFrames(0));

        _hub.Detach();
        Assert.Empty(provider.BuildFrames(0));
        _hub.Attach(new UmbraDeviceFake { Ports = { [0] = new[] { 8, 0, 1, 0, 0 } } });
        Assert.True(_hub.Connect());

        Assert.Same(before, Assert.Single(provider.BuildFrames(0)));
    }

    [Fact]
    public void A_port_resized_to_zero_gets_a_fresh_frame_when_it_returns()
    {
        Connect((0, 8), (1, 8));
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);
        var before = provider.BuildFrames(0).Single(f => f.Id.EndsWith(":2"));

        _fake.Ports[1][0] = 0;
        _hub.RefreshPorts();
        Assert.Single(provider.BuildFrames(0));
        _fake.Ports[1][0] = 8;
        _hub.RefreshPorts();

        Assert.NotSame(before, provider.BuildFrames(0).Single(f => f.Id.EndsWith(":2")));
    }

    [Fact]
    public void Structures_are_single_fixed_segments_that_cannot_be_partitioned()
    {
        Connect((0, 8));
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);

        var structure = Assert.Single(provider.GetStructures());

        Assert.False(structure.Partitionable);
        var segment = Assert.Single(structure.Segments);
        Assert.False(segment.Resizable);
        Assert.Equal(120, segment.MaxLedCount);
        Assert.Equal(8, segment.LedCount);
    }

    [Fact]
    public void SetZoneLedCount_writes_nothing_because_a_count_change_restarts_the_controller()
    {
        Connect((0, 8));
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);
        _fake.Writes.Clear();

        provider.SetZoneLedCount("asiahorse-umbra:port:1", 24);

        Assert.Empty(_fake.Writes);
        Assert.Equal(8, _fake.Ports[0][0]);
    }

    [Fact]
    public void Switching_the_source_away_from_nexus_raises_DevicesChanged()
    {
        Connect((0, 8));
        var provider = new AsiahorseUmbraLightingDeviceProvider(_hub, _store);
        var changed = 0;
        provider.DevicesChanged += () => changed++;
        provider.OnHubStateUpdated();
        changed = 0;

        _store.Update(s => s.Devices.AsiahorseUmbra.Source = "firmware");
        provider.OnHubStateUpdated();

        Assert.Equal(1, changed);
        Assert.Empty(provider.GetAll().Devices);
    }

    // ── Cooling channel ──

    [Fact]
    public void Fan_channel_exists_while_fans_do_not_follow_the_motherboard()
    {
        Connect();
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);

        var channel = Assert.Single(cooling.GetFanChannels());

        Assert.Equal("asiahorse-umbra:fan", channel.Id);
        Assert.True(channel.RpmUnavailable);
        Assert.Single(cooling.GetAll());
    }

    [Fact]
    public void Fan_channel_is_withheld_while_fans_follow_the_motherboard()
    {
        Connect();
        _store.Update(s => s.Devices.AsiahorseUmbra.FanSyncMb = true);
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);

        Assert.Empty(cooling.GetFanChannels());
        Assert.Empty(cooling.GetAll());
    }

    [Fact]
    public void No_fan_channel_without_a_connection()
    {
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);

        Assert.Empty(cooling.GetFanChannels());
    }

    [Fact]
    public void SetFanSpeed_clamps_and_writes_the_duty()
    {
        Connect();
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);
        _fake.Writes.Clear();

        var result = cooling.SetFanSpeed("asiahorse-umbra:fan", 150);

        Assert.Equal(100, result);
        Assert.Equal(new byte[] { 0xFB, 100 }, _fake.Writes.Single()[4..6]);
        Assert.Equal(100, cooling.Duty);
        Assert.Equal(FanModes.Manual, cooling.GetFanChannels().Single().Mode);
    }

    [Fact]
    public void A_write_is_dropped_while_fans_follow_the_motherboard()
    {
        Connect();
        _store.Update(s => s.Devices.AsiahorseUmbra.FanSyncMb = true);
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);
        _fake.Writes.Clear();

        cooling.SetFanSpeed("asiahorse-umbra:fan", 60);

        Assert.Empty(_fake.Writes);
    }

    [Theory]
    [InlineData(false, 0x01)]
    [InlineData(true, 0x00)]
    public void ApplyFanSync_writes_the_persisted_mode(bool syncMb, int wireByte)
    {
        Connect();
        _store.Update(s => s.Devices.AsiahorseUmbra.FanSyncMb = syncMb);
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);
        _fake.Writes.Clear();

        Assert.True(cooling.ApplyFanSync());

        Assert.Equal(new byte[] { 0x08, (byte)wireByte }, _fake.Writes.Single()[4..6]);
    }

    [Fact]
    public void Reassert_resends_the_duty_after_a_reconnect()
    {
        Connect();
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);
        cooling.SetFanSpeed("asiahorse-umbra:fan", 40);
        _hub.Detach();
        var replacement = new UmbraDeviceFake();
        _hub.Attach(replacement);
        Assert.True(_hub.Connect());
        replacement.Writes.Clear();

        cooling.OnConnected();

        Assert.Equal(new byte[] { 0x08, 0xFB }, replacement.Writes.Select(w => w[4]).ToArray());
        Assert.Equal(40, replacement.Writes[1][5]);
    }

    [Fact]
    public void Release_returns_the_channel_to_auto_and_stops_reasserting()
    {
        Connect();
        var cooling = new AsiahorseUmbraCoolingProvider(_hub, _store);
        cooling.SetFanSpeed("asiahorse-umbra:fan", 40);
        cooling.ReleaseFan("asiahorse-umbra:fan");
        _fake.Writes.Clear();

        cooling.ReassertControl();

        Assert.Empty(_fake.Writes);
        Assert.Equal(FanModes.Auto, cooling.GetFanChannels().Single().Mode);
    }
}
