using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using Nexus.Service.Lighting.Engine.Gpu;
using Nexus.Service.Persistence;
using Nexus.Service.Serialization;

namespace Nexus.Service.Lighting.CustomEffects;

/// <summary>One user-made shader. Recipe is the web editor's opaque document and is never parsed here.</summary>
public sealed class CustomEffectEntry
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Recipe { get; set; } = "";
    public string Frag { get; set; } = "";
    public long UpdatedAt { get; set; }
}

public sealed class CustomEffectsDoc
{
    public List<CustomEffectEntry> Entries { get; set; } = new();
}

/// <summary>Compiles a fragment on the GL thread; null means it is accepted (compiled, or no GPU to check with).</summary>
public delegate string? CustomFragCompiler(string frag);

/// <summary>
/// User-made GPU effects in one machine-scope file, shared by every profile.
/// The store mirrors its sources into <see cref="ShaderLibrary"/> so a
/// "custom:id" key resolves anywhere a built-in key does.
/// </summary>
public sealed class CustomEffectStore
{
    public const int MaxNameLength = 40;
    public const int MaxRecipeBytes = 16 * 1024;
    public const int MaxFragBytes = 32 * 1024;
    public const int MaxEntries = 200;
    private const string VersionLine = "#version 330 core";

    /// <summary>Most iterations one loop may run; a compiled shader that never finishes would wedge the GL thread.</summary>
    public const int MaxLoopIterations = 64;

    // \z, not $: $ also matches before a trailing newline.
    private static readonly Regex IdPattern = new(@"^[a-z0-9]{1,32}\z", RegexOptions.CultureInvariant);
    private static readonly Regex ForHeader = new(
        @"\G\s*\(\s*int\s+([A-Za-z_]\w*)\s*=\s*(-?\d+)\s*;\s*\1\s*(<=|<)\s*(-?\d+)\s*;\s*\1\s*\+\+\s*\)",
        RegexOptions.CultureInvariant);

    private readonly string _path;
    private readonly object _lock = new();
    private readonly Func<long> _clock;
    private List<CustomEffectEntry> _entries;

    public CustomEffectStore() : this(NexusDataPaths.NexusRoot()) { }

    /// <param name="clock">Unix milliseconds; defaults to the wall clock. UpdatedAt is strictly increasing regardless.</param>
    public CustomEffectStore(string dir, Func<long>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _path = Path.Combine(dir, "custom-effects.json");
        _entries = Read();
        foreach (var e in _entries)
        {
            ShaderLibrary.RegisterCustom(e.Id, e.Frag);
        }
    }

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    /// <summary>Entries by UpdatedAt ascending.</summary>
    public IReadOnlyList<CustomEffectEntry> List()
    {
        lock (_lock)
        {
            return _entries.OrderBy(e => e.UpdatedAt).Select(Clone).ToList();
        }
    }

    public CustomEffectEntry? Get(string id)
    {
        lock (_lock)
        {
            var e = _entries.Find(x => x.Id == id);
            return e is null ? null : Clone(e);
        }
    }

    /// <summary>The first problem with the request, or null. Checks everything except the GL compile and the entry count.</summary>
    public static string? Validate(string? id, string? name, string? recipe, string? frag)
    {
        if (!IsValidId(id))
        {
            return "id must be 1 to 32 lowercase letters or digits";
        }
        var trimmed = (name ?? "").Trim();
        if (trimmed.Length is 0 or > MaxNameLength)
        {
            return $"name must be 1 to {MaxNameLength} characters";
        }
        if (System.Text.Encoding.UTF8.GetByteCount(recipe ?? "") > MaxRecipeBytes)
        {
            return $"recipe exceeds {MaxRecipeBytes} bytes";
        }
        if (string.IsNullOrEmpty(frag))
        {
            return "frag is required";
        }
        if (frag.Length > MaxFragBytes)
        {
            return $"frag exceeds {MaxFragBytes} bytes";
        }
        foreach (var c in frag)
        {
            if (c > 0x7F)
            {
                return "frag must be ASCII only";
            }
        }
        if (!frag.StartsWith(VersionLine, StringComparison.Ordinal))
        {
            return $"frag must start with {VersionLine}";
        }
        return ValidateLoops(frag);
    }

    /// <summary>Only counted for-loops are allowed: no while or do, and every for must be
    /// `for (int v = lit; v &lt;|&lt;= lit; v++)` running at most <see cref="MaxLoopIterations"/> times.</summary>
    internal static string? ValidateLoops(string frag)
    {
        var code = StripComments(frag);
        var i = 0;
        while (i < code.Length)
        {
            if (!(char.IsAsciiLetterOrDigit(code[i]) || code[i] == '_'))
            {
                i++;
                continue;
            }
            var start = i;
            while (i < code.Length && (char.IsAsciiLetterOrDigit(code[i]) || code[i] == '_'))
            {
                i++;
            }
            var word = code.AsSpan(start, i - start);
            if (word.SequenceEqual("while") || word.SequenceEqual("do"))
            {
                return $"'{word}' loops are not allowed; use a bounded for loop like for (int i = 0; i < 5; i++)";
            }
            if (!word.SequenceEqual("for"))
            {
                continue;
            }
            var m = ForHeader.Match(code, i);
            if (!m.Success || m.Index != i)
            {
                return "for loops must look like for (int i = 0; i < 5; i++) with integer literal bounds";
            }
            if (!long.TryParse(m.Groups[2].Value, out var lo) || !long.TryParse(m.Groups[4].Value, out var hi))
            {
                return "for loop bounds are out of range";
            }
            var count = hi - lo + (m.Groups[3].Value == "<=" ? 1 : 0);
            if (count > MaxLoopIterations)
            {
                return $"a for loop may run at most {MaxLoopIterations} iterations";
            }
        }
        return null;
    }

    private static string StripComments(string src)
    {
        var sb = new System.Text.StringBuilder(src.Length);
        var i = 0;
        while (i < src.Length)
        {
            if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '/')
            {
                while (i < src.Length && src[i] != '\n')
                {
                    i++;
                }
            }
            else if (src[i] == '/' && i + 1 < src.Length && src[i + 1] == '*')
            {
                var end = src.IndexOf("*/", i + 2, StringComparison.Ordinal);
                i = end < 0 ? src.Length : end + 2;
                sb.Append(' ');
            }
            else
            {
                sb.Append(src[i++]);
            }
        }
        return sb.ToString();
    }

    /// <summary>Insert or replace. Returns the saved entry, or an error message when the entry cap rejects a new id.</summary>
    public (CustomEffectEntry? Entry, string? Error) Put(string id, string name, string recipe, string frag)
    {
        CustomEffectEntry saved;
        lock (_lock)
        {
            var next = new List<CustomEffectEntry>(_entries);
            var idx = next.FindIndex(x => x.Id == id);
            if (idx < 0 && next.Count >= MaxEntries)
            {
                return (null, $"at most {MaxEntries} custom effects");
            }
            saved = new CustomEffectEntry
            {
                Id = id,
                Name = name.Trim(),
                Recipe = recipe ?? "",
                Frag = frag,
                UpdatedAt = Math.Max(_clock(), next.Count == 0 ? 0 : next.Max(x => x.UpdatedAt) + 1),
            };
            if (idx < 0)
            {
                next.Add(saved);
            }
            else
            {
                next[idx] = saved;
            }
            Write(next);
            _entries = next;
            ShaderLibrary.RegisterCustom(id, frag);
        }
        return (Clone(saved), null);
    }

    public bool Delete(string id)
    {
        lock (_lock)
        {
            var next = _entries.Where(x => x.Id != id).ToList();
            if (next.Count == _entries.Count)
            {
                return false;
            }
            Write(next);
            _entries = next;
            ShaderLibrary.UnregisterCustom(id);
            return true;
        }
    }

    private List<CustomEffectEntry> Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return new();
            }
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return new();
            }
            var doc = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.CustomEffectsDoc);
            // A hand-edited file can hold nulls or ids the routes would reject.
            return (doc?.Entries ?? new()).Where(e => e is not null && IsValidId(e.Id) && !string.IsNullOrEmpty(e.Frag)).ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[custom-effects] load failed: {ex.GetType().Name}: {ex.Message}");
            QuarantineUnreadable();
            return new();
        }
    }

    // The next write would replace an unreadable file with an empty list; keep it aside instead.
    private void QuarantineUnreadable()
    {
        try
        {
            if (File.Exists(_path))
            {
                File.Move(_path, _path + ".bad-" + _clock());
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[custom-effects] could not set the unreadable file aside: {ex.Message}");
        }
    }

    private void Write(List<CustomEffectEntry> entries)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        AtomicJsonFile.Write(_path, JsonSerializer.Serialize(new CustomEffectsDoc { Entries = entries }, PersistenceJsonContext.Default.CustomEffectsDoc));
    }

    private static CustomEffectEntry Clone(CustomEffectEntry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Recipe = e.Recipe,
        Frag = e.Frag,
        UpdatedAt = e.UpdatedAt,
    };
}
