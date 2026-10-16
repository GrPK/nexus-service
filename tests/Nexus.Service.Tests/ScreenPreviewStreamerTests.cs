using Nexus.Service.Lighting.Capture;
using Nexus.Service.Lighting.Engine;
using Nexus.Service.Sockets;

namespace Nexus.Service.Tests;

public class ScreenPreviewStreamerTests
{
    private sealed class FakeSource : IScreenFrameSource
    {
        public int Starts, Stops;
        public string? LastMonitor;
        public void Start(string monitorId, int width, int height) { Starts++; LastMonitor = monitorId; }
        public void Stop() { Stops++; }
        public byte[]? TryAcquireFrame(out int width, out int height)
        {
            width = 16;
            height = 9;
            return new byte[16 * 9 * 3];
        }
    }

    private static bool WaitFor(Func<bool> cond)
    {
        for (var i = 0; i < 100 && !cond(); i++) Thread.Sleep(20);
        return cond();
    }

    private static ScreenPreviewStreamer Make(SharedScreenFrameSource shared, Func<string> monitor)
        => new(new ScreenPreviewHub(), shared.CreateHandle, monitor, new PostProcessState());

    [Fact]
    public void NoClients_NoCapture()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        using var streamer = Make(shared, () => "m1");
        Thread.Sleep(250);
        Assert.Equal(0, inner.Starts);
        Assert.Equal(0, shared.ActiveHandles);
    }

    [Fact]
    public void Subscribe_OpensCapture_Unsubscribe_ReleasesHandle()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        using var streamer = Make(shared, () => "m1");
        streamer.Start();
        Assert.True(WaitFor(() => shared.ActiveHandles == 1));
        Assert.Equal("m1", inner.LastMonitor);
        streamer.Stop();
        Assert.Equal(0, shared.ActiveHandles);
        Assert.Equal(1, inner.Stops);
    }

    [Fact]
    public void SharesCaptureWithGlobalMirror_NeverDoubled_AndKeepsItOpen()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        var global = shared.CreateHandle();
        global.Start("m1", 160, 90);
        using var streamer = Make(shared, () => "m1");
        streamer.Start();
        Assert.True(WaitFor(() => shared.ActiveHandles == 2));
        Assert.Equal(1, inner.Starts);
        streamer.Stop();
        Assert.Equal(1, shared.ActiveHandles);
        Assert.Equal(0, inner.Stops);
        global.Stop();
        Assert.Equal(1, inner.Stops);
    }

    [Fact]
    public void MonitorChange_ReopensOnNewMonitor()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        var monitor = "m1";
        using var streamer = Make(shared, () => monitor);
        streamer.Start();
        Assert.True(WaitFor(() => inner.LastMonitor == "m1"));
        monitor = "m2";
        Assert.True(WaitFor(() => inner.LastMonitor == "m2"));
        streamer.Stop();
        Assert.Equal(0, shared.ActiveHandles);
    }

    [Fact]
    public void StopThenStart_RunsASingleLoop()
    {
        var inner = new FakeSource();
        var shared = new SharedScreenFrameSource(inner);
        using var streamer = Make(shared, () => "m1");
        streamer.Start();
        Assert.True(WaitFor(() => shared.ActiveHandles == 1));
        streamer.Stop();
        streamer.Start();
        Assert.True(WaitFor(() => shared.ActiveHandles == 1));
        streamer.Stop();
        Assert.Equal(0, shared.ActiveHandles);
    }
}
