using System;
using System.IO;
using Nexus.Service.Activity;
using Xunit;

namespace Nexus.Service.Tests.Activity;

public sealed class ShortcutTargetsTests
{
    [Fact]
    public void ParseInternetShortcut_ReadsUrlAndIcon()
    {
        var (url, iconFile, iconIndex) = ShortcutTargets.ParseInternetShortcut(
            "[InternetShortcut]\r\nURL=steam://rungameid/730\r\nIconIndex=2\r\nIconFile=C:\\Steam\\games\\abc.ico\r\n");

        Assert.Equal("steam://rungameid/730", url);
        Assert.Equal("C:\\Steam\\games\\abc.ico", iconFile);
        Assert.Equal(2, iconIndex);
    }

    [Fact]
    public void ParseInternetShortcut_MissingIconFile_IsNullWithIndexZero()
    {
        var (url, iconFile, iconIndex) = ShortcutTargets.ParseInternetShortcut("[InternetShortcut]\nURL=https://example.com\n");

        Assert.Equal("https://example.com", url);
        Assert.Null(iconFile);
        Assert.Equal(0, iconIndex);
    }

    [Fact]
    public void ParseInternetShortcut_IgnoresKeysOutsideTheSection()
    {
        var (url, iconFile, _) = ShortcutTargets.ParseInternetShortcut("[Other]\nURL=x\nIconFile=y\n[InternetShortcut]\nURL=steam://a\n");

        Assert.Equal("steam://a", url);
        Assert.Null(iconFile);
    }

    [Theory]
    [InlineData("steam://rungameid/123", true)]
    [InlineData("ms-settings:display", true)]
    [InlineData("C:\\Games\\game.exe", false)]
    [InlineData("C:/Games/game.exe", false)]
    [InlineData("\\\\host\\share\\a.exe", false)]
    [InlineData("file:///C:/a.exe", false)]
    [InlineData("{6D809377-6AF0-444B-8957-A3773F02200E}\\a.exe", false)]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", false)]
    [InlineData("", false)]
    public void IsLaunchUri_SeparatesSchemesFromPaths(string id, bool expected)
    {
        Assert.Equal(expected, ShortcutTargets.IsLaunchUri(id));
    }

    [Fact]
    public void Normalize_StripsQuotesAndWhitespace()
    {
        Assert.Equal("C:\\Games\\a.exe", ShortcutTargets.Normalize("  \"C:\\Games\\a.exe\" "));
    }

    [Fact]
    public void Normalize_ExpandsEnvironmentVariables()
    {
        var name = "NEXUS_TEST_SHORTCUT_DIR";
        Environment.SetEnvironmentVariable(name, "/opt/games");
        try
        {
            Assert.Equal("/opt/games/a.exe", ShortcutTargets.Normalize($"%{name}%/a.exe"));
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, null);
        }
    }

    [Fact]
    public void Normalize_ReplacesKnownFolderGuidPrefix()
    {
        var name = "ProgramW6432";
        var previous = Environment.GetEnvironmentVariable(name);
        Environment.SetEnvironmentVariable(name, "/pf64");
        try
        {
            var result = ShortcutTargets.Normalize("{6D809377-6AF0-444B-8957-A3773F02200E}\\Vendor\\app.exe");
            Assert.Equal(Path.Combine("/pf64", "Vendor\\app.exe"), result);
        }
        finally
        {
            Environment.SetEnvironmentVariable(name, previous);
        }
    }

    [Fact]
    public void Normalize_LeavesUnknownGuidPrefixAlone()
    {
        const string id = "{00000000-0000-0000-0000-000000000000}\\a.exe";
        Assert.Equal(id, ShortcutTargets.Normalize(id));
    }

    [Theory]
    [InlineData("C:\\Games\\a.bat", true)]
    [InlineData("\"C:\\Games\\a.exe\"", true)]
    [InlineData("steam://rungameid/1", true)]
    [InlineData("ms-msdt:/id x", true)]
    [InlineData("search-ms:query=a&crumb=location:\\\\host\\share", true)]
    [InlineData("\\\\host\\share\\a.exe", true)]
    [InlineData("{6D809377-6AF0-444B-8957-A3773F02200E}\\Vendor\\app.exe", false)]
    [InlineData("{6D809377-6AF0-444B-8957-A3773F02200E}\\..\\..\\Users\\x\\a.bat", true)]
    [InlineData("Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", false)]
    [InlineData("Notepad", false)]
    [InlineData("", false)]
    public void IsDirectTarget_ClassifiesByShape(string id, bool expected)
    {
        Assert.Equal(expected, ShortcutTargets.IsDirectTarget(id));
    }

    [Theory]
    [InlineData("steam://rungameid/1", true)]
    [InlineData("HTTPS://example.com", true)]
    [InlineData("http://example.com", true)]
    [InlineData("ms-msdt:/id x", false)]
    [InlineData("search-ms:query=a", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("steam://run/440", true)]
    [InlineData("STEAM://RUNGAMEID/440", true)]
    [InlineData("steam://run/440//+connect evil", false)]
    [InlineData("steam://run/440/", false)]
    [InlineData("steam://rungameid/440/x", false)]
    [InlineData("steam://rungameid/", false)]
    [InlineData("steam://rungameid/44a", false)]
    [InlineData("steam://install/440", false)]
    [InlineData("steam://open/games", false)]
    [InlineData("steam:rungameid/440", false)]
    public void IsAllowedLaunchScheme_AllowsOnlyHttpHttpsSteam(string uri, bool expected)
    {
        Assert.Equal(expected, ShortcutTargets.IsAllowedLaunchScheme(uri));
    }

    [Theory]
    [InlineData("\\\\host\\share\\a.exe")]
    [InlineData("\\\\?\\C:\\a.exe")]
    [InlineData("relative\\a.exe")]
    public void IsLocalDrivePath_RejectsUncDeviceAndRelative(string path)
    {
        Assert.False(ShortcutTargets.IsLocalDrivePath(path));
    }

    [Fact]
    public void IsLocalPath_AcceptsAbsolutePosixPathOffWindowsAndRejectsRelative()
    {
        Assert.False(ShortcutTargets.IsLocalPath("relative/app"));
        if (!OperatingSystem.IsWindows())
        {
            Assert.True(ShortcutTargets.IsLocalPath("/Applications/Foo.app"));
        }
        else
        {
            Assert.False(ShortcutTargets.IsLocalPath("\\\\host\\share\\a.exe"));
        }
    }

    [Fact]
    public void ReadInternetShortcutText_DecodesUtf8AndFallsBackForAnsiBytes()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nexus-url-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var utf8 = Path.Combine(dir, "u.url");
            File.WriteAllText(utf8, "IconFile=C:\\caf\u00e9.ico");
            Assert.Equal("IconFile=C:\\caf\u00e9.ico", ShortcutTargets.ReadInternetShortcutText(utf8));

            var ansi = Path.Combine(dir, "a.url");
            File.WriteAllBytes(ansi, [0x61, 0xE9]);
            Assert.Equal(2, ShortcutTargets.ReadInternetShortcutText(ansi).Length);
            Assert.StartsWith("a", ShortcutTargets.ReadInternetShortcutText(ansi));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
