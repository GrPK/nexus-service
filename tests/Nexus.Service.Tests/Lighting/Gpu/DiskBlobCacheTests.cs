using System;
using System.IO;
using System.Linq;
using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public sealed class DiskBlobCacheTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-blobcache-" + Guid.NewGuid().ToString("N"));
    private static readonly byte[] KeyA = Enumerable.Range(1, 32).Select(i => (byte)i).ToArray();
    private static readonly byte[] KeyB = Enumerable.Range(101, 32).Select(i => (byte)i).ToArray();

    public void Dispose()
    {
        try
        { Directory.Delete(_dir, true); }
        catch { }
    }

    private DiskBlobCache Make(long cap = 1024, byte[]? key = null, bool fresh = false, long maxQueued = DiskBlobCache.DefaultMaxQueuedBytes, string purpose = "p")
    {
        var c = new DiskBlobCache(_dir, cap, purpose, () => new BlobKey(key ?? KeyA, fresh), maxQueued);
        Assert.True(c.WaitReady(TimeSpan.FromSeconds(10)));
        return c;
    }

    [Fact]
    public void Round_trips_and_misses_cleanly()
    {
        var c = Make();

        Assert.Null(c.TryGet("aa"));
        c.Put("aa", new byte[] { 1, 2, 3 });
        c.Flush();

        Assert.Equal(new byte[] { 1, 2, 3 }, c.TryGet("aa"));
        Assert.Equal(35, new FileInfo(Path.Combine(_dir, "aa")).Length);
    }

    [Fact]
    public void No_key_means_no_cache()
    {
        var c = new DiskBlobCache(_dir, 1024, "p", () => null);

        Assert.False(c.WaitReady(TimeSpan.FromSeconds(10)));
        c.Put("aa", new byte[] { 1 });
        c.Flush();
        Assert.Null(c.TryGet("aa"));
    }

    [Fact]
    public void Oversize_and_empty_values_are_not_stored()
    {
        var c = Make(cap: 40);

        c.Put("big", new byte[9]);
        c.Put("empty", ReadOnlySpan<byte>.Empty);
        c.Flush();

        Assert.Null(c.TryGet("big"));
        Assert.Null(c.TryGet("empty"));
    }

    [Fact]
    public void A_tampered_file_is_rejected_and_deleted()
    {
        var c = Make();
        c.Put("t", new byte[] { 1, 2, 3, 4 });
        c.Flush();
        var path = Path.Combine(_dir, "t");
        var bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0xFF;
        File.WriteAllBytes(path, bytes);

        Assert.Null(c.TryGet("t"));
        Assert.False(File.Exists(path));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(5)]
    [InlineData(32)]
    public void A_planted_short_or_unsigned_file_is_rejected_and_deleted(int length)
    {
        var c = Make();
        File.WriteAllBytes(Path.Combine(_dir, "planted"), new byte[length]);
        var again = Make();

        Assert.Null(again.TryGet("planted"));
        Assert.False(File.Exists(Path.Combine(_dir, "planted")));
        _ = c;
    }

    [Fact]
    public void A_planted_file_with_a_forged_tag_is_rejected()
    {
        var c = Make();
        File.WriteAllBytes(Path.Combine(_dir, "forged"), new byte[40]);
        var again = Make();

        Assert.Null(again.TryGet("forged"));
        Assert.False(File.Exists(Path.Combine(_dir, "forged")));
    }

    [Fact]
    public void A_file_written_under_another_key_or_purpose_is_rejected()
    {
        var a = Make();
        a.Put("k", new byte[] { 7 });
        a.Flush();

        Assert.Null(Make(key: KeyB).TryGet("k"));
        Assert.Null(Make(purpose: "other").TryGet("k"));
    }

    [Fact]
    public void A_file_moved_to_another_name_is_rejected()
    {
        var c = Make();
        c.Put("one", new byte[] { 7 });
        c.Flush();
        File.Copy(Path.Combine(_dir, "one"), Path.Combine(_dir, "two"));

        Assert.Null(Make().TryGet("two"));
    }

    [Fact]
    public void A_fresh_key_wipes_the_cache()
    {
        var c = Make();
        c.Put("k", new byte[] { 7 });
        c.Flush();

        var fresh = Make(fresh: true);

        Assert.Null(fresh.TryGet("k"));
        Assert.Empty(new DirectoryInfo(_dir).EnumerateFiles());
    }

    [Fact]
    public void Exceeding_the_cap_evicts_least_recently_used_first()
    {
        var c = Make(cap: 3 * 42 + 5);
        c.Put("old", new byte[10]);
        c.Put("mid", new byte[10]);
        c.Put("new", new byte[10]);
        c.Flush();
        Assert.NotNull(c.TryGet("old"));

        c.Put("extra", new byte[10]);
        c.Flush();

        Assert.Null(c.TryGet("mid"));
        Assert.NotNull(c.TryGet("old"));
        Assert.NotNull(c.TryGet("new"));
        Assert.NotNull(c.TryGet("extra"));
    }

    [Fact]
    public void Overwriting_a_name_does_not_double_count_its_size()
    {
        var c = Make(cap: 60);

        for (var i = 0; i < 5; i++)
        {
            c.Put("same", new byte[20]);
            c.Flush();
        }

        Assert.NotNull(c.TryGet("same"));
    }

    [Fact]
    public void A_put_that_replaces_an_entry_mid_write_is_written_again()
    {
        var c = Make();
        var entered = new System.Threading.ManualResetEventSlim();
        var release = new System.Threading.ManualResetEventSlim();
        var first = true;
        c.BeforeWrite = _ =>
        {
            if (first)
            {
                first = false;
                entered.Set();
                release.Wait(TimeSpan.FromSeconds(10));
            }
        };

        c.Put("k", new byte[] { 1 });
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        c.Put("k", new byte[] { 2 });
        release.Set();
        c.Flush();

        Assert.Equal(new byte[] { 2 }, Make().TryGet("k"));
    }

    [Fact]
    public void Pending_writes_are_capped_by_bytes()
    {
        var c = Make(cap: 1_000_000, maxQueued: 100);
        var release = new System.Threading.ManualResetEventSlim();
        c.BeforeWrite = _ => release.Wait(TimeSpan.FromSeconds(10));

        c.Put("a", new byte[60]);
        c.Put("b", new byte[60]);
        c.Put("c", new byte[30]);
        release.Set();
        c.Flush();

        Assert.NotNull(c.TryGet("a"));
        Assert.Null(c.TryGet("b"));
        Assert.NotNull(c.TryGet("c"));
    }

    [Fact]
    public void A_queued_write_is_readable_before_it_reaches_disk_and_survives_a_new_instance()
    {
        var c = Make();
        c.Put("k", new byte[] { 9 });
        Assert.Equal(new byte[] { 9 }, c.TryGet("k"));
        c.Flush();

        Assert.Equal(new byte[] { 9 }, Make().TryGet("k"));
    }

    [Fact]
    public void Leftover_temp_files_are_removed_and_never_served()
    {
        Directory.CreateDirectory(_dir);
        File.WriteAllBytes(Path.Combine(_dir, "x.abc.tmp"), new byte[50]);

        var c = Make();

        Assert.Null(c.TryGet("x.abc.tmp"));
        Assert.False(File.Exists(Path.Combine(_dir, "x.abc.tmp")));
    }

    [Fact]
    public void Hex_name_is_lowercase_and_stable()
    {
        Assert.Equal("00ff10", DiskBlobCache.HexName(new byte[] { 0x00, 0xFF, 0x10 }));
    }

    [Theory]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, false, "t", "1", true)]
    [InlineData("fire", 0, false, "t", "1", "fire", 1, false, "t", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, true, "t", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "plasma", 0, false, "t", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, false, "u", "1", false)]
    [InlineData("fire", 0, false, "t", "1", "fire", 0, false, "t", "2", false)]
    public void Thumbnail_name_changes_with_every_input(
        string e1, int s1, bool f1, string t1, string v1,
        string e2, int s2, bool f2, string t2, string v2, bool same)
    {
        var a = DiskBlobCache.ThumbnailName(e1, s1, f1, t1, v1);
        var b = DiskBlobCache.ThumbnailName(e2, s2, f2, t2, v2);

        Assert.Equal(same, a == b);
    }
}

public sealed class BlobCacheKeyTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-blobkey-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        { Directory.Delete(_dir, true); }
        catch { }
    }

    [Fact]
    public void A_missing_key_is_generated_fresh_and_then_reloaded_stable()
    {
        var path = Path.Combine(_dir, "k.key");

        var first = BlobCacheKey.Load(path, dpapi: false);
        var second = BlobCacheKey.Load(path, dpapi: false);

        Assert.True(first!.Value.Fresh);
        Assert.False(second!.Value.Fresh);
        Assert.Equal(first.Value.Key, second.Value.Key);
        Assert.Equal(32, first.Value.Key.Length);
    }

    [Fact]
    public void A_damaged_key_file_is_replaced_and_reported_fresh()
    {
        var path = Path.Combine(_dir, "k.key");
        var first = BlobCacheKey.Load(path, dpapi: false);
        File.WriteAllBytes(path, new byte[5]);

        var again = BlobCacheKey.Load(path, dpapi: false);

        Assert.True(again!.Value.Fresh);
        Assert.NotEqual(first!.Value.Key, again.Value.Key);
    }

    [Fact]
    public void Windows_protection_is_refused_off_the_service()
    {
        Assert.Null(BlobCacheKey.Load(Path.Combine(_dir, "k.key"), dpapi: true));
    }
}
