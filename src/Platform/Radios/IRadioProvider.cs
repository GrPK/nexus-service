using System.Threading;
using System.Threading.Tasks;
using Nexus.Service.Models.Radios;

namespace Nexus.Service.Platform.Radios;

/// <summary>
/// System radio toggles (Wi-Fi, Bluetooth, Airplane Mode) for the panel
/// control center. A radio the machine lacks, or a platform with no control
/// path, reports null for that kind.
/// </summary>
public interface IRadioProvider
{
    Task<RadiosState> GetStateAsync(CancellationToken ct = default);

    /// <summary>Applies the change for a kind in <see cref="RadioKinds"/>. False only
    /// when the kind is unknown or that radio is absent; an OS refusal or an expired
    /// settle wait still returns true, so the caller reads the real state back.</summary>
    Task<bool> SetAsync(string kind, bool on, CancellationToken ct = default);
}

public sealed class StubRadioProvider : IRadioProvider
{
    public Task<RadiosState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(new RadiosState());
    public Task<bool> SetAsync(string kind, bool on, CancellationToken ct = default) => Task.FromResult(false);
}
