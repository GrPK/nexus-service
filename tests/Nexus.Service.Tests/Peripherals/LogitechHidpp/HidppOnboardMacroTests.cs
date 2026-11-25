using System;
using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Peripherals.LogitechHidpp;
using Xunit;

namespace Nexus.Service.Tests.Peripherals.LogitechHidpp;

public class HidppOnboardMacroTests
{
    private static readonly HidppMacroStep[] CtrlC =
    {
        new(HidppMacroStepKind.KeyDown, 0xE0),
        new(HidppMacroStepKind.KeyDown, 0x06),
        new(HidppMacroStepKind.Delay, 50),
        new(HidppMacroStepKind.KeyUp, 0x06),
        new(HidppMacroStepKind.KeyUp, 0xE0),
    };

    [Fact]
    public void FromSteps_EncodesModifierKeysAsTheirBit_AndEndsTheMacro()
    {
        var macro = HidppOnboardMacro.FromSteps(CtrlC);

        Assert.Equal(Convert.FromHexString("430100" + "430006" + "400032" + "440006" + "440100" + "FF"), macro.Bytes);
    }

    [Fact]
    public void Parse_ReadsBackWhatFromStepsWrote_AtAnAddress()
    {
        var sector = Enumerable.Repeat((byte)0xFF, 255).ToArray();
        var steps = new List<HidppMacroStep>(CtrlC)
        {
            new(HidppMacroStepKind.MouseDown, 0x01),
            new(HidppMacroStepKind.MouseUp, 0x01),
            new(HidppMacroStepKind.ConsumerDown, 0xE9),
            new(HidppMacroStepKind.ConsumerUp, 0xE9),
            new(HidppMacroStepKind.Wheel, -3),
            new(HidppMacroStepKind.HWheel, 2),
        };
        var bytes = HidppOnboardMacro.FromSteps(steps).Bytes;
        bytes.CopyTo(sector, 32);

        var parsed = HidppOnboardMacro.Parse(sector, 32);

        Assert.NotNull(parsed);
        Assert.True(parsed!.Editable);
        Assert.Equal(steps, parsed.Steps);
        Assert.Equal(bytes, parsed.Bytes);
    }

    [Fact]
    public void FromSteps_RefusesMacrosThatLeaveSomethingPressed()
    {
        Assert.Throws<ArgumentException>(() => HidppOnboardMacro.FromSteps(new[] { new HidppMacroStep(HidppMacroStepKind.KeyDown, 0x04) }));
        Assert.Throws<ArgumentException>(() => HidppOnboardMacro.FromSteps(new[] { new HidppMacroStep(HidppMacroStepKind.KeyUp, 0x04) }));
        Assert.Throws<ArgumentException>(() => HidppOnboardMacro.FromSteps(new[] { new HidppMacroStep(HidppMacroStepKind.MouseDown, 0x02) }));
        Assert.Throws<ArgumentException>(() => HidppOnboardMacro.FromSteps(new[] { new HidppMacroStep(HidppMacroStepKind.Delay, 0) }));
        Assert.Throws<ArgumentException>(() => HidppOnboardMacro.FromSteps(Array.Empty<HidppMacroStep>()));
    }

    [Fact]
    public void Parse_KeepsMacrosWithOtherCommands_AsNotEditable()
    {
        // key down, wait for release (0x01), key up, pointer move (0x61, 5 bytes), end
        var sector = Enumerable.Repeat((byte)0xFF, 64).ToArray();
        var bytes = Convert.FromHexString("430004" + "01" + "440004" + "6100050005" + "FF");
        bytes.CopyTo(sector, 0);

        var parsed = HidppOnboardMacro.Parse(sector, 0);

        Assert.NotNull(parsed);
        Assert.False(parsed!.Editable);
        Assert.Empty(parsed.Steps);
        Assert.Equal(bytes, parsed.Bytes);
    }

    [Fact]
    public void Parse_GivesUpWithoutAnEndMarker()
    {
        var sector = new byte[32]; // all NOOPs, no end before the CRC
        Assert.Null(HidppOnboardMacro.Parse(sector, 0));
        Assert.Null(HidppOnboardMacro.Parse(Convert.FromHexString("8000FFFF"), 0));
    }

    [Fact]
    public void Binding_DecodesMacroPointers_ButNotSectorZero()
    {
        var macro = HidppButtonBinding.Decode(Convert.FromHexString("00060010"));
        Assert.Equal(HidppButtonKind.Macro, macro.Kind);
        Assert.Equal(6, macro.Code);
        Assert.Equal(16, macro.Modifiers);
        Assert.Equal(Convert.FromHexString("00060010"), macro.Encode());
        Assert.Equal(HidppButtonKind.Raw, HidppButtonBinding.Decode(new byte[4]).Kind);
        Assert.Equal(HidppButtonKind.Raw, HidppButtonBinding.Decode(Convert.FromHexString("FFFFFFFF")).Kind);
    }
}
