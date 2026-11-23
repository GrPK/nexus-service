using System.Collections.Generic;
using Nexus.Service.Devices;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>The 8.8 bezel follows its own Nexus Control switch, not the glass's, and OpenRGB keeps it while that is off.</summary>
public class UniversalScreen88LedGateTests
{
    private const string Glass = UniversalScreen88Driver.Id;
    private const string Ring = UniversalScreen88LedProtocol.HandlerId;
    private const string RingDetector = "Lian Li Universal Screen";

    private static DeviceControlGate Gate(IEnumerable<string> on, IEnumerable<string>? off = null)
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Devices.NexusControlEnabled = new List<string>(on);
            s.Devices.NexusControlDisabled = new List<string>(off ?? new List<string>());
        });
        return new DeviceControlGate(store);
    }

    private static List<string> HeldOff(DeviceControlGate gate) =>
        NativeDetectorClaims.ResolveDisabled(handler => gate.IsChosenOn(handler), macOS: false);

    [Fact]
    public void The_ring_has_its_own_switch_apart_from_the_glass()
    {
        Assert.NotEqual(Glass, Ring);
        Assert.Equal(Ring, Nexus.Service.Lighting.UniversalScreen88LedLightingDeviceProvider.HandlerId);
        // Like every Lian Li native driver, it competes with L-Connect and starts off.
        Assert.False(DeviceControlPolicy.DefaultOn(Ring));
    }

    [Fact]
    public void Glass_off_and_ring_on_drives_the_ring_natively()
    {
        // The user's set: the glass stays a plain Windows monitor.
        var gate = Gate(new[] { "lianli-wireless", "tryx", "lianli-hydroshift2-curve", Ring });
        Assert.False(gate.IsEnabled(Glass));
        Assert.True(UniversalScreen88LedBoard.ShouldDrive(gate));
        Assert.Contains(RingDetector, HeldOff(gate));
    }

    [Fact]
    public void Glass_on_alone_leaves_the_ring_to_OpenRGB()
    {
        var gate = Gate(new[] { Glass });
        Assert.False(UniversalScreen88LedBoard.ShouldDrive(gate));
        Assert.DoesNotContain(RingDetector, HeldOff(gate));
    }

    [Fact]
    public void Ring_switched_off_by_the_user_is_not_driven_and_OpenRGB_keeps_it()
    {
        var gate = Gate(new[] { Glass }, off: new[] { Ring });
        Assert.False(UniversalScreen88LedBoard.ShouldDrive(gate));
        Assert.DoesNotContain(RingDetector, HeldOff(gate));
    }

    [Fact]
    public void Other_Lian_Li_claims_still_hold_OpenRGB_off_while_their_switch_is_off()
    {
        // geretti's rule for the other native Lian Li drivers is unchanged: off means L-Connect's, not OpenRGB's.
        var gate = Gate(new string[0]);
        Assert.Contains("Lian Li Strimer L Connect", HeldOff(gate));
    }
}
