using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Nexus.Service.Devices.Detection;
using Nexus.Service.Media;
using Nexus.Service.Models;
using Nexus.Service.Peripherals.BulkPanels;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Serialization;

namespace Nexus.Service.Routes;

public static partial class DevicesRoutes
{
    private static void MapUniversalScreen88Endpoints(WebApplication app)
    {
        app.MapGet("/devices/lianli-screen88/settings", (IServiceProvider services, IConfigStore store, HardwarePresence presence) =>
        {
            var hub = Screen88Hub(services);
            var player = services.GetService<UniversalScreen88Player>();
            var s = store.Load().Devices.UniversalScreen88;
            var rotation = player?.VideoMount().Rotation ?? 0;
            var (videoWidth, videoHeight) = UniversalScreen88Protocol.VideoPicture(rotation);
            return Results.Json(
                new UniversalScreen88SettingsResponse
                {
                    Connected = hub?.IsConnected == true,
                    Firmware = (hub?.Driver as UniversalScreen88Driver)?.Firmware,
                    InWindowsDisplay = presence.UsbPresent(UniversalScreen88Protocol.DesktopVendorId, UniversalScreen88Protocol.DesktopProductId),
                    VideoPortrait = UniversalScreen88Media.Profile.IsPortrait(rotation),
                    VideoWidth = videoWidth,
                    VideoHeight = videoHeight,
                    OfflineMode = s.OfflineMode,
                    WindowsDisplay = s.WindowsDisplay,
                    ScreenMode = s.ScreenMode,
                    Video = s.Video,
                    Playing = player?.Playing,
                    ScreenSaverMinutes = s.ScreenSaverMinutes,
                    ScreenSaverVideo = s.ScreenSaverVideo,
                    ScreenSaverBrightness = s.ScreenSaverBrightness,
                },
                AppJsonContext.Default.UniversalScreen88SettingsResponse);
        });

        app.MapPut("/devices/lianli-screen88/settings", (UniversalScreen88SettingsRequest body, IServiceProvider services, IConfigStore store) =>
        {
            var media = services.GetService<UniversalScreen88Media>();
            if (body.ScreenMode is { } mode && mode is not (UniversalScreen88Settings.ScreenNexus or UniversalScreen88Settings.ScreenVideo))
            {
                return Results.BadRequest(ApiResponse.Fail("screenMode must be 'nexus' or 'video'"));
            }
            if (body.ScreenSaverMinutes is { } minutes && Array.IndexOf(ScreenSaverIntervals, minutes) < 0)
            {
                return Results.BadRequest(ApiResponse.Fail("screenSaverMinutes must be 0, 5, 10, 15, 30, 45 or 60"));
            }
            if (body.ScreenSaverBrightness is < 0 or > 100)
            {
                return Results.BadRequest(ApiResponse.Fail("screenSaverBrightness must be 0-100"));
            }
            if ((body.Video is { Length: > 0 } v && media?.Exists(v) != true) || (body.ScreenSaverVideo is { Length: > 0 } sv && media?.Exists(sv) != true))
            {
                return Results.BadRequest(ApiResponse.Fail("no such video"));
            }
            store.Update(s =>
            {
                var g = s.Devices.UniversalScreen88;
                g.OfflineMode = body.OfflineMode ?? g.OfflineMode;
                g.WindowsDisplay = body.WindowsDisplay ?? g.WindowsDisplay;
                g.ScreenMode = body.ScreenMode ?? g.ScreenMode;
                g.Video = body.Video is null ? g.Video : body.Video.Length > 0 ? body.Video : null;
                g.ScreenSaverMinutes = body.ScreenSaverMinutes ?? g.ScreenSaverMinutes;
                g.ScreenSaverVideo = body.ScreenSaverVideo is null ? g.ScreenSaverVideo : body.ScreenSaverVideo.Length > 0 ? body.ScreenSaverVideo : null;
                g.ScreenSaverBrightness = body.ScreenSaverBrightness ?? g.ScreenSaverBrightness;
            });
            var hub = Screen88Hub(services);
            if (hub?.Driver is UniversalScreen88Driver driver)
            {
                if (body.OfflineMode is { } offline
                    && !hub.Exchange(pipe => driver.ApplyOfflineMode(pipe, offline), false))
                {
                    ServiceLog.Info($"[{UniversalScreen88Driver.Id}] offline mode {(offline ? "on" : "off")} sent at the next connect");
                }
                // Into the Windows display mode happens in the handshake; back out of it is the desktop-mode exit's next poll.
                if (body.WindowsDisplay == true && hub.IsConnected)
                {
                    driver.RequestHandshake();
                    hub.WakeWorker();
                }
            }
            services.GetService<UniversalScreen88Player>()?.Wake();
            return Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse);
        });

        // Thumbnails follow the mount: 320x80 landscape, 80x320 portrait.
        app.MapGet("/devices/lianli-screen88/media", (UniversalScreen88Player player, UniversalScreen88Media media) =>
            Results.Json(
                new HydroShift2CurveMediaResponse { Media = MediaDtos(media, UniversalScreen88Media.Profile.IsPortrait(player.VideoMount().Rotation)) },
                AppJsonContext.Default.HydroShift2CurveMediaResponse));

        app.MapPost("/devices/lianli-screen88/media", async (HttpContext ctx, UniversalScreen88Player player, UniversalScreen88Media media, IHostApplicationLifetime lifetime) =>
        {
            if (!ctx.Request.HasFormContentType)
            {
                return UploadResult(null, "Expected multipart/form-data");
            }
            var form = await ctx.Request.ReadFormAsync(ctx.RequestAborted);
            var file = form.Files.Count > 0 ? form.Files[0] : null;
            if (file is null || file.Length == 0)
            {
                return UploadResult(null, "No file provided");
            }
            if (DecoderVideoMedia.VideoExtension(file.FileName) is not { } ext)
            {
                return UploadResult(null, "Not a video file");
            }
            // "crop" is the 4:1 crop landscape mounts show, "portraitCrop" the 1:4 one portrait mounts show; a mount
            // without its own shows the centre of the whole clip at its aspect, never a stretched crop.
            if (!TryCropField(form["crop"].ToString(), out var crop))
            {
                return UploadResult(null, "Invalid crop");
            }
            if (!TryCropField(form["portraitCrop"].ToString(), out var portraitCrop))
            {
                return UploadResult(null, "Invalid portraitCrop");
            }
            var label = form["label"].ToString();
            if (string.IsNullOrWhiteSpace(label))
            {
                label = Path.GetFileNameWithoutExtension(file.FileName);
            }
            var staged = Path.Combine(Path.GetTempPath(), $"nexus-screen88-in-{Guid.NewGuid():N}{ext}");
            try
            {
                await using (var stream = File.Create(staged))
                {
                    await file.CopyToAsync(stream, ctx.RequestAborted);
                }
                var name = await media.ImportAsync(staged, label, crop, portraitCrop, ctx.RequestAborted);
                if (name is null)
                {
                    return UploadResult(null, "Could not read that video");
                }
                var mount = player.VideoMount();
                media.EncodeImport(name, mount.Rotation, mount.Mirror, lifetime.ApplicationStopping);
                return UploadResult(name, null);
            }
            finally
            {
                try { File.Delete(staged); } catch (IOException) { }
            }
        }).DisableAntiforgery();

        // The preview follows the mount (960x240 or 240x960); ?portrait=true|false asks for one orientation.
        app.MapGet("/devices/lianli-screen88/media/{name}/preview", async (string name, bool? portrait, HttpContext ctx, UniversalScreen88Player player, UniversalScreen88Media media) =>
        {
            var tall = portrait ?? UniversalScreen88Media.Profile.IsPortrait(player.VideoMount().Rotation);
            if (!media.Exists(name) || await media.EnsurePreviewAsync(name, tall, ctx.RequestAborted) is not { } path)
            {
                return Results.NotFound();
            }
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            return Results.File(stream, "video/mp4", enableRangeProcessing: true);
        });

        app.MapPost("/devices/lianli-screen88/media/delete", async (HydroShift2CurveMediaDeleteRequest body, UniversalScreen88Player player, UniversalScreen88Media media, IConfigStore store) =>
        {
            if (body.Name is not { } name || !media.Exists(name))
            {
                return Results.BadRequest(ApiResponse.Fail("no such video"));
            }
            // Unpicked first, so the player lets go of the file before it is removed.
            var before = store.Load().Devices.UniversalScreen88;
            var (mode, video, saver) = (before.ScreenMode, before.Video, before.ScreenSaverVideo);
            store.Update(s =>
            {
                var g = s.Devices.UniversalScreen88;
                if (g.Video == name)
                {
                    g.Video = null;
                    g.ScreenMode = UniversalScreen88Settings.ScreenNexus;
                }
                if (g.ScreenSaverVideo == name)
                {
                    g.ScreenSaverVideo = null;
                }
            });
            player.Wake();
            if (player.Playing == name)
            {
                try
                {
                    await player.WhenStopped.WaitAsync(DeleteStopTimeout);
                }
                catch (TimeoutException)
                {
                }
            }
            var result = media.Delete(name);
            if (result == HydroShift2CurveMediaDelete.Busy)
            {
                // The clip stays, so it stays picked, unless a pick changed meanwhile.
                store.Update(s =>
                {
                    var g = s.Devices.UniversalScreen88;
                    if (video == name && g.Video is null && g.ScreenMode == UniversalScreen88Settings.ScreenNexus)
                    {
                        (g.Video, g.ScreenMode) = (video, mode);
                    }
                    if (saver == name && g.ScreenSaverVideo is null)
                    {
                        g.ScreenSaverVideo = saver;
                    }
                });
                player.Wake();
            }
            return result switch
            {
                HydroShift2CurveMediaDelete.Deleted => Results.Json(ApiResponse.Ok(), AppJsonContext.Default.ApiResponse),
                HydroShift2CurveMediaDelete.Busy => Results.Conflict(ApiResponse.Fail("video is in use, try again")),
                _ => Results.BadRequest(ApiResponse.Fail("no such video")),
            };
        });

        // L-Connect's own template videos, read from the user's L-Connect folder; nothing of them ships with Nexus.
        app.MapGet("/devices/lianli-screen88/lconnect-videos", (UniversalScreen88Media media) =>
        {
            var imported = new HashSet<string>(media.List().Select(i => i.Label), StringComparer.Ordinal);
            var videos = LConnectVideos.List(LConnectVideos.DefaultFolder())
                .Select(v => new LConnectVideoDto { File = v.File, Label = v.Label, SizeBytes = v.SizeBytes, Imported = imported.Contains(v.Label) })
                .ToArray();
            return Results.Json(new LConnectVideosResponse { Folder = LConnectVideos.DefaultFolder(), Videos = videos }, AppJsonContext.Default.LConnectVideosResponse);
        });

        app.MapPost("/devices/lianli-screen88/lconnect-videos/import", async (LConnectVideoImportRequest body, HttpContext ctx, UniversalScreen88Player player, UniversalScreen88Media media, IHostApplicationLifetime lifetime) =>
        {
            var folder = LConnectVideos.DefaultFolder();
            var known = LConnectVideos.List(folder).ToDictionary(v => v.File, StringComparer.OrdinalIgnoreCase);
            var names = new List<string>();
            foreach (var file in body.Files ?? Array.Empty<string>())
            {
                if (!known.TryGetValue(file, out var video))
                {
                    return Results.BadRequest(ApiResponse.Fail($"not an L-Connect video: {file}"));
                }
                // Copied, never moved: L-Connect's folder is left as it was.
                var staged = Path.Combine(Path.GetTempPath(), $"nexus-screen88-lc-{Guid.NewGuid():N}.mp4");
                try
                {
                    File.Copy(Path.Combine(folder, video.File), staged);
                    // No crop: each mount shows the centre of the template video at its own aspect (L-Connect's are made either way).
                    if (await media.ImportAsync(staged, video.Label, crop: null, portraitCrop: null, ctx.RequestAborted) is { } name)
                    {
                        var mount = player.VideoMount();
                        media.EncodeImport(name, mount.Rotation, mount.Mirror, lifetime.ApplicationStopping);
                        names.Add(name);
                    }
                }
                finally
                {
                    try { File.Delete(staged); } catch (IOException) { }
                }
            }
            return Results.Json(new LConnectVideoImportResponse { Names = names.ToArray() }, AppJsonContext.Default.LConnectVideoImportResponse);
        });
    }

    /// <summary>An absent field is no crop; a present one must parse.</summary>
    internal static bool TryCropField(string raw, out CropRect? crop)
    {
        crop = null;
        if (raw.Length == 0)
        {
            return true;
        }
        if (!CropRect.TryParse(raw, out var parsed))
        {
            return false;
        }
        crop = parsed;
        return true;
    }

    private static List<HydroShift2CurveMediaDto> MediaDtos(DecoderVideoMedia media, bool portrait)
    {
        var items = new List<HydroShift2CurveMediaDto>();
        foreach (var item in media.List())
        {
            var thumb = media.Thumbnail(item.Name, portrait);
            items.Add(new HydroShift2CurveMediaDto
            {
                Name = item.Name,
                Label = item.Label,
                Thumb = thumb is null ? null : "data:image/jpeg;base64," + Convert.ToBase64String(thumb),
                DurationSec = item.DurationSec,
                Ready = item.Ready,
            });
        }
        return items;
    }

    private static BulkPanelHub? Screen88Hub(IServiceProvider services) =>
        services.GetServices<BulkPanelHub>().FirstOrDefault(h => h.Driver is UniversalScreen88Driver);
}

/// <summary>The template videos L-Connect unpacked for the Universal Screen 8.8 (its "universal-screen-8.8-inch.zip" assets).</summary>
public static partial class LConnectVideos
{
    public sealed record Video(string File, string Label, long SizeBytes);

    /// <summary>%ProgramData%\Lian-Li\L-Connect 3\universal-screen-8.8-inch\video, where L-Connect keeps them.</summary>
    public static string DefaultFolder() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "Lian-Li", "L-Connect 3", "universal-screen-8.8-inch", "video");

    /// <summary>The original .mp4 of each template video (the .h264 beside it is L-Connect's ready-to-play copy), labelled by its template number.</summary>
    public static IReadOnlyList<Video> List(string folder)
    {
        var list = new List<Video>();
        if (!Directory.Exists(folder))
        {
            return list;
        }
        foreach (var path in Directory.GetFiles(folder, "*.mp4"))
        {
            var file = Path.GetFileName(path);
            var m = TemplateNumber().Match(file);
            list.Add(new Video(file, m.Success ? $"L-Connect {m.Groups[1].Value}" : $"L-Connect {Path.GetFileNameWithoutExtension(file)}", new FileInfo(path).Length));
        }
        list.Sort((a, b) => string.CompareOrdinal(a.File, b.File));
        return list;
    }

    [GeneratedRegex(@"^(88\d{3,4})")]
    private static partial Regex TemplateNumber();
}

public sealed class UniversalScreen88SettingsResponse
{
    /// <summary>The glass is attached as a panel under Nexus Control.</summary>
    public bool Connected { get; set; }
    public string? Firmware { get; set; }
    /// <summary>The glass is in its own Windows display mode now (1A86:AD21 present).</summary>
    public bool InWindowsDisplay { get; set; }
    /// <summary>The panel record's rotation is 90 or 270: library videos play portrait, cropped 1:4 ("portraitCrop" on upload).</summary>
    public bool VideoPortrait { get; set; }
    /// <summary>A library video's picture at the current mount: 1920x480 landscape, 480x1920 portrait. The crop aspect is VideoWidth / VideoHeight.</summary>
    public int VideoWidth { get; set; }
    public int VideoHeight { get; set; }
    /// <summary>Null until set in Nexus; the glass's own flag is then left as found.</summary>
    public bool? OfflineMode { get; set; }
    /// <summary>Null leaves the mode as found; true wants the Windows display mode, false the panel.</summary>
    public bool? WindowsDisplay { get; set; }
    public string ScreenMode { get; set; } = UniversalScreen88Settings.ScreenNexus;
    public string? Video { get; set; }
    /// <summary>Library item on the glass now (video mode or a running screen saver).</summary>
    public string? Playing { get; set; }
    public int ScreenSaverMinutes { get; set; }
    public string? ScreenSaverVideo { get; set; }
    public int ScreenSaverBrightness { get; set; }
}

public sealed class UniversalScreen88SettingsRequest
{
    public bool? OfflineMode { get; set; }
    public bool? WindowsDisplay { get; set; }
    public string? ScreenMode { get; set; }
    /// <summary>Empty string clears it.</summary>
    public string? Video { get; set; }
    public int? ScreenSaverMinutes { get; set; }
    /// <summary>Empty string clears it.</summary>
    public string? ScreenSaverVideo { get; set; }
    public int? ScreenSaverBrightness { get; set; }
}

public sealed class LConnectVideoDto
{
    public string File { get; set; } = "";
    public string Label { get; set; } = "";
    public long SizeBytes { get; set; }
    /// <summary>A library item with this label exists already.</summary>
    public bool Imported { get; set; }
}

public sealed class LConnectVideosResponse
{
    public string Folder { get; set; } = "";
    public LConnectVideoDto[] Videos { get; set; } = Array.Empty<LConnectVideoDto>();
}

public sealed class LConnectVideoImportRequest
{
    public string[]? Files { get; set; }
}

public sealed class LConnectVideoImportResponse
{
    public string[] Names { get; set; } = Array.Empty<string>();
}
