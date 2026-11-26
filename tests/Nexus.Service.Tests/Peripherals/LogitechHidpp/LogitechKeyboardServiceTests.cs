using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Nexus.Service.Models.Devices;
using Nexus.Service.Peripherals.LogitechHidpp;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechHidpp;

public class LogitechKeyboardServiceTests : IDisposable
{
    private const int G1 = 0x6E, G3 = 0x6F, G4 = 0x78;
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-keyboard-test-" + Guid.NewGuid().ToString("N"));

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

    private static LogitechKeyboardRowDto Row(int key, string kind, int code = 0, int mods = 0) => new() { Key = key, Kind = kind, Code = code, Modifiers = mods };

    [Fact]
    public void GetState_ReadsControlsLayoutAndSwitch()
    {
        var keyboard = new FakeKeyboard();
        var service = LogitechKeyboardService.ForTest(keyboard, _dir);

        var state = service.GetState();

        Assert.True(state.Present);
        Assert.Equal("G915 X", state.Model);
        Assert.Equal(128, state.Controls.Count);
        Assert.Equal(399, state.Controls[G1]);
        Assert.Equal(0x37, state.LayoutCode);
        Assert.Equal("iso", state.Layout);
        Assert.False(state.Enabled);
        Assert.False(state.Written);
        Assert.Empty(keyboard.Files);
    }

    [Fact]
    public void SetLayers_WritesThreeTables_GShiftCarriesBase_AndTurnsAssignmentsOn()
    {
        var keyboard = new FakeKeyboard();
        var service = LogitechKeyboardService.ForTest(keyboard, _dir);

        var result = service.SetLayers(new LogitechKeyboardLayersDto
        {
            Base = { Row(G1, "keyboard", 0x0F), Row(G3, "mouse", 0x10), Row(G4, "gshift") },
            Fn = { Row(0x09, "consumer", 0xCD) },
            GShift = { Row(G1, "keyboard", 0x14) },
        });

        Assert.True(result.Ok, result.Message);
        Assert.Equal(new[] { HidppKeyTable.LayerBase, HidppKeyTable.LayerFn, HidppKeyTable.LayerGShift }, keyboard.WriteOrder);
        var layer1 = HidppKeyTable.Parse(keyboard.Files[1])!;
        Assert.Equal(new[] { G1, G3, G4 }, layer1.Select(r => r.Key));
        var layer3 = HidppKeyTable.Parse(keyboard.Files[3])!;
        Assert.Equal(HidppKeyAction.Key(0x14), layer3.Single(r => r.Key == G1).Action);
        Assert.Equal(new HidppKeyAction(HidppKeyActionKind.Mouse, 0x10), layer3.Single(r => r.Key == G3).Action);
        Assert.Equal(HidppKeyAction.GShift, layer3.Single(r => r.Key == G4).Action);
        Assert.Equal(new HidppKeyAction(HidppKeyActionKind.Consumer, 0xCD), HidppKeyTable.Parse(keyboard.Files[2])!.Single().Action);
        Assert.True(keyboard.Enabled);
        var state = service.GetState();
        Assert.True(state.Written);
        Assert.True(state.Enabled);
        Assert.Single(state.Layers.GShift);

        // Kept across restarts.
        var again = LogitechKeyboardService.ForTest(new FakeKeyboard(), _dir).GetState();
        Assert.Equal(3, again.Layers.Base.Count);
        Assert.True(again.Written);
    }

    [Fact]
    public void SetLayers_RejectsBadInput_WithoutWriting()
    {
        var keyboard = new FakeKeyboard();
        var service = LogitechKeyboardService.ForTest(keyboard, _dir);

        Assert.Equal("invalid", service.SetLayers(new LogitechKeyboardLayersDto { Fn = { Row(G4, "gshift") } }).Status);
        Assert.Equal("invalid", service.SetLayers(new LogitechKeyboardLayersDto { Base = { Row(1, "keyboard", 4), Row(1, "keyboard", 5) } }).Status);
        Assert.Equal("invalid", service.SetLayers(new LogitechKeyboardLayersDto { Base = { Row(1, "macro", 4) } }).Status);
        Assert.Equal("invalid", service.SetLayers(new LogitechKeyboardLayersDto { Base = { Row(1, "keyboard", 0) } }).Status);
        Assert.Empty(keyboard.Files);
    }

    [Fact]
    public void FailedWrite_IsKept_AndRetriedOnTheNextConnection()
    {
        var keyboard = new FakeKeyboard { RefuseStore = true };
        var service = LogitechKeyboardService.ForTest(keyboard, _dir);

        var result = service.SetLayers(new LogitechKeyboardLayersDto { Base = { Row(G1, "keyboard", 0x0F) } });

        Assert.Equal("failed", result.Status);
        Assert.False(service.GetState().Written);
        keyboard.RefuseStore = false;
        keyboard.Reconnect();
        service.Tick();
        Assert.True(service.GetState().Written);
        Assert.Equal(HidppKeyAction.Key(0x0F), HidppKeyTable.Parse(keyboard.Files[1])!.Single().Action);
    }

    [Fact]
    public void Absent_KeepsTheLayers_AndWritesThemWhenTheKeyboardArrives()
    {
        var keyboard = new FakeKeyboard { Present = false };
        var service = LogitechKeyboardService.ForTest(keyboard, _dir);

        Assert.Equal("not-present", service.SetLayers(new LogitechKeyboardLayersDto { Base = { Row(G1, "disabled") } }).Status);
        keyboard.Present = true;
        keyboard.Reconnect();
        service.Tick();

        Assert.Equal(HidppKeyAction.Disabled, HidppKeyTable.Parse(keyboard.Files[1])!.Single().Action);
        Assert.True(service.GetState().Written);
    }

    [Fact]
    public void SetEnabled_FlipsTheSwitch()
    {
        var keyboard = new FakeKeyboard();
        var service = LogitechKeyboardService.ForTest(keyboard, _dir);

        Assert.True(service.SetEnabled(true).Ok);
        Assert.True(keyboard.Enabled);
        Assert.True(service.SetEnabled(false).Ok);
        Assert.False(keyboard.Enabled);
        Assert.False(service.GetState().Enabled);
    }

    // A G915 X: control list 0x1B10 (index 8), layout 0x4540 (10), files 0x8101 (12), key customization 0x1B05 (13).
    private sealed class FakeKeyboard : IHidppChannel
    {
        private const byte ControlList = 8, Layout = 10, Files8101 = 12, Fkc = 13;
        public readonly Dictionary<int, byte[]> Files = new();
        public readonly List<int> WriteOrder = new();
        public bool Present = true;
        public bool RefuseStore;
        public bool Enabled;
        private readonly int[] _cids = Enumerable.Range(0, 128).Select(i => i == G1 ? 399 : 100 + i).ToArray();
        private byte[]? _buffer;
        private int _filled;
        private int _chunks;

        public int Generation { get; private set; } = 1;

        public void Reconnect() => Generation++;

        public bool IsPresent() => Present;

        public byte? FeatureIndex(ushort featureId) => featureId switch
        {
            0x1B10 => ControlList,
            0x4540 => Layout,
            0x8101 => Files8101,
            0x1B05 => Fkc,
            _ => null,
        };

        public byte[]? Call(byte featureIndex, byte function, ReadOnlySpan<byte> p)
        {
            if (!Present)
            {
                return null;
            }
            var reply = new byte[16];
            switch (featureIndex, function)
            {
                case (ControlList, 0):
                    reply[0] = (byte)_cids.Length;
                    return reply;
                case (ControlList, 1):
                    for (var i = 0; i < 8; i++)
                    {
                        reply[2 * i] = (byte)(_cids[p[0] + i] >> 8);
                        reply[2 * i + 1] = (byte)_cids[p[0] + i];
                    }
                    return reply;
                case (Layout, 0):
                    reply[0] = 0x37;
                    return reply;
                case (Files8101, 2):
                    _buffer = new byte[p[0] << 8 | p[1]];
                    _filled = 0;
                    _chunks = 0;
                    return reply;
                case (Files8101, 3):
                    if (_buffer is null)
                    {
                        return null;
                    }
                    var take = Math.Min(16, _buffer.Length - _filled);
                    p[..take].CopyTo(_buffer.AsSpan(_filled));
                    _filled += take;
                    reply[1] = (byte)++_chunks;
                    return reply;
                case (Files8101, 9):
                    var size = p[5] << 8 | p[6];
                    var crc = (uint)(p[7] << 24 | p[8] << 16 | p[9] << 8 | p[10]);
                    if (RefuseStore || _buffer is null || _filled != size || p[0] != 0x1B || p[1] != 0x05 || p[3] != 0x02
                        || HidppKeyTable.Crc32(_buffer) != crc)
                    {
                        return null;
                    }
                    Files[p[2]] = _buffer;
                    WriteOrder.Add(p[2]);
                    _buffer = null;
                    return reply;
                case (Fkc, 1):
                    if (p[0] == 1)
                    {
                        Enabled = p[1] != 0;
                    }
                    reply[0] = (byte)(Enabled ? 1 : 0);
                    return reply;
                case (Fkc, 3):
                    Enabled = p[1] != 0;
                    reply[0] = (byte)(Enabled ? 1 : 0);
                    reply[1] = 1;
                    return reply;
            }
            return null;
        }
    }
}
