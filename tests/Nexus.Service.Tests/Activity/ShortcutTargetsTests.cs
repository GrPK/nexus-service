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
}
