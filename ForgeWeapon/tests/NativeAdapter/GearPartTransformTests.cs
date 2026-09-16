using System.Reflection;
using Gear;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.NativeAdapter;

// The authored gear-part files and the one presentation hook that applies them. Every case reads real files from a
// private temp package directory and drives the postfix the way Harmony does; the native holder is a managed
// double, so nothing here is game-verified.
public sealed class GearPartTransformTests
{
    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static void Reset() => World.Reset();

    /// <summary>Invokes the presentation postfix the way Harmony does, with the patch's own instance.</summary>
    private static void Hook(Type hook, object instance)
        => hook.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new[] { instance });

    private static readonly Type spawned = typeof(GearPartsSpawned);

    /// <summary>One spawn of one holder, exactly as the native postfix drives it.</summary>
    private static void Spawn(World world, GearPartHolder holder)
    {
        Hook(spawned, holder);
        Require(!world.Session!.Faulted, "The presentation hook faulted the session: " + world.Session.LastFault
            + " reports=" + string.Join(" | ", world.Reports));
    }

    private static float[] Xyz(UnityEngine.Vector3 value) => new[] { value.x, value.y, value.z };

    [Fact]
    public void authored_file_is_applied_after_the_holder_spawns()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            {
              "blockId": "10001",
              "parts": [
                { "component": "SightPart", "enabled": false,
                  "localPosition": [0.25, -0.5, 1],
                  "localEulerAngles": [45, -90, 725],
                  "localScale": [0.5, 2, 4],
                  "children": [ { "path": "Bone/Tip", "localScale": [3, 3, 3] },
                                { "path": "Glass", "enabled": true } ] }
              ]
            }
            """);
        var applier = world.StartSession().GearParts;
        Require(applier.BlockCount == 1, "The authored file was not loaded: " + string.Join(" | ", world.Reports));
        var sight = World.Part();
        var bone = World.Child();
        var tip = World.Child(9, 9, 9);
        var glass = World.Child(8, 8, 8);
        sight.transform.Children["Bone"] = bone;
        bone.Children["Tip"] = tip;
        sight.transform.Children["Glass"] = glass;
        var holder = World.Holder(10001, (GearPartSlot.SightPart, sight));

        Spawn(world, holder);

        Require(!sight.activeSelf && Xyz(sight.transform.localPosition).SequenceEqual(new[] { 0.25f, -0.5f, 1f })
            && Xyz(sight.transform.localEulerAngles).SequenceEqual(new[] { 45f, 270f, 5f })
            && Xyz(sight.transform.localScale).SequenceEqual(new[] { 0.5f, 2f, 4f }),
            "The part's authored pose was not applied: " + string.Join(" | ", Xyz(sight.transform.localPosition)));
        Require(tip.localScale.x == 3f && tip.localScale.y == 3f && tip.localScale.z == 3f,
            "The nested child's authored scale was not applied.");
        Require(glass.gameObject.activeSelf && Xyz(glass.localPosition).SequenceEqual(new[] { 8f, 8f, 8f }),
            "A child the file only made visible was moved or hidden.");
        Require(world.Reports.Count == 0, "A valid file was reported: " + string.Join(" | ", world.Reports));
        Require(world.Infos.Any(line => line.StartsWith("weapon.gear-part-applied block=10001 parts=1", StringComparison.Ordinal)),
            "The applied pose was not reported at info level: " + string.Join(" | ", world.Infos));
    }

    /// <summary>A child entry may nest `children` under itself, and the whole tree is applied: every level is a node
    /// in its own right, resolved by its full path from the part, so the two levels below the first child are posed
    /// and not just the one level the top array names.</summary>
    [Fact]
    public void a_nested_child_tree_is_applied_to_its_deepest_node()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            {
              "blockId": "10001",
              "parts": [
                { "component": "SightPart", "localPosition": [0.25, 0, 0],
                  "children": [
                    { "path": "Bone", "localScale": [2, 2, 2],
                      "children": [
                        { "path": "Tip", "localPosition": [0, 0.5, 0],
                          "children": [
                            { "path": "Glass", "enabled": false, "localEulerAngles": [0, 90, 0] } ] } ] } ] }
              ]
            }
            """);
        world.StartSession();
        var sight = World.Part(5, 5, 5);
        var bone = World.Child(1, 1, 1);
        var tip = World.Child(2, 2, 2);
        var glass = World.Child(3, 3, 3);
        sight.transform.Children["Bone"] = bone;
        bone.Children["Tip"] = tip;
        tip.Children["Glass"] = glass;

        Spawn(world, World.Holder(10001, (GearPartSlot.SightPart, sight)));

        Require(Xyz(bone.localScale).SequenceEqual(new[] { 2f, 2f, 2f }), "The first nested level was not applied.");
        Require(Xyz(tip.localPosition).SequenceEqual(new[] { 0f, 0.5f, 0f }), "The second nested level was not applied.");
        Require(!glass.gameObject.activeSelf && Xyz(glass.localEulerAngles).SequenceEqual(new[] { 0f, 90f, 0f }),
            "The third nested level was not applied: " + string.Join(" | ", Xyz(glass.localEulerAngles)));
        Require(world.Reports.Count == 0, "A valid nested tree was reported: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void repeated_spawns_are_idempotent()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0.2, 0.3] } ] }
            """);
        world.StartSession();
        var stock = World.Part(5, 5, 5);
        var holder = World.Holder(10001, (GearPartSlot.StockPart, stock));

        Spawn(world, holder);
        var first = Xyz(stock.transform.localPosition);
        Spawn(world, holder);
        Spawn(world, holder);

        Require(first.SequenceEqual(new[] { 0.1f, 0.2f, 0.3f }), "The first spawn did not write the authored pose.");
        Require(Xyz(stock.transform.localPosition).SequenceEqual(first), "A repeated spawn changed the pose again.");
        Require(world.Reports.Count == 0, "A repeated spawn was reported: " + string.Join(" | ", world.Reports));
        Require(world.Infos.Count(line => line.StartsWith("weapon.gear-part-applied", StringComparison.Ordinal)) == 3,
            "Each holder spawn must report its own applied line: " + string.Join(" | ", world.Infos));
    }

    /// <summary>Every view the ruling names assembles its own holder — first person, another player's third person
    /// copy, the menu preview, an icon render and a deployed sentry. The hook cannot tell them apart and writes the
    /// same authored numbers to each, so five independent holders of one block all end up posed.</summary>
    [Fact]
    public void every_view_of_the_same_block_is_posed_from_the_same_file()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "MagPart", "localPosition": [0.4, 0, -0.2] } ] }
            """);
        world.StartSession();
        var holders = Enumerable.Range(0, 5).Select(_ => World.Holder(10001, (GearPartSlot.MagPart, World.Part(1, 1, 1)))).ToArray();

        foreach (var holder in holders) Spawn(world, holder);

        Require(holders.All(h => Xyz(h.MagPart!.transform.localPosition).SequenceEqual(new[] { 0.4f, 0f, -0.2f })),
            "A view's holder kept the game's own pose.");
        Require(world.Reports.Count == 0 && world.Infos.Count(line => line.StartsWith("weapon.gear-part-applied", StringComparison.Ordinal)) == 5,
            "Each holder must report its own applied line: " + string.Join(" | ", world.Infos.Concat(world.Reports)));
    }

    [Fact]
    public void a_block_with_no_authored_file_is_left_untouched()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0.2, 0.3] } ] }
            """);
        world.StartSession();
        var stock = World.Part(5, 5, 5);
        var other = World.Part(1, 2, 3);

        Spawn(world, World.Holder(10002, (GearPartSlot.StockPart, stock)));
        Spawn(world, World.Holder(10003, (GearPartSlot.StockPart, other)));

        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 5f, 5f, 5f })
            && Xyz(other.transform.localPosition).SequenceEqual(new[] { 1f, 2f, 3f }),
            "A holder with no authored file was written to.");
        Require(world.Reports.Count == 0 && !world.Infos.Any(line => line.StartsWith("weapon.gear-part-applied", StringComparison.Ordinal)),
            "An unconfigured holder was reported as applied: " + string.Join(" | ", world.Infos.Concat(world.Reports)));
    }

    /// <summary>A holder whose gear carries no offline block record at all — a gear the game did not build from a
    /// block, or one whose record this build does not spell — names no block, so nothing can be looked up.</summary>
    [Fact]
    public void a_holder_without_a_readable_block_record_is_left_untouched()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0.2, 0.3] } ] }
            """);
        world.StartSession();
        var bare = World.Holder(10001, (GearPartSlot.StockPart, World.Part(5, 5, 5)));
        bare.GearIDRange!.PlayfabItemInstanceId = null;
        var padded = World.Holder(10001, (GearPartSlot.StockPart, World.Part(6, 6, 6)));
        padded.GearIDRange!.PlayfabItemInstanceId = "OfflineGear_ID_010001";
        var missing = World.Holder(10001, (GearPartSlot.StockPart, World.Part(7, 7, 7)));
        missing.GearIDRange = null;

        Spawn(world, bare);
        Spawn(world, padded);
        Spawn(world, missing);

        Require(Xyz(bare.StockPart!.transform.localPosition).SequenceEqual(new[] { 5f, 5f, 5f })
            && Xyz(padded.StockPart!.transform.localPosition).SequenceEqual(new[] { 6f, 6f, 6f })
            && Xyz(missing.StockPart!.transform.localPosition).SequenceEqual(new[] { 7f, 7f, 7f }),
            "A holder whose block could not be read was posed anyway.");
        Require(world.Reports.Count == 0 && !world.Infos.Any(line => line.StartsWith("weapon.gear-part-applied", StringComparison.Ordinal)),
            "A holder with no readable block was reported: " + string.Join(" | ", world.Infos.Concat(world.Reports)));
    }

    [Fact]
    public void a_part_the_file_does_not_mention_is_left_alone()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0.2, 0.3] } ] }
            """);
        world.StartSession();
        var stock = World.Part(5, 5, 5);
        var sight = World.Part(4, 4, 4);

        Spawn(world, World.Holder(10001, (GearPartSlot.StockPart, stock), (GearPartSlot.SightPart, sight)));

        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 0.1f, 0.2f, 0.3f })
            && Xyz(sight.transform.localPosition).SequenceEqual(new[] { 4f, 4f, 4f }),
            "A part the file does not mention was written to.");
        Require(world.Reports.Count == 0, "An unmentioned part was reported: " + string.Join(" | ", world.Reports));
    }

    /// <summary>A slot the holder left empty is the ordinary case of a gear that does not carry that part: the entry
    /// is skipped, and the rest of the file still applies.</summary>
    [Fact]
    public void an_empty_part_slot_is_skipped_without_a_report()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "FlashlightPart", "localPosition": [0.3, 0, 0] },
                                             { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        world.StartSession();
        var stock = World.Part(5, 5, 5);

        Spawn(world, World.Holder(10001, (GearPartSlot.StockPart, stock)));

        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 0.1f, 0f, 0f }), "A file with an absent slot did not apply its other entries.");
        Require(world.Reports.Count == 0, "An absent slot was reported: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void a_file_over_an_authored_limit_is_rejected()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [1.01, 0, 0] } ] }
            """);
        world.Place("InfiniMap", 10002, """
            { "blockId": "10002", "parts": [ { "component": "StockPart", "localScale": [4.01, 1, 1] } ] }
            """);
        world.Place("InfiniMap", 10003, Block(10003, 65));
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "A file over a limit was accepted.");
        Require(world.Reports.Count == 3 && world.Reports.All(line => line.StartsWith("weapon.gear-part-limit file=", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-limit file=plugins/InfiniMap/forge/gear-parts/10001.json", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-limit file=plugins/InfiniMap/forge/gear-parts/10002.json", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-limit file=plugins/InfiniMap/forge/gear-parts/10003.json", StringComparison.Ordinal)),
            "The three over-limit files were not each refused once with a reason: " + string.Join(" | ", world.Reports));
        var stock = World.Part(5, 5, 5);
        Spawn(world, World.Holder(10001, (GearPartSlot.StockPart, stock)));
        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 5f, 5f, 5f }), "A refused file was applied anyway.");
    }

    /// <summary>Every authored bound is closed at both ends: the cap is 64 part entries, position is within a metre
    /// per axis, scale is inside its band, and the deepest accepted child path is eight segments.</summary>
    [Fact]
    public void values_exactly_on_an_authored_limit_are_accepted()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10004, """
            { "blockId": "10004", "parts": [ { "component": "StockPart", "localPosition": [1, -1, 0], "localScale": [0.05, 4, 1],
                "localEulerAngles": [-1, 360, 721],
                "children": [ { "path": "1/2/3/4/5/6/7/8", "localPosition": [0, 0, 0] } ] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 1, "A value exactly on a limit was refused: " + string.Join(" | ", world.Reports));
        Require(world.Reports.Count == 0, "A value exactly on a limit was reported: " + string.Join(" | ", world.Reports));
    }

    /// <summary>The eight-segment limit is counted on the finished path, so nesting cannot buy extra depth one level
    /// at a time: four segments plus four is exactly eight and loads, five plus four is nine and is refused, and a
    /// deep node below a short parent is refused the same way.</summary>
    [Fact]
    public void the_child_path_depth_is_counted_on_the_finished_path()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10004, """
            { "blockId": "10004", "parts": [ { "component": "StockPart",
                "children": [ { "path": "a/b/c/d", "children": [ { "path": "e/f/g/h", "localScale": [2, 2, 2] } ] } ] } ] }
            """);
        world.Place("InfiniMap", 10005, """
            { "blockId": "10005", "parts": [ { "component": "StockPart",
                "children": [ { "path": "a/b/c/d/e", "children": [ { "path": "f/g/h/i" } ] } ] } ] }
            """);
        world.Place("InfiniMap", 10006, """
            { "blockId": "10006", "parts": [ { "component": "StockPart",
                "children": [ { "path": "a", "children": [ { "path": "b/c/d/e/f/g/h/i" } ] } ] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 1, "A path exactly eight segments deep was refused, or a nine-segment path was accepted: "
            + string.Join(" | ", world.Reports));
        Require(world.Reports.Count == 2 && world.Reports.All(line => line.StartsWith("weapon.gear-part-path file=", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-path file=plugins/InfiniMap/forge/gear-parts/10005.json", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-path file=plugins/InfiniMap/forge/gear-parts/10006.json", StringComparison.Ordinal)),
            "The two over-deep nested paths were not each refused once: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void a_file_that_is_too_large_is_rejected_before_it_is_read()
    {
        Reset();
        using var world = new World(start: false);
        // A valid header followed by padding: the file is refused on its length, never on its content.
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [] }
            """ + new string(' ', GearPartTransformData.MaximumFileBytes));
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "An oversized file was accepted.");
        Require(world.Reports.Count == 1
            && world.Reports[0].StartsWith("weapon.gear-part-size file=plugins/InfiniMap/forge/gear-parts/10001.json", StringComparison.Ordinal),
            "The oversized file was not refused on its size: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void a_file_whose_name_and_content_disagree_is_rejected()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", "10001.json", """
            { "blockId": "10002", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        world.Place("InfiniMap", "10003.json", """
            { "blockId": "10004", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        world.Place("InfiniMap", "010005.json", """
            { "blockId": "010005", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "A file whose name and content disagree was accepted.");
        Require(world.Reports.Count == 3
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-id file=plugins/InfiniMap/forge/gear-parts/10001.json", StringComparison.Ordinal)
                && line.Contains("10002", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-id file=plugins/InfiniMap/forge/gear-parts/10003.json", StringComparison.Ordinal)
                && line.Contains("10004", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-id file=plugins/InfiniMap/forge/gear-parts/010005.json", StringComparison.Ordinal)),
            "The disagreeing or unspellable names were not each refused once: " + string.Join(" | ", world.Reports));
    }

    /// <summary>A file that carries no usable block id at all cannot be matched to a gear, so the name itself has to
    /// spell one; a name that is not a canonical decimal id is refused before its content is even considered.</summary>
    [Fact]
    public void a_file_name_that_is_not_a_block_id_is_rejected()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", "sight.json", """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        world.Place("InfiniMap", "10006.json", """
            { "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "A file whose name carries no block id was accepted.");
        Require(world.Reports.Count == 2
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-id file=plugins/InfiniMap/forge/gear-parts/sight.json", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-id file=plugins/InfiniMap/forge/gear-parts/10006.json", StringComparison.Ordinal)),
            "The two unusable names were not each refused once: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void a_part_id_mismatch_keeps_the_games_part_and_reports_once()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "SightPart", "partId": 77, "localPosition": [0.1, 0.2, 0.3] } ] }
            """);
        world.StartSession();
        var holder = World.Holder(10001, (GearPartSlot.SightPart, World.Part(5, 5, 5)));
        holder.GearIDRange!.Components[eGearComponent.SightPart] = 88;
        var sight = holder.SightPart!;

        Spawn(world, holder);
        Spawn(world, holder);

        Require(Xyz(sight.transform.localPosition).SequenceEqual(new[] { 5f, 5f, 5f }), "A mismatched part was posed anyway.");
        Require(world.Reports.Count == 1
            && world.Reports[0].StartsWith("weapon.gear-part-mismatch: block=10001 component=SightPart expected=77 actual=88", StringComparison.Ordinal),
            "The mismatch was not reported exactly once with both ids: " + string.Join(" | ", world.Reports));
        holder.GearIDRange.Components[eGearComponent.SightPart] = 77;
        Spawn(world, holder);
        Require(Xyz(sight.transform.localPosition).SequenceEqual(new[] { 0.1f, 0.2f, 0.3f }),
            "A matching part id was refused after a mismatch had been reported.");
    }

    /// <summary>A slot whose id is unreadable reads back as zero, which is not the authored block either, so the
    /// entry is refused the same way an explicitly different block is.</summary>
    [Fact]
    public void a_part_id_that_cannot_be_read_back_is_not_applied()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "ReceiverPart", "partId": 77, "localPosition": [0.1, 0.2, 0.3] } ] }
            """);
        world.StartSession();
        var holder = World.Holder(10001, (GearPartSlot.ReceiverPart, World.Part(5, 5, 5)));
        var receiver = holder.ReceiverPart!;

        Spawn(world, holder);

        Require(Xyz(receiver.transform.localPosition).SequenceEqual(new[] { 5f, 5f, 5f }), "A part with no readable id was posed anyway.");
        Require(world.Reports.Count == 1
            && world.Reports[0].StartsWith("weapon.gear-part-mismatch: block=10001 component=ReceiverPart expected=77 actual=0", StringComparison.Ordinal),
            "The unreadable part id was not refused once: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void a_missing_child_is_reported_once_and_the_part_still_applies()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001",
              "parts": [ { "component": "StockPart", "localPosition": [0.1, 0.2, 0.3],
                           "children": [ { "path": "Absent/Bone", "localScale": [2, 2, 2] } ] } ] }
            """);
        world.StartSession();
        var stock = World.Part(5, 5, 5);
        var holder = World.Holder(10001, (GearPartSlot.StockPart, stock));

        Spawn(world, holder);
        Spawn(world, holder);

        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 0.1f, 0.2f, 0.3f }),
            "A missing child stopped the part's own pose from being applied.");
        Require(world.Reports.Count == 1
            && world.Reports[0].StartsWith("weapon.gear-part-child-missing: block=10001 component=StockPart path=Absent/Bone", StringComparison.Ordinal),
            "The missing child was not reported exactly once: " + string.Join(" | ", world.Reports));
    }

    /// <summary>Diagnostics are per authored path, not per part or per spawn: a nested path below a node that does
    /// exist is reported on its own, while a node that is already missing takes its subtree with it — nothing below
    /// it could resolve, so that branch gets exactly one line.</summary>
    [Fact]
    public void each_missing_child_path_is_reported_on_its_own()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001",
              "parts": [ { "component": "StockPart", "children": [ { "path": "Absent", "children": [ { "path": "Below" } ] } ] },
                         { "component": "SightPart", "children": [ { "path": "Gone" },
                                                                    { "path": "Frame", "children": [ { "path": "Missing" } ] } ] } ] }
            """);
        world.StartSession();
        var stock = World.Part();
        var sight = World.Part();
        sight.transform.Children["Frame"] = World.Child();
        var holder = World.Holder(10001, (GearPartSlot.StockPart, stock), (GearPartSlot.SightPart, sight));

        Spawn(world, holder);
        Spawn(world, holder);

        Require(world.Reports.Count == 3
            && world.Reports.Count(line => line.StartsWith("weapon.gear-part-child-missing: block=10001 component=SightPart path=Gone", StringComparison.Ordinal)) == 1
            && world.Reports.Count(line => line.StartsWith("weapon.gear-part-child-missing: block=10001 component=SightPart path=Frame/Missing", StringComparison.Ordinal)) == 1
            && world.Reports.Count(line => line.StartsWith("weapon.gear-part-child-missing: block=10001 component=StockPart path=Absent", StringComparison.Ordinal)) == 1,
            "Each absent path must be reported exactly once, per component: " + string.Join(" | ", world.Reports));
        Require(!world.Reports.Any(line => line.Contains("path=Absent/Below", StringComparison.Ordinal)),
            "A node below an already missing one was reported as a second failure: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void a_child_path_that_is_not_a_relative_path_is_rejected()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "children": [ { "path": "../Escape" } ] } ] }
            """);
        world.Place("InfiniMap", 10002, """
            { "blockId": "10002", "parts": [ { "component": "StockPart", "children": [ { "path": "A//B" } ] } ] }
            """);
        world.Place("InfiniMap", 10003, """
            { "blockId": "10003", "parts": [ { "component": "StockPart", "children": [ { "path": "1/2/3/4/5/6/7/8/9" } ] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "A path that leaves the part or exceeds the depth was accepted.");
        Require(world.Reports.Count == 3 && world.Reports.All(line => line.StartsWith("weapon.gear-part-path file=", StringComparison.Ordinal)),
            "The three unusable child paths were not each refused: " + string.Join(" | ", world.Reports));
    }

    [Fact]
    public void a_component_the_holder_does_not_have_is_rejected()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "MuzzleFlash", "localPosition": [0.1, 0, 0] },
                                             { "component": "sightpart", "localPosition": [0.1, 0, 0] } ] }
            """);
        world.Place("InfiniMap", 10002, """
            { "blockId": "10002", "parts": [ { "component": "StockPart", "localPosition": [0, 0, 0] },
                                             { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "A component the holder has no part for was accepted.");
        Require(world.Reports.Count == 2
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-component file=plugins/InfiniMap/forge/gear-parts/10001.json", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-component file=plugins/InfiniMap/forge/gear-parts/10002.json", StringComparison.Ordinal)),
            "The unusable component sets were not refused per file: " + string.Join(" | ", world.Reports));
    }

    /// <summary>Two files claiming one block do not race: the block is withdrawn whole rather than decided by scan
    /// order, so neither the first nor the second file poses anything.</summary>
    [Fact]
    public void two_files_claiming_one_block_withdraw_the_block()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        world.Place("OtherPackage", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.9, 0, 0] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "Two files claimed the same block, yet the block stayed loaded.");
        Require(world.Reports.Count == 2 && world.Reports.All(line => line.StartsWith("weapon.gear-part-duplicate-block file=", StringComparison.Ordinal)
                && line.Contains("claimed by 2 files", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-duplicate-block file=plugins/InfiniMap/forge/gear-parts/10001.json", StringComparison.Ordinal))
            && world.Reports.Any(line => line.StartsWith("weapon.gear-part-duplicate-block file=plugins/OtherPackage/forge/gear-parts/10001.json", StringComparison.Ordinal)),
            "Each claiming file must be named once: " + string.Join(" | ", world.Reports));
        var stock = World.Part(5, 5, 5);
        Spawn(world, World.Holder(10001, (GearPartSlot.StockPart, stock)));
        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 5f, 5f, 5f }),
            "A block claimed twice was posed by one of the two files anyway.");
        Require(!world.Infos.Any(line => line.StartsWith("weapon.gear-part-applied", StringComparison.Ordinal)),
            "A withdrawn block was reported as applied: " + string.Join(" | ", world.Infos));
    }

    /// <summary>Three claims are the same refusal as two, and every claimant is named: the count is what decides,
    /// never which file the scan reached first.</summary>
    [Fact]
    public void three_files_claiming_one_block_withdraw_the_block()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("APackage", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        world.Place("BPackage", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.2, 0, 0] } ] }
            """);
        world.Place("CPackage", 10001, """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.3, 0, 0] } ] }
            """);
        world.Place("InfiniMap", 10002, """
            { "blockId": "10002", "parts": [ { "component": "StockPart", "localPosition": [0.4, 0, 0] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 1, "A block claimed three times stayed loaded, or the single-claim block was lost.");
        Require(world.Reports.Count == 3 && world.Reports.All(line => line.StartsWith("weapon.gear-part-duplicate-block file=", StringComparison.Ordinal)
                && line.Contains("claimed by 3 files", StringComparison.Ordinal)),
            "Each of the three claiming files must be named once: " + string.Join(" | ", world.Reports));
        foreach (var package in new[] { "APackage", "BPackage", "CPackage" })
            Require(world.Reports.Any(line => line.StartsWith("weapon.gear-part-duplicate-block file=plugins/" + package + "/forge/gear-parts/10001.json", StringComparison.Ordinal)),
                "The claiming file in " + package + " was not named: " + string.Join(" | ", world.Reports));
        var stock = World.Part(5, 5, 5);
        Spawn(world, World.Holder(10001, (GearPartSlot.StockPart, stock)));
        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 5f, 5f, 5f }),
            "A block claimed three times was posed anyway.");
        var other = World.Part(5, 5, 5);
        Spawn(world, World.Holder(10002, (GearPartSlot.StockPart, other)));
        Require(Xyz(other.transform.localPosition).SequenceEqual(new[] { 0.4f, 0f, 0f }),
            "A block claimed by exactly one file stopped applying next to a withdrawn block.");
    }

    [Fact]
    public void a_file_that_is_not_readable_json_is_rejected_alone()
    {
        Reset();
        using var world = new World(start: false);
        world.Place("InfiniMap", 10001, "{ not json");
        world.Place("InfiniMap", 10002, """
            { "blockId": "10002", "parts": [ { "component": "StockPart", "localPosition": [0.2, 0, 0] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 1, "One bad file took the whole scan down.");
        Require(world.Reports.Count == 1
            && world.Reports[0].StartsWith("weapon.gear-part-json file=plugins/InfiniMap/forge/gear-parts/10001.json", StringComparison.Ordinal),
            "The unparsable file was not refused once: " + string.Join(" | ", world.Reports));
        var stock = World.Part(5, 5, 5);
        Spawn(world, World.Holder(10002, (GearPartSlot.StockPart, stock)));
        Require(Xyz(stock.transform.localPosition).SequenceEqual(new[] { 0.2f, 0f, 0f }), "The good file was not applied.");
    }

    /// <summary>Only one level below `plugins/` is a package directory, exactly like the plan scanner: a file one
    /// level deeper is not package data and is never read.</summary>
    [Fact]
    public void a_file_outside_a_package_directory_is_not_read()
    {
        Reset();
        using var world = new World(start: false);
        var nested = Path.Combine(world.GearPartsRoot, "plugins", "InfiniMap", "nested", "forge", "gear-parts");
        Directory.CreateDirectory(nested);
        File.WriteAllText(Path.Combine(nested, "10001.json"), """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        var root = Path.Combine(world.GearPartsRoot, "plugins");
        Directory.CreateDirectory(root);
        File.WriteAllText(Path.Combine(root, "10001.json"), """
            { "blockId": "10001", "parts": [ { "component": "StockPart", "localPosition": [0.1, 0, 0] } ] }
            """);
        var applier = world.StartSession().GearParts;

        Require(applier.BlockCount == 0, "A file outside a package directory was read.");
        Require(world.Reports.Count == 0, "A file outside a package directory was reported: " + string.Join(" | ", world.Reports));
    }

    private static string Block(uint blockId, int parts)
    {
        var entries = string.Join(",", Enumerable.Range(0, parts)
            .Select(_ => """{ "component": "StockPart", "localPosition": [0.1, 0, 0] }"""));
        return "{ \"blockId\": \"" + blockId + "\", \"parts\": [" + entries + "] }";
    }
}
