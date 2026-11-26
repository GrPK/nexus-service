using System;
using System.IO;
using Nexus.Service.Peripherals.LogitechHidpp;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechHidpp;

public class GHubDeviceImagesTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nexus-ghub-test-" + Guid.NewGuid().ToString("N"));
    private readonly string _previous = GHubDeviceImages.DepotsRoot;

    public GHubDeviceImagesTests()
    {
        GHubDeviceImages.DepotsRoot = _root;
    }

    public void Dispose()
    {
        GHubDeviceImages.DepotsRoot = _previous;
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (DirectoryNotFoundException)
        {
        }
    }

    private static readonly byte[] Webp = { (byte)'R', (byte)'I', (byte)'F', (byte)'F', 0, 0, 0, 0, (byte)'W', (byte)'E', (byte)'B', (byte)'P', (byte)'V', (byte)'P', (byte)'8', (byte)'X' };

    private void Depot(string name, string model, string src, byte[] image, string metadata)
    {
        var dir = Path.Combine(_root, "887386", name);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, src))!);
        File.WriteAllText(Path.Combine(dir, "manifest.json"),
            $"{{\"devices\":[{{\"modelId\":\"{model}\",\"resources\":[{{\"key\":\"device_image\",\"src\":\"{src}\"}}]}}]}}");
        File.WriteAllBytes(Path.Combine(dir, src), image);
        File.WriteAllText(Path.Combine(dir, "metadata.json"), metadata);
    }

    [Fact]
    public void Get_PicksTheLayoutVariant_AndSkipsEncryptedPictures()
    {
        var model = "kb" + Guid.NewGuid().ToString("N");
        Depot("ansi", model, "front.webp", Webp, "{\"inputs\":[{\"cid\": 286}]}");
        Depot("iso", model, "front.webp", Webp, "{\"inputs\":[{\"cid\": 361}]}");
        var mouse = "ms" + Guid.NewGuid().ToString("N");
        Depot("enc", mouse, "images/device/abc", new byte[] { 0x21, 0x05, 0x21, 0x20, 0x82, 0, 0, 0, (byte)'{' }, "{}");

        var iso = GHubDeviceImages.Get(model, "device_image", "\"cid\":361");

        Assert.NotNull(iso);
        Assert.Equal("image/webp", iso!.Value.ContentType);
        Assert.Null(GHubDeviceImages.Get(model, "device_image", "\"cid\":999"));
        Assert.Null(GHubDeviceImages.Get(mouse, "device_image"));
        Assert.Null(GHubDeviceImages.Get("missing-" + model, "device_image"));
    }
}
