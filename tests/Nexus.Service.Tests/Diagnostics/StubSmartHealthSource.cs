using System;
using Nexus.Service.Diagnostics.Storage;

namespace Nexus.Service.Tests;

/// <summary>
/// SMART source that reports nothing. The real SmartHealthMonitor needs an
/// LhmComputer on Windows (whose constructor opens the hardware), so tests that
/// only need "no drives" wire this instead and build on every host.
/// </summary>
internal sealed class StubSmartHealthSource : ISmartHealthSource
{
    public SmartSnapshot Result { get; set; } = new() { Supported = false, Drives = Array.Empty<SmartDriveInfo>() };
    public SmartSnapshot Snapshot() => Result;
    public void ForceRefresh() { }
}
