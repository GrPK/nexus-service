using System;
using System.IO;
using System.Linq;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public sealed class DiskBlobCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-blobcache-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        { Directory.Delete(_dir, true); }
        catch { }
    }

    [Fact]
    public void Round_trips_and_misses_cleanly()
    {
        var c = new DiskBlobCache(_dir, 1024);

        Assert.Null(c.TryGet("aa"));
        c.Put("aa", new byte[] { 1, 2, 3 });

        Assert.Equal(new byte[] { 1, 2, 3 }, c.TryGet("aa"));
    }

    [Fact]
    public void Oversize_and_empty_values_are_not_stored()
    {
        var c = new DiskBlobCache(_dir, 8);

        c.Put("big", new byte[9]);
        c.Put("empty", ReadOnlySpan<byte>.Empty);

        Assert.Null(c.TryGet("big"));
        Assert.Null(c.TryGet("empty"));
    }

    [Fact]
    public void Exceeding_the_cap_evicts_least_recently_used_first()
    {
        var c = new DiskBlobCache(_dir, 30);
        c.Put("old", new byte[10]);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "old"), DateTime.UtcNow.AddMinutes(-10));
        c.Put("mid", new byte[10]);
        File.SetLastWriteTimeUtc(Path.Combine(_dir, "mid"), DateTime.UtcNow.AddMinutes(-5));
        c.Put("new", new byte[10]);

        c.Put("extra", new byte[10]);

        Assert.Null(c.TryGet("old"));
        Assert.NotNull(c.TryGet("mid"));
        Assert.NotNull(c.TryGet("new"));
        Assert.NotNull(c.TryGet("extra"));
        Assert.True(new DirectoryInfo(_dir).EnumerateFiles().Sum(f => f.Length) <= 30);
    }

    [Fact]
    public void Overwriting_a_name_does_not_double_count_its_size()
    {
        var c = new DiskBlobCache(_dir, 25);

        for (var i = 0; i < 5; i++)
        {
            c.Put("same", new byte[20]);
        }

        Assert.NotNull(c.TryGet("same"));
    }

    [Fact]
    public void Hex_name_is_lowercase_and_stable()
    {
        Assert.Equal("00ff10", DiskBlobCache.HexName(new byte[] { 0x00, 0xFF, 0x10 }));
    }

    [Theory]
    [InlineData("fire", 0, false, "t", 1, "fire", 0, false, "t", 1, true)]
    [InlineData("fire", 0, false, "t", 1, "fire", 1, false, "t", 1, false)]
    [InlineData("fire", 0, false, "t", 1, "fire", 0, true, "t", 1, false)]
    [InlineData("fire", 0, false, "t", 1, "plasma", 0, false, "t", 1, false)]
    [InlineData("fire", 0, false, "t", 1, "fire", 0, false, "u", 1, false)]
    [InlineData("fire", 0, false, "t", 1, "fire", 0, false, "t", 2, false)]
    public void Thumbnail_name_changes_with_every_input(
        string e1, int s1, bool f1, string t1, int v1,
        string e2, int s2, bool f2, string t2, int v2, bool same)
    {
        var a = DiskBlobCache.ThumbnailName(e1, s1, f1, t1, v1);
        var b = DiskBlobCache.ThumbnailName(e2, s2, f2, t2, v2);

        Assert.Equal(same, a == b);
    }
}
