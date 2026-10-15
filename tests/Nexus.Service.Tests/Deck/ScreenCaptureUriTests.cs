using System.Linq;
using Nexus.Service.Actions;
using Xunit;

namespace Nexus.Service.Tests.Deck;

public sealed class ScreenCaptureUriTests
{
    // The bare scheme is the native snip overlay (Win+Shift+S / Win+Shift+R); the //capture/ form opens the Snipping Tool app window.
    [Fact]
    public void WindowsCapture_UsesTheOverlayUris()
    {
        Assert.Equal("ms-screenclip:", SystemActions.ScreenShotUri);
        Assert.Equal("ms-screenclip:?type=recording", SystemActions.ScreenRecordUri);
    }

    [Fact]
    public void ScreenRecordDefaultIcon_HasABundledPng()
    {
        var found = typeof(SystemActions).Assembly.GetManifestResourceNames().Where(n => n == "deck-icon-Video.png");
        Assert.NotEmpty(found);
    }
}
