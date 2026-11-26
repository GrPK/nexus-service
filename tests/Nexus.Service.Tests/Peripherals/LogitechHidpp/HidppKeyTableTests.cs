using System;
using Nexus.Service.Peripherals.LogitechHidpp;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechHidpp;

// Tables and CRCs as G HUB wrote them to a G915 X (captured 2026-11-25/26).
public class HidppKeyTableTests
{
    private const int G1 = 0x6E, G2 = 0x77, G3 = 0x6F, G4 = 0x78, G5 = 0x70;

    [Fact]
    public void Build_OneKeyRemap_MatchesGHub()
    {
        var data = HidppKeyTable.Build(new[] { new HidppKeyRow(G1, HidppKeyAction.Key(0x0E)) });

        Assert.Equal(Convert.FromHexString("00016E01008000000080020" + "00E"), data);
        Assert.Equal(0x23EF1665u, HidppKeyTable.Crc32(data));
    }

    [Fact]
    public void Build_MouseForward_MatchesGHub()
    {
        var data = HidppKeyTable.Build(new[] { new HidppKeyRow(G1, new HidppKeyAction(HidppKeyActionKind.Mouse, 0x10)) });

        Assert.Equal(Convert.FromHexString("00016E0100800000008001" + "0010"), data);
        Assert.Equal(0xDBA6955Fu, HidppKeyTable.Crc32(data));
    }

    [Fact]
    public void Build_KeysMacroAndGShift_SortedByKey_MatchesGHub()
    {
        var data = HidppKeyTable.Build(new[]
        {
            new HidppKeyRow(G4, HidppKeyAction.GShift),
            new HidppKeyRow(G2, HidppKeyAction.Disabled, DivertSlot: 1),
            new HidppKeyRow(G5, HidppKeyAction.Key(0x27)),
            new HidppKeyRow(G3, HidppKeyAction.Key(0x10)),
            new HidppKeyRow(G1, HidppKeyAction.Key(0x0F)),
        });

        Assert.Equal(Convert.FromHexString(
            "0005" +
            "6E0100800000008002000F" +
            "6F01008000000080020010" +
            "70010080000000800200" + "27" +
            "77010100008000" + "80000000" +
            "78010080000000800400" + "02"), data);
        Assert.Equal(0xF29A3709u, HidppKeyTable.Crc32(data));
    }

    [Fact]
    public void Build_Empty_IsTwoZeroBytes()
    {
        var data = HidppKeyTable.Build(Array.Empty<HidppKeyRow>());

        Assert.Equal(new byte[] { 0, 0 }, data);
        Assert.Equal(0x41D912FFu, HidppKeyTable.Crc32(data));
    }

    [Fact]
    public void Parse_RoundTripsEveryKind()
    {
        var rows = new[]
        {
            new HidppKeyRow(0x01, HidppKeyAction.Key(0x04, 0x02)),
            new HidppKeyRow(0x02, new HidppKeyAction(HidppKeyActionKind.Mouse, 0x08)),
            new HidppKeyRow(0x03, new HidppKeyAction(HidppKeyActionKind.Consumer, 0x00CD)),
            new HidppKeyRow(0x04, HidppKeyAction.Disabled),
            new HidppKeyRow(G4, HidppKeyAction.GShift),
        };

        var parsed = HidppKeyTable.Parse(HidppKeyTable.Build(rows));

        Assert.Equal(rows, parsed);
    }

    [Fact]
    public void Build_RejectsDuplicatesAndBadActions()
    {
        Assert.Throws<ArgumentException>(() => HidppKeyTable.Build(new[]
        {
            new HidppKeyRow(1, HidppKeyAction.Key(0x04)), new HidppKeyRow(1, HidppKeyAction.Key(0x05)),
        }));
        Assert.Throws<ArgumentException>(() => HidppKeyTable.Build(new[] { new HidppKeyRow(1, HidppKeyAction.Key(0)) }));
        Assert.Throws<ArgumentException>(() => HidppKeyTable.Build(new[] { new HidppKeyRow(0x80, HidppKeyAction.Key(4)) }));
    }
}
