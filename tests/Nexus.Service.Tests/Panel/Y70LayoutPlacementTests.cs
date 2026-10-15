using System.Collections.Generic;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public sealed class Y70LayoutPlacementTests
{
    [Fact]
    public void SpanForSize_FourByOneIsOneRowTall()
    {
        Assert.Equal((4, 1), Y70LayoutPlacement.SpanForSize("4x1"));
    }

    [Fact]
    public void Append_FourByOneStripsStackOnConsecutiveRows()
    {
        var pages = new List<PanelPageDto>();
        var first = new PanelWidgetDto { Id = "a", Type = "deck", Size = "4x1" };
        var second = new PanelWidgetDto { Id = "b", Type = "deck", Size = "4x1" };

        Assert.True(Y70LayoutPlacement.Append(pages, first, "4x1"));
        Assert.True(Y70LayoutPlacement.Append(pages, second, "4x1"));

        Assert.Single(pages);
        Assert.Equal((0, 0), (first.Col, first.Row));
        Assert.Equal((0, 1), (second.Col, second.Row));
    }

    [Fact]
    public void Append_TwoByTwoAfterAStripKeepsTheCoarseRowGrid()
    {
        var pages = new List<PanelPageDto>();
        var strip = new PanelWidgetDto { Id = "a", Type = "deck", Size = "4x1" };
        var tile = new PanelWidgetDto { Id = "b", Type = "clock", Size = "2x2" };

        Y70LayoutPlacement.Append(pages, strip, "4x1");
        Y70LayoutPlacement.Append(pages, tile, "2x2");

        Assert.Equal((0, 2), (tile.Col, tile.Row));
    }
}
