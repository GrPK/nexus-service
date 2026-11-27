using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LogitechHidpp;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechHidpp;

public class LogitechMouseServiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-mouse-test-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            Directory.Delete(_dir, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    [Fact]
    public void GetState_ReadsDirectoryProfilesAndMode()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);

        var state = service.GetState();

        Assert.True(state.Present);
        Assert.Equal("onboard", state.Mode);
        Assert.Equal(1, state.ActiveProfile);
        Assert.Equal(11, state.ButtonCount);
        Assert.Equal(new[] { 1000, 500, 250, 125 }, state.ReportRatesHz);
        Assert.Equal(2, state.Profiles.Count);
        Assert.True(state.Profiles[0].Enabled);
        Assert.False(state.Profiles[1].Enabled);
        Assert.Equal(new[] { 1000, 1500, 2000, 2500, 3000 }, state.Profiles[0].Dpi);
        Assert.Equal("special", state.Profiles[0].Buttons[4].Kind);
        Assert.Equal(7, state.Profiles[0].Buttons[4].Code);
        Assert.True(state.Profiles[0].CanRestore);
    }

    [Fact]
    public void GetLive_ReadsTheProfileAndDpiLevelFromTheMouse_AndFollowsTheProfileButton()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);

        var live = service.GetLive();
        Assert.True(live.Present);
        Assert.Equal(1, live.ActiveProfile);
        Assert.Equal(2, live.DpiIndex);

        // DPI up on the mouse, then the profile button.
        mouse.DpiIndex = 3;
        mouse.Current = 2;
        live = service.GetLive();
        Assert.Equal(2, live.ActiveProfile);
        Assert.Equal(3, live.DpiIndex);
        Assert.Equal(2, service.GetState().ActiveProfile);

        mouse.Present = false;
        Assert.False(service.GetLive().Present);
    }

    [Fact]
    public void UpdateProfile_WritesTheSector_KeepsBackups_AndReselectsTheActiveProfile()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);
        var original = mouse.Sectors[1].ToArray();

        var result = service.UpdateProfile(1, new LogitechMouseProfileRequest { Dpi = new List<int> { 800, 1600, 2400, 3200, 6400 }, ReportRateHz = 500 });

        Assert.True(result.Ok, result.Message);
        var written = HidppOnboardProfile.Parse(mouse.Sectors[1])!;
        Assert.True(written.CrcValid);
        Assert.Equal(new[] { 800, 1600, 2400, 3200, 6400 }, written.Dpi);
        Assert.Equal(2, written.ReportIntervalMs);
        Assert.Equal(1, mouse.SelectCount);
        var deviceDir = Directory.GetDirectories(_dir).Single();
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(deviceDir, "original-sector-0001.bin")));
        Assert.Contains(Directory.GetFiles(deviceDir), f => f.EndsWith("-sector-0001.bin") && !f.Contains("original"));
    }

    [Fact]
    public void SaveCopy_ThenRestoreCopy_PutsTheSavedProfilesBack()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);
        Assert.Equal("invalid", service.RestoreCopy().Status);
        var before = mouse.Sectors[1].ToArray();

        Assert.True(service.SaveCopy().Ok);
        Assert.NotNull(service.GetState().SavedAt);
        Assert.True(service.UpdateProfile(1, new LogitechMouseProfileRequest { Name = "Changed" }).Ok);
        Assert.NotEqual(before, mouse.Sectors[1]);

        var result = service.RestoreCopy();

        Assert.True(result.Ok, result.Message);
        Assert.Equal(before, mouse.Sectors[1]);
    }

    [Fact]
    public void UpdateProfile_PutsThePreviousBytesBack_WhenTheMouseDoesNotKeepTheWrite()
    {
        var mouse = new FakeMouse { DropNextWrite = true };
        var service = LogitechMouseService.ForTest(mouse, _dir);
        var original = mouse.Sectors[1].ToArray();

        var result = service.UpdateProfile(1, new LogitechMouseProfileRequest { Name = "Lost" });

        Assert.False(result.Ok);
        Assert.Equal(original, mouse.Sectors[1]);
        Assert.Equal("PROFILE_NAME_DEFAULT", service.GetState().Profiles[0].Name);
    }

    [Fact]
    public void UpdateProfile_RejectsBadValuesWithoutWriting()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);

        Assert.Equal("invalid", service.UpdateProfile(1, new LogitechMouseProfileRequest { ReportRateHz = 333 }).Status);
        Assert.Equal("invalid", service.UpdateProfile(9, new LogitechMouseProfileRequest { Name = "x" }).Status);
        Assert.Equal("invalid", service.UpdateProfile(1, new LogitechMouseProfileRequest
        {
            Buttons = new List<LogitechMouseButtonDto> { new() { Slot = 2, Kind = "special", Code = 99 } },
        }).Status);
        Assert.Equal(0, mouse.Writes);
    }

    [Fact]
    public void RestoreProfile_WritesTheFirstReadCopyBack()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);
        var original = mouse.Sectors[1].ToArray();
        Assert.True(service.UpdateProfile(1, new LogitechMouseProfileRequest { Name = "CS2" }).Ok);

        var result = service.RestoreProfile(1);

        Assert.True(result.Ok, result.Message);
        Assert.Equal(original, mouse.Sectors[1]);
    }

    [Fact]
    public void SetActiveProfile_AndSetMode_GoToTheMouse()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);

        Assert.True(service.SetActiveProfile(2).Ok);
        Assert.Equal(2, mouse.Current);
        // Profile 2 was out of the cycle; the mouse only switches to one in it.
        Assert.Equal(1, mouse.Sectors[0][6]);
        Assert.True(service.GetState().Profiles[1].Enabled);
        Assert.True(service.SetMode("host").Ok);
        Assert.Equal(2, mouse.Mode);
        Assert.Equal("invalid", service.SetMode("both").Status);
    }

    [Fact]
    public void SetProfileEnabled_RewritesTheDirectoryWithItsCrc_AndKeepsOneProfileInTheCycle()
    {
        var mouse = new FakeMouse();
        var service = LogitechMouseService.ForTest(mouse, _dir);

        Assert.True(service.SetProfileEnabled(2, true).Ok);
        var dir = mouse.Sectors[0];
        Assert.Equal(1, dir[6]);
        Assert.Equal(HidppOnboardProfile.Crc16(dir.AsSpan(0, dir.Length - 2)), System.Buffers.Binary.BinaryPrimitives.ReadUInt16BigEndian(dir.AsSpan(dir.Length - 2)));
        Assert.True(File.Exists(Path.Combine(_dir, "g502-x-plus", "original-sector-0000.bin")));

        Assert.True(service.SetProfileEnabled(1, false).Ok);
        Assert.Equal("invalid", service.SetProfileEnabled(2, false).Status);
        Assert.False(service.GetState().Profiles[0].Enabled);
    }

    [Fact]
    public void Tick_PutsTheMouseBackOnboard_UnlessGHubRunsOrTheUserChoseHost()
    {
        var mouse = new FakeMouse { Mode = 2 };
        var ghub = false;
        var service = LogitechMouseService.ForTest(mouse, _dir, () => ghub);

        service.Tick();
        Assert.Equal(1, mouse.Mode);
        Assert.True(service.GetState().KeepOnboard);

        mouse.Mode = 2;
        ghub = true;
        service.Tick();
        Assert.Equal(2, mouse.Mode);

        ghub = false;
        Assert.True(service.SetMode("host").Ok);
        service.Tick();
        Assert.Equal(2, mouse.Mode);
        Assert.False(service.GetState().KeepOnboard);

        Assert.True(service.SetKeepOnboard(true).Ok);
        Assert.Equal(1, mouse.Mode);
    }

    [Fact]
    public void Picture_IsReported_WhenTheImageAndItsSpotsAreThere()
    {
        var service = LogitechMouseService.ForTest(new FakeMouse(), _dir);
        Assert.Null(service.GetState().Picture);

        var dir = Path.Combine(_dir, "g502-x-plus");
        Directory.CreateDirectory(dir);
        File.WriteAllBytes(Path.Combine(dir, "picture.png"), new byte[] { 0x89, (byte)'P', (byte)'N', (byte)'G' });
        File.WriteAllText(Path.Combine(dir, "picture.json"), "{\"width\":700,\"height\":700,\"spots\":[{\"slot\":0,\"x\":175,\"y\":175}]}");

        var picture = service.GetState().Picture;
        Assert.NotNull(picture);
        Assert.Equal(700, picture!.Width);
        Assert.Equal(175, Assert.Single(picture.Spots).X);
        Assert.Equal("image/png", service.Picture()!.Value.ContentType);
    }

    [Fact]
    public void NotPresent_AnswersWithoutTouchingTheDevice()
    {
        var mouse = new FakeMouse { Present = false };
        var service = LogitechMouseService.ForTest(mouse, _dir);

        Assert.False(service.GetState().Present);
        Assert.Equal("not-present", service.UpdateProfile(1, new LogitechMouseProfileRequest { Name = "x" }).Status);
        Assert.Equal(0, mouse.Calls);
    }

    private static List<LogitechMouseMacroStepDto> Tap(int usage) => new()
    {
        new() { Type = "keyDown", Code = usage },
        new() { Type = "delay", Code = 20 },
        new() { Type = "keyUp", Code = usage },
    };

    [Fact]
    public void UpdateProfile_StoresMacrosInAFreeSector_PacksThem_AndReusesIdenticalOnes()
    {
        var mouse = new FakeMouse();
        mouse.EraseMacroSectors();
        var service = LogitechMouseService.ForTest(mouse, _dir);

        var result = service.UpdateProfile(2, new LogitechMouseProfileRequest
        {
            Buttons = new List<LogitechMouseButtonDto>
            {
                new() { Slot = 3, Kind = "macro", Macro = Tap(0x04) },
                new() { Slot = 5, Kind = "macro", Macro = Tap(0x05) },
            },
            GShiftButtons = new List<LogitechMouseButtonDto> { new() { Slot = 3, Kind = "macro", Macro = Tap(0x04) } },
        });

        Assert.True(result.Ok, result.Message);
        var profile = HidppOnboardProfile.Parse(mouse.Sectors[2])!;
        Assert.Equal(HidppButtonBinding.Macro(6, 0), profile.Button(3));
        Assert.Equal(HidppButtonBinding.Macro(6, 16), profile.Button(5));
        Assert.Equal(HidppButtonBinding.Macro(6, 0), profile.GShiftButton(3));
        var sector = mouse.Sectors[6];
        Assert.Equal(HidppOnboardProfile.Crc16(sector.AsSpan(0, 253)), sector[253] << 8 | sector[254]);
        Assert.Equal(Convert.FromHexString("430004400014440004FF"), sector[..10]);
        Assert.Equal(Convert.FromHexString("430005400014440005FF"), sector[16..26]);
        Assert.All(mouse.Sectors.Where(kv => kv.Key > 6).Select(kv => kv.Value), b => Assert.All(b, x => Assert.Equal(0xFF, x)));

        var button = service.GetState().Profiles[1].Buttons[5];
        Assert.Equal("macro", button.Kind);
        Assert.True(button.MacroEditable);
        Assert.Equal(new[] { "keyDown", "delay", "keyUp" }, button.Macro!.Select(m => m.Type));
        Assert.Equal(0x05, button.Macro![0].Code);

        // A later macro goes after the ones in use.
        Assert.True(service.UpdateProfile(1, new LogitechMouseProfileRequest
        {
            Buttons = new List<LogitechMouseButtonDto> { new() { Slot = 9, Kind = "macro", Macro = Tap(0x06) } },
        }).Ok);
        Assert.Equal(HidppButtonBinding.Macro(6, 32), HidppOnboardProfile.Parse(mouse.Sectors[1])!.Button(9));
    }

    [Fact]
    public void UpdateProfile_RefusesAMacroWhenNoSectorHasRoom_WithoutTouchingTheProfile()
    {
        var mouse = new FakeMouse(); // macro sectors unreadable
        var service = LogitechMouseService.ForTest(mouse, _dir);
        var before = mouse.Sectors[1].ToArray();

        var result = service.UpdateProfile(1, new LogitechMouseProfileRequest
        {
            Buttons = new List<LogitechMouseButtonDto> { new() { Slot = 3, Kind = "macro", Macro = Tap(0x04) } },
        });

        Assert.False(result.Ok);
        Assert.Equal(before, mouse.Sectors[1]);
        Assert.Equal(0, mouse.Writes);
        Assert.Equal("invalid", service.UpdateProfile(1, new LogitechMouseProfileRequest
        {
            Buttons = new List<LogitechMouseButtonDto> { new() { Slot = 3, Kind = "macro", Macro = new() { new() { Type = "keyDown", Code = 4 } } } },
        }).Status);
    }

    // An in-memory G502 X PLUS: 0x8100 at index 11, 0x8060 at index 13, two profile sectors.
    private sealed class FakeMouse : IHidppChannel
    {
        private const int Size = 255;
        public readonly Dictionary<int, byte[]> Sectors = new();
        public bool Present = true;
        public bool DropNextWrite;
        public int Mode = 1;
        public int Current = 1;
        public int DpiIndex = 2;
        public int SelectCount;
        public int Writes;
        public int Calls;
        private int _writeSector;
        private int _writeOffset;
        private byte[]? _pending;

        public FakeMouse()
        {
            var profile = Convert.FromHexString(
                "010000e803dc05d007c409b80bffffffff00ffffffffffffffffffff3c002c018001000180010002800100048001000890070000" +
                "800100109001000090020000900a000090030000900400009010000090110000ffffffffffffffffffffffff80010001800100" +
                "02800100048001000890070000800100109001000090020000900a000090030000900400009010000090110000ffffffffffff" +
                "ffffffffffff500052004f00460049004c0045005f004e0041004d0045005f00440045004600410055004c0054000000000000" +
                "00000000000000000000000000000a9307d91388006400000000000000000000000000000a9307d9138800640000000033c2");
            Sectors[1] = profile;
            Sectors[2] = HidppOnboardProfile.Parse(profile)!.With(new HidppOnboardProfileEdit { Name = "Two" }).ToBytes();
            var dir = Enumerable.Repeat((byte)0xFF, Size).ToArray();
            new byte[] { 0x00, 0x01, 0x01, 0xFF, 0x00, 0x02, 0x00, 0xFF }.CopyTo(dir, 0);
            System.Buffers.Binary.BinaryPrimitives.WriteUInt16BigEndian(dir.AsSpan(Size - 2), HidppOnboardProfile.Crc16(dir.AsSpan(0, Size - 2)));
            Sectors[0] = dir;
        }

        public void EraseMacroSectors()
        {
            for (var sector = 6; sector < 16; sector++)
            {
                Sectors[sector] = Enumerable.Repeat((byte)0xFF, Size).ToArray();
            }
        }

        public int Generation => 1;

        public bool IsPresent() => Present;

        public byte? FeatureIndex(ushort featureId) => featureId switch
        {
            HidppOnboardProfiles.FeatureId => 11,
            0x8060 => 13,
            _ => null,
        };

        public byte[]? Call(byte featureIndex, byte function, ReadOnlySpan<byte> p)
        {
            Calls++;
            var reply = new byte[16];
            if (featureIndex == 13 && function == 0)
            {
                reply[0] = 0x8B;
                return reply;
            }
            if (featureIndex != 11)
            {
                return null;
            }
            switch (function)
            {
                case 0:
                    new byte[] { 1, 5, 1, 5, 2, 11, 16, 0, Size }.CopyTo(reply, 0);
                    return reply;
                case 1:
                    Mode = p[0];
                    return reply;
                case 2:
                    reply[0] = (byte)Mode;
                    return reply;
                case 3:
                    Current = p[0] << 8 | p[1];
                    SelectCount++;
                    return reply;
                case 4:
                    reply[1] = (byte)Current;
                    return reply;
                case 11:
                    reply[0] = (byte)DpiIndex;
                    return reply;
                case 5:
                    var sector = p[0] << 8 | p[1];
                    var at = p[2] << 8 | p[3];
                    if (!Sectors.TryGetValue(sector, out var data))
                    {
                        return null;
                    }
                    Array.Copy(data, at, reply, 0, 16);
                    return reply;
                case 6:
                    _writeSector = p[0] << 8 | p[1];
                    _writeOffset = 0;
                    _pending = new byte[p[4] << 8 | p[5]];
                    return reply;
                case 7:
                    var n = Math.Min(16, _pending!.Length - _writeOffset);
                    p[..n].CopyTo(_pending.AsSpan(_writeOffset));
                    _writeOffset += n;
                    return reply;
                case 8:
                    Writes++;
                    if (DropNextWrite)
                    {
                        DropNextWrite = false;
                        return reply;
                    }
                    Sectors[_writeSector] = _pending!;
                    return reply;
            }
            return null;
        }
    }
}
