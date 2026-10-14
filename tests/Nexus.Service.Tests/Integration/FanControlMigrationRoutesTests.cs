using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Nexus.Service.Auth;
using Nexus.Service.Cooling;
using Nexus.Service.Migration.FanControl;
using Nexus.Service.Models.Cooling;
using Nexus.Service.Persistence;

namespace Nexus.Service.Tests.Integration;

/// <summary>
/// /migration/fancontrol status and apply over the real request pipeline, with
/// IFanControlDetector swapped for a fake (the real detector is Windows-only).
/// </summary>
public sealed class FanControlMigrationRoutesTests
{
    private sealed class FakeDetector : IFanControlDetector
    {
        private readonly string _path = Path.GetFullPath(Path.Combine("Fixtures", "FanControl", "all-curve-kinds.json"));

        public FanControlDetectionResult Detect() => new(
            Detected: true, Running: false, InstallLocation: "C:\\FanControl", Version: "217",
            Configs: new List<FanControlConfigFile> { new(_path, "userConfig", 0, true) },
            AutostartPresent: false);

        public string? ReadConfig(string path) => File.ReadAllText(_path);

        public Task<bool> CloseAppAsync() => Task.FromResult(true);

        public bool DisableAutostart() => true;
    }

    private sealed class FakeFans : IFanControlProvider
    {
        private readonly List<FanChannel> _channels = new()
        {
            new() { Id = "/lpc/nct6797d/0/control/0", Name = "Fan #1" },
            new() { Id = "/lpc/nct6797d/0/control/1", Name = "Fan #2" },
        };

        public IReadOnlyList<FanChannel> GetFanChannels() => _channels;
        public IReadOnlyList<TemperatureSource> GetTemperatureSources() => new List<TemperatureSource>();
        public float? ReadTemperature(string sensorId) => 50f;
        public int SetFanSpeed(string channelId, int dutyPercent) => dutyPercent;
        public void DriveFanSpeed(string channelId, int dutyPercent) { }
        public void ReleaseFan(string channelId) { }
        public void ReleaseAll() { }

        public Task<IReadOnlyList<FanCalibration>> CalibrateAsync(
            IReadOnlyList<string> fanIds, IProgress<FanCalibrationProgress> progress, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<FanCalibration>>(Array.Empty<FanCalibration>());
    }

    private static (WebApplicationFactory<Program> Factory, HttpClient Client) Boot()
    {
        var factory = new NexusAppFactory().WithWebHostBuilder(b =>
            b.ConfigureTestServices(s =>
            {
                s.RemoveAll<IFanControlDetector>();
                s.AddSingleton<IFanControlDetector>(new FakeDetector());
                s.RemoveAll<IFanControlProvider>();
                s.AddSingleton<IFanControlProvider>(new FakeFans());
            }));
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", factory.Services.GetRequiredService<TokenService>().Token);
        return (factory, client);
    }

    [Fact]
    public async Task Status_is_pending_until_an_apply_lands_then_completed_and_not_pending()
    {
        var (factory, client) = Boot();
        using (factory)
        {
            var before = await client.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>("/migration/fancontrol");
            Assert.True(before!["pending"].GetBoolean());
            Assert.False(before["completed"].GetBoolean());

            var apply = await client.PostAsJsonAsync("/migration/fancontrol/apply", new { categories = new[] { "names" } });
            apply.EnsureSuccessStatusCode();
            Assert.True(factory.Services.GetRequiredService<IConfigStore>().Load().FanControlImportCompleted);

            var after = await client.GetFromJsonAsync<Dictionary<string, System.Text.Json.JsonElement>>("/migration/fancontrol");
            Assert.False(after!["pending"].GetBoolean());
            Assert.True(after["completed"].GetBoolean());
        }
    }
}
