using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Models.Peripherals;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Peripherals.Hid;
using Xunit;
using GlassPipe = Nexus.Service.Tests.BulkPanels.UniversalScreen88Tests.GlassPipe;

namespace Nexus.Service.Tests.BulkPanels;

/// <summary>The 8.8 glass's own settings, as L-Connect's Additional page and orientation dialog send them.</summary>
public class UniversalScreen88ScreenSettingsTests
{
    [Theory]
    [InlineData("lianli88_0001_0023", "1.5")]
    [InlineData("lianli88_0001_0020", "1.2")]
    [InlineData("lianli88_0001_0018", "1.0")]
    [InlineData("lianli88_0001_0017", null)]
    [InlineData("lianli88_1_23", null)]
    [InlineData(null, null)]
    public void Firmware_version_is_read_like_lconnect(string? raw, string? expected) =>
        Assert.Equal(expected, UniversalScreen88Protocol.FirmwareVersion(raw)?.ToString());

    [Theory]
    [InlineData(true, false, "1.5", 0)]
    [InlineData(true, true, "1.5", 2)]
    [InlineData(false, false, "1.5", 1)]
    [InlineData(false, true, "1.5", 3)]
    [InlineData(false, true, "1.1", 2)]
    [InlineData(false, false, "1.1", 0)]
    [InlineData(false, true, null, 3)]
    public void Rotate_values_follow_lconnect_by_firmware(bool portrait, bool halfTurn, string? firmware, int expected) =>
        Assert.Equal(expected, UniversalScreen88Protocol.RotateValue(portrait, halfTurn, firmware is null ? null : Version.Parse(firmware)));

    [Fact]
    public void A_half_turned_landscape_mount_sends_rotate_three()
    {
        var driver = new UniversalScreen88Driver();
        driver.BindHalfTurn(() => true);
        var pipe = new GlassPipe();

        driver.Connect(pipe, null);

        var rotate = pipe.Commands.IndexOf(UniversalScreen88Protocol.CommandRotate);
        Assert.Equal(UniversalScreen88Protocol.RotateLandscapeHalfTurn, pipe.Params[rotate][0]);
    }

    [Fact]
    public void A_mount_turned_half_way_renegotiates()
    {
        var halfTurn = false;
        var driver = new UniversalScreen88Driver();
        driver.BindHalfTurn(() => halfTurn);
        var pipe = new GlassPipe();
        using var hub = new BulkPanelHub(driver);
        Assert.True(hub.Attach(pipe, null));
        Assert.False(driver.GeometryStale);

        halfTurn = true;
        Assert.True(driver.GeometryStale);
        Assert.True(hub.Renegotiate());
        Assert.False(driver.GeometryStale);
    }

    [Theory]
    [InlineData(true, UniversalScreen88Protocol.ClockOfflineOn)]
    [InlineData(false, UniversalScreen88Protocol.ClockOfflineOff)]
    public void A_set_offline_mode_goes_out_before_the_sync_only_clock(bool offline, byte mode)
    {
        var driver = new UniversalScreen88Driver { OfflineModeSource = () => offline };
        var pipe = new GlassPipe();

        driver.Connect(pipe, null);

        var clocks = pipe.Commands.Select((c, i) => (c, i)).Where(x => x.c == UniversalScreen88Protocol.CommandSetClock).Select(x => x.i).ToList();
        Assert.Equal(2, clocks.Count);
        Assert.Equal(mode, pipe.Params[clocks[0]][7]);
        Assert.Equal(2, pipe.Params[clocks[1]][7]);
    }

    [Fact]
    public void An_unset_offline_mode_leaves_the_glass_flag_alone()
    {
        var driver = new UniversalScreen88Driver { OfflineModeSource = () => null };
        var pipe = new GlassPipe();

        driver.Connect(pipe, null);

        Assert.Single(pipe.Commands, c => c == UniversalScreen88Protocol.CommandSetClock);
    }

    [Fact]
    public void Release_with_offline_mode_on_keeps_the_clock_instead_of_rebooting()
    {
        var driver = new UniversalScreen88Driver { OfflineModeSource = () => true };
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        driver.Disconnect(pipe, null);

        Assert.Equal(new[] { UniversalScreen88Protocol.CommandStopPlay, UniversalScreen88Protocol.CommandSetClock }, pipe.Commands);
        Assert.Equal(UniversalScreen88Protocol.ClockOfflineOn, pipe.Params[1][7]);
    }

    [Fact]
    public void A_live_offline_toggle_sends_the_mode_then_takes_the_clock_face_down()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        driver.Connect(pipe, null);
        pipe.Clear();

        Assert.True(driver.ApplyOfflineMode(pipe, true));

        Assert.Equal(new[] { UniversalScreen88Protocol.CommandSetClock, UniversalScreen88Protocol.CommandSetClock, UniversalScreen88Protocol.CommandStopClock }, pipe.Commands);
        Assert.Equal(UniversalScreen88Protocol.ClockOfflineOn, pipe.Params[0][7]);
    }

    [Fact]
    public void Asking_for_the_windows_display_switches_and_reboots_instead_of_streaming()
    {
        var driver = new UniversalScreen88Driver { WindowsDisplaySource = () => true };
        var pipe = new GlassPipe();

        Assert.Null(driver.Connect(pipe, null));

        Assert.Equal(
            new[]
            {
                UniversalScreen88Protocol.CommandGetVersion, UniversalScreen88Protocol.CommandStopPlay,
                UniversalScreen88Protocol.CommandSwitchToDesktop, UniversalScreen88Protocol.CommandReboot,
            },
            pipe.Commands);
        Assert.True(driver.SwitchedToWindowsDisplay);
    }

    [Fact]
    public void A_glass_that_refuses_the_switch_stays_a_panel()
    {
        var driver = new UniversalScreen88Driver { WindowsDisplaySource = () => true };
        var pipe = new RefusingSwitchPipe();

        Assert.Equal((1920, 480), driver.Connect(pipe, null));
        Assert.False(driver.SwitchedToWindowsDisplay);
    }

    [Fact]
    public void A_changed_setting_asks_for_the_handshake_again()
    {
        var driver = new UniversalScreen88Driver();
        var pipe = new GlassPipe();
        using var hub = new BulkPanelHub(driver);
        Assert.True(hub.Attach(pipe, null));

        driver.RequestHandshake();
        Assert.True(driver.GeometryStale);
        Assert.True(hub.Renegotiate());
        Assert.False(driver.GeometryStale);
    }

    // ── back from the Windows display mode: only when asked ──

    private const string Path88 = @"\\?\hid#vid_1a86&pid_ad21&mi_01#8&1&0&0000#{4d1e55b2}";

    private static UsbDeviceEntry Usb(int vid, int pid) => new() { VendorId = vid, ProductId = pid };

    [Fact]
    public async Task The_screen_is_left_as_a_windows_display_unless_asked_back()
    {
        var rig = new Rig { Wanted = false };

        await rig.Poll();
        await rig.Poll();

        Assert.Equal(0, rig.Writes);
    }

    [Fact]
    public async Task Asked_back_it_gets_the_lconnect_exit_on_its_hid_interface()
    {
        var rig = new Rig { Wanted = true };
        rig.OnWrite = () => rig.Bus = new() { Usb(0x0416, 0x8050), Usb(0x1CBE, 0xA088) };

        await rig.Poll();

        Assert.Equal(1, rig.Writes);
        Assert.Equal(DesktopModeTable.ExitReport(UniversalScreen88Protocol.DesktopReportLength), rig.LastReport);
        Assert.Equal(513, rig.LastReport!.Length);
    }

    [Fact]
    public async Task A_later_ask_acts_on_the_same_arrival()
    {
        var rig = new Rig { Wanted = false };
        await rig.Poll();
        Assert.Equal(0, rig.Writes);

        rig.Wanted = true;
        await rig.Poll();

        Assert.Equal(1, rig.Writes);
    }

    [Fact]
    public async Task An_interface_without_the_report_length_is_refused()
    {
        var rig = new Rig { Wanted = true, OutputLength = 65 };

        await rig.Poll();

        Assert.Equal(0, rig.Writes);
    }

    private sealed class Rig
    {
        public List<UsbDeviceEntry> Bus = new() { Usb(0x1A86, 0xAD21), Usb(0x0416, 0x8050) };
        public bool Wanted;
        public int OutputLength = UniversalScreen88Protocol.DesktopReportLength;
        public int Writes;
        public byte[]? LastReport;
        public Action? OnWrite;
        private long _now;
        private readonly DesktopModeExit _exit;

        public Rig()
        {
            _exit = new DesktopModeExit(
                new Hid(this), new HardwarePresence(new UsbBus(this)), () => false, _ => true,
                now: () => _now,
                delay: (ms, _) =>
                {
                    _now += ms;
                    return Task.CompletedTask;
                },
                wanted: row => row.HandlerId != UniversalScreen88Driver.Id || Wanted);
        }

        public Task Poll() => _exit.PollAsync(CancellationToken.None);

        private sealed class UsbBus(Rig rig) : IUsbEnumerator
        {
            public List<UsbDeviceEntry> Enumerate() => rig.Bus.ToList();
        }

        private sealed class Hid(Rig rig) : IHidEnumerator
        {
            public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
                vendorId == 0x1A86 && productId == 0xAD21 && rig.Bus.Any(b => b.ProductId == 0xAD21)
                    ? new[]
                    {
                        new HidDeviceInfo
                        {
                            VendorId = 0x1A86, ProductId = 0xAD21, Path = Path88, UsagePage = 0xFF00,
                            InputReportByteLength = rig.OutputLength, OutputReportByteLength = rig.OutputLength,
                        },
                    }
                    : Array.Empty<HidDeviceInfo>();

            public IReadOnlyList<HidDeviceInfo> FindAll() => Find(0x1A86, 0xAD21);

            public IHidDevice? Open(string path, bool forInput = false) => new Device(rig);
        }

        private sealed class Device(Rig rig) : IHidDevice
        {
            public int VendorId => 0x1A86;
            public int ProductId => 0xAD21;
            public string Path => Path88;
            public string? Serial => null;
            public int UsagePage => 0xFF00;
            public int Usage => 0;
            public bool SetFeature(ReadOnlySpan<byte> report) => false;
            public bool GetFeature(Span<byte> buffer) => false;
            public bool GetInputReport(Span<byte> buffer) => false;
            public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
            public int Read(Span<byte> buffer, int timeoutMs) => 0;
            public void Dispose() { }

            public bool Write(ReadOnlySpan<byte> report)
            {
                rig.Writes++;
                rig.LastReport = report.ToArray();
                rig.OnWrite?.Invoke();
                return true;
            }
        }
    }

    /// <summary>A glass that answers everything but the switch to its Windows display mode.</summary>
    private sealed class RefusingSwitchPipe : IBulkUsbPipe
    {
        private readonly GlassPipe _inner = new();
        private bool _drop;

        public bool Write(ReadOnlySpan<byte> data)
        {
            var before = _inner.Commands.Count;
            var ok = _inner.Write(data);
            _drop = _inner.Commands.Count > before && _inner.Commands[^1] == UniversalScreen88Protocol.CommandSwitchToDesktop;
            return ok;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_drop)
            {
                _drop = false;
                _inner.Read(buffer, timeoutMs);
                return 0;
            }
            return _inner.Read(buffer, timeoutMs);
        }

        public void Dispose() { }
    }
}
