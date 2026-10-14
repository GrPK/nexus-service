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

    /// <summary>Applies the change for a kind in <see cref="RadioKinds"/> and
    /// returns false when that radio is absent or the OS refused it.</summary>
    Task<bool> SetAsync(string kind, bool on, CancellationToken ct = default);
}

public sealed class StubRadioProvider : IRadioProvider
{
    public Task<RadiosState> GetStateAsync(CancellationToken ct = default) => Task.FromResult(new RadiosState());
    public Task<bool> SetAsync(string kind, bool on, CancellationToken ct = default) => Task.FromResult(false);
}
