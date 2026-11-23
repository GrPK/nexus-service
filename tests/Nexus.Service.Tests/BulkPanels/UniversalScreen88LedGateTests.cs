using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Conflicts;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Handlers;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Tests.Cloud;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>
/// The 8.8 is one device, like the OLED Curved: its one Nexus Control switch drives the glass and the bezel ring,
/// the ring works while the glass is a Windows monitor, and OpenRGB keeps the ring while the switch is off.
/// </summary>
public class UniversalScreen88LedGateTests
{
    private const string Screen = UniversalScreen88Driver.Id;
    private const string RetiredRing = UniversalScreen88ControlMigration.RetiredRingId;
    private const string RingDetector = "Lian Li Universal Screen";

    private static readonly UsbDeviceEntry Panel = Usb(UniversalScreen88Protocol.VendorId, UniversalScreen88Protocol.ProductId);
    private static readonly UsbDeviceEntry WindowsDisplay = Usb(UniversalScreen88Protocol.DesktopVendorId, UniversalScreen88Protocol.DesktopProductId);
    private static readonly UsbDeviceEntry LedBoard = Usb(UniversalScreen88LedProtocol.VendorId, UniversalScreen88LedProtocol.ProductId);

    private static UsbDeviceEntry Usb(int vid, int pid) => new() { VendorId = vid, ProductId = pid };

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

    private static BulkPanelHandler ScreenRow(BulkPanelHub hub) =>
        new(hub, BulkPanelHandler.CompanionsFor(hub.Driver));

    [Fact]
    public void The_ring_has_no_switch_of_its_own()
    {
        // The ring's lighting device id stays, so its card and saved layouts keep their keys; it is not a handler.
        Assert.Equal(RetiredRing, Nexus.Service.Lighting.UniversalScreen88LedLightingDeviceProvider.HandlerId);
        Assert.Null(DeviceControlPolicy.ConflictAppFor(RetiredRing));
        // Like every Lian Li native driver, the 8.8 competes with L-Connect and starts off.
        Assert.False(DeviceControlPolicy.DefaultOn(Screen));
    }

    [Fact]
    public void The_screen_on_drives_the_ring_natively()
    {
        var gate = Gate(new[] { "lianli-wireless", "tryx", "lianli-hydroshift2-curve", Screen });
        Assert.True(UniversalScreen88LedBoard.ShouldDrive(gate));
        Assert.Contains(RingDetector, HeldOff(gate));
    }

    [Fact]
    public void The_screen_never_chosen_leaves_the_ring_to_OpenRGB()
    {
        var gate = Gate(new string[0]);
        Assert.False(UniversalScreen88LedBoard.ShouldDrive(gate));
        Assert.DoesNotContain(RingDetector, HeldOff(gate));
    }

    [Fact]
    public void The_screen_switched_off_by_the_user_leaves_the_ring_to_OpenRGB()
    {
        var gate = Gate(new string[0], off: new[] { Screen });
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

    [Fact]
    public void L_Connect_competes_for_the_ring_through_the_screens_switch()
    {
        var lconnect = ConflictAppCatalog.All.Single(a => a.Id == "lian-li-l-connect");
        Assert.Contains(Screen, lconnect.AlsoHandlers);
        Assert.Contains(Screen, lconnect.DisplayHandlers);
        Assert.DoesNotContain(RetiredRing, lconnect.AlsoHandlers);
    }

    // ── one row, listed in every mode the glass can be in ──

    [Fact]
    public void The_glass_in_Windows_display_mode_keeps_the_row_connected_without_a_display_to_drive()
    {
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        var handler = ScreenRow(hub);
        var bus = new[] { WindowsDisplay, LedBoard };

        Assert.True(handler.IsConnected(bus));
        // Nexus has no panel there, so L-Connect is not a display conflict; the ring is the lighting one.
        Assert.False(handler.DrivesDisplay(bus));
    }

    [Fact]
    public void The_glass_in_Windows_display_mode_alone_keeps_the_row_connected()
    {
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        Assert.True(ScreenRow(hub).IsConnected(new[] { WindowsDisplay }));
    }

    [Fact]
    public void The_LED_board_alone_keeps_the_row_connected()
    {
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        var handler = ScreenRow(hub);

        Assert.True(handler.IsConnected(new[] { LedBoard }));
        Assert.False(handler.DrivesDisplay(new[] { LedBoard }));
    }

    [Fact]
    public void The_glass_as_a_panel_drives_its_display()
    {
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        var handler = ScreenRow(hub);

        Assert.True(handler.IsConnected(new[] { Panel, LedBoard }));
        Assert.True(handler.DrivesDisplay(new[] { Panel, LedBoard }));
    }

    [Fact]
    public void Nothing_of_the_8_8_present_is_not_connected()
    {
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        Assert.False(ScreenRow(hub).IsConnected(new[] { Usb(0x0416, 0x8051) }));
    }

    [Fact]
    public void Companions_are_not_inventory_identities()
    {
        using var hub = new BulkPanelHub(new UniversalScreen88Driver());
        var handler = ScreenRow(hub);

        Assert.DoesNotContain(handler.Identifiers, i => i.ProductId == UniversalScreen88Protocol.DesktopProductId);
        Assert.DoesNotContain(handler.Identifiers, i => i.ProductId == UniversalScreen88LedProtocol.ProductId);
    }

    [Fact]
    public void Other_bulk_panels_keep_their_companions()
    {
        Assert.Null(BulkPanelHandler.CompanionsFor(new ThermalrightPanelDriver()));
        var curve = Assert.Single(BulkPanelHandler.CompanionsFor(new HydroShift2CurveLcdDriver())!);
        Assert.Equal(HydroShift2CurveProtocol.BoardProductId, curve.ProductId);
    }

    // ── saved switches from builds that had the ring's own ──

    private static NexusSettings Settings(IEnumerable<string> on, IEnumerable<string>? off = null)
    {
        var doc = new NexusSettings();
        doc.Devices.NexusControlEnabled = new List<string>(on);
        doc.Devices.NexusControlDisabled = new List<string>(off ?? new List<string>());
        return doc;
    }

    [Fact]
    public void A_ring_turned_on_turns_the_screen_on()
    {
        // The user's set: the glass off as a plain Windows monitor, the ring on.
        var doc = Settings(new[] { "tryx", RetiredRing }, off: new[] { Screen });

        Assert.True(UniversalScreen88ControlMigration.Apply(doc));

        Assert.Equal(new[] { "tryx", Screen }, doc.Devices.NexusControlEnabled);
        Assert.Empty(doc.Devices.NexusControlDisabled);
        Assert.True(UniversalScreen88LedBoard.ShouldDrive(Gate(doc.Devices.NexusControlEnabled, doc.Devices.NexusControlDisabled)));
    }

    [Fact]
    public void A_ring_turned_on_with_the_screen_on_keeps_one_entry()
    {
        var doc = Settings(new[] { Screen, RetiredRing });

        Assert.True(UniversalScreen88ControlMigration.Apply(doc));

        Assert.Equal(new[] { Screen }, doc.Devices.NexusControlEnabled);
    }

    [Fact]
    public void A_ring_turned_off_is_dropped_and_the_screen_keeps_its_choice()
    {
        var doc = Settings(new[] { Screen }, off: new[] { RetiredRing });

        Assert.True(UniversalScreen88ControlMigration.Apply(doc));

        Assert.Equal(new[] { Screen }, doc.Devices.NexusControlEnabled);
        Assert.Empty(doc.Devices.NexusControlDisabled);
    }

    [Fact]
    public void A_ring_turned_off_with_the_screen_off_leaves_it_off()
    {
        var doc = Settings(new string[0], off: new[] { RetiredRing, Screen });

        Assert.True(UniversalScreen88ControlMigration.Apply(doc));

        Assert.Empty(doc.Devices.NexusControlEnabled);
        Assert.Equal(new[] { Screen }, doc.Devices.NexusControlDisabled);
    }

    [Fact]
    public void Without_the_retired_id_nothing_changes_and_it_runs_once()
    {
        var untouched = Settings(new[] { Screen });
        Assert.False(UniversalScreen88ControlMigration.Apply(untouched));

        var doc = Settings(new[] { RetiredRing.ToUpperInvariant() });
        Assert.True(UniversalScreen88ControlMigration.Apply(doc));
        Assert.False(UniversalScreen88ControlMigration.Apply(doc));
        Assert.Equal(new[] { Screen }, doc.Devices.NexusControlEnabled);
    }
}
