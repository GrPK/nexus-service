using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Lighting;
using Nexus.Service.Peripherals.CorsairLink;
using Nexus.Service.Peripherals.Hid;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.CorsairLink;

/// <summary>Lock override on the shared LCD class and worker; a recording HID double, no hardware.</summary>
public class CorsairLcdLockTests
{
    private static readonly byte[] BrightnessZero = { 0x03, 0x0b, 0x00, 0x01 };

    private sealed class Rig
    {
        public readonly LcdLockState Lock = new();
        public readonly CorsairLinkLcd Lcd;
        public readonly CorsairLinkLcdWorker Worker;
        public readonly Device Hid = new();
        public readonly CorsairSettings Settings = new() { LcdBrightness = 70 };
        private readonly Enumerator _enumerator;

        public Rig(string usbSerial = "S1")
        {
            Lcd = new CorsairLinkLcd(Lock);
            Worker = new CorsairLinkLcdWorker(Lcd, null!, null!, null!, null!);
            _enumerator = new Enumerator(Hid, usbSerial);
            ChainType = 6;
            Attach();
        }

        public int ChainType { get; }

        public void Attach() => Lcd.DiscoverAndAttach(
            new CorsairLinkDevice[] { new() { Channel = 1, Type = ChainType, Serial = "S1" } }, _enumerator);

        public void Pass()
        {
            Worker.SyncGeneration();
            Worker.SyncBrightness(Settings);
        }

        public byte? LastBrightness =>
            Hid.Features.LastOrDefault(f => f.Length >= 3 && f[1] == 0x0b)?[2];
    }

    [Fact]
    public void Lock_sets_the_glass_dark_and_unlock_restores_the_stored_value_without_touching_it()
    {
        var rig = new Rig();
        rig.Pass();
        Assert.Equal((byte)70, rig.LastBrightness);

        rig.Lock.Set(true);
        rig.Pass();
        Assert.Equal((byte)0, rig.LastBrightness);
        Assert.Equal((byte)70, rig.Settings.LcdBrightness);

        rig.Lock.Set(false);
        rig.Pass();
        Assert.Equal((byte)70, rig.LastBrightness);
    }

    // A TITAN RX LCD's USB serial differs from its chain serial and attaches through the
    // sole-LCD fallback; the override sits on the class, so it takes it the same way.
    [Theory]
    [InlineData("S1")]
    [InlineData("TITAN-USB")]
    public void Matched_and_sole_fallback_attach_take_the_same_override(string usbSerial)
    {
        var rig = new Rig(usbSerial);
        Assert.True(rig.Lcd.HasDevice);
        rig.Lock.Set(true);
        rig.Pass();
        Assert.Equal((byte)0, rig.LastBrightness);
    }

    [Fact]
    public void A_reattach_while_locked_applies_the_dark_value_not_the_stored_one()
    {
        var rig = new Rig();
        rig.Pass();
        rig.Lock.Set(true);
        rig.Pass();

        // The LCD re-enumerates: the hub drops the handle and the next discovery re-opens it.
        rig.Lcd.Detach(handBack: false);
        rig.Hid.Features.Clear();
        rig.Attach();
        rig.Pass();

        Assert.Equal((byte)0, rig.LastBrightness);
        Assert.DoesNotContain(rig.Hid.Features, f => f.Length >= 3 && f[1] == 0x0b && f[2] != 0);
    }

    [Fact]
    public void Unlock_wakes_even_when_the_setting_was_turned_off_mid_lock()
    {
        var rig = new Rig();
        rig.Pass();
        rig.Lock.Set(true);
        rig.Pass();

        rig.Settings.SleepWhenLocked = false;
        rig.Pass();
        Assert.Equal((byte)0, rig.LastBrightness);

        rig.Lock.Set(false);
        rig.Pass();
        Assert.Equal((byte)70, rig.LastBrightness);
    }

    [Fact]
    public void A_screen_with_the_setting_off_stays_lit_through_a_lock()
    {
        var rig = new Rig();
        rig.Settings.SleepWhenLocked = false;
        rig.Pass();

        rig.Lock.Set(true);
        rig.Pass();

        Assert.Equal((byte)70, rig.LastBrightness);
    }

    [Fact]
    public void The_lock_pass_sends_only_a_brightness_report_and_never_a_hand_back()
    {
        var rig = new Rig();
        rig.Pass();
        rig.Lcd.SendFrame(new byte[] { 0xFF, 0xD8, 0xFF });
        rig.Hid.Features.Clear();

        rig.Lock.Set(true);
        rig.Pass();

        // The worker's brightness pass: one brightness report, no host-mode-off, no hand-back select.
        Assert.Equal(new[] { BrightnessZero }, rig.Hid.Features.Select(f => f).ToArray());
        Assert.DoesNotContain(rig.Hid.Features, f => f.Length >= 3 && f[1] == 0x1d && f[2] == 0x00);
    }

    private sealed class Device : IHidDevice
    {
        public List<byte[]> Features { get; } = new();
        public int VendorId => 0x1B1C;
        public int ProductId => CorsairLinkLcd.AioPid;
        public string Path => "/dev/fake";
        public string? Serial => null;
        public int UsagePage => 0;
        public int Usage => 0;

        public bool SetFeature(ReadOnlySpan<byte> report)
        {
            Features.Add(report.ToArray());
            return true;
        }

        public bool Write(ReadOnlySpan<byte> report) => true;
        public bool GetFeature(Span<byte> buffer) => false;
        public bool GetInputReport(Span<byte> buffer) => false;
        public bool SetOutputReport(ReadOnlySpan<byte> report) => false;
        public int Read(Span<byte> buffer, int timeoutMs) => 0;
        public void Dispose() { }
    }

    private sealed class Enumerator : IHidEnumerator
    {
        private readonly Device _device;
        private readonly string _usbSerial;
        public Enumerator(Device device, string usbSerial) { _device = device; _usbSerial = usbSerial; }

        private IReadOnlyList<HidDeviceInfo> Infos() => new[]
        {
            new HidDeviceInfo { VendorId = CorsairLinkLcd.LcdVendorId, ProductId = CorsairLinkLcd.AioPid, Path = "/dev/hid-lcd", Serial = _usbSerial, OutputReportByteLength = 1024 },
        };

        public IReadOnlyList<HidDeviceInfo> Find(int vendorId, int productId) =>
            Infos().Where(i => i.VendorId == vendorId && i.ProductId == productId).ToList();

        public IReadOnlyList<HidDeviceInfo> FindAll() => Infos();
        public IHidDevice? Open(string path, bool forInput = false) => _device;
    }
}
