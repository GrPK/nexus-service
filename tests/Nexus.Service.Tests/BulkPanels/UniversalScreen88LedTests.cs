using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Devices;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Lighting;
using Nexus.Service.Lighting.Rgb;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Xunit;

namespace Nexus.Service.Tests.BulkPanels;

public class UniversalScreen88LedTests
{
    private static readonly (byte, byte, byte)[] SixColors =
    {
        (255, 1, 127), (5, 200, 17), (90, 33, 240), (18, 77, 150), (200, 180, 0), (0, 128, 255),
    };

    // FNV-1a over every frame at L-Connect's five brightness stops (0, 63, 127, 191, 255) x both
    // directions, from L-Connect 2.1.29's Universal Screen 8.8 renderer (RgbEffect.L88Mode1..19) with
    // these six colours; Twinkle's random colour per LED drawn from Random(7) on both sides.
    [Theory]
    [InlineData(UniversalScreen88LedEffects.Rainbow, 0xc2ef7f42f8319aa5UL)]
    [InlineData(UniversalScreen88LedEffects.Wave, 0x7f33a6d01d129ad5UL)]
    [InlineData(UniversalScreen88LedEffects.Static, 0x723e951f09cce985UL)]
    [InlineData(UniversalScreen88LedEffects.Breathing, 0x55a3377ca3339cf5UL)]
    [InlineData(UniversalScreen88LedEffects.RainbowMorph, 0x4f748ec85450347dUL)]
    [InlineData(UniversalScreen88LedEffects.Paint, 0x920549ecd1ad2ff5UL)]
    [InlineData(UniversalScreen88LedEffects.Runway, 0x8e49db6e7635e745UL)]
    [InlineData(UniversalScreen88LedEffects.Tide, 0xed1deb4aac7bb3b5UL)]
    [InlineData(UniversalScreen88LedEffects.BlowUp, 0x5bc529ea14aa9d71UL)]
    [InlineData(UniversalScreen88LedEffects.Meteor, 0x9478f1060fa13dfdUL)]
    [InlineData(UniversalScreen88LedEffects.Snooker, 0x871d2d2485bb3405UL)]
    [InlineData(UniversalScreen88LedEffects.Mixing, 0x4963919de106c0a9UL)]
    [InlineData(UniversalScreen88LedEffects.PingPong, 0x0459f38e00eeab6dUL)]
    [InlineData(UniversalScreen88LedEffects.BulletStack, 0x73846e3b868053b9UL)]
    [InlineData(UniversalScreen88LedEffects.Twinkle, 0xec8c6097bf3fd241UL)]
    [InlineData(UniversalScreen88LedEffects.River, 0xbd35a4c8cea85f65UL)]
    [InlineData(UniversalScreen88LedEffects.Hourglass, 0x2a6866c80903a1e5UL)]
    [InlineData(UniversalScreen88LedEffects.ElectricCurrent, 0x6fe7be0d1bf5da55UL)]
    [InlineData(UniversalScreen88LedEffects.RainbowWave, 0x76545b5d8cfbdebdUL)]
    public void Frames_match_the_vendor_effect(string mode, ulong expected)
    {
        ulong hash = 0xcbf29ce484222325;
        foreach (var brightness in new[] { 0, 63, 127, 191, 255 })
        {
            foreach (var reverse in new[] { false, true })
            {
                var frames = UniversalScreen88LedEffects.Render(mode, SixColors, brightness, reverse, new Random(7));
                hash = Fnv(hash, BitConverter.GetBytes(frames.Count));
                foreach (var frame in frames)
                {
                    Assert.Equal(UniversalScreen88LedEffects.LedCount * 3, frame.Length);
                    hash = Fnv(hash, frame);
                }
            }
        }
        Assert.Equal(expected, hash);
    }

    // L-Connect: interval = (int)(step x base), sleep (int)(interval x 0.625) between frames.
    [Theory]
    [InlineData(UniversalScreen88LedEffects.Rainbow, 7, 71)]
    [InlineData(UniversalScreen88LedEffects.Rainbow, 4, 41)]
    [InlineData(UniversalScreen88LedEffects.Rainbow, 3, 30)]
    [InlineData(UniversalScreen88LedEffects.Wave, 7, 62)]
    [InlineData(UniversalScreen88LedEffects.Wave, 5, 44)]
    [InlineData(UniversalScreen88LedEffects.River, 3, 26)]
    [InlineData(UniversalScreen88LedEffects.Meteor, 7, 48)]
    [InlineData(UniversalScreen88LedEffects.Meteor, 4, 27)]
    [InlineData(UniversalScreen88LedEffects.Meteor, 3, 20)]
    public void Frame_period_follows_the_vendor_interval(string mode, int step, int periodMs)
    {
        Assert.Equal(periodMs, UniversalScreen88LedEffects.FramePeriodMs(mode, step));
    }

    [Theory]
    [InlineData(0, 7, 0)]
    [InlineData(1, 6, 63)]
    [InlineData(2, 5, 127)]
    [InlineData(3, 4, 191)]
    [InlineData(4, 3, 255)]
    public void Levels_follow_the_vendor_sliders(int level, int step, int brightness)
    {
        Assert.Equal(step, UniversalScreen88LedEffects.SpeedStep(UniversalScreen88LedEffects.SliderPercent(level)));
        Assert.Equal(brightness, UniversalScreen88LedEffects.BrightnessByte(UniversalScreen88LedEffects.SliderPercent(level)));
    }

    [Fact]
    public void Controls_and_defaults_match_the_vendor_app()
    {
        Assert.Equal(19, UniversalScreen88LedEffects.Modes.Count);
        Assert.Equal(6, UniversalScreen88LedEffects.ColorCount(UniversalScreen88LedEffects.Wave));
        Assert.Equal(2, UniversalScreen88LedEffects.ColorCount(UniversalScreen88LedEffects.Runway));
        Assert.Equal(4, UniversalScreen88LedEffects.ColorCount(UniversalScreen88LedEffects.ElectricCurrent));
        Assert.Equal(0, UniversalScreen88LedEffects.ColorCount(UniversalScreen88LedEffects.BulletStack));
        // This screen's Static keeps a speed in L-Connect, so the slider shows there too.
        Assert.True(UniversalScreen88LedEffects.HasSpeed(UniversalScreen88LedEffects.Static));
        Assert.False(UniversalScreen88LedEffects.HasDirection(UniversalScreen88LedEffects.Tide));
        Assert.True(UniversalScreen88LedEffects.HasDirection(UniversalScreen88LedEffects.River));
        Assert.Equal(new[] { "#ff0096", "#0000ff", "#fff026", "#00ff00" }, UniversalScreen88LedEffects.DefaultColors(UniversalScreen88LedEffects.Hourglass));
        var unset = UniversalScreen88LedEffects.Render(UniversalScreen88LedEffects.Runway, [], 255, false);
        var vendor = UniversalScreen88LedEffects.Render(UniversalScreen88LedEffects.Runway, [(255, 0, 0), (0, 255, 0)], 255, false);
        Assert.Equal(vendor.SelectMany(f => f), unset.SelectMany(f => f));
    }

    [Fact]
    public void Twinkle_draws_its_own_palette_like_the_vendor()
    {
        var a = UniversalScreen88LedEffects.Render(UniversalScreen88LedEffects.Twinkle, [], 255, false, new Random(1));
        var b = UniversalScreen88LedEffects.Render(UniversalScreen88LedEffects.Twinkle, [], 255, false, new Random(1));
        Assert.Equal(200, a.Count);
        Assert.Equal(a.SelectMany(f => f), b.SelectMany(f => f));
    }

    [Fact]
    public void Led_frame_travels_as_three_chunks_of_twenty()
    {
        var rgb = new byte[UniversalScreen88LedProtocol.LedCount * 3];
        for (int i = 0; i < rgb.Length; i++) rgb[i] = (byte)i;
        var chunks = UniversalScreen88LedProtocol.EncodeLedFrame(rgb);
        Assert.Equal(3, chunks.Length);
        for (int c = 0; c < 3; c++)
        {
            Assert.Equal(64, chunks[c].Length);
            Assert.Equal(0x11, chunks[c][0]);
            Assert.Equal(c * 20, chunks[c][1]);
            Assert.Equal(0, chunks[c][2]);
            Assert.Equal(0, chunks[c][3]);
            Assert.Equal(rgb.Skip(c * 60).Take(60), chunks[c].Skip(4).Take(60));
        }
        Assert.Equal("1_3", UniversalScreen88LedProtocol.DecodeVersion(new byte[] { 0x10, 1, 3 }));
        Assert.Null(UniversalScreen88LedProtocol.DecodeVersion(new byte[] { 0x11, 1, 3 }));
    }

    [Fact]
    public void Board_reads_the_version_then_sends_only_changed_frames()
    {
        var pipe = new LedPipe();
        var store = new MemoryStore();
        var board = new UniversalScreen88LedBoard(new PipeFactory(pipe), new DeviceControlGate(store), new HardwarePresence(new NoUsb()));
        Assert.True(board.TryConnect());
        Assert.Equal("2_1", board.Firmware);
        Assert.Equal(UniversalScreen88LedProtocol.Version, pipe.Writes[0][0]);

        var frame = new byte[UniversalScreen88LedProtocol.LedCount * 3];
        frame[0] = 200;
        board.SetLeds(frame);
        board.SendLedsIfDirty();
        board.SetLeds(frame);
        board.SendLedsIfDirty();
        var ledWrites = pipe.Writes.Where(w => w[0] == UniversalScreen88LedProtocol.LedFrame).ToList();
        Assert.Equal(3, ledWrites.Count);
        Assert.Equal(200, ledWrites[0][4]);
    }

    [Fact]
    public void Writer_plays_the_bezel_stretch_of_a_merged_Quick_Sync_effect_on_the_shared_clock()
    {
        var pipe = new LedPipe();
        var store = new MemoryStore();
        var board = new UniversalScreen88LedBoard(new PipeFactory(pipe), new DeviceControlGate(store), new HardwarePresence(new NoUsb()));
        Assert.True(board.TryConnect());
        var curve = new HydroShift2CurveBoard(new PipeFactory(new LedPipe()), new DeviceControlGate(store), new HardwarePresence(new NoUsb()), store);
        var hub = new Nexus.Service.Peripherals.LianLiWireless.Slv3Hub(new Nexus.Service.Tests.LianLiWireless.Slv3TestHub.FakeDiscovery(), _ => throw new InvalidOperationException());
        long now = 5000;
        var merge = new LianLiMergeService(store, hub, curve, board, () => now);
        store.Update(s => s.Devices.LianLiSync = new LianLiSyncSettings { Enabled = true, Effect = "mergeMeteor" });
        using var engine = new Nexus.Service.Lighting.Engine.LightingEngine();
        var writer = new UniversalScreen88LedLightingFrameWriter(engine, board, store, new Np50IdentifyTracker(), merge: merge);

        var wait = writer.Tick();
        var share = merge.Current(store.Load())!.Outputs[LianLiMergeService.Screen88Id];
        Assert.Equal(share.PeriodMs, wait);
        Assert.Equal(share.Frames![0], SentFrame(board, pipe));

        now += (share.PeriodMs * 3) + 1;
        writer.Tick();
        Assert.Equal(share.Frames[3], SentFrame(board, pipe));
        // First seen, so stored at the end of the merge order.
        Assert.Equal(new[] { LianLiMergeService.Screen88Id }, store.Load().Devices.LianLiSync.MergeDevices.Select(m => m.Id));
    }

    private static byte[] SentFrame(UniversalScreen88LedBoard board, LedPipe pipe)
    {
        board.SendLedsIfDirty();
        var chunks = pipe.Writes.Where(w => w[0] == UniversalScreen88LedProtocol.LedFrame).TakeLast(3).ToList();
        return chunks.SelectMany(c => c.Skip(4).Take(60)).ToArray();
    }

    [Fact]
    public void The_bundled_OpenRGB_detector_is_held_off_for_the_bezel_only_while_the_ring_is_native()
    {
        var claim = NativeDetectorClaims.All.Single(c => c.Key == NativeDetectorClaims.UniversalScreen88);
        Assert.Equal(new[] { "Lian Li Universal Screen" }, claim.Detectors);
        Assert.Equal(new[] { UniversalScreen88Driver.Id }, claim.Handlers);
        Assert.Contains("Lian Li Universal Screen", NativeDetectorClaims.ResolveDisabled(_ => true, macOS: false));
        Assert.DoesNotContain("Lian Li Universal Screen", NativeDetectorClaims.ResolveDisabled(_ => false, macOS: false));
    }

    [Fact]
    public void Writer_render_applies_the_palette_and_colour_order()
    {
        var settings = new UniversalScreen88LightingSettings
        {
            Mode = UniversalScreen88LedEffects.Static,
            Colors = new List<string> { "#ff8000" },
        };
        var rgb = UniversalScreen88LedLightingFrameWriter.Render(settings, 255, false, null);
        Assert.Equal(new byte[] { 254, 127, 0 }, rgb[0][..3]);
        var grb = UniversalScreen88LedLightingFrameWriter.Render(settings, 255, false, "GRB");
        Assert.Equal(new byte[] { 127, 254, 0 }, grb[0][..3]);
    }

    private static ulong Fnv(ulong hash, IEnumerable<byte> bytes)
    {
        foreach (var b in bytes)
        {
            hash = (hash ^ b) * 0x100000001b3;
        }
        return hash;
    }

    private sealed class LedPipe : IBulkUsbPipe
    {
        private readonly Queue<byte[]> _replies = new();

        public List<byte[]> Writes { get; } = new();

        public bool Write(ReadOnlySpan<byte> data)
        {
            var copy = data.ToArray();
            Writes.Add(copy);
            if (copy[0] == UniversalScreen88LedProtocol.Version)
            {
                var reply = new byte[64];
                reply[0] = UniversalScreen88LedProtocol.Version;
                reply[1] = 2;
                reply[2] = 1;
                _replies.Enqueue(reply);
            }
            return true;
        }

        public bool Write(byte pipeId, ReadOnlySpan<byte> data) => Write(data);

        public int Read(Span<byte> buffer, int timeoutMs)
        {
            if (_replies.Count == 0) return 0;
            var reply = _replies.Dequeue();
            reply.CopyTo(buffer);
            return reply.Length;
        }

        public void Dispose() { }
    }

    private sealed class PipeFactory : IBulkUsbPipeFactory
    {
        private readonly IBulkUsbPipe _pipe;
        public PipeFactory(IBulkUsbPipe pipe) => _pipe = pipe;
        public IBulkUsbPipe? Open(int vendorId, int productId, byte writePipeId, byte readPipeId) => _pipe;
    }

    private sealed class NoUsb : IUsbEnumerator
    {
        public List<UsbDeviceEntry> Enumerate() => new();
    }

    private sealed class MemoryStore : IConfigStore
    {
        private readonly NexusSettings _settings = new();
        public string SettingsPath => ":memory:";
        public NexusSettings Load() => _settings;
        public void Update(Action<NexusSettings> mutator) { mutator(_settings); OnChanged?.Invoke(); }
        public void Reload() { }
        public void FlushNow() { }
        public event Action? OnChanged;
    }
}
