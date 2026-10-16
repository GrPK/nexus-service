using System.Collections.Generic;
using System.Linq;
using Nexus.Service.Models.Displays;
using Nexus.Service.Models.Panel;
using Nexus.Service.Panel;
using Nexus.Service.Platform.Displays;
using Xunit;

namespace Nexus.Service.Tests.Panel;

public class PanelDisplayRelinkTests
{
    private static DisplayFingerprint Fp(long serial = 0, string? text = null, string mfg = "DEL", string product = "41B7")
        => new() { Manufacturer = mfg, Product = product, Serial = serial, SerialText = text };

    private static RelinkRecord Rec(string id, string displayId, DisplayFingerprint? fp) => new(id, displayId, fp);

    private static RelinkDisplay Disp(string id, DisplayFingerprint? fp) => new(id, fp);

    [Fact]
    public void Unique_serial_match_moves_the_record()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "DEL41B7-old", Fp(1234)) },
            new[] { Disp("DEL41B7-new", Fp(1234)) });

        var move = Assert.Single(plan.Moves);
        Assert.Equal(("p1", "DEL41B7-old", "DEL41B7-new"), (move.RecordId, move.OldDisplayId, move.NewDisplayId));
    }

    [Fact]
    public void Serial_text_participates_in_the_match()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(0, "ABC123")) },
            new[] { Disp("new-a", Fp(0, "ZZZ999")), Disp("new-b", Fp(0, "ABC123")) });

        Assert.Equal("new-b", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Fact]
    public void Changed_serial_with_single_orphan_and_single_candidate_moves()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(1111)) },
            new[] { Disp("new", Fp(2222)) });

        Assert.Equal("new", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Theory]
    [InlineData(0L)]
    [InlineData(0x01010101L)]
    public void Weak_serial_with_single_candidate_moves(long serial)
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(serial)) },
            new[] { Disp("new", Fp(serial)) });

        Assert.Equal("new", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Fact]
    public void Two_identical_monitors_with_weak_serials_do_nothing()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(0)) },
            new[] { Disp("a", Fp(0)), Disp("b", Fp(0)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Two_orphans_of_one_model_do_nothing()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old1", Fp(0)), Rec("p2", "old2", Fp(0)) },
            new[] { Disp("new", Fp(0)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Two_orphans_with_identical_strong_fingerprints_do_nothing()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old1", Fp(7)), Rec("p2", "old2", Fp(7)) },
            new[] { Disp("new", Fp(7)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Strong_serials_pair_each_orphan_with_its_own_monitor()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old1", Fp(1)), Rec("p2", "old2", Fp(2)) },
            new[] { Disp("newB", Fp(2)), Disp("newA", Fp(1)) });

        Assert.Equal(
            new[] { ("p1", "newA"), ("p2", "newB") },
            plan.Moves.OrderBy(m => m.RecordId).Select(m => (m.RecordId, m.NewDisplayId)).ToArray());
    }

    [Fact]
    public void Display_owned_by_a_live_record_is_never_taken()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("orphan", "old", Fp(5)), Rec("live", "owned", Fp(5)) },
            new[] { Disp("owned", Fp(5)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Live_record_without_fingerprint_still_claims_its_display()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("orphan", "old", Fp(0)), Rec("live", "owned", null) },
            new[] { Disp("owned", Fp(0)) });

        Assert.Empty(plan.Moves);
        var backfill = Assert.Single(plan.Backfills);
        Assert.Equal("live", backfill.RecordId);
    }

    [Fact]
    public void Different_model_never_matches()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "old", Fp(9)) },
            new[] { Disp("new", Fp(9, mfg: "AOC", product: "2790")) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Backfill_stamps_fingerprint_on_attached_record_only()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "here", null), Rec("p2", "gone", null) },
            new[] { Disp("here", Fp(3)) });

        Assert.Equal("p1", Assert.Single(plan.Backfills).RecordId);
        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Record_without_fingerprint_is_never_an_orphan_to_move()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "gone", null) },
            new[] { Disp("new", Fp(3)) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Windows_fallback_id_on_the_record_side_relinks_to_the_pnp_id()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", @"\\.\DISPLAY3", Fp(44)) },
            new[] { Disp("DEL41B7-5-4-UID44", Fp(44)) });

        Assert.Equal("DEL41B7-5-4-UID44", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Fact]
    public void Windows_fallback_id_on_the_display_side_without_fingerprint_is_not_a_candidate()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "DEL41B7-old", Fp(44)) },
            new[] { Disp(@"\\.\DISPLAY2", null) });

        Assert.Empty(plan.Moves);
    }

    [Fact]
    public void Windows_fallback_id_display_with_fingerprint_can_be_a_target()
    {
        var plan = PanelDisplayRelink.Plan(
            new[] { Rec("p1", "DEL41B7-old", Fp(44)) },
            new[] { Disp(@"\\.\DISPLAY2", Fp(44)) });

        Assert.Equal(@"\\.\DISPLAY2", Assert.Single(plan.Moves).NewDisplayId);
    }

    [Fact]
    public void Registry_applies_backfill_then_relink_and_keeps_layout()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var (record, _) = registry.AllocateForDisplay("old-id", "Side panel", null);
        store.Update(s =>
        {
            s.PanelDevices[record.Id].Enabled = false;
            s.PanelDevices[record.Id].ThemeMode = "dark";
        });

        // Scan 1: monitor attached under its original id, fingerprint is backfilled.
        Assert.Empty(registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(77)) }));
        Assert.Equal(77, store.Load().PanelDevices[record.Id].DisplayFingerprint!.Serial);

        // Scan 2: same hardware under a new id.
        var moves = registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(77)) });

        Assert.Equal(("old-id", "new-id"), (moves.Single().OldDisplayId, moves.Single().NewDisplayId));
        var stored = store.Load().PanelDevices[record.Id];
        Assert.Equal("new-id", stored.DisplayId);
        Assert.Equal("dark", stored.ThemeMode);
        Assert.False(stored.Enabled);
        Assert.Single(store.Load().PanelDevices);
    }

    [Fact]
    public void Registry_never_resurrects_a_deleted_record()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var (record, _) = registry.AllocateForDisplay("old-id", null, null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(77)) });
        registry.Remove(record.Id);

        var moves = registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(77)) });

        Assert.Empty(moves);
        Assert.Empty(store.Load().PanelDevices);
    }

    [Fact]
    public void Registry_relink_makes_auto_promotion_skip_the_display()
    {
        var store = new InMemoryConfigStore();
        var registry = new PanelDeviceRegistry(store);
        var (record, _) = registry.AllocateForDisplay("old-id", "Edge", null);
        registry.ReconcileDisplayFingerprints(new[] { Disp("old-id", Fp(0, mfg: "CRX", product: "ED00")) });

        registry.ReconcileDisplayFingerprints(new[] { Disp("new-id", Fp(0, mfg: "CRX", product: "ED00")) });

        Assert.Equal(record.Id, registry.FindByDisplayId("new-id")?.Id);
        Assert.Null(registry.FindByDisplayId("old-id"));
    }

    [Fact]
    public void Edid_blob_yields_manufacturer_product_serial_and_descriptor_text()
    {
        var edid = new byte[128];
        edid[1] = edid[2] = edid[3] = edid[4] = edid[5] = edid[6] = 0xFF;
        // DEL: D=4 E=5 L=12 -> 00010 00101 01100
        edid[8] = 0x10; edid[9] = 0xAC;
        edid[10] = 0xB7; edid[11] = 0x41;
        edid[12] = 0x78; edid[13] = 0x56; edid[14] = 0x34; edid[15] = 0x12;
        edid[72 + 3] = 0xFF;
        var text = System.Text.Encoding.ASCII.GetBytes("SN-42\n");
        text.CopyTo(edid, 72 + 5);

        var fp = EdidFingerprint.FromEdid(edid)!;

        Assert.Equal("DEL", fp.Manufacturer);
        Assert.Equal("41B7", fp.Product);
        Assert.Equal(0x12345678, fp.Serial);
        Assert.Equal("SN-42", fp.SerialText);
    }

    [Fact]
    public void Non_edid_blob_yields_null()
    {
        Assert.Null(EdidFingerprint.FromEdid(new byte[128]));
        Assert.Null(EdidFingerprint.FromEdid(new byte[10]));
    }

    [Fact]
    public void FromParts_requires_manufacturer_and_product()
    {
        Assert.Null(EdidFingerprint.FromParts("", "41B7"));
        Assert.Null(EdidFingerprint.FromParts("DEL", null));
        Assert.Equal(5, EdidFingerprint.FromParts("DEL", "41B7", 5)!.Serial);
    }
}
