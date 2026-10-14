using System;
using System.IO;

namespace Nexus.Service.Cooling;

/// <summary>
/// Marks ADLX init on disk: amdadlx64.dll can abort the process inside ADLXInitialize, so a
/// start that finds the mark keeps ADLX off for that key instead of dying on every restart.
/// </summary>
internal static class AdlxInitGuard
{
    private const string MarkFile = "adlx-init-crashguard";
    private const string LatchFile = "adlx-init-latched";

    /// <summary>True while ADLX stays off for <paramref name="key"/>. A mark a dead run left becomes the latch; another key clears it.</summary>
    public static bool Latched(string dir, string key)
    {
        var mark = Path.Combine(dir, MarkFile);
        var latch = Path.Combine(dir, LatchFile);
        try
        {
            if (File.Exists(mark)) File.Move(mark, latch, overwrite: true);
            if (!File.Exists(latch)) return false;
            if (File.ReadAllText(latch).Trim() == key) return true;
            File.Delete(latch);
            return false;
        }
        catch (Exception) { return false; }
    }

    private static volatile bool _stopped;

    /// <summary>Service stop: a stop that lands inside ADLX init, or a retry during teardown, must not read as ADLX killing the process.</summary>
    public static void Stop(string dir)
    {
        _stopped = true;
        Disarm(dir);
    }

    public static void Arm(string dir, string key)
    {
        if (_stopped) return;
        try
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, MarkFile), key);
        }
        catch (Exception) { }
    }

    public static void Disarm(string dir)
    {
        try { File.Delete(Path.Combine(dir, MarkFile)); }
        catch (Exception) { }
    }
}
