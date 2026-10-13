using System;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.DependencyInjection;
using Nexus.Service.Persistence;
using Nexus.Service.Widgets;
using Nexus.Service.Widgets.AppActions;
using Xunit;

namespace Nexus.Service.Tests.Widgets.AppActions;

public class UnitsActionsTests
{
    [Fact]
    public async Task Units_are_what_the_user_chose()
    {
        var store = new InMemoryConfigStore();
        store.Update(s =>
        {
            s.Units.TimeFormat = "24h";
            s.Units.DateFormat = "dd/mm/yyyy";
        });
        var services = new ServiceCollection().AddSingleton<IConfigStore>(store).BuildServiceProvider();
        var registry = new AppActionRegistry();
        UnitsActions.RegisterAll(registry);
        Assert.True(registry.TryGet("prefs.units", out var handler));

        var result = await handler(services, null, CancellationToken.None);

        Assert.Equal("24h", result!.Value.GetProperty("timeFormat").GetString());
        Assert.Equal("dd/mm/yyyy", result.Value.GetProperty("dateFormat").GetString());
        Assert.Equal("c", result.Value.GetProperty("monitoringTempUnit").GetString());
    }

    [Fact]
    public void The_action_is_listed()
    {
        Assert.Equal(new[] { "prefs.units" }, UnitsActions.AllActions);
    }
}
