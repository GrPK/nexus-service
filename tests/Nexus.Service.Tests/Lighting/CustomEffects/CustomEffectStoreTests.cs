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
    public void Unbraced_nested_loops_still_multiply()
    {
        var frag = CustomFrags.Frag(extra: "void f() { float x = 0.0; for (int a = 0; a < 20; a++) for (int b = 0; b < 20; b++) x += 1.0; }\n");
        Assert.Contains("nested", CustomEffectStore.Validate("abc", "n", "", frag));
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
