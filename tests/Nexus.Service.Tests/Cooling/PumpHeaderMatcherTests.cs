using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Cooling;
using Nexus.Service.Models.Cooling;
using Xunit;

namespace Nexus.Service.Tests.Cooling;

/// <summary>
/// Readings come from a bench rig whose Tryx Panorama pump is wired to CPU_FAN: the header's
/// tach as LibreHardwareMonitor reports it against the pump speed the panel reports over USB.
/// </summary>
public class PumpHeaderMatcherTests
{
    private const string CpuFan = "/lpc/nct6687dr/0/control/0";
    private const string ChipsetFan = "/lpc/nct6687dr/0/control/2";

    private static List<FanChannel> Channels(int cpuRpm, int chipsetRpm = 855) => new()
    {
        new FanChannel { Id = CpuFan, Name = "CPU Fan", Rpm = cpuRpm },
        new FanChannel { Id = "/lpc/nct6687dr/0/control/1", Name = "Pump Fan #1", Kind = FanKinds.Pump, Rpm = 0 },
        new FanChannel { Id = ChipsetFan, Name = "Chipset Fan", Rpm = chipsetRpm },
        new FanChannel { Id = "tryx:fan", Name = "VRM Fan", Rpm = 2850, DeviceId = "tryx" },
    };

    private static string NameOf(List<FanChannel> channels, string id) => channels.Single(c => c.Id == id).Name;

    [Fact]
    public void Labels_the_header_that_tracked_three_changes_of_the_pump_speed()
    {
        var matcher = new PumpHeaderMatcher();
        matcher.Label(Channels(2912), 2910);
        matcher.Label(Channels(2884), 2880);
        var channels = Channels(2830);

        matcher.Label(channels, 2850);

        Assert.Equal("CPU Fan / Pump", NameOf(channels, CpuFan));
        Assert.Equal("Chipset Fan", NameOf(channels, ChipsetFan));
    }

    [Fact]
    public void Counts_only_changes_of_the_reported_speed_as_evidence()
    {
        var matcher = new PumpHeaderMatcher();
        for (var i = 0; i < 5; i++) matcher.Label(Channels(2912), 2910);

        var channels = Channels(2912);
        matcher.Label(channels, 2910);

        Assert.Equal("CPU Fan", NameOf(channels, CpuFan));
    }

    [Fact]
    public void Leaves_two_headers_at_the_pump_speed_alone()
    {
        var matcher = new PumpHeaderMatcher();
        var channels = Channels(0);
        foreach (var rpm in new[] { 2910, 2880, 2850, 2790 })
        {
            channels = Channels(rpm, chipsetRpm: rpm);
            matcher.Label(channels, rpm);
        }

        Assert.Equal("CPU Fan", NameOf(channels, CpuFan));
        Assert.Equal("Chipset Fan", NameOf(channels, ChipsetFan));
    }

    [Fact]
    public void Never_labels_a_device_channel()
    {
        var matcher = new PumpHeaderMatcher();
        var channels = Channels(0);
        foreach (var rpm in new[] { 2860, 2840, 2820, 2800 })
        {
            // Only the VRM fan (a device channel, 2850 rpm) sits near the pump speed.
            channels = Channels(cpuRpm: 1200);
            matcher.Label(channels, rpm);
        }

        Assert.Equal("VRM Fan", NameOf(channels, "tryx:fan"));
        Assert.Equal("CPU Fan", NameOf(channels, CpuFan));
    }

    [Fact]
    public void Keeps_the_label_through_one_stray_reading_and_drops_it_once_the_pump_is_elsewhere()
    {
        var matcher = new PumpHeaderMatcher();
        matcher.Label(Channels(2912), 2910);
        matcher.Label(Channels(2884), 2880);
        matcher.Label(Channels(2830), 2850);

        // The pump moved between the two samples, so this reading misses by more than the slack.
        var stray = Channels(2900);
        matcher.Label(stray, 2640);
        Assert.Equal("CPU Fan / Pump", NameOf(stray, CpuFan));

        var channels = Channels(0);
        foreach (var rpm in new[] { 2610, 2580, 2550, 2520, 2490 })
        {
            channels = Channels(cpuRpm: 1200);
            matcher.Label(channels, rpm);
        }
        Assert.Equal("CPU Fan", NameOf(channels, CpuFan));
    }

    [Fact]
    public void Keeps_the_label_while_another_header_ramps_through_the_pump_speed()
    {
        var matcher = new PumpHeaderMatcher();
        matcher.Label(Channels(2912), 2910);
        matcher.Label(Channels(2884), 2880);
        matcher.Label(Channels(2830), 2850);

        // The chipset fan sits inside the pump's band for more readings than it takes to forget.
        var channels = Channels(0);
        foreach (var rpm in new[] { 2820, 2790, 2760, 2730, 2700, 2670 })
        {
            channels = Channels(cpuRpm: rpm + 10, chipsetRpm: rpm - 20);
            matcher.Label(channels, rpm);
        }

        Assert.Equal("CPU Fan / Pump", NameOf(channels, CpuFan));
        Assert.Equal("Chipset Fan", NameOf(channels, ChipsetFan));
    }

    [Fact]
    public void Labels_a_channel_object_a_provider_hands_back_again_only_once()
    {
        var matcher = new PumpHeaderMatcher();
        var channels = Channels(2900);
        foreach (var rpm in new[] { 2910, 2880, 2850, 2820 })
        {
            channels[0].Rpm = rpm;
            matcher.Label(channels, rpm);
        }

        Assert.Equal("CPU Fan / Pump", NameOf(channels, CpuFan));
    }
}
