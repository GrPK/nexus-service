using Nexus.Service.Lighting.CustomEffects;
using Nexus.Service.Lighting.Engine.Gpu;

namespace Nexus.Service.Tests.Lighting.CustomEffects;

public sealed class CustomEffectStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "nexus-custom-fx-" + Guid.NewGuid().ToString("N"));

    public CustomEffectStoreTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* already gone */ }
    }

    [Fact]
    public void Put_then_Get_round_trips_and_trims_the_name()
    {
        var store = new CustomEffectStore(_dir);
        var id = CustomFrags.NewId();

        var (entry, error) = store.Put(id, "  Sunset  ", "{\"a\":1}", CustomFrags.Frag());

        Assert.Null(error);
        Assert.Equal("Sunset", entry!.Name);
        Assert.True(entry.UpdatedAt > 0);
        var got = store.Get(id)!;
        Assert.Equal("{\"a\":1}", got.Recipe);
        Assert.Equal(CustomFrags.Frag(), got.Frag);
    }

    [Fact]
    public void A_new_store_over_the_same_directory_sees_the_entries()
    {
        var id = CustomFrags.NewId();
        new CustomEffectStore(_dir).Put(id, "Kept", "r", CustomFrags.Frag());
        ShaderLibrary.UnregisterCustom(id);

        var reloaded = new CustomEffectStore(_dir);

        Assert.Equal("Kept", reloaded.Get(id)!.Name);
        Assert.True(ShaderLibrary.IsKnownKey("custom:" + id));
    }

    [Fact]
    public void List_orders_by_UpdatedAt_ascending()
    {
        // A frozen clock: ordering must come from the store, not from wall-clock gaps.
        var store = new CustomEffectStore(_dir, () => 1000);
        var a = CustomFrags.NewId();
        var b = CustomFrags.NewId();
        store.Put(a, "A", "", CustomFrags.Frag());
        store.Put(b, "B", "", CustomFrags.Frag());
        store.Put(a, "A2", "", CustomFrags.Frag());

        Assert.Equal([b, a], store.List().Select(e => e.Id));
    }

    [Fact]
    public void Delete_removes_the_entry_and_unregisters_the_source()
    {
        var store = new CustomEffectStore(_dir);
        var id = CustomFrags.NewId();
        store.Put(id, "Gone", "", CustomFrags.Frag());

        Assert.True(store.Delete(id));
        Assert.False(store.Delete(id));
        Assert.Null(store.Get(id));
        Assert.False(ShaderLibrary.IsKnownKey("custom:" + id));
        Assert.Null(new CustomEffectStore(_dir).Get(id));
    }

    [Fact]
    public void The_entry_cap_rejects_a_new_id_but_still_allows_replacing_one()
    {
        var store = new CustomEffectStore(_dir);
        var ids = Enumerable.Range(0, CustomEffectStore.MaxEntries).Select(_ => CustomFrags.NewId()).ToList();
        foreach (var id in ids)
        {
            Assert.Null(store.Put(id, "x", "", CustomFrags.Frag()).Error);
        }

        var (entry, error) = store.Put(CustomFrags.NewId(), "over", "", CustomFrags.Frag());
        Assert.Null(entry);
        Assert.NotNull(error);
        Assert.Null(store.Put(ids[0], "replaced", "", CustomFrags.Frag()).Error);
        Assert.Equal(CustomEffectStore.MaxEntries, store.List().Count);

        foreach (var id in ids)
        {
            ShaderLibrary.UnregisterCustom(id);
        }
    }

    [Theory]
    [InlineData("abc123", true)]
    [InlineData("a", true)]
    [InlineData("", false)]
    [InlineData("ABC", false)]
    [InlineData("has-dash", false)]
    [InlineData("has space", false)]
    [InlineData("custom:abc", false)]
    [InlineData("123456789012345678901234567890123", false)]
    public void Ids_are_one_to_thirty_two_lowercase_alphanumerics(string id, bool valid)
    {
        Assert.Equal(valid, CustomEffectStore.IsValidId(id));
    }

    [Fact]
    public void Validate_accepts_a_well_formed_request()
    {
        Assert.Null(CustomEffectStore.Validate("abc", "Name", "{}", CustomFrags.Frag()));
    }

    [Fact]
    public void Validate_rejects_a_bad_id()
    {
        Assert.NotNull(CustomEffectStore.Validate("Bad-Id", "Name", "{}", CustomFrags.Frag()));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_rejects_a_blank_name(string name)
    {
        Assert.NotNull(CustomEffectStore.Validate("abc", name, "{}", CustomFrags.Frag()));
    }

    [Fact]
    public void Validate_bounds_the_name_after_trimming()
    {
        Assert.Null(CustomEffectStore.Validate("abc", " " + new string('n', CustomEffectStore.MaxNameLength) + " ", "", CustomFrags.Frag()));
        Assert.NotNull(CustomEffectStore.Validate("abc", new string('n', CustomEffectStore.MaxNameLength + 1), "", CustomFrags.Frag()));
    }

    [Fact]
    public void Validate_caps_recipe_and_frag_size()
    {
        Assert.NotNull(CustomEffectStore.Validate("abc", "n", new string('r', CustomEffectStore.MaxRecipeBytes + 1), CustomFrags.Frag()));
        Assert.Null(CustomEffectStore.Validate("abc", "n", new string('r', CustomEffectStore.MaxRecipeBytes), CustomFrags.Frag()));
        var big = CustomFrags.Frag(extra: "//" + new string('x', CustomEffectStore.MaxFragBytes) + "\n");
        Assert.NotNull(CustomEffectStore.Validate("abc", "n", "", big));
    }

    [Fact]
    public void Validate_rejects_non_ascii_frag()
    {
        Assert.NotNull(CustomEffectStore.Validate("abc", "n", "", CustomFrags.Frag(extra: "// café\n")));
    }

    [Theory]
    [InlineData("void main() {}")]
    [InlineData("#version 410 core\nvoid main() {}")]
    [InlineData("\n#version 330 core\nvoid main() {}")]
    [InlineData("")]
    public void Validate_requires_the_version_line_first(string frag)
    {
        Assert.NotNull(CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void A_corrupt_file_reads_as_empty_and_is_set_aside_so_the_next_write_cannot_destroy_it()
    {
        var path = Path.Combine(_dir, "custom-effects.json");
        File.WriteAllText(path, "{ not json");

        var store = new CustomEffectStore(_dir, () => 4242);

        Assert.Empty(store.List());
        Assert.False(File.Exists(path));
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad-4242"));
        var id = CustomFrags.NewId();
        store.Put(id, "n", "", CustomFrags.Frag());
        Assert.Equal("{ not json", File.ReadAllText(path + ".bad-4242"));
        Assert.Equal(id, new CustomEffectStore(_dir).List().Single().Id);
    }

    [Theory]
    [InlineData("abc\n")]
    [InlineData("abc\r\n")]
    public void An_id_with_a_trailing_newline_is_rejected(string id)
    {
        Assert.False(CustomEffectStore.IsValidId(id));
        Assert.NotNull(CustomEffectStore.Validate(id, "n", "", CustomFrags.Frag()));
    }

    [Theory]
    [InlineData("for (int j = -1; j <= 1; j++)", true)]
    [InlineData("for (int i = 0; i < 5; i++)", true)]
    [InlineData("for(int i=0;i<64;i++)", true)]
    [InlineData("for (int i = 0; i <= 63; i++)", true)]
    [InlineData("for (int i = 0; i < 65; i++)", false)]
    [InlineData("for (int i = 0; i <= 64; i++)", false)]
    [InlineData("for (int i = -40; i < 40; i++)", false)]
    [InlineData("for (int i = 0; i < 99999999999999999999; i++)", false)]
    [InlineData("for (;;)", false)]
    [InlineData("for (int i = 0; i < 5; i += 1)", false)]
    [InlineData("for (int i = 0; i > -5; i--)", false)]
    [InlineData("for (int i = 0; j < 5; i++)", false)]
    [InlineData("for (int i = 0; i < n; i++)", false)]
    [InlineData("for (float f = 0.0; f < 5.0; f++)", false)]
    [InlineData("while (true) { }", false)]
    [InlineData("do { } while (x < 3);", false)]
    public void Loops_must_be_bounded_counted_for_loops(string loop, bool allowed)
    {
        var frag = CustomFrags.Frag(extra: "void f() { " + loop + " { } }\n");
        Assert.Equal(allowed, CustomEffectStore.Validate("abc", "n", "", frag) is null);
    }

    [Fact]
    public void Loop_words_inside_comments_and_longer_identifiers_are_fine()
    {
        var frag = CustomFrags.Frag(extra: "// while (true) do\n/* for (;;) */\nfloat window = 1.0; float undo = 2.0; float format = 3.0;\n");
        Assert.Null(CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Theory]
    [InlineData("i = 0;")]
    [InlineData("i += 1;")]
    [InlineData("i -= 1;")]
    [InlineData("i *= 2;")]
    [InlineData("i /= 2;")]
    [InlineData("i++;")]
    [InlineData("i--;")]
    [InlineData("++i;")]
    [InlineData("--i;")]
    [InlineData("int k = (i = 0);")]
    public void The_loop_variable_cannot_be_written_in_the_body(string statement)
    {
        var frag = CustomFrags.Frag(extra: "void f() { for (int i = 0; i < 2; i++) { " + statement + " } }\n");
        Assert.Contains("loop variable", CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void Reading_the_loop_variable_and_similar_names_in_the_body_is_fine()
    {
        var frag = CustomFrags.Frag(extra: "void f() { float s = 0.0; for (int i = 0; i < 4; i++) { float fi = float(i); if (i == 2 || i <= 1 || i >= 3 || i != 9) { s += fi * float(i) + s.x; } int ii = 3; ii = 4; } }\n");
        Assert.Null(CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Theory]
    [InlineData("#define W wh##ile\n")]
    [InlineData("#define N 5\n")]
    [InlineData("#ifdef GL_ES\n#endif\n")]
    public void Preprocessor_directives_after_the_version_line_are_rejected(string directive)
    {
        var msg = CustomEffectStore.Validate("abc", "n", "", CustomFrags.Frag(extra: directive));
        Assert.Contains("preprocessor", msg);
    }

    [Fact]
    public void Nested_loops_multiply_against_the_cap()
    {
        string Nest(int outer, int inner) =>
            CustomFrags.Frag(extra: $"void f() {{ for (int a = 0; a < {outer}; a++) {{ for (int b = 0; b < {inner}; b++) {{ }} }} }}\n");

        Assert.Null(CustomEffectStore.Validate("abc", "n", "", Nest(8, 8)));
        Assert.Contains("nested", CustomEffectStore.Validate("abc", "n", "", Nest(9, 8)));
        Assert.Contains("nested", CustomEffectStore.Validate("abc", "n", "", Nest(64, 2)));
    }

    [Fact]
    public void Sibling_loops_do_not_multiply()
    {
        var frag = CustomFrags.Frag(extra: "void f() { for (int a = 0; a < 60; a++) { } for (int b = 0; b < 60; b++) { } }\n");
        Assert.Null(CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void A_loop_body_may_not_call_a_function_that_loops()
    {
        var frag = CustomFrags.Frag(extra:
            "float fbm(vec2 p) { float s = 0.0; for (int i = 0; i < 5; i++) { s += p.x; } return s; }\n"
            + "void f() { float t = 0.0; for (int a = 0; a < 3; a++) { t += fbm(vec2(1.0)); } }\n");
        Assert.Contains("fbm", CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void A_function_that_calls_a_looping_function_counts_as_looping()
    {
        var frag = CustomFrags.Frag(extra:
            "float fbm(vec2 p) { float s = 0.0; for (int i = 0; i < 5; i++) { s += p.x; } return s; }\n"
            + "float wrap(vec2 p) { return fbm(p); }\n"
            + "void f() { float t = 0.0; for (int a = 0; a < 3; a++) { t += wrap(vec2(1.0)); } }\n");
        Assert.Contains("wrap", CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void The_studio_patterns_pass()
    {
        var frag = CustomFrags.Frag(extra:
            "float hash21(vec2 p) { return fract(sin(dot(p, vec2(12.9898, 78.233))) * 43758.5453); }\n"
            + "float fbm(vec2 p) { float s = 0.0; float a = 0.5; for (int i = 0; i < 5; i++) { s += a * hash21(p); a *= 0.5; } return s; }\n"
            + "void cells(vec2 uv) { float d = 9.0; for (int j = -1; j <= 1; j++) { for (int i = -1; i <= 1; i++) { d = min(d, hash21(uv + vec2(float(i), float(j)))); } } float n = fbm(uv); }\n");
        Assert.Null(CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void Stored_entries_the_current_rules_reject_are_not_registered_or_listed_but_stay_in_the_file()
    {
        var bad = CustomFrags.NewId();
        var good = CustomFrags.NewId();
        var json = "{\"entries\":[{\"id\":\"" + bad + "\",\"name\":\"old\",\"recipe\":\"\",\"frag\":\"#version 330 core\\nvoid main() { while (true) { } }\",\"updatedAt\":1},"
            + "{\"id\":\"" + good + "\",\"name\":\"ok\",\"recipe\":\"\",\"frag\":" + System.Text.Json.JsonSerializer.Serialize(CustomFrags.Frag()) + ",\"updatedAt\":2}]}";
        var path = Path.Combine(_dir, "custom-effects.json");
        File.WriteAllText(path, json);

        var store = new CustomEffectStore(_dir);

        Assert.Equal([good], store.List().Select(e => e.Id));
        Assert.Null(store.Get(bad));
        Assert.False(ShaderLibrary.IsKnownKey("custom:" + bad));
        Assert.True(ShaderLibrary.IsKnownKey("custom:" + good));
        // A later write must not destroy the skipped entry.
        store.Put(CustomFrags.NewId(), "new", "", CustomFrags.Frag());
        Assert.Contains(bad, File.ReadAllText(path));
        // Replacing it with a valid source takes it over.
        Assert.Null(store.Put(bad, "fixed", "", CustomFrags.Frag()).Error);
        Assert.Contains(bad, store.List().Select(e => e.Id));
    }

    [Fact]
    public void An_unreadable_file_fails_closed_keeps_the_file_and_recovers_when_readable()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(_dir, "custom-effects.json");
        var id = CustomFrags.NewId();
        new CustomEffectStore(_dir).Put(id, "keep", "", CustomFrags.Frag());
        var original = File.ReadAllText(path);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var store = new CustomEffectStore(_dir);

            Assert.False(store.EnsureAvailable());
            Assert.NotNull(store.Put(CustomFrags.NewId(), "x", "", CustomFrags.Frag()).Error);
            Assert.False(store.Delete(id));
            Assert.Empty(Directory.GetFiles(_dir, "*.bad-*"));

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            Assert.True(store.EnsureAvailable());
            Assert.Equal(id, store.List().Single().Id);
            Assert.Equal(original, File.ReadAllText(path));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData("void f() { float c = 0.0; for (int i = 0; i < 2; i++) c += 1.0; }", "braces")]
    [InlineData("void f() { float c = 0.0; for (int i = 0; i < 2; i++) if (c > 1e9) c += 1.0; else i--; }", "braces")]
    [InlineData("void f() { float c = 0.0; for (int a = 0; a < 20; a++) for (int b = 0; b < 20; b++) c += 1.0; }", "braces")]
    [InlineData("void g(inout float x) { x += 1.0; }", "inout")]
    [InlineData("void g(out float x) { x = 1.0; }", "out")]
    [InlineData("float g(float x);", "prototype")]
    [InlineData("float g(float x);\nfloat h(float x) { return x; }", "prototype")]
    [InlineData("void f() { for (int i = 0; i < 2147483647; i++) { } }", "-64 to 64")]
    [InlineData("void f() { for (int i = -2147483647; i < 2; i++) { } }", "-64 to 64")]
    [InlineData("void f() { for (int i = 0; i < 65; i++) { } }", "-64 to 64")]
    [InlineData("highp float g(float x) { return x; }", "plain parameters")]
    [InlineData("vec2[2] g(float x) { return vec2[2](vec2(x), vec2(x)); }", "plain parameters")]
    [InlineData("mat2x2 g(float x) { return mat2x2(x); }", "plain parameters")]
    [InlineData("struct S { float a; }; S g(float x) { return S(x); }", "plain parameters")]
    [InlineData("float g(float x[(2)]) { return x[0]; }", "plain parameters")]
    [InlineData("float g(float x[2]) { return x[0]; }", "plain parameters")]
    public void The_allowlist_rejects_shapes_the_studio_never_emits(string code, string expected)
    {
        var msg = CustomEffectStore.Validate("abc", "n", "", CustomFrags.Frag(extra: code + "\n"));
        Assert.NotNull(msg);
        Assert.Contains(expected, msg);
    }

    [Theory]
    [InlineData("block-cells.frag")]
    [InlineData("block-clouds.frag")]
    [InlineData("block-warp.frag")]
    [InlineData("mods.frag")]
    [InlineData("starter-lavaLamp.frag")]
    [InlineData("starter-starWarp.frag")]
    public void Studio_generated_shaders_pass(string fixture)
    {
        var frag = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "CustomEffects", fixture));
        Assert.Null(CustomEffectStore.Validate("abc", "n", "", frag));
        Assert.Null(CustomEffectStore.ValidateLoops(frag));
    }

    [Fact]
    public void The_entry_cap_does_not_count_skipped_entries()
    {
        var entries = new List<string>();
        for (var i = 0; i < 5; i++)
        {
            entries.Add("{\"id\":\"bad" + i + "\",\"name\":\"old\",\"recipe\":\"\",\"frag\":\"#version 330 core\\nvoid main() { while (true) { } }\",\"updatedAt\":1}");
        }
        File.WriteAllText(Path.Combine(_dir, "custom-effects.json"), "{\"entries\":[" + string.Join(",", entries) + "]}");
        var store = new CustomEffectStore(_dir);
        var ids = Enumerable.Range(0, CustomEffectStore.MaxEntries).Select(_ => CustomFrags.NewId()).ToList();

        foreach (var id in ids)
        {
            Assert.Null(store.Put(id, "x", "", CustomFrags.Frag()).Error);
        }

        Assert.NotNull(store.Put(CustomFrags.NewId(), "over", "", CustomFrags.Frag()).Error);
        foreach (var id in ids)
        {
            ShaderLibrary.UnregisterCustom(id);
        }
    }

    [Fact]
    public void List_and_Get_retry_an_unreadable_store_and_raise_Recovered()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(_dir, "custom-effects.json");
        var id = CustomFrags.NewId();
        new CustomEffectStore(_dir).Put(id, "keep", "", CustomFrags.Frag());
        ShaderLibrary.UnregisterCustom(id);
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var store = new CustomEffectStore(_dir);
            var recovered = 0;
            store.Recovered += () => recovered++;
            Assert.Empty(store.List());
            Assert.Null(store.Get(id));
            Assert.Equal(0, recovered);

            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Equal("keep", store.Get(id)!.Name);
            Assert.Single(store.List());
            Assert.Equal(1, recovered);
            Assert.True(ShaderLibrary.IsKnownKey("custom:" + id));
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Theory]
    [InlineData("(i) = -1;")]
    [InlineData("((i)) = (0);")]
    [InlineData("--(i);")]
    [InlineData("++ ( i );")]
    [InlineData("(i)--;")]
    [InlineData("(i)++;")]
    [InlineData("(i) -= 1;")]
    [InlineData("( ( i ) ) += 1;")]
    [InlineData("i.x = 0;")]
    public void Parenthesized_and_member_writes_to_the_loop_variable_are_rejected(string statement)
    {
        var frag = CustomFrags.Frag(extra: "void f() { for (int i = 0; i < 2; i++) { " + statement + " } }\n");
        Assert.Contains("loop variable", CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void Parenthesized_reads_and_comparisons_of_the_loop_variable_are_fine()
    {
        var frag = CustomFrags.Frag(extra: "void f() { float s = 0.0; for (int i = 0; i < 4; i++) { if ((i) == 2 || (i) <= 1 || ((i)) >= 3 || (i) != 9) { s += float((i)) - (float(i)); } } }\n");
        Assert.Null(CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Theory]
    [InlineData("void f() { float b(float y); }")]
    [InlineData("void f() { float s = 0.0; float b(float y); s = b(1.0); }")]
    [InlineData("void f() { for (int i = 0; i < 2; i++) { vec3 b(float y); } }")]
    [InlineData("void f() { const float b(float y); }")]
    public void Function_declarations_inside_a_body_are_rejected(string code)
    {
        Assert.Contains("inside a function body", CustomEffectStore.Validate("abc", "n", "", CustomFrags.Frag(extra: code + "\n")));
    }

    [Fact]
    public void A_deep_chain_through_local_prototypes_is_rejected()
    {
        var frag = CustomFrags.Frag(extra:
            "float a(float x) { float b(float y); return b(x); }\n"
            + "float b(float y) { float c(float z); return c(y); }\n"
            + "float c(float z) { float s = 0.0; for (int i = 0; i < 64; i++) { s += z; } return s; }\n"
            + "void f() { float t = 0.0; for (int i = 0; i < 64; i++) { t += a(1.0); } }\n");
        Assert.NotNull(CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void A_wrapper_chain_of_defined_functions_is_flagged_through_every_level()
    {
        var frag = CustomFrags.Frag(extra:
            "float c(float z) { float s = 0.0; for (int i = 0; i < 64; i++) { s += z; } return s; }\n"
            + "float b(float y) { return c(y); }\n"
            + "float a(float x) { return b(x); }\n"
            + "void f() { float t = 0.0; for (int i = 0; i < 64; i++) { t += a(1.0); } }\n");
        Assert.Contains("'a'", CustomEffectStore.Validate("abc", "n", "", frag));
    }

    [Fact]
    public void A_throwing_Recovered_handler_does_not_fail_the_caller_and_the_recovery_stays_latched()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(_dir, "custom-effects.json");
        var id = CustomFrags.NewId();
        new CustomEffectStore(_dir).Put(id, "keep", "", CustomFrags.Frag());
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var store = new CustomEffectStore(_dir);
            var calls = 0;
            store.Recovered += () =>
            {
                calls++;
                throw new InvalidOperationException("boom");
            };
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Equal("keep", store.Get(id)!.Name);
            Assert.Single(store.List());
            Assert.Equal(1, calls);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void Recovered_is_raised_outside_the_store_lock_even_when_a_write_triggers_the_retry()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }
        var path = Path.Combine(_dir, "custom-effects.json");
        var id = CustomFrags.NewId();
        new CustomEffectStore(_dir).Put(id, "keep", "", CustomFrags.Frag());
        File.SetUnixFileMode(path, UnixFileMode.None);
        try
        {
            var store = new CustomEffectStore(_dir);
            var otherThreadGotTheLock = false;
            store.Recovered += () => otherThreadGotTheLock = Task.Run(() => store.List()).Wait(TimeSpan.FromSeconds(3));
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);

            Assert.Null(store.Put(CustomFrags.NewId(), "x", "", CustomFrags.Frag()).Error);
            Assert.True(otherThreadGotTheLock);
        }
        finally
        {
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
        }
    }

    [Fact]
    public void A_rejected_loop_says_why()
    {
        var msg = CustomEffectStore.Validate("abc", "n", "", CustomFrags.Frag(extra: "void f() { while (true) { } }\n"));
        Assert.Contains("while", msg);
    }

    [Fact]
    public void A_hand_edited_file_drops_entries_the_routes_would_reject()
    {
        var good = CustomFrags.NewId();
        var json = "{\"entries\":[null,{\"id\":\"BAD ID\",\"name\":\"x\",\"frag\":\"f\"},{\"id\":\"" + good + "\",\"name\":\"ok\",\"recipe\":\"\",\"frag\":\"#version 330 core\",\"updatedAt\":5}]}";
        File.WriteAllText(Path.Combine(_dir, "custom-effects.json"), json);

        Assert.Equal([good], new CustomEffectStore(_dir).List().Select(e => e.Id));
    }
}
