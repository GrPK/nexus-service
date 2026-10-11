using Nexus.Service.Lighting.Engine;

namespace Nexus.Service.Tests;

// Coordinates derive from the engine's canvas size (LightingEngine's
// CanvasBuffer ctor) and the logical frame space (CW/CH in
// SampleDevicesFromCanvas): at 160x90 over 1000x600, the RAM-stick frame
// below (300,100 40x250 rot 90) maps to canvas rect x=48 y=15 w=6.4 h=37.5
// with 10 LEDs pitched 3.75 rows apart down the x~51 centerline.
public class LightingEngineSamplingTests
{
    private sealed class PaintEffect : IEffect
    {
        private readonly Action<CanvasBuffer> _paint;
        public PaintEffect(Action<CanvasBuffer> paint) { _paint = paint; }
        public string Name => "paint";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) => _paint(canvas);
        public void Dispose() { }
    }

    private static async Task<byte[]> RenderOnce(DeviceFrame device, Action<CanvasBuffer> paint, bool fullFrame = false)
    {
        using var engine = new LightingEngine();
        engine.FullFrameSampling = fullFrame;
        engine.UpdateDevices(new[] { device });
        engine.FrameIntervalMs = 10;
        var tcs = new TaskCompletionSource<byte[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        engine.OnFrame += _ => tcs.TrySetResult(device.LedBytes.ToArray());
        engine.SetEffect(new PaintEffect(paint));
        var done = await Task.WhenAny(tcs.Task, Task.Delay(2000));
        Assert.Same(tcs.Task, done);
        return await tcs.Task;
    }

    private static (byte r, byte g, byte b) Led(byte[] leds, int i) => (leds[i * 3], leds[i * 3 + 1], leds[i * 3 + 2]);

    // A 250x40 strip turned a quarter clockwise about its centre: the same
    // 40x250 upright rect at x 300..340, first LED at the top.
    private static DeviceFrame MakeRam() => new(0, "ram", 10, x: 195, y: 205, w: 250, h: 40, rotation: 90);

    [Fact]
    public async Task SingleLed_GoesBlackWhenABlackBandCoversItsCentre()
    {
        // 1-LED frame maps to canvas rect x=96 y=30 w=16 h=15, centre (104,37).
        var device = new DeviceFrame(0, "one", 1, x: 600, y: 200, w: 100, h: 100);
        var leds = await RenderOnce(device, c =>
        {
            c.Fill(200, 100, 50);
            for (int y = 0; y < c.Height; y++)
                for (int x = 102; x <= 106; x++) { c.SetPixel(x, y, 0, 0, 0); }
        });
        Assert.Equal(((byte)0, (byte)0, (byte)0), Led(leds, 0));
    }

    [Fact]
    public async Task SingleLed_LitCentreGivesTheLitColour_EvenWithTheRestOfTheBoxDark()
    {
        var device = new DeviceFrame(0, "one", 1, x: 600, y: 200, w: 100, h: 100);
        var leds = await RenderOnce(device, c =>
        {
            c.Clear();
            for (int y = 36; y <= 38; y++)
                for (int x = 103; x <= 105; x++) { c.SetPixel(x, y, 255, 128, 0); }
        });
        Assert.Equal(((byte)255, (byte)128, (byte)0), Led(leds, 0));
    }

    [Fact]
    public async Task SingleLed_AveragesTheSpotBlackIncluded()
    {
        // Two of the spot's three columns lit at 150: the plain mean is 100.
        var device = new DeviceFrame(0, "one", 1, x: 600, y: 200, w: 100, h: 100);
        var leds = await RenderOnce(device, c =>
        {
            c.Clear();
            for (int y = 36; y <= 38; y++)
                for (int x = 104; x <= 105; x++) { c.SetPixel(x, y, 150, 0, 0); }
        });
        Assert.Equal((byte)100, Led(leds, 0).r);
    }

    [Fact]
    public async Task DenseStrip_DoesNotBlurBetweenNeighbouringLeds()
    {
        // 60 LEDs over 250 units (40 canvas px) sit under a pixel apart, so the
        // cell, not the 3 px spot, bounds the read: a one pixel column lights
        // its own LEDs at full strength and never a neighbour further away.
        var strip = new DeviceFrame(0, "dense", 60, x: 300, y: 290, w: 250, h: 20, rotation: 0);
        var leds = await RenderOnce(strip, c =>
        {
            c.Clear();
            for (int y = 0; y < c.Height; y++) { c.SetPixel(80, y, 255, 255, 255); }
        });
        var lit = new List<int>();
        for (int i = 0; i < 60; i++) { if (Led(leds, i).r > 0) lit.Add(i); }
        Assert.NotEmpty(lit);
        Assert.True(lit.Count <= 2, $"lit LEDs: {string.Join(",", lit)}");
        Assert.All(lit, i => Assert.Equal((byte)255, Led(leds, i).r));
    }

    [Fact]
    public async Task Spot_ClampsAtTheCanvasEdge()
    {
        // A 1-LED frame in the top-left corner reads only in-canvas pixels.
        var device = new DeviceFrame(0, "corner", 1, x: 0, y: 0, w: 4, h: 4);
        var leds = await RenderOnce(device, c => c.Fill(90, 90, 90));
        Assert.Equal(((byte)90, (byte)90, (byte)90), Led(leds, 0));

        var far = new DeviceFrame(0, "far", 1, x: 996, y: 596, w: 4, h: 4);
        var farLeds = await RenderOnce(far, c => c.Fill(90, 90, 90));
        Assert.Equal(((byte)90, (byte)90, (byte)90), Led(farLeds, 0));
    }

    [Fact]
    public async Task LinearStrip_TurnedFortyFiveDegrees_SamplesAlongTheDiagonal()
    {
        // A 200x20 strip centred at (500,300) units, turned 45 degrees: its two
        // LEDs sit 100 units from the centre along the diagonal, so in the
        // 160x90 canvas the last lands at (91,55) and the first near (69,34).
        var strip = new DeviceFrame(0, "diag", 2, x: 400, y: 290, w: 200, h: 20, rotation: 45);
        var leds = await RenderOnce(strip, c =>
        {
            c.Clear();
            for (int y = 54; y <= 56; y++)
                for (int x = 90; x <= 92; x++) { c.SetPixel(x, y, 0, 200, 255); }
        });
        Assert.Equal(((byte)0, (byte)0, (byte)0), Led(leds, 0));
        Assert.Equal(((byte)0, (byte)200, (byte)255), Led(leds, 1));
    }

    [Fact]
    public async Task AllDarkCanvas_AllLedsBlack()
    {
        var leds = await RenderOnce(MakeRam(), c => c.Clear());
        Assert.All(leds, v => Assert.Equal(0, v));
    }

    [Fact]
    public async Task DisabledLed_StaysBlack_WhenItsCellIsLit()
    {
        var device = MakeRam();
        device.LedDisabled = new bool[10];
        device.LedDisabled[3] = true;
        var leds = await RenderOnce(device, c =>
        {
            c.Clear();
            for (int y = 16; y <= 51; y++)
                for (int x = 49; x <= 53; x++) { c.SetPixel(x, y, 255, 0, 180); }
        });
        Assert.Equal(((byte)0, (byte)0, (byte)0), Led(leds, 3));
        Assert.Equal(((byte)255, (byte)0, (byte)180), Led(leds, 2));
    }

    [Fact]
    public async Task UvLayout_SamplesCellAroundEachLed()
    {
        // 6x4 UV grid on a frame mapping to canvas rect x=16 y=45 w=48 h=18.
        var device = new DeviceFrame(0, "kbd", 24, x: 100, y: 300, w: 300, h: 120);
        var u = new float[24];
        var v = new float[24];
        for (int row = 0; row < 4; row++)
        {
            for (int col = 0; col < 6; col++)
            {
                u[row * 6 + col] = (col + 0.5f) / 6f;
                v[row * 6 + col] = (row + 0.5f) / 4f;
            }
        }
        device.LedU = u;
        device.LedV = v;

        // The 3x3 spot around LED 8's point (36,51), inside its cell and no
        // other LED's.
        var leds = await RenderOnce(device, c =>
        {
            c.Clear();
            for (int y = 50; y <= 52; y++)
                for (int x = 35; x <= 37; x++) { c.SetPixel(x, y, 0, 255, 0); }
        });
        Assert.Equal(((byte)0, (byte)255, (byte)0), Led(leds, 8));
        for (int i = 0; i < 24; i++)
        {
            if (i == 8) continue;
            Assert.Equal(((byte)0, (byte)0, (byte)0), Led(leds, i));
        }
    }

    // Simple mode's sweep set: every device reads the whole canvas, so a small
    // frame parked in one corner still shows the pattern end to end.
    [Fact]
    public async Task FullFrameSampling_StripReadsWholeCanvas()
    {
        // A small frame parked in the canvas's top-left corner, over a canvas
        // painted red on the left half and blue on the right.
        var device = new DeviceFrame(0, "strip", 10, x: 20, y: 20, w: 100, h: 20);
        void Paint(CanvasBuffer c)
        {
            c.Clear();
            for (int y = 0; y < 90; y++)
            {
                for (int x = 0; x < 160; x++) { c.SetPixel(x, y, x < 80 ? (byte)255 : (byte)0, 0, x < 80 ? (byte)0 : (byte)255); }
            }
        }
        var full = await RenderOnce(device, Paint, fullFrame: true);
        Assert.Equal(((byte)255, (byte)0, (byte)0), Led(full, 0));
        Assert.Equal(((byte)0, (byte)0, (byte)255), Led(full, 9));

        // Same frame without the flag reads its own rect, which sits entirely
        // in the red half - the difference the flag makes, not the paint.
        var placed = await RenderOnce(device, Paint);
        Assert.Equal(((byte)255, (byte)0, (byte)0), Led(placed, 0));
        Assert.Equal(((byte)255, (byte)0, (byte)0), Led(placed, 9));
    }

    [Fact]
    public async Task FullFrameSampling_KeepsTheGapsBetweenBands()
    {
        // Ten LEDs over the whole canvas width sit a pitch apart that is wider
        // than the unlit gap between these bands, so a cell always overlaps a
        // band. a lit-pixel weighted read would hand every LED a fully lit red; the
        // plain cell mean keeps the ones sitting over mostly-gap dark.
        var device = new DeviceFrame(0, "strip", 10, x: 20, y: 20, w: 100, h: 20);
        void Bands(CanvasBuffer c)
        {
            c.Clear();
            for (int y = 0; y < c.Height; y++)
            {
                for (int x = 0; x < c.Width; x++)
                {
                    if (x % 16 < 8) { c.SetPixel(x, y, 255, 0, 0); }
                }
            }
        }
        var full = await RenderOnce(device, Bands, fullFrame: true);
        // The band period divides the canvas width a whole number of times, so
        // each cell holds a fixed share of lit pixels and the reading is the
        // same every frame. The two end LEDs clamp against the canvas edge onto
        // a half cell - the first wholly over a band, the last wholly over a
        // gap - which is what pins the mapping: read over the device's own
        // 100-unit rect instead, or through a lit-weighted read, and none of
        // these numbers survive (the weighted read returns 255 throughout,
        // because every cell touches a band).
        var reds = new byte[10];
        for (int i = 0; i < 10; i++)
        {
            var (r, g, b) = Led(full, i);
            reds[i] = r;
            Assert.Equal((byte)0, g);
            Assert.Equal((byte)0, b);
        }
        Assert.Equal(new byte[] { 255, 120, 120, 120, 120, 135, 135, 135, 135, 0 }, reds);
    }

    [Fact]
    public async Task FullFrameSampling_UvDeviceKeepsTheGapsToo()
    {
        // Same bands, but a device carrying per-LED UVs so the read goes down
        // the UV branch. Its cell would otherwise come from the cols x rows
        // density estimate, which under full frame spreads cols over the whole
        // canvas and hands a strip-shaped device a cell an order of magnitude
        // too wide - the box mean then washes the bands into one flat tone.
        var device = new DeviceFrame(0, "uv", 10, x: 20, y: 20, w: 100, h: 20);
        var u = new float[10];
        var v = new float[10];
        for (int i = 0; i < 10; i++) { u[i] = i / 9f; v[i] = 0.5f; }
        device.LedU = u;
        device.LedV = v;
        var full = await RenderOnce(device, c =>
        {
            c.Clear();
            for (int y = 0; y < c.Height; y++)
            {
                for (int x = 0; x < c.Width; x++)
                {
                    if (x % 16 < 8) { c.SetPixel(x, y, 255, 0, 0); }
                }
            }
        }, fullFrame: true);
        var reds = new byte[10];
        for (int i = 0; i < 10; i++) { reds[i] = Led(full, i).r; }
        // Both extremes have to survive; the grid-estimate cell returned a flat
        // mid-red across all ten.
        Assert.Contains(reds, r => r == 0);
        Assert.Contains(reds, r => r == 255);
    }

}
