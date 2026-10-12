using Nexus.Service.Persistence;

namespace Nexus.Service.Tests;

/// <summary>
/// The pre-pin shape (static looks, fan lock overrides) has to
/// reach pins at every point a document is read, before anything else uses it.
/// </summary>
public sealed class PinMigrationLoadPointsTests : IDisposable
{
    private const string PrePin = """
        {
          "lighting": {
            "staticDeviceLooks": { "static-dev": { "effect": "flat", "color": "#0a0b0c", "slot": 1, "locked": true } },
            "layoutPresets": [ { "id": "p1", "name": "Old", "layouts": {}, "staticDeviceLooks": { "pre-dev": { "effect": "flat", "color": "#010101" } } } ]
          },
          "cooling": { "fanLockOverrides": { "fan1": true } }
        }
        """;

    private readonly string _tempDir = Path.Combine(Path.GetTempPath(), "nexus-pin-load-" + Guid.NewGuid().ToString("N")[..8]);

    public PinMigrationLoadPointsTests() => Directory.CreateDirectory(_tempDir);

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); } catch { }
    }

    private static void AssertPins(NexusSettings s)
    {
        Assert.Equal(DevicePinKinds.Static, s.Lighting.DevicePins["static-dev"].Kind);
        Assert.Equal("#0a0b0c", s.Lighting.DevicePins["static-dev"].Color);
        Assert.Equal(1, s.Lighting.DevicePins["static-dev"].Slot);
        Assert.True(s.Cooling.FanPinOverrides["fan1"]);
        Assert.Null(s.Lighting.StaticDeviceLooks);
        Assert.Null(s.Cooling.FanLockOverrides);
    }

    [Fact]
    public void A_pre_pin_settings_file_loads_with_pins_and_a_second_load_leaves_the_file_unchanged()
    {
        var path = Path.Combine(_tempDir, "settings.json");
        File.WriteAllText(path, PrePin);

        using (var first = new JsonConfigStore(path))
        {
            var s = first.Load();
            AssertPins(s);
            Assert.Equal("#010101", s.Lighting.LayoutPresets[0].DevicePins!["pre-dev"].Color);
            first.FlushNow();
        }
        var afterFirst = File.ReadAllText(path);
        Assert.DoesNotContain("staticDeviceLooks", afterFirst);
        Assert.DoesNotContain("fanLockOverrides", afterFirst);

        using (var second = new JsonConfigStore(path))
        {
            AssertPins(second.Load());
            second.FlushNow();
        }
        Assert.Equal(afterFirst, File.ReadAllText(path));
    }

    [Fact]
    public void A_pre_pin_profile_file_ends_with_pins_after_a_switch()
    {
        using var store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        using var profiles = new ProfileManager(store);
        profiles.Initialize();
        var target = profiles.CreateProfile("Target");
        profiles.CreateProfile("Other");
        File.WriteAllText(Path.Combine(_tempDir, $"profile-{target.Id}.json"), PrePin);

        profiles.SwitchProfile(target.Id);

        AssertPins(store.Load());
    }

    [Fact]
    public void A_pre_pin_profile_import_ends_with_pins_after_applying_it()
    {
        using var store = new JsonConfigStore(Path.Combine(_tempDir, "settings.json"));
        using var profiles = new ProfileManager(store);
        profiles.Initialize();

        var imported = profiles.ImportProfileJson(PrePin);
        profiles.SwitchProfile(imported.Id);

        AssertPins(store.Load());
    }
}
