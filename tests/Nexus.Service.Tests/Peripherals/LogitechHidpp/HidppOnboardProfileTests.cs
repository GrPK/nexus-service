using System;
using System.Collections.Generic;
using Nexus.Service.Peripherals.LogitechHidpp;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechHidpp;

public class HidppOnboardProfileTests
{
    // Profile 1 of a G502 X PLUS (format 5, 255-byte sector) as read over 0x8100 memoryRead.
    private const string G502XProfile1 =
        "010000e803dc05d007c409b80bffffffff00ffffffffffffffffffff3c002c01800100018001000280010004800100089007" +
        "0000800100109001000090020000900a000090030000900400009010000090110000ffffffffffffffffffffffff80010001" +
        "80010002800100048001000890070000800100109001000090020000900a000090030000900400009010000090110000ffff" +
        "ffffffffffffffffffff500052004f00460049004c0045005f004e0041004d0045005f00440045004600410055004c005400" +
        "000000000000000000000000000000000000000a9307d91388006400000000000000000000000000000a9307d91388006400" +
        "00000033c2";

    private static HidppOnboardProfile Sample() => HidppOnboardProfile.Parse(Convert.FromHexString(G502XProfile1))!;

    [Fact]
    public void Parse_ReadsTheG502XDefaultProfile()
    {
        var p = Sample();

        Assert.Equal(255, p.SectorSize);
        Assert.True(p.CrcValid);
        Assert.Equal(1, p.ReportIntervalMs);
        Assert.Equal(new[] { 1000, 1500, 2000, 2500, 3000 }, p.Dpi);
        Assert.Equal(0, p.DefaultDpiIndex);
        Assert.Equal(0, p.ShiftDpiIndex);
        Assert.Equal("PROFILE_NAME_DEFAULT", p.Name);
    }

    [Fact]
    public void Parse_DecodesTheButtonBindings()
    {
        var p = Sample();

        Assert.Equal(HidppButtonBinding.Mouse(HidppButtonBinding.MouseLeft), p.Button(0));
        Assert.Equal(HidppButtonBinding.Mouse(HidppButtonBinding.MouseBack), p.Button(3));
        Assert.Equal(HidppButtonBinding.Special(7), p.Button(4));
        Assert.Equal(HidppButtonBinding.Mouse(HidppButtonBinding.MouseForward), p.Button(5));
        Assert.Equal(HidppButtonBinding.Special(10), p.Button(8));
        Assert.Equal(HidppButtonBinding.Special(17), p.Button(12));
        Assert.Equal(HidppButtonKind.Raw, p.Button(13).Kind);
        Assert.Equal(p.Button(4), p.GShiftButton(4));
    }

    [Fact]
    public void Crc16_IsCcittFalse()
    {
        Assert.Equal(0x29B1, HidppOnboardProfile.Crc16("123456789"u8));
    }

    [Fact]
    public void With_NoChanges_KeepsEveryByte()
    {
        var p = Sample();

        Assert.Equal(p.ToBytes(), p.With(new HidppOnboardProfileEdit()).ToBytes());
    }

    [Fact]
    public void With_ChangesOnlyTheNamedFields_AndKeepsTheCrcValid()
    {
        var p = Sample();

        var edited = p.With(new HidppOnboardProfileEdit
        {
            Dpi = new[] { 800, 1600, 2400, 3200, 6400 },
            DefaultDpiIndex = 1,
            ShiftDpiIndex = 0,
            ReportIntervalMs = 2,
            Buttons = new Dictionary<int, HidppButtonBinding> { [4] = HidppButtonBinding.Key(0x04, 0x01) },
            Name = "CS2",
        });

        Assert.True(edited.CrcValid);
        Assert.Equal(new[] { 800, 1600, 2400, 3200, 6400 }, edited.Dpi);
        Assert.Equal(1, edited.DefaultDpiIndex);
        Assert.Equal(2, edited.ReportIntervalMs);
        Assert.Equal(HidppButtonBinding.Key(0x04, 0x01), edited.Button(4));
        Assert.Equal(HidppButtonBinding.Special(7), edited.GShiftButton(4));
        Assert.Equal("CS2", edited.Name);
        var before = p.ToBytes();
        var after = edited.ToBytes();
        // Timeouts, lighting and the unused button slots are untouched.
        Assert.Equal(before.AsSpan(28, 4).ToArray(), after.AsSpan(28, 4).ToArray());
        Assert.Equal(before.AsSpan(208, 45).ToArray(), after.AsSpan(208, 45).ToArray());
        Assert.Equal(before.AsSpan(32 + 13 * 4, 12).ToArray(), after.AsSpan(32 + 13 * 4, 12).ToArray());
    }

    [Theory]
    [InlineData(3)]
    [InlineData(0)]
    [InlineData(16)]
    public void With_RejectsReportIntervalsTheMouseDoesNotHave(int ms)
    {
        Assert.Throws<ArgumentException>(() => Sample().With(new HidppOnboardProfileEdit { ReportIntervalMs = ms }));
    }

    [Fact]
    public void With_RejectsADefaultLevelPastTheSetLevels()
    {
        var p = Sample();

        Assert.Throws<ArgumentException>(() => p.With(new HidppOnboardProfileEdit { Dpi = new[] { 800, 1600, 0, 0, 0 }, DefaultDpiIndex = 2 }));
        Assert.Equal(1, p.With(new HidppOnboardProfileEdit { Dpi = new[] { 800, 1600, 0, 0, 0 }, DefaultDpiIndex = 1 }).DefaultDpiIndex);
    }

    [Fact]
    public void With_RejectsANameLongerThanTheSector()
    {
        Assert.Throws<ArgumentException>(() => Sample().With(new HidppOnboardProfileEdit { Name = new string('x', 25) }));
    }

    [Fact]
    public void ButtonBinding_RawBytesSurviveARoundTrip()
    {
        var raw = new byte[] { 0xA0, 0x05, 0x00, 0x20 }; // an encoding the profile format does not define

        var binding = HidppButtonBinding.Decode(raw);

        Assert.Equal(HidppButtonKind.Raw, binding.Kind);
        Assert.Equal(raw, binding.Encode());
    }

    [Fact]
    public void Directory_ListsSectorsUpToTheEndMarker()
    {
        var dir = new byte[255];
        Array.Fill(dir, (byte)0xFF);
        new byte[] { 0x00, 0x01, 0x01, 0x00, 0x00, 0x02, 0x00, 0x00 }.CopyTo(dir, 0);

        var slots = HidppOnboardProfiles.ParseDirectory(dir);

        Assert.Equal(new[] { new HidppProfileSlot(1, true), new HidppProfileSlot(2, false) }, slots);
    }
}
