using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Media;
using Nexus.Service.Platform;

namespace Nexus.Service.Peripherals.BulkPanels;

/// <summary>One video in the HydroShift II OLED Curved library; not ready while its upload is still being encoded.</summary>
public sealed record HydroShift2CurveMediaItem(string Name, string Label, double? DurationSec, bool Ready);

public enum HydroShift2CurveMediaDelete { Deleted, Missing, Busy }

/// <summary>
/// How one glass's decoder wants its videos: where its library lives, the preview and thumbnail
/// sizes, and the ffmpeg filter and variant file for a mount (record rotation and mirror).
/// </summary>
/// <param name="PortraitMount">
/// Set for a glass that can be mounted either way (the 8.8): true for a record rotation that shows the
/// picture portrait. Each clip then keeps a crop per orientation, and its previews and thumbnails are made
/// per orientation (the portrait ones with the sizes swapped), scaled to cover rather than stretched.
/// Null keeps one landscape crop, preview and thumbnail (the OLED Curved).
/// </param>
public sealed record DecoderVideoProfile(
    string HandlerId,
    int PreviewWidth,
    int PreviewHeight,
    string ThumbScale,
    Func<int, bool, string> MountFilter,
    Func<int, bool, string> VariantFile,
    Func<int, bool>? PortraitMount = null)
{
    public bool HasPortrait => PortraitMount is not null;

    /// <summary>The picture at this record rotation is portrait (always false without <see cref="PortraitMount"/>).</summary>
    public bool IsPortrait(int rotation) => PortraitMount?.Invoke(rotation) == true;

    /// <summary>Thumbnail width and height for an orientation, from <see cref="ThumbScale"/> ("w:h", the landscape one).</summary>
    public (int Width, int Height) ThumbSize(bool portrait)
    {
        var parts = ThumbScale.Split(':');
        var w = int.Parse(parts[0], CultureInfo.InvariantCulture);
        var h = int.Parse(parts[1], CultureInfo.InvariantCulture);
        return portrait ? (h, w) : (w, h);
    }

    public (int Width, int Height) PreviewSize(bool portrait) =>
        portrait ? (PreviewHeight, PreviewWidth) : (PreviewWidth, PreviewHeight);

    /// <summary>
    /// Scales to cover <paramref name="width"/> x <paramref name="height"/> and takes the centre: a picture of
    /// that aspect is only scaled, any other is trimmed, never stretched.
    /// </summary>
    public static string CoverScale(int width, int height) =>
        $"scale={width}:{height}:force_original_aspect_ratio=increase:flags=lanczos,crop={width}:{height}";
}

/// <summary>
/// Videos a glass plays on its own decoder (<see cref="IDecoderVideoDriver"/>). Each keeps its upload
/// and crop, and is encoded per mount the first time that mount needs it.
/// </summary>
public partial class DecoderVideoMedia
{
    private const string SourcePrefix = "source";
    private const string MetaFile = "meta.txt";
    private const string ThumbFile = "thumb.jpg";
    private const string PreviewFile = "preview.mp4";
    private const string TrashPrefix = ".deleted-";

    /// <summary>Longest clip kept; the glass loops whatever it gets.</summary>
    private const int MaxSeconds = 300;
    private const int EncodeTimeoutSeconds = 30 * 60;
    private const int ThumbTimeoutSeconds = 60;
    private const int FrameRate = 30;
    /// <summary>A failed encode is not retried for this long; a broken source would otherwise rerun ffmpeg on every play attempt.</summary>
    private const long FailedRetryMs = 10 * 60_000;

    private static readonly string[] VideoExtensions = { ".mp4", ".webm", ".mov", ".avi", ".mkv", ".wmv", ".m4v", ".mpg", ".mpeg", ".gif" };

    private readonly string _root;
    private readonly DecoderVideoProfile _profile;
    private readonly SemaphoreSlim _encode = new(1, 1);
    private readonly Dictionary<string, long> _failedAt = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _importing = new(StringComparer.Ordinal);
    /// <summary>Thumbnail files being made in the background, so a list read twice starts one ffmpeg.</summary>
    private readonly ConcurrentDictionary<string, byte> _thumbing = new(StringComparer.Ordinal);
    private volatile string? _encoding;

    private static readonly CropRect FullFrame = new(0, 0, 1, 1);

    /// <summary>
    /// What meta.txt keeps: the label, the landscape crop (null when none was chosen), the duration and, for a
    /// glass that mounts either way, the portrait crop.
    /// </summary>
    private sealed record Meta(string Label, CropRect? Crop, double? DurationSec, CropRect? PortraitCrop);

    protected DecoderVideoMedia(DecoderVideoProfile profile, string? root = null)
    {
        _profile = profile;
        _root = root ?? Path.Combine(MediaLibrary.DeviceStoreDir(profile.HandlerId), "media");
    }

    public static bool IsSafeName(string? name) => !string.IsNullOrEmpty(name) && SafeName().IsMatch(name);

    /// <summary>The upload's extension when it is a video container ffmpeg should read, else null.</summary>
    public static string? VideoExtension(string? fileName)
    {
        var ext = Path.GetExtension(fileName ?? "").ToLowerInvariant();
        return Array.IndexOf(VideoExtensions, ext) >= 0 ? ext : null;
    }

    public IReadOnlyList<HydroShift2CurveMediaItem> List()
    {
        var items = new List<HydroShift2CurveMediaItem>();
        if (!Directory.Exists(_root))
        {
            return items;
        }
        foreach (var dir in Directory.GetDirectories(_root))
        {
            var name = Path.GetFileName(dir);
            if (!IsSafeName(name) || SourcePath(name) is null)
            {
                continue;
            }
            var meta = ReadMeta(name);
            items.Add(new HydroShift2CurveMediaItem(name, meta.Label, meta.DurationSec, !_importing.ContainsKey(name)));
        }
        items.Sort((a, b) => Directory.GetCreationTimeUtc(ItemDir(b.Name)).CompareTo(Directory.GetCreationTimeUtc(ItemDir(a.Name))));
        return items;
    }

    public bool Exists(string name) => IsSafeName(name) && SourcePath(name) is not null;

    /// <summary>The landscape thumbnail.</summary>
    public byte[]? Thumbnail(string name) => Thumbnail(name, portrait: false);

    /// <summary>
    /// The thumbnail for an orientation. A clip from before a glass kept portrait thumbnails gets its one
    /// made in the background, and lists without it until then.
    /// </summary>
    public byte[]? Thumbnail(string name, bool portrait)
    {
        if (!IsSafeName(name))
        {
            return null;
        }
        var path = Path.Combine(ItemDir(name), ThumbFileFor(portrait));
        if (File.Exists(path))
        {
            return File.ReadAllBytes(path);
        }
        if (_profile.HasPortrait && !_importing.ContainsKey(name) && SourcePath(name) is not null
            && _thumbing.TryAdd(path, 0))
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    await MakeThumbAsync(name, portrait, CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    ServiceLog.Warn($"[{_profile.HandlerId}] thumbnail for '{name}' failed: {ex.Message}");
                }
                finally
                {
                    _thumbing.TryRemove(path, out _);
                }
            });
        }
        return null;
    }

    public double? Duration(string name) => IsSafeName(name) ? ReadMeta(name).DurationSec : null;

    /// <summary>
    /// Takes ownership of an uploaded file: stores it with its crop, reads its duration and
    /// grabs a thumbnail. Returns the new item's name, or null when ffmpeg cannot read it.
    /// </summary>
    public Task<string?> ImportAsync(string stagedPath, string label, CropRect crop, CancellationToken ct) =>
        ImportAsync(stagedPath, label, crop, portraitCrop: null, ct);

    /// <summary>
    /// As <see cref="ImportAsync(string, string, CropRect, CancellationToken)"/> with a crop per orientation, for a
    /// glass that mounts either way: <paramref name="crop"/> the landscape one, <paramref name="portraitCrop"/> the
    /// portrait one. An orientation without its own crop shows the whole source, turned as the other crop turns it,
    /// trimmed to its aspect from the centre. Without portrait mounts only <paramref name="crop"/> counts.
    /// </summary>
    public async Task<string?> ImportAsync(string stagedPath, string label, CropRect? crop, CropRect? portraitCrop, CancellationToken ct)
    {
        if (VideoExtension(stagedPath) is not { } ext)
        {
            return null;
        }
        if (!_profile.HasPortrait)
        {
            crop ??= FullFrame;
            portraitCrop = null;
        }
        var id = MediaImporter.SanitizeId(label);
        var name = $"{(id.Length > 24 ? id[..24] : id)}-{Guid.NewGuid():N}";
        var dir = ItemDir(name);
        Directory.CreateDirectory(dir);
        var source = Path.Combine(dir, SourcePrefix + ext);
        File.Move(stagedPath, source);
        _importing[name] = 0;
        try
        {
            var meta = new Meta(label, crop, null, portraitCrop);
            var stderr = await MediaImporter.RunFfmpeg(ThumbTimeoutSeconds, ct, ThumbArgs(source, meta, portrait: false, Path.Combine(dir, ThumbFileFor(false))));
            meta = meta with { DurationSec = ParseDuration(stderr) is { } seconds ? Math.Min(seconds, MaxSeconds) : null };
            WriteMeta(name, meta);
            if (_profile.HasPortrait)
            {
                try
                {
                    await MediaImporter.RunFfmpeg(ThumbTimeoutSeconds, ct, ThumbArgs(source, meta, portrait: true, Path.Combine(dir, ThumbFileFor(true))));
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // The clip read fine; Thumbnail makes this one again when it is first listed.
                    ServiceLog.Warn($"[{_profile.HandlerId}] portrait thumbnail for '{label}' failed: {ex.Message}");
                }
            }
            return name;
        }
        catch (OperationCanceledException)
        {
            _importing.TryRemove(name, out _);
            Delete(name);
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{_profile.HandlerId}] could not read upload '{label}': {ex.Message}");
            _importing.TryRemove(name, out _);
            Delete(name);
            return null;
        }
    }

    /// <summary>Encodes a fresh upload for the head's mount off the request; the item lists as not ready until it is done, and is dropped if it cannot be encoded.</summary>
    public void EncodeImport(string name, bool flip180, bool mirror, CancellationToken ct) =>
        EncodeImport(name, flip180 ? 180 : 0, mirror, ct);

    /// <summary>As <see cref="EncodeImport(string, bool, bool, CancellationToken)"/> for a mount given as the record rotation.</summary>
    public void EncodeImport(string name, int rotation, bool mirror, CancellationToken ct) =>
        _ = Task.Run(async () =>
        {
            try
            {
                var ok = await EnsureVariantAsync(name, rotation, mirror, ct).ConfigureAwait(false) is not null;
                if (ok)
                {
                    await EnsurePreviewAsync(name, _profile.IsPortrait(rotation), ct).ConfigureAwait(false);
                }
                _importing.TryRemove(name, out _);
                if (!ok)
                {
                    Delete(name);
                }
            }
            catch (Exception ex)
            {
                _importing.TryRemove(name, out _);
                if (ex is not OperationCanceledException)
                {
                    ServiceLog.Warn($"[{_profile.HandlerId}] encoding upload '{name}' failed: {ex.Message}");
                }
            }
        }, CancellationToken.None);

    /// <summary>The glass-ready stream for this mount, encoding it first if needed; null when that fails.</summary>
    public Task<string?> EnsureVariantAsync(string name, bool flip180, bool mirror, CancellationToken ct) =>
        EnsureVariantAsync(name, flip180 ? 180 : 0, mirror, ct);

    /// <summary>The glass-ready stream for a mount given as the record rotation (0, 90, 180, 270).</summary>
    public async Task<string?> EnsureVariantAsync(string name, int rotation, bool mirror, CancellationToken ct)
    {
        var target = VariantPath(name, rotation, mirror);
        if (File.Exists(target))
        {
            return target;
        }
        if (SourcePath(name) is not { } source)
        {
            return null;
        }
        await _encode.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(target))
            {
                return target;
            }
            if (_failedAt.TryGetValue(target, out var failedAt) && Environment.TickCount64 - failedAt < FailedRetryMs)
            {
                return null;
            }
            var meta = ReadMeta(name);
            var temp = target + ".tmp";
            _encoding = name;
            ServiceLog.Info($"[{_profile.HandlerId}] encoding '{name}' for the glass (rotation={rotation}, mirror={mirror})");
            // Raw Annex-B: the bundled ffmpeg has no h264 muxer, and rawvideo writes the
            // packets exactly as libx264 emits them. No B-frames, as the glass's decoder needs.
            await MediaImporter.RunFfmpeg(EncodeTimeoutSeconds, ct, new[]
            {
                "-y", "-i", source,
                "-vf", VariantFilter(meta, rotation, mirror),
                "-r", FrameRate.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "23",
                "-bf", "0", "-g", FrameRate.ToString(CultureInfo.InvariantCulture), "-pix_fmt", "yuv420p",
                "-an", "-t", MaxSeconds.ToString(CultureInfo.InvariantCulture),
                "-f", "rawvideo", temp,
            }).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            _failedAt.Remove(target);
            return target;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(target + ".tmp");
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{_profile.HandlerId}] encoding '{name}' failed: {ex.Message}");
            TryDeleteFile(target + ".tmp");
            _failedAt[target] = Environment.TickCount64;
            return null;
        }
        finally
        {
            _encoding = null;
            _encode.Release();
        }
    }

    /// <summary>A browser-playable landscape mp4 of the cropped clip for the dashboard, made on first use; null when that fails.</summary>
    public Task<string?> EnsurePreviewAsync(string name, CancellationToken ct) => EnsurePreviewAsync(name, portrait: false, ct);

    /// <summary>The dashboard preview for an orientation: the portrait one has the preview size turned, for a glass that mounts either way.</summary>
    public async Task<string?> EnsurePreviewAsync(string name, bool portrait, CancellationToken ct)
    {
        portrait &= _profile.HasPortrait;
        var target = Path.Combine(ItemDir(name), PreviewFileFor(portrait));
        if (File.Exists(target))
        {
            return target;
        }
        if (SourcePath(name) is not { } source)
        {
            return null;
        }
        await _encode.WaitAsync(ct).ConfigureAwait(false);
        var temp = target + ".tmp.mp4";
        try
        {
            if (File.Exists(target))
            {
                return target;
            }
            if (_failedAt.TryGetValue(target, out var failedAt) && Environment.TickCount64 - failedAt < FailedRetryMs)
            {
                return null;
            }
            _encoding = name;
            await MediaImporter.RunFfmpeg(EncodeTimeoutSeconds, ct, new[]
            {
                "-y", "-i", source,
                "-vf", PreviewFilter(ReadMeta(name), portrait),
                "-r", FrameRate.ToString(CultureInfo.InvariantCulture),
                "-c:v", "libx264", "-preset", "veryfast", "-crf", "26", "-pix_fmt", "yuv420p",
                "-an", "-movflags", "+faststart", "-t", MaxSeconds.ToString(CultureInfo.InvariantCulture),
                temp,
            }).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch (OperationCanceledException)
        {
            TryDeleteFile(temp);
            throw;
        }
        catch (Exception ex)
        {
            ServiceLog.Warn($"[{_profile.HandlerId}] preview for '{name}' failed: {ex.Message}");
            TryDeleteFile(temp);
            _failedAt[target] = Environment.TickCount64;
            return null;
        }
        finally
        {
            _encoding = null;
            _encode.Release();
        }
    }

    /// <summary>Removes a clip; Busy while ffmpeg is still writing it or a file is held open.</summary>
    public HydroShift2CurveMediaDelete Delete(string name)
    {
        if (!IsSafeName(name) || !Directory.Exists(ItemDir(name)))
        {
            return HydroShift2CurveMediaDelete.Missing;
        }
        if (_encoding == name)
        {
            return HydroShift2CurveMediaDelete.Busy;
        }
        // Renamed out of the library first: Windows refuses the rename while any file in it is
        // open, so a clip in use stays whole instead of losing some files to a recursive delete.
        var trash = Path.Combine(_root, TrashPrefix + name);
        try
        {
            Directory.Move(ItemDir(name), trash);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            ServiceLog.Warn($"[{_profile.HandlerId}] could not delete '{name}': {ex.Message}");
            return HydroShift2CurveMediaDelete.Busy;
        }
        SweepTrash();
        return HydroShift2CurveMediaDelete.Deleted;
    }

    /// <summary>Removes deleted clips whose files could not all go at once; their names never list.</summary>
    private void SweepTrash()
    {
        foreach (var dir in Directory.GetDirectories(_root, TrashPrefix + "*"))
        {
            try
            {
                Directory.Delete(dir, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
        }
    }

    internal static double? ParseDuration(string stderr)
    {
        var m = DurationLine().Match(stderr);
        if (!m.Success)
        {
            return null;
        }
        return (int.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600)
            + (int.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60)
            + double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
    }

    private string ItemDir(string name) => Path.Combine(_root, name);

    private string? SourcePath(string name)
    {
        var dir = ItemDir(name);
        if (!Directory.Exists(dir))
        {
            return null;
        }
        foreach (var file in Directory.GetFiles(dir, SourcePrefix + ".*"))
        {
            return file;
        }
        return null;
    }

    private string VariantPath(string name, int rotation, bool mirror) =>
        Path.Combine(ItemDir(name), _profile.VariantFile(rotation, mirror));

    private string ThumbFileFor(bool portrait) => _profile.HasPortrait ? $"thumb-r{(portrait ? 90 : 0)}.jpg" : ThumbFile;

    private string PreviewFileFor(bool portrait) => _profile.HasPortrait ? $"preview-r{(portrait ? 90 : 0)}.mp4" : PreviewFile;

    /// <summary>
    /// The filters that take the part of the source an orientation shows, before it is scaled to the glass:
    /// that orientation's own crop; for a glass that mounts either way and has none for it, the whole source
    /// turned and mirrored as the other crop turns it, which the cover scale after it trims from the centre.
    /// Empty when nothing is to be done.
    /// </summary>
    private string SourceFilter(Meta meta, bool portrait)
    {
        if (!_profile.HasPortrait)
        {
            return (meta.Crop ?? FullFrame).ToFfmpegCrop();
        }
        if ((portrait ? meta.PortraitCrop : meta.Crop) is { } own)
        {
            return own.ToFfmpegCrop();
        }
        return (portrait ? meta.Crop : meta.PortraitCrop) is { } other
            ? CropRect.OrientationFilter(other.Rotate, other.Mirror).TrimEnd(',')
            : "";
    }

    /// <summary>One ffmpeg filter chain from its non-empty parts.</summary>
    private static string Chain(params string[] parts) => string.Join(',', Array.FindAll(parts, p => p.Length > 0));

    /// <summary>The -vf of a clip's glass encode for a mount, from its stored crops.</summary>
    internal string VariantFilter(string name, int rotation, bool mirror) => VariantFilter(ReadMeta(name), rotation, mirror);

    /// <summary>The -vf of a clip's dashboard preview for an orientation.</summary>
    internal string PreviewFilter(string name, bool portrait) => PreviewFilter(ReadMeta(name), portrait && _profile.HasPortrait);

    /// <summary>The -vf of a clip's thumbnail for an orientation.</summary>
    internal string ThumbFilter(string name, bool portrait) => ThumbFilter(ReadMeta(name), portrait && _profile.HasPortrait);

    private string VariantFilter(Meta meta, int rotation, bool mirror) =>
        Chain(SourceFilter(meta, _profile.IsPortrait(rotation)), _profile.MountFilter(rotation, mirror));

    private string PreviewFilter(Meta meta, bool portrait)
    {
        var (width, height) = _profile.PreviewSize(portrait);
        return Chain(SourceFilter(meta, portrait), _profile.HasPortrait
            ? DecoderVideoProfile.CoverScale(width, height)
            : $"scale={width}:{height}:flags=lanczos");
    }

    private string ThumbFilter(Meta meta, bool portrait)
    {
        var (width, height) = _profile.ThumbSize(portrait);
        return Chain(SourceFilter(meta, portrait), _profile.HasPortrait
            ? DecoderVideoProfile.CoverScale(width, height)
            : $"scale={_profile.ThumbScale}");
    }

    private string[] ThumbArgs(string source, Meta meta, bool portrait, string target) => new[]
    {
        "-y", "-i", source,
        "-vf", ThumbFilter(meta, portrait),
        "-frames:v", "1", "-q:v", "5",
        target,
    };

    private async Task MakeThumbAsync(string name, bool portrait, CancellationToken ct)
    {
        if (SourcePath(name) is not { } source)
        {
            return;
        }
        var target = Path.Combine(ItemDir(name), ThumbFileFor(portrait));
        var temp = Path.Combine(ItemDir(name), $"thumb-tmp-{Guid.NewGuid():N}.jpg");
        try
        {
            await MediaImporter.RunFfmpeg(ThumbTimeoutSeconds, ct, ThumbArgs(source, ReadMeta(name), portrait, temp)).ConfigureAwait(false);
            File.Move(temp, target, overwrite: true);
        }
        finally
        {
            TryDeleteFile(temp);
        }
    }

    /// <summary>Label, landscape crop wire form (blank when none was chosen), duration in seconds (blank when unknown), and, where kept, the portrait crop.</summary>
    private void WriteMeta(string name, Meta meta)
    {
        var lines = new List<string>
        {
            meta.Label.ReplaceLineEndings(" "),
            meta.Crop is { } crop ? CropText(crop) : "",
            meta.DurationSec?.ToString(CultureInfo.InvariantCulture) ?? "",
        };
        if (_profile.HasPortrait)
        {
            lines.Add(meta.PortraitCrop is { } portrait ? CropText(portrait) : "");
        }
        File.WriteAllLines(Path.Combine(ItemDir(name), MetaFile), lines);
    }

    private static string CropText(CropRect crop) => string.Join(',',
        crop.X.ToString(CultureInfo.InvariantCulture), crop.Y.ToString(CultureInfo.InvariantCulture),
        crop.W.ToString(CultureInfo.InvariantCulture), crop.H.ToString(CultureInfo.InvariantCulture),
        crop.Rotate.ToString(CultureInfo.InvariantCulture), crop.Mirror ? "1" : "0");

    private Meta ReadMeta(string name)
    {
        var path = Path.Combine(ItemDir(name), MetaFile);
        var lines = File.Exists(path) ? File.ReadAllLines(path) : Array.Empty<string>();
        var label = lines.Length > 0 && lines[0].Length > 0 ? lines[0] : name;
        CropRect? crop = lines.Length > 1 && CropRect.TryParse(lines[1], out var c) ? c : null;
        double? duration = lines.Length > 2 && double.TryParse(lines[2], NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;
        CropRect? portraitCrop = _profile.HasPortrait && lines.Length > 3 && CropRect.TryParse(lines[3], out var p) ? p : null;
        return new Meta(label, crop, duration, portraitCrop);
    }

    [GeneratedRegex("^[A-Za-z0-9_-]{1,64}$")]
    private static partial Regex SafeName();

    [GeneratedRegex(@"Duration: (\d+):(\d{2}):(\d{2}(?:\.\d+)?)")]
    private static partial Regex DurationLine();
}
