using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.AspNetCore.Http;
using Nexus.Service.Auth;
using Nexus.Service.Conflicts;
using Nexus.Service.Persistence;
using Nexus.Service.Platform;
using Nexus.Service.Sockets;

namespace Nexus.Service.Routes;

/// <summary>
/// One-time onboarding flags in settings.json; a factory reset wipes them.
/// The desktop dashboard's first-run screens (welcome, feature pillars,
/// lighting device selection) are
/// <see cref="LocalhostOnlyEndpointExtensions.LocalhostOnly"/>:
///   GET  /onboarding                    -> { completed, featuresCompleted, lightingCompleted, dismissedHints }
///   POST /onboarding/complete           -> set completed, return status
///   POST /onboarding/features-complete  -> set featuresCompleted, return status
///   POST /onboarding/lighting-complete  -> set lightingCompleted, return status
///   POST /onboarding/conflicts-step     -> log how the user left the conflict step, return status
///   POST /onboarding/hints/dismiss      -> { id } adds to dismissedHints (idempotent, capped), return status
/// The touch-panel swipe-up hint's flag is .AllowPanel() and not loopback-bound,
/// since a paired phone is a touch panel too:
///   GET  /onboarding/panel-swipe           -> { completed, immersiveCompleted }
///   POST /onboarding/panel-swipe/complete  -> set panel-swipe completed
///   POST /onboarding/panel-swipe/immersive/complete -> set the immersive swipe-down hint completed
/// The home-dashboard banner remembers the key it was dismissed under, LocalhostOnly:
///   GET  /onboarding/banner                -> { dismissedKey }
///   POST /onboarding/banner/dismiss/{key}  -> store key
/// </summary>
internal static partial class OnboardingRoutes
{
    public static void MapOnboardingEndpoints(this WebApplication app)
    {
        // Existing installs start open; a fresh one holds notifications until
        // the sequence ends. featuresCompleted sits before lighting-complete
        // in the sequence, so it does not gate the notification hold - only
        // the last step (lighting) matters for when it releases.
        var initial = app.Services.GetRequiredService<IConfigStore>().Load();
        Nexus.Service.Notifications.NotificationGate.Initialize(
            initial.OnboardingCompleted && initial.LightingOnboardingCompleted);

        app.MapGet("/onboarding", (IConfigStore store) =>
            Results.Ok(Status(store))).LocalhostOnly();

        app.MapPost("/onboarding/complete", (IConfigStore store, Nexus.Service.Telemetry.ITelemetry telemetry) =>
        {
            // Fires on a false->true transition only, so a repeat post is
            // silent. NOT strictly once per install: POST /onboarding/reset
            // clears the flag, so a machine that re-runs onboarding reports
            // again. Count distinct installs, not events.
            if (!store.Load().OnboardingCompleted)
                telemetry.Capture(Nexus.Service.Telemetry.TelemetryEvents.OnboardingCompleted);
            store.Update(s => s.OnboardingCompleted = true);
            // A skip completes both flags at once; releasing here covers it.
            if (store.Load().LightingOnboardingCompleted)
            {
                _ = Nexus.Service.Notifications.NotificationGate.ReleaseAsync(Nexus.Service.Notifications.NotificationGate.ReasonOnboarding);
            }
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        app.MapPost("/onboarding/features-complete", (IConfigStore store, Nexus.Service.Lifecycle.FeatureReconciler reconciler) =>
        {
            // Through the reconciler: the chosen pillars switch on here, so the transition side effects must run.
            reconciler.ApplyPatch(s => s.FeaturesOnboardingCompleted = true);
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        app.MapPost("/onboarding/lighting-complete", (IConfigStore store) =>
        {
            store.Update(s => s.LightingOnboardingCompleted = true);
            // Last server-side step: anything held while the screens owned the
            // display goes out now.
            _ = Nexus.Service.Notifications.NotificationGate.ReleaseAsync(Nexus.Service.Notifications.NotificationGate.ReasonOnboarding);
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        // Log-only, so a support bundle tells a Resolve all from a Skip. Each
        // app it ended, whitelisted or took off startup logs from its own route.
        app.MapPost("/onboarding/conflicts-step", (OnboardingConflictsStepBody body, IConfigStore store) =>
        {
            if (!ConflictStepActions.Contains(body?.Action ?? "")) return Results.BadRequest();
            ServiceLog.Info(ConflictStepLine(body!));
            return Results.Ok(Status(store));
        }).LocalhostOnly();

        app.MapGet("/onboarding/panel-swipe", (IConfigStore store) =>
            Results.Ok(PanelSwipeStatus(store))).AllowPanel();

        app.MapPost("/onboarding/panel-swipe/complete", (IConfigStore store) =>
        {
            store.Update(s => s.PanelSwipeOnboardingCompleted = true);
            return Results.Ok(PanelSwipeStatus(store));
        }).AllowPanel();

        // Under the panel-swipe prefix so the relay allowlist already admits it.
        app.MapPost("/onboarding/panel-swipe/immersive/complete", (IConfigStore store) =>
        {
            store.Update(s => s.ImmersiveSwipeOnboardingCompleted = true);
            return Results.Ok(PanelSwipeStatus(store));
        }).AllowPanel();

        app.MapGet("/onboarding/banner", (IConfigStore store) =>
            Results.Ok(new DashboardBannerDto { DismissedKey = store.Load().DashboardBannerDismissed })).LocalhostOnly();

        app.MapPost("/onboarding/banner/dismiss/{key}", (string key, IConfigStore store) =>
        {
            key = key.Trim();
            if (key.Length == 0 || key.Length > 64) return Results.BadRequest();
            store.Update(s => s.DashboardBannerDismissed = key);
            return Results.Ok(new DashboardBannerDto { DismissedKey = key });
        }).LocalhostOnly();

        app.MapPost("/onboarding/hints/dismiss", ([Microsoft.AspNetCore.Mvc.FromBody] OnboardingHintDismissBody? body, IConfigStore store) =>
        {
            var id = body?.Id;
            if (id is null || !HintIdPattern().IsMatch(id)) return Results.BadRequest();
            var full = false;
            store.Update(s =>
            {
                if (s.DismissedOnboardingHints.Contains(id)) return;
                if (s.DismissedOnboardingHints.Count >= MaxDismissedHints)
                {
                    full = true;
                    return;
                }
                s.DismissedOnboardingHints = new List<string>(s.DismissedOnboardingHints) { id };
            });
            return full ? Results.Conflict() : Results.Ok(Status(store));
        }).LocalhostOnly();

        // Replays the whole sequence, except the features decision: that flag
        // stays set so fans and lighting keep running through the replay.
        // The import latches (two offered flags and
        // the FanControl completed flag) are cleared as well, or the import
        // step is silently skipped - they are per-app flags
        // the migration routes own, not onboarding ones. An app that is no
        // longer installed still will not be offered: that gate also requires
        // live detection.
        app.MapPost("/onboarding/reset", (IConfigStore store, MultiplexHub hub) =>
        {
            store.Update(s =>
            {
                s.OnboardingCompleted = false;
                s.LightingOnboardingCompleted = false;
                s.PanelSwipeOnboardingCompleted = false;
                s.ImmersiveSwipeOnboardingCompleted = false;
                s.Nexus2MigrationOffered = false;
                s.FanControlImportOffered = false;
                s.FanControlImportCompleted = false;
            });
            // Re-close, or notifications keep firing through the replay.
            Nexus.Service.Notifications.NotificationGate.Initialize(onboardingComplete: false);
            // A live kiosk re-reads its swipe-hint flag on this topic.
            PanelTopics.BroadcastPrefs(hub);
            return Results.Ok(Status(store));
        }).LocalhostOnly();
    }

    private const int MaxDismissedHints = 100;

    [System.Text.RegularExpressions.GeneratedRegex("^[a-z0-9][a-z0-9-]{0,63}\\z")]
    private static partial System.Text.RegularExpressions.Regex HintIdPattern();

    private static readonly string[] ConflictStepActions = { "resolveAll", "skip", "skipOnboarding" };

    internal static string ConflictStepLine(OnboardingConflictsStepBody body)
        => $"[onboarding] conflicts step: {body.Action} listed={CatalogIds(body.Listed)} "
            + $"whitelisted={CatalogIds(body.Whitelisted)} alreadyEnded={CatalogIds(body.AlreadyEnded)}";

    // Catalog ids only, so a request cannot write arbitrary text into the log.
    private static string CatalogIds(List<string>? ids)
    {
        var known = (ids ?? new List<string>())
            .Select(id => ConflictWatcher.FindById(id)?.Id)
            .OfType<string>()
            .Distinct()
            .ToList();
        return known.Count == 0 ? "-" : string.Join(",", known);
    }

    private static OnboardingStatusDto Status(IConfigStore store)
    {
        var s = store.Load();
        return new OnboardingStatusDto
        {
            Completed = s.OnboardingCompleted,
            FeaturesCompleted = s.FeaturesOnboardingCompleted,
            LightingCompleted = s.LightingOnboardingCompleted,
            DismissedHints = new List<string>(s.DismissedOnboardingHints),
        };
    }

    private static PanelSwipeOnboardingDto PanelSwipeStatus(IConfigStore store)
    {
        var s = store.Load();
        return new() { Completed = s.PanelSwipeOnboardingCompleted, ImmersiveCompleted = s.ImmersiveSwipeOnboardingCompleted };
    }
}

public sealed class PanelSwipeOnboardingDto
{
    public bool Completed { get; set; }
    public bool ImmersiveCompleted { get; set; }
}

public sealed class DashboardBannerDto
{
    public string DismissedKey { get; set; } = "";
}

public sealed class OnboardingConflictsStepBody
{
    /// <summary>"resolveAll", "skip" or "skipOnboarding".</summary>
    public string Action { get; set; } = "";
    public List<string> Listed { get; set; } = new();
    public List<string> Whitelisted { get; set; } = new();
    /// <summary>Rows already gone when the user chose: ended here, closed elsewhere, or exited.</summary>
    public List<string> AlreadyEnded { get; set; } = new();
}

public sealed class OnboardingStatusDto
{
    public bool Completed { get; set; }
    public bool FeaturesCompleted { get; set; }
    public bool LightingCompleted { get; set; }
    public List<string> DismissedHints { get; set; } = new();
}

public sealed class OnboardingHintDismissBody
{
    public string? Id { get; set; }
}
