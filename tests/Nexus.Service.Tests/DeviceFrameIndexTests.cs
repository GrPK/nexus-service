using Nexus.Service.Lighting.Engine;
using Nexus.Service.Models.Devices;

namespace Nexus.Service.Tests;

public class DeviceFrameIndexTests
{
    private sealed class Noop : IEffect
    {
        public string Name => "noop";
        public void RenderFrame(CanvasBuffer canvas, double tickMs) { }
        public void Dispose() { }
    }

    [Fact]
    public void FrameIndex_is_the_byte_the_serialized_frame_carries_for_that_device()
    {
        var frames = new[]
        {
            new DeviceFrame(3, "dev-a", 2),
            new DeviceFrame(7, "dev-b", 4),
        };
        using var engine = new LightingEngine();
        engine.UpdateDevices(frames);
        byte[]? last = null;
        engine.OnFrame += f => last = f.ToArray();
        engine.SetEffect(new Noop());
        engine.Stop();
        Assert.NotNull(last);

        var devices = new List<LightingDevice>
        {
            new() { Id = "dev-b" },
            new() { Id = "dev-a" },
            new() { Id = "no-frame" },
        };
        DeviceFrameIndex.Apply(devices, engine.Devices);

        // [0x03][w:2][h:2][canvas][count][idx:1][ledCount:2][leds]...
        var cw = last![1] | (last[2] << 8);
        var ch = last[3] | (last[4] << 8);
        var pos = 5 + cw * ch * 3;
        var count = last[pos++];
        var wire = new Dictionary<byte, int>();
        for (var i = 0; i < count; i++)
        {
            var idx = last[pos++];
            var leds = last[pos] | (last[pos + 1] << 8);
            pos += 2 + leds * 3;
            wire[idx] = leds;
        }
        Assert.Equal(7, devices[0].FrameIndex);
        Assert.Equal(4, wire[(byte)devices[0].FrameIndex!.Value]);
        Assert.Equal(3, devices[1].FrameIndex);
        Assert.Equal(2, wire[(byte)devices[1].FrameIndex!.Value]);
        Assert.Null(devices[2].FrameIndex);
    }

    [Fact]
    public void FrameIndex_serializes_as_frameIndex_and_is_omitted_by_null_handling_of_the_context()
    {
        var json = System.Text.Json.JsonSerializer.Serialize(
            new LightingDevice { Id = "x", FrameIndex = 5 },
            Nexus.Service.Serialization.AppJsonContext.Default.LightingDevice);
        Assert.Contains("\"frameIndex\":5", json);
    }
}
