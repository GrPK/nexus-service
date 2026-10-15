using Nexus.Service.Lighting.Engine.Gpu;
using Xunit;

namespace Nexus.Service.Tests.Lighting.Gpu;

public class GlslDialectTests
{
    [Fact]
    public void Version_line_becomes_es300_with_default_precision_and_no_extra_line()
    {
        var src = "#version 330 core\nout vec4 c;\nvoid main() { c = vec4(1.0); }\n";

        var es = GlslDialect.ToEs300(src);

        Assert.StartsWith("#version 300 es\nprecision highp float; precision highp int; out vec4 c;\n", es);
        Assert.Equal(src.Split('\n').Length, es.Split('\n').Length);
        Assert.EndsWith("void main() { c = vec4(1.0); }\n", es);
    }

    [Fact]
    public void Directive_on_the_next_line_gets_precision_on_its_own_line()
    {
        var es = GlslDialect.ToEs300("#version 330 core\n#define K 1.0\nvoid main() {}\n");

        Assert.Equal("#version 300 es\nprecision highp float; precision highp int; \n#define K 1.0\nvoid main() {}\n", es);
    }

    [Theory]
    [InlineData("#version 330 core\r\nvoid main() {}")]
    [InlineData("\n#version 330 core\nvoid main() {}")]
    [InlineData("#version 330\nvoid main() {}")]
    public void Variants_of_the_330_line_are_rewritten(string src)
    {
        var es = GlslDialect.ToEs300(src);

        Assert.Contains("#version 300 es", es);
        Assert.DoesNotContain("330", es);
        Assert.Contains("precision highp float;", es);
    }

    [Theory]
    [InlineData("#version 410 core\nvoid main() {}")]
    [InlineData("#version 300 es\nvoid main() {}")]
    [InlineData("not glsl")]
    [InlineData("")]
    public void Other_text_passes_through_for_the_driver_to_judge(string src)
    {
        Assert.Equal(src, GlslDialect.ToEs300(src));
    }

    [Fact]
    public void Desktop_contexts_are_untouched()
    {
        var src = "#version 330 core\nvoid main() {}";

        Assert.Equal(src, GlslDialect.For(false, src));
        Assert.NotEqual(src, GlslDialect.For(true, src));
    }

    [Fact]
    public void Translation_keeps_the_rest_of_the_source_byte_for_byte()
    {
        var body = "uniform float u_time;\nvoid main() { float a = 0.5 * u_time; }\n";

        Assert.EndsWith(body, GlslDialect.ToEs300("#version 330 core\n" + body));
    }
}
