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

    /// <summary>The marketplace listing this effect was downloaded from or published as; null when it has no link.</summary>
    public string? MarketId { get; set; }

    /// <summary>Account username of that listing's author.</summary>
    public string? MarketAuthor { get; set; }

    /// <summary>True when this effect came from a Marketplace download; null on rows saved before the flag existed.</summary>
    public bool? Downloaded { get; set; }
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
    private List<CustomEffectEntry> _entries = new();
    // Stored entries the current rules reject. Never registered or listed, but kept in the
    // file so a write does not destroy them.
    private List<CustomEffectEntry> _skipped = new();
    private bool _unavailable;

    public CustomEffectStore() : this(NexusDataPaths.NexusRoot()) { }

    /// <param name="clock">Unix milliseconds; defaults to the wall clock. UpdatedAt is strictly increasing regardless.</param>
    public CustomEffectStore(string dir, Func<long>? clock = null)
    {
        _clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
        _path = Path.Combine(dir, "custom-effects.json");
        lock (_lock)
        {
            Load();
        }
    }

    /// <summary>
    /// False while the file could not be read for a reason other than being malformed (a
    /// sharing violation, permissions). Writes are refused then so a transient failure never
    /// replaces a valid file; each call retries the load.
    /// </summary>
    public bool EnsureAvailable()
    {
        bool recovered = false, available;
        lock (_lock)
        {
            if (_unavailable)
            {
                Load();
                recovered = !_unavailable;
            }
            available = !_unavailable;
        }
        if (recovered)
        {
            try
            {
                Recovered?.Invoke();
            }
            catch (Exception ex)
            {
                // The load already latched as recovered; a subscriber's failure must not fail the caller.
                Console.Error.WriteLine($"[custom-effects] recovery handler failed: {ex.GetType().Name}: {ex.Message}");
            }
        }
        return available;
    }

    /// <summary>Raised, outside the lock, when a load succeeds after an earlier one failed, so a running custom effect can pick up its source.</summary>
    public event Action? Recovered;

    // Caller holds _lock.
    private void Load()
    {
        var (valid, skipped, unavailable) = Read();
        foreach (var old in _entries)
        {
            ShaderLibrary.UnregisterCustom(old.Id);
        }
        _unavailable = unavailable;
        _entries = valid;
        _skipped = skipped;
        foreach (var e in _entries)
        {
            ShaderLibrary.RegisterCustom(e.Id, e.Frag);
        }
    }

    public static bool IsValidId(string? id) => id is not null && IdPattern.IsMatch(id);

    private static readonly Regex MarketAuthorPattern = new(@"^[A-Za-z0-9_]{4,15}\z", RegexOptions.CultureInvariant);

    /// <summary>The first problem with the optional marketplace link fields, or null. Mirrors the cloud's username rule (length and charset; reserved words are the cloud's call).</summary>
    public static string? ValidateMarketLink(string? marketId, string? marketAuthor)
    {
        if (marketId is not null && !(marketId.Length == 36 && Guid.TryParseExact(marketId, "D", out _)))
        {
            return "marketId must be a GUID";
        }
        if (marketAuthor is not null && !MarketAuthorPattern.IsMatch(marketAuthor))
        {
            return "marketAuthor must be 4 to 15 letters, digits or underscores";
        }
        return null;
    }

    /// <summary>Entries by UpdatedAt ascending.</summary>
    public IReadOnlyList<CustomEffectEntry> List()
    {
        EnsureAvailable();
        lock (_lock)
        {
            return _entries.OrderBy(e => e.UpdatedAt).Select(Clone).ToList();
        }
    }

    public CustomEffectEntry? Get(string id)
    {
        EnsureAvailable();
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

    private readonly record struct LoopInfo(int Pos, int BodyStart, int BodyEnd, string Var, long Count);

    private static readonly Regex IdentRe = new(@"[A-Za-z_]\w*", RegexOptions.CultureInvariant);

    // The only function shape the Studio emits: a plain return type and a flat parameter list.
    private static readonly Regex FuncHeader = new(
        @"^\s*(?:void|float|vec2|vec3|vec4|mat2|int|bool)\s+([A-Za-z_]\w*)\s*\(([^()\[\]]*)\)\s*$",
        RegexOptions.CultureInvariant);

    // A declaration without a body at the start of a statement inside a function:
    // `[qualifier] type name(` (return and else lead real statements, so they are excluded).
    private static readonly Regex LocalPrototype = new(
        @"(?:^|[;{}])\s*(?!(?:return|else)\b)(?:[A-Za-z_]\w*(?:\s*\[[^\]]*\])?\s+)+[A-Za-z_]\w*\s*\(",
        RegexOptions.CultureInvariant);

    // `type name (` with nothing between: a declaration without a body.
    private static readonly Regex Prototype = new(
        @"^\s*(?:[A-Za-z_]\w*\s+)+[A-Za-z_]\w*\s*\(", RegexOptions.CultureInvariant);

    /// <summary>
    /// A narrow allowlist of what the Studio emits, not a general GLSL analysis. No
    /// preprocessor, no while or do, no inout, and only plain top-level functions without
    /// out parameters or prototypes. Every for is `for (int v = lit; v &lt;|&lt;= lit; v++) {`
    /// with literals in [-64, 64], a braced body that never writes v, nested loops that
    /// multiply to at most <see cref="MaxLoopIterations"/>, and no call to a function that
    /// itself loops. Known limit: exponential fan-out through loop-free function calls is not
    /// bounded here.
    /// </summary>
    internal static string? ValidateLoops(string frag)
    {
        var code = StripComments(frag);
        if (code.IndexOf('#', Math.Min(VersionLine.Length, code.Length)) >= 0)
        {
            return "preprocessor directives are not allowed";
        }
        var structure = ScanTopLevel(code, out var funcs);
        if (structure is not null)
        {
            return structure;
        }
        var loops = new List<LoopInfo>();
        foreach (Match w in IdentRe.Matches(code))
        {
            if (w.Value is "while" or "do")
            {
                return $"'{w.Value}' loops are not allowed; use a bounded for loop like for (int i = 0; i < 5; i++)";
            }
            if (w.Value == "inout")
            {
                return "inout parameters are not allowed";
            }
            if (w.Value != "for")
            {
                continue;
            }
            var after = w.Index + w.Length;
            var m = ForHeader.Match(code, after);
            if (!m.Success || m.Index != after)
            {
                return "for loops must look like for (int i = 0; i < 5; i++) with integer literal bounds";
            }
            if (!TryBound(m.Groups[2].Value, out var lo) || !TryBound(m.Groups[4].Value, out var hi))
            {
                return $"for loop bounds must be integer literals from -{MaxLoopIterations} to {MaxLoopIterations}";
            }
            var count = hi - lo + (m.Groups[3].Value == "<=" ? 1 : 0);
            if (count > MaxLoopIterations)
            {
                return $"a for loop may run at most {MaxLoopIterations} iterations";
            }
            var bodyStart = m.Index + m.Length;
            var brace = bodyStart;
            while (brace < code.Length && char.IsWhiteSpace(code[brace]))
            {
                brace++;
            }
            if (brace >= code.Length || code[brace] != '{')
            {
                return "a for loop body must be wrapped in braces";
            }
            var bodyEnd = MatchBrace(code, brace);
            if (bodyEnd < 0)
            {
                return "unbalanced braces in a for loop body";
            }
            var v = m.Groups[1].Value;
            if (WritesVariable(code.Substring(bodyStart, bodyEnd - bodyStart), v))
            {
                return $"the loop variable '{v}' must not be changed inside its loop";
            }
            loops.Add(new LoopInfo(w.Index, bodyStart, bodyEnd, v, Math.Max(count, 1)));
        }

        // Nested loops multiply: walk in source order, keeping the enclosing bodies. Each count
        // is at most the cap and the running product is checked at every level, so it cannot overflow.
        var open = new Stack<(int End, long Product)>();
        foreach (var l in loops)
        {
            while (open.Count > 0 && open.Peek().End <= l.Pos)
            {
                open.Pop();
            }
            var product = (open.Count > 0 ? open.Peek().Product : 1) * l.Count;
            if (product > MaxLoopIterations)
            {
                return $"nested for loops may run at most {MaxLoopIterations} iterations in total";
            }
            open.Push((l.BodyEnd, product));
        }

        foreach (var f in funcs)
        {
            if (LocalPrototype.IsMatch(code.Substring(f.Open, f.End - f.Open)))
            {
                return "function declarations are not allowed inside a function body";
            }
        }
        // Functions that loop, directly or through a looping callee, to a fixed point.
        var loopFns = new List<string>();
        bool changed;
        do
        {
            changed = false;
            foreach (var f in funcs)
            {
                if (loopFns.Contains(f.Name))
                {
                    continue;
                }
                var text = code.Substring(f.Open, f.End - f.Open);
                if (loops.Any(l => l.Pos > f.Open && l.Pos < f.End) || loopFns.Any(n => CallsFunction(text, n)))
                {
                    loopFns.Add(f.Name);
                    changed = true;
                }
            }
        }
        while (changed);
        foreach (var l in loops)
        {
            var body = code.Substring(l.BodyStart, l.BodyEnd - l.BodyStart);
            foreach (var fn in loopFns)
            {
                if (CallsFunction(body, fn))
                {
                    return $"a for loop body may not call '{fn}', which contains a loop";
                }
            }
        }
        return null;
    }

    private static bool TryBound(string literal, out long value)
    {
        value = 0;
        // Length first: a huge literal never reaches the parser.
        return literal.Length <= 4 && long.TryParse(literal, out value) && value is >= -MaxLoopIterations and <= MaxLoopIterations;
    }

    // Walks the top level after the version line: statements end in ';', functions have a braced body.
    private static string? ScanTopLevel(string code, out List<(string Name, int Open, int End)> funcs)
    {
        funcs = new();
        var i = Math.Min(VersionLine.Length, code.Length);
        while (true)
        {
            while (i < code.Length && char.IsWhiteSpace(code[i]))
            {
                i++;
            }
            if (i >= code.Length)
            {
                return null;
            }
            var start = i;
            var paren = 0;
            for (; i < code.Length; i++)
            {
                var c = code[i];
                if (c == '(')
                {
                    paren++;
                }
                else if (c == ')')
                {
                    paren--;
                }
                else if (paren == 0 && c is ';' or '{' or '}')
                {
                    break;
                }
            }
            if (i >= code.Length || code[i] == '}')
            {
                return "unbalanced braces or a missing semicolon at the top level";
            }
            var header = code.Substring(start, i - start);
            if (code[i] == ';')
            {
                if (Prototype.IsMatch(header))
                {
                    return "function prototypes are not allowed; define the function before use";
                }
                i++;
                continue;
            }
            var m = FuncHeader.Match(header);
            if (!m.Success)
            {
                return "only void, float, vec2, vec3, vec4, mat2, int and bool functions with plain parameters are allowed";
            }
            if (Regex.IsMatch(m.Groups[2].Value, @"\b(?:out|inout)\b", RegexOptions.CultureInvariant))
            {
                return "out and inout parameters are not allowed";
            }
            var end = MatchBrace(code, i);
            if (end < 0)
            {
                return "unbalanced braces";
            }
            funcs.Add((m.Groups[1].Value, i, end));
            i = end;
        }
    }

    private static bool CallsFunction(string text, string name) =>
        Regex.IsMatch(text, @"(?<![\w.])" + Regex.Escape(name) + @"\s*\(", RegexOptions.CultureInvariant);

    private static bool WritesVariable(string body, string v)
    {
        var id = Regex.Escape(v);
        // Parentheses around the variable do not make it a different lvalue: (i) = 0, (i)--, --(i).
        return Regex.IsMatch(body, @"(?<![\w.])\(*\s*" + id + @"(?!\w)(?:\s*\))*(?:\s*\.\s*\w+)?\s*(?:\+\+|--|(?:<<|>>|[-+*/%&|^])?=(?!=))", RegexOptions.CultureInvariant)
            || Regex.IsMatch(body, @"(?:\+\+|--)(?:\s*\()*\s*" + id + @"(?!\w)", RegexOptions.CultureInvariant);
    }

    // Index just past the brace matching the one at openIdx, or -1.
    private static int MatchBrace(string code, int openIdx)
    {
        var depth = 0;
        for (var i = openIdx; i < code.Length; i++)
        {
            if (code[i] == '{')
            {
                depth++;
            }
            else if (code[i] == '}' && --depth == 0)
            {
                return i + 1;
            }
        }
        return -1;
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
    /// <remarks>A null <paramref name="marketId"/> or <paramref name="marketAuthor"/> keeps the stored value, so a Studio re-save does not drop the link.</remarks>
    public (CustomEffectEntry? Entry, string? Error) Put(string id, string name, string recipe, string frag, string? marketId = null, string? marketAuthor = null, bool? downloaded = null)
    {
        CustomEffectEntry saved;
        // Outside the lock, so a Recovered handler never runs under it.
        EnsureAvailable();
        lock (_lock)
        {
            if (_unavailable)
            {
                return (null, UnavailableMessage);
            }
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
                MarketId = marketId ?? (idx < 0 ? null : next[idx].MarketId),
                MarketAuthor = marketAuthor ?? (idx < 0 ? null : next[idx].MarketAuthor),
                Downloaded = downloaded ?? (idx < 0 ? null : next[idx].Downloaded),
            };
            if (idx < 0)
            {
                next.Add(saved);
            }
            else
            {
                next[idx] = saved;
            }
            var skipped = _skipped.Where(x => x.Id != id).ToList();
            Write(next, skipped);
            _entries = next;
            _skipped = skipped;
            ShaderLibrary.RegisterCustom(id, frag);
        }
        return (Clone(saved), null);
    }

    public bool Delete(string id)
    {
        EnsureAvailable();
        lock (_lock)
        {
            if (_unavailable)
            {
                return false;
            }
            var next = _entries.Where(x => x.Id != id).ToList();
            var skipped = _skipped.Where(x => x.Id != id).ToList();
            if (next.Count == _entries.Count && skipped.Count == _skipped.Count)
            {
                return false;
            }
            Write(next, skipped);
            _entries = next;
            _skipped = skipped;
            ShaderLibrary.UnregisterCustom(id);
            return true;
        }
    }

    public const string UnavailableMessage = "custom effects are temporarily unavailable; try again";

    private (List<CustomEffectEntry> Valid, List<CustomEffectEntry> Skipped, bool Unavailable) Read()
    {
        try
        {
            if (!File.Exists(_path))
            {
                return (new(), new(), false);
            }
            var json = File.ReadAllText(_path);
            if (string.IsNullOrWhiteSpace(json))
            {
                return (new(), new(), false);
            }
            var doc = JsonSerializer.Deserialize(json, PersistenceJsonContext.Default.CustomEffectsDoc);
            var valid = new List<CustomEffectEntry>();
            var skipped = new List<CustomEffectEntry>();
            // A hand-edited file can hold nulls, and a file from another build can hold entries
            // today's rules reject (loops, size, encoding); those stay on disk but never run.
            foreach (var e in doc?.Entries ?? new())
            {
                if (e is null)
                {
                    continue;
                }
                var problem = Validate(e.Id, e.Name, e.Recipe, e.Frag);
                if (problem is null)
                {
                    valid.Add(e);
                }
                else
                {
                    skipped.Add(e);
                    Console.Error.WriteLine($"[custom-effects] skipping stored effect '{e.Id}': {problem}");
                }
            }
            return (valid, skipped, false);
        }
        catch (JsonException ex)
        {
            Console.Error.WriteLine($"[custom-effects] load failed: {ex.GetType().Name}: {ex.Message}");
            QuarantineUnreadable();
            return (new(), new(), false);
        }
        catch (Exception ex)
        {
            // IO or permission trouble may be transient: leave the file alone and refuse writes.
            Console.Error.WriteLine($"[custom-effects] load failed, writes disabled until it succeeds: {ex.GetType().Name}: {ex.Message}");
            return (new(), new(), true);
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

    private void Write(List<CustomEffectEntry> entries, List<CustomEffectEntry> skipped)
    {
        var dir = Path.GetDirectoryName(_path);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
        {
            Directory.CreateDirectory(dir);
        }
        AtomicJsonFile.Write(_path, JsonSerializer.Serialize(new CustomEffectsDoc { Entries = entries.Concat(skipped).ToList() }, PersistenceJsonContext.Default.CustomEffectsDoc));
    }

    private static CustomEffectEntry Clone(CustomEffectEntry e) => new()
    {
        Id = e.Id,
        Name = e.Name,
        Recipe = e.Recipe,
        Frag = e.Frag,
        UpdatedAt = e.UpdatedAt,
        MarketId = e.MarketId,
        MarketAuthor = e.MarketAuthor,
        Downloaded = e.Downloaded,
    };
}
