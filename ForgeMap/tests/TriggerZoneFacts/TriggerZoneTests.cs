using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using Xunit;

namespace ForgeMap.Tests.TriggerZoneFacts;

/// <summary>
/// The trigger-zone document grammar, the shape it describes, and the tick that judges it. Every case runs against
/// the module's own public surface and the fixture's own world: no game assembly and no native hook is involved,
/// because the judgment is the module's and the game's part is only where the reads and the facts go.
/// </summary>
public sealed class TriggerZoneTests
{
    private const string Entered = TriggerZoneContract.EnteredFact;
    private const string Exited = TriggerZoneContract.ExitedFact;

    [Fact]
    public void DocumentReadsEveryShapeAndRefusesWhatCannotBeBuilt()
    {
        using var world = TriggerZoneWorld.Start();
        world.Load($$"""
        {"schemaVersion":1,"zones":[
          {"id":"hall","level":"31:A:0","room":{{TriggerZoneWorld.RoomJson()}},"shape":"box","size":[6,3,4],"position":[10,0,-2],"rotation":[0,0,0,1],"blocksPlayers":true},
          {"id":"gate","level":"31:A:0","room":{{TriggerZoneWorld.RoomJson()}},"shape":"sphere","size":[5,5,5],"position":[0,1,0],"rotation":[0,0,0,1],"who":"player"},
          {"id":"tunnel","level":"31:B:1","room":{{TriggerZoneWorld.RoomJson()}},"shape":"capsule","size":[2,6,2],"position":[0,2,0],"rotation":[0,0.7071068,0,0.7071068],"who":"enemy"}
        ]}
        """);
        var zones = world.Zones.Zones;
        Assert.Equal(3, zones.Count);
        Assert.Equal(TriggerZoneShape.Box, zones[0].Shape);
        Assert.True(zones[0].BlocksPlayers);
        Assert.Equal(TriggerZoneWho.Player, zones[1].Who);
        Assert.Equal(TriggerZoneShape.Capsule, zones[2].Shape);
        Assert.Equal(new[] { 2d, 6d, 2d }, zones[2].BlockingExtents());
        Assert.Equal("31:B:1", zones[2].Level.ToString());
        // A zone is a map object: its address carries the category and its own authored id as the key.
        Assert.Equal("trigger_zone/0/0/0/hall", TriggerZoneAddress.Of("hall")!.ToString());
        Assert.Equal("hall", TriggerZoneAddress.TryParse("trigger_zone/0/0/0/hall")!.Key);
        // The category and another category's address are not this one's.
        Assert.Null(TriggerZoneAddress.TryParse("door/0/0/0/security"));
        Assert.Null(TriggerZoneAddress.TryParse("trigger_zone/0/0/0/hall/extra"));
        // The document names the room each zone stands in, in the package's own locator grammar, and the native
        // zone that room belongs to: the authored zone id alone is not a zone the level can be asked about.
        Assert.Equal("zone-1", zones[0].Room.ZoneAuthorId);
        Assert.Equal(0, zones[0].Room.Dimension);
        Assert.Equal(0, zones[0].Room.Layer);
        Assert.Equal(1, zones[0].Room.LocalIndex);
        Assert.Equal("room-definition", zones[0].Room.RoomId);
        Assert.Equal(TriggerZoneWorld.RoomRevision, zones[0].Room.Revision);
        Assert.Equal("Assets/Complex/Mining/Room.prefab", zones[0].Room.SourcePrefab);

        Refuses("{\"schemaVersion\":2,\"zones\":[]}", "trigger-zone-schema");
        Refuses("{\"schemaVersion\":1,\"zones\":[],\"extra\":1}", "trigger-zone-field");
        // One id twice names no zone set at all, so neither claimant becomes a volume.
        Refuses("{\"schemaVersion\":1,\"zones\":["
            + "{\"id\":\"a\",\"level\":\"31:A:0\",\"room\":" + TriggerZoneWorld.RoomJson() + ",\"shape\":\"box\",\"size\":[1,1,1],\"position\":[0,0,0],\"rotation\":[0,0,0,1]},"
            + "{\"id\":\"a\",\"level\":\"31:A:0\",\"room\":" + TriggerZoneWorld.RoomJson() + ",\"shape\":\"box\",\"size\":[1,1,1],\"position\":[1,0,0],\"rotation\":[0,0,0,1]}]}", "trigger-zone-duplicate");
        // A sphere's three sizes are one diameter, a capsule is at least as tall as its diameter, and a quaternion
        // that is not a unit one is not a rotation.
        Refuses(OneJson("s", "sphere", "5, 6, 5", "0, 0, 0", "0, 0, 0, 1"), "trigger-zone-size");
        Refuses(OneJson("c", "capsule", "6, 2, 6", "0, 0, 0", "0, 0, 0, 1"), "trigger-zone-size");
        Refuses(OneJson("r", "box", "4, 4, 4", "0, 0, 0", "0, 0, 0, 0.5"), "trigger-zone-rotation");
        // The id is the address key, so a colon — the entity id's own separator — is refused at the document.
        Refuses(OneJson("i", "box", "4, 4, 4", "0, 0, 0", "0, 0, 0, 1").Replace("\"i\"", "\"a:b\""), "trigger-zone-id");
        Refuses(OneJson("i", "box", "4, 4, 4", "0, 0, 0", "0, 0, 0, 1").Replace("\"i\"", "\"a/b\""), "trigger-zone-id");
        Refuses(OneJson("z", "box", "4, 4, 4", "0, 0, 0", "0, 0, 0, 1").Replace("\"shape\":\"box\"", "\"shape\":\"cone\""), "trigger-zone-shape");
        Refuses(OneJson("z", "box", "4, 4, 4", "0, 0, 0", "0, 0, 0, 1").Replace("\"level\":\"31:A:0\"", "\"level\":\"31\""), "trigger-zone-level");
    }

    [Fact]
    public void ARoomLocatorIsTheOneThePackageReferencesCarry()
    {
        using var world = TriggerZoneWorld.Start();
        // A zone names the room it stands in, and this world's level places it: the authored pose is the room's
        // local coordinates and the judged table is the world one.
        world.LoadOne("hall", "box", "4, 4, 4", "0, 0, 0");
        Assert.True(world.Zones.ById("hall")!.Placed);

        // A document without a room names a pose in a coordinate system nothing can convert: refused whole.
        Refuses(OneJson("hall", "box", "4, 4, 4", "0, 0, 0", "0, 0, 0, 1")
            .Replace(",\"room\":" + TriggerZoneWorld.RoomJson(), ""), "trigger-zone-room");
        // The locator grammar is the package reference grammar: the kind, the authored zone, the room definition
        // and a complete native prefab identity are all required, and an extra or mistyped field is refused.
        var room = TriggerZoneWorld.RoomJson();
        Refuses(OneJsonWithRoom("hall", room.Replace("\"unique-geomorph-in-zone\"", "\"room-by-name\"")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"zone-1\"", "\"zone 1\"")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace(TriggerZoneWorld.RoomRevision, new string('C', 64))), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("Assets/Complex/Mining/Room.prefab", "Room.prefab")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("Assets/Complex/Mining/Room.prefab", "Assets/Complex/../Room.prefab")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"kind\":", "\"sort\":")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"id\":\"room-definition\"", "\"id\":\"room-definition\",\"extra\":1")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"zoneAuthorId\"", "\"zoneId\"")), "trigger-zone-room");
        // 区域的原生定位是定位符的一部分：缺一个数、不是整数、超出区域坐标范围，模组都定不了区域。
        Refuses(OneJsonWithRoom("hall", room.Replace(",\"dimension\":0", "")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"dimension\":0", "\"dimension\":0.5")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"dimension\":0", "\"dimension\":-1")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"layer\":0", "\"layer\":3")), "trigger-zone-room");
        Refuses(OneJsonWithRoom("hall", room.Replace("\"localIndex\":1", "\"localIndex\":-1")), "trigger-zone-room");
    }

    [Fact]
    public void AZonesLocalPoseIsPlacedThroughItsRoomsWorldPose()
    {
        using var world = TriggerZoneWorld.Start();
        // The room sits 100 m along X and a quarter turn about Y, and the zone is two metres along the room's own
        // X: the world volume is the one the room's pose puts it in, not the numbers the author typed.
        world.Rooms = _ => TriggerZoneRoomPose.Of(new double[] { 100, 5, -20 },
            new[] { 0d, Math.Sqrt(0.5), 0d, Math.Sqrt(0.5) });
        world.LoadOne("hall", "box", "2, 2, 2", "2, 0, 0");
        var zone = world.Zones.ById("hall")!;
        Assert.True(zone.Placed);
        // A quarter turn about Y sends the room's local +X to world -Z, and the zone's own orientation turns with
        // it: the placed zone carries the room's world rotation.
        Assert.Equal(new[] { 100d, 5d, -22d }, zone.Position.Select(value => Math.Round(value, 6)).ToArray());
        Assert.Equal(new[] { 0d, Math.Sqrt(0.5), 0d, Math.Sqrt(0.5) }, zone.Rotation.ToArray());

        // A body at the world centre of the zone is inside it, and the author's own local numbers name nothing.
        world.Player("1", 100, 5, -22);
        Assert.Equal(1, world.Tick().Entered);
        Assert.True(zone.Contains(new[] { 100d, 5d, -22d }));
        Assert.False(zone.Contains(new[] { 2d, 0d, 0d }));

        // Rotating the room without moving it still turns the zone: a body on the axis the room's own orientation
        // sends there is in the volume, and the point the author wrote in room-local metres is not.
        using var turned = TriggerZoneWorld.Start();
        turned.Rooms = _ => TriggerZoneRoomPose.Of(new double[] { 0, 0, 0 }, new[] { 0d, Math.Sqrt(0.5), 0d, Math.Sqrt(0.5) });
        turned.LoadOne("thin", "box", "2, 2, 0.5", "2, 0, 0");
        var thin = turned.Zones.ById("thin")!;
        Assert.True(thin.Contains(new[] { 0d, 0d, -2d }));
        Assert.False(thin.Contains(new[] { 2d, 0d, 0d }));
    }

    [Fact]
    public void AZoneWhoseRoomCannotBeResolvedIsRefusedByName()
    {
        using var world = TriggerZoneWorld.Start();
        world.Rooms = _ => null;
        world.LoadOne("hall", "box", "4, 4, 4", "0, 0, 0");
        // The room the zone names is not in this level: the zone is not judged at a pose nobody converted.
        Assert.Null(world.Zones.ById("hall"));
        Assert.Contains(world.Reports, message => message.StartsWith(TriggerZonePlacement.RefusedCode + ": zone `hall`", StringComparison.Ordinal));
        world.Player("1", 0, 0, 0);
        Assert.Equal(0, world.Tick().Zones);
        Assert.Empty(world.Published);

        // A build with no resolver at all refuses the same way, and says so, instead of reading the document's
        // room-local numbers as world metres.
        using var unresolved = TriggerZoneWorld.Start();
        unresolved.Rooms = null;
        unresolved.LoadOne("hall", "box", "4, 4, 4", "0, 0, 0");
        Assert.Null(unresolved.Zones.ById("hall"));
        Assert.Contains(unresolved.Reports, message => message.Contains("no room resolver is installed", StringComparison.Ordinal));

        // A resolver that fails is a refusal too: the zone is left out by name, and no other zone is judged in its
        // place.
        using var faulted = TriggerZoneWorld.Start();
        faulted.Rooms = _ => throw new InvalidOperationException("the generated level cannot be read");
        faulted.LoadOne("hall", "box", "4, 4, 4", "0, 0, 0");
        Assert.Null(faulted.Zones.ById("hall"));
        Assert.Contains(faulted.Reports, message => message.Contains("the room resolver failed (InvalidOperationException)", StringComparison.Ordinal));

        // A pose that is not three finite metres and a unit quaternion is not a room's world pose.
        Assert.Throws<RuntimeContractException>(() => TriggerZoneRoomPose.Of(new double[] { 1, 2, 3 }, new double[] { 0, 0, 0, 0.5 }));
        Assert.Throws<RuntimeContractException>(() => TriggerZoneRoomPose.Of(new double[] { 1, 2, 3 }, new double[] { 0, 0, 0, 1.5 }));
        Assert.Throws<RuntimeContractException>(() => TriggerZoneRoomPose.Of(new double[] { 1, 2, double.NaN }, new double[] { 0, 0, 0, 1 }));
    }

    [Fact]
    public void BoundariesAreClosedAndFollowTheZonesOwnRotation()
    {
        using var world = TriggerZoneWorld.Start();
        // A box's own axes are the zone's axes: rotating it a quarter turn about Y swaps its thin axis into X.
        world.LoadOne("thin", "box", "2, 2, 0.5", "0, 0, 0", rotation: "0, 0.7071068, 0, 0.7071068");
        var thin = world.Zones.ById("thin")!;
        Assert.True(thin.Contains(new[] { 0.2, 0, 0 }));
        Assert.False(thin.Contains(new[] { 0.4, 0, 0 }));
        // The zone's own long axis is Z: a point a metre out along it is still inside, one past it is not.
        Assert.True(thin.Contains(new[] { 0, 0, 0.9 }));
        Assert.False(thin.Contains(new[] { 0, 0, 1.1 }));

        // Closed on the boundary, and outside one millimetre past it.
        world.LoadOne("box", "box", "2, 2, 2", "1, 0, 0");
        var box = world.Zones.ById("box")!;
        Assert.True(box.Contains(new[] { 2d, 1d, 1d }));
        Assert.False(box.Contains(new[] { 2.001, 1, 1 }));

        world.LoadOne("sphere", "sphere", "2, 2, 2", "0, 0, 0");
        var sphere = world.Zones.ById("sphere")!;
        Assert.True(sphere.Contains(new[] { 0d, 0d, 1d }));
        Assert.False(sphere.Contains(new[] { 0d, 0d, 1.001 }));

        // A capsule is a segment with round caps: inside along its own Y at the cap's own reach, outside past it.
        world.LoadOne("capsule", "capsule", "1, 4, 1", "0, 0, 0");
        var capsule = world.Zones.ById("capsule")!;
        Assert.True(capsule.Contains(new[] { 0d, 1.6, 0d }));
        Assert.False(capsule.Contains(new[] { 0d, 2.1, 0d }));
        Assert.False(capsule.Contains(new[] { 0.6, 0d, 0d }));
    }

    [Fact]
    public void OnlyThisWorldsLevelIsJudged()
    {
        using var world = TriggerZoneWorld.Start();
        world.Load($$"""
        {"schemaVersion":1,"zones":[
          {"id":"a","level":"31:A:0","room":{{TriggerZoneWorld.RoomJson()}},"shape":"box","size":[4,4,4],"position":[0,0,0],"rotation":[0,0,0,1]},
          {"id":"b","level":"31:B:1","room":{{TriggerZoneWorld.RoomJson()}},"shape":"box","size":[4,4,4],"position":[0,0,0],"rotation":[0,0,0,1]}
        ]}
        """);
        world.Player("1", 0, 0, 0);
        var first = world.Tick();
        Assert.Equal(1, first.Zones);
        Assert.Equal("gtfo.map_object:trigger_zone/0/0/0/a", ZoneOf(world.Last(Entered)!));

        // The other level's zone is not this world's volume: the same body is not inside it, and the level change
        // starts the new level's membership from nothing.
        world.Level = MapLevelReference.TryParse("31:B:1");
        var second = world.Tick();
        Assert.Equal(1, second.Zones);
        // The new level's zone is judged from nothing: the body is inside it, so it enters again, and no exit is
        // published for the level that ended.
        Assert.Equal(2, world.Count(Entered));
        Assert.Equal(0, world.Count(Exited));
        Assert.Equal("gtfo.map_object:trigger_zone/0/0/0/b", ZoneOf(world.Last(Entered)!));
    }

    [Fact]
    public void EntryIsOneFactPerTargetAndZone()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0");
        var one = world.Player("1", 0, 0, 0);
        var two = world.Player("2", 10, 0, 0);

        var tick = world.Tick();
        Assert.Equal("complete", tick.Status);
        Assert.Equal(1, tick.Entered);
        var fact = world.Last(Entered)!;
        Assert.Equal(TriggerZoneContract.BindingOf(Entered), fact.BindingId);
        Assert.Equal("gtfo.player:1", fact.Outputs.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal(fact.WorldEpoch, fact.Outputs.GetProperty("target").GetProperty("worldEpoch").GetInt64());
        // The zone is the fact's own subject: a plan hung on the zone is dispatched for it and for no other.
        Assert.Equal("gtfo.map_object:trigger_zone/0/0/0/z1", ZoneOf(fact));
        Assert.Equal(fact.WorldEpoch, fact.Outputs.GetProperty("zone").GetProperty("worldEpoch").GetInt64());
        Assert.Equal("gtfo.world:" + fact.WorldEpoch, fact.ScopeId);
        Assert.Single(world.ZoneModule.Inside("z1"));

        // Standing still is not a second entry: the same membership publishes nothing.
        Assert.Equal(0, world.Tick().Entered);
        Assert.Equal(1, world.Count(Entered));

        // A second body entering is its own fact, and the exit is one fact for the one that left.
        world.Move(two, 1, 0, 0);
        Assert.Equal(1, world.Tick().Entered);
        Assert.Equal(2, world.Count(Entered));
        world.Move(one, 9, 0, 0);
        var left = world.Tick();
        Assert.Equal(1, left.Exited);
        Assert.Equal(1, world.Count(Exited));
        Assert.Equal("gtfo.player:1", world.Last(Exited)!.Outputs.GetProperty("target").GetProperty("id").GetString());
    }

    [Fact]
    public void ABodyThatCrossesAThinZoneInOneBeatEntersAndLeavesInThatOrder()
    {
        using var world = TriggerZoneWorld.Start();
        // Twenty centimetres thick: a running body is inside it for less than one beat, so a tick that judged only
        // where the body ended up would see neither an entry nor an exit.
        world.LoadOne("thin", "box", "0.2, 4, 4", "0, 0, 0");
        var runner = world.Player("1", -1, 0, 0);
        Assert.Equal(0, world.Tick().Entered);

        world.Move(runner, 1, 0, 0);
        var crossed = world.Tick();

        Assert.Equal(1, crossed.Entered);
        Assert.Equal(1, crossed.Exited);
        Assert.Equal(1, world.Count(Entered));
        Assert.Equal(1, world.Count(Exited));
        // The two edges are the order the body made them in, and the same body on both.
        Assert.Equal(2, world.Published.Count);
        Assert.Equal(TriggerZoneContract.BindingOf(Entered), world.Published[0].BindingId);
        Assert.Equal(TriggerZoneContract.BindingOf(Exited), world.Published[1].BindingId);
        Assert.Equal("gtfo.player:1",
            world.Published[0].Outputs.GetProperty("target").GetProperty("id").GetString());
        Assert.Equal("gtfo.player:1",
            world.Published[1].Outputs.GetProperty("target").GetProperty("id").GetString());
        // It left the volume, so it is not a member of it.
        Assert.Empty(world.ZoneModule.Inside("thin"));
    }

    [Fact]
    public void APlacementAcrossAThinZonePublishesNoEdge()
    {
        // A body set down on the far side of a wall never walked through it. Forty metres in one beat is not the
        // game carrying the body along a path, so the line between the two places crosses nothing.
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("thin", "box", "0.2, 4, 4", "0, 0, 0");
        var warped = world.Player("1", -20, 0, 0);
        Assert.Equal(0, world.Tick().Entered);

        world.Move(warped, 20, 0, 0);
        var placed = world.Tick();

        Assert.Equal(0, placed.Entered);
        Assert.Equal(0, placed.Exited);
        Assert.Empty(world.Published);
        Assert.Empty(world.ZoneModule.Inside("thin"));
    }

    [Fact]
    public void APlacementJustPastTheJudgedDistanceCrossesNothing()
    {
        // The bound is what decides, and it decides the same way on its own doorstep: one hundredth of a metre past
        // the furthest the game can carry a body in a beat is already a placement and not a path.
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("thin", "box", "0.2, 4, 4", "0, 0, 0");
        var placed = world.Player("1", -1, 0, 0);
        Assert.Equal(0, world.Tick().Entered);

        world.Move(placed, -1 + TriggerZoneModule.MaximumJudgedTravel + 0.01, 0, 0);
        var tick = world.Tick();

        Assert.Equal(0, tick.Entered);
        Assert.Equal(0, tick.Exited);
        Assert.Empty(world.Published);
    }

    [Fact]
    public void TheFurthestJudgedTravelStillCrossesAThinZone()
    {
        // Exactly one beat of the quickest movement this module judges is still movement: a body carried that far
        // walked the line, so the thin wall it crossed fires once in and once out.
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("thin", "box", "0.2, 4, 4", "0, 0, 0");
        var runner = world.Player("1", -TriggerZoneModule.MaximumJudgedTravel / 2, 0, 0);
        Assert.Equal(0, world.Tick().Entered);

        world.Move(runner, TriggerZoneModule.MaximumJudgedTravel / 2, 0, 0);
        var crossed = world.Tick();

        Assert.Equal(1, crossed.Entered);
        Assert.Equal(1, crossed.Exited);
    }

    [Fact]
    public void APlacementIntoAZonePublishesOnlyAnEntry()
    {
        // Landing inside is a fact about where the body is, not about how it got there: the zone reports the
        // arrival it can see and invents no exit to go with it.
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("room", "box", "4, 4, 4", "0, 0, 0");
        var warped = world.Player("1", -20, 0, 0);
        Assert.Equal(0, world.Tick().Entered);

        world.Move(warped, 0, 0, 0);
        var arrived = world.Tick();

        Assert.Equal(1, arrived.Entered);
        Assert.Equal(0, arrived.Exited);
        Assert.Single(world.ZoneModule.Inside("room"));
    }

    [Fact]
    public void APlacementOutOfAZonePublishesTheExitItReallyMade()
    {
        // The other half of the same rule. The body is observably outside a volume it was a member of, so the
        // membership really changed; suppressing that exit would leave the zone believing in a body that has left.
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("room", "box", "4, 4, 4", "0, 0, 0");
        var warped = world.Player("1", 0, 0, 0);
        Assert.Equal(1, world.Tick().Entered);

        world.Move(warped, 20, 0, 0);
        var left = world.Tick();

        Assert.Equal(0, left.Entered);
        Assert.Equal(1, left.Exited);
        Assert.Empty(world.ZoneModule.Inside("room"));
    }

    [Fact]
    public void ABodyThatStaysInsidePublishesOneEntry()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("thin", "box", "0.2, 4, 4", "0, 0, 0");
        var sitter = world.Player("1", -1, 0, 0);
        world.Tick();

        world.Move(sitter, 0, 0, 0);
        Assert.Equal(1, world.Tick().Entered);

        // Staying put is not a second entry, and moving within the volume is not a leave and a re-entry.
        Assert.Equal(0, world.Tick().Entered);
        world.Move(sitter, 0.04, 0, 0);
        var inside = world.Tick();
        Assert.Equal(0, inside.Entered);
        Assert.Equal(0, inside.Exited);
        Assert.Equal(1, world.Count(Entered));
        Assert.Equal(0, world.Count(Exited));
        Assert.Single(world.ZoneModule.Inside("thin"));
    }

    [Fact]
    public void ABodyThatPassesBesideAZonePublishesNothing()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("gate", "box", "2, 2, 2", "0, 0, 0");
        var passer = world.Player("1", -3, 2, 0);
        world.Tick();

        // Two metres above the volume for the whole of the travel: the line between the two places never meets it.
        world.Move(passer, 3, 2, 0);
        var tick = world.Tick();

        Assert.Equal(0, tick.Entered);
        Assert.Equal(0, tick.Exited);
        Assert.Empty(world.Published);
        Assert.Empty(world.ZoneModule.Inside("gate"));
    }

    [Fact]
    public void AnEnemyCrossingAThinZoneIsJudgedLikeAPlayer()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("thin", "box", "0.2, 4, 4", "0, 0, 0", who: "enemy");
        var runner = world.Enemy("9", -1, 0, 0);
        world.Tick();

        world.Move(runner, 1, 0, 0);
        var crossed = world.Tick();

        Assert.Equal(1, crossed.Entered);
        Assert.Equal(1, crossed.Exited);
        Assert.Equal("gtfo.enemy:9", world.Last(Exited)!.Outputs.GetProperty("target").GetProperty("id").GetString());
    }

    [Fact]
    public void CrossingIsJudgedForEveryShape()
    {
        // The segment test is the zone's own rule carried from a point to a line, so every shape the document can
        // build crosses the same way a box does.
        using var ball = TriggerZoneWorld.Start();
        ball.LoadOne("ball", "sphere", "0.2, 0.2, 0.2", "0, 0, 0");
        var throughBall = ball.Player("1", -1, 0, 0);
        ball.Tick();
        ball.Move(throughBall, 1, 0, 0);
        Assert.Equal(1, ball.Tick().Entered);
        Assert.Equal(1, ball.Count(Exited));

        using var pill = TriggerZoneWorld.Start();
        pill.LoadOne("pill", "capsule", "0.2, 4, 0.2", "0, 0, 0");
        var throughPill = pill.Player("1", -1, 0, 0);
        pill.Tick();
        pill.Move(throughPill, 1, 0, 0);
        Assert.Equal(1, pill.Tick().Entered);
        Assert.Equal(1, pill.Count(Exited));

        // The same capsule crossed lengthways instead: a line that stays above its top cap meets neither the cap
        // nor the axis between them, so the shape's own extents are part of the test and not just its diameter.
        using var over = TriggerZoneWorld.Start();
        over.LoadOne("pill", "capsule", "0.2, 4, 0.2", "0, 0, 0");
        var passer = over.Player("1", -1, 3, 0);
        over.Tick();
        over.Move(passer, 1, 3, 0);
        var missed = over.Tick();
        Assert.Equal(0, missed.Entered);
        Assert.Equal(0, missed.Exited);
    }

    [Fact]
    public void OnlyPlayersAZoneReactsToArePublished()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("z1", "box", "6, 6, 6", "0, 0, 0", who: "player");
        world.Enemy("9", 0, 0, 0);
        Assert.Equal(0, world.Tick().Entered);
        Assert.Empty(world.Published);

        using var enemyZone = TriggerZoneWorld.Start();
        enemyZone.LoadOne("z1", "box", "6, 6, 6", "0, 0, 0", who: "enemy");
        enemyZone.Player("1", 0, 0, 0);
        enemyZone.Enemy("9", 1, 0, 0);
        var judged = enemyZone.Tick();
        Assert.Equal(1, judged.Entered);
        Assert.Equal("gtfo.enemy:9", enemyZone.Last(Entered)!.Outputs.GetProperty("target").GetProperty("id").GetString());
    }

    [Fact]
    public void ATargetThatLeftTheWorldPublishesNoExit()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0");
        var player = world.Player("1", 0, 0, 0);
        Assert.Equal(1, world.Tick().Entered);

        world.Vanish(player);
        var tick = world.Tick();
        Assert.Equal("complete", tick.Status);
        Assert.Equal(0, tick.Exited);
        Assert.Equal(0, world.Count(Exited));
        Assert.Empty(world.ZoneModule.Inside("z1"));
    }

    [Fact]
    public void ANewWorldDropsMembershipWithoutAnExit()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0");
        world.Player("1", 0, 0, 0);
        Assert.Equal(1, world.Tick().Entered);

        // The next world's player is a new life in a new epoch, and the world change itself drops the membership
        // the old one held: no exit is published across the boundary.
        world.NewWorld(2);
        world.Player("1", 0, 0, 0);
        var tick = world.Tick();
        Assert.All(world.ZoneModule.Inside("z1"), reference => Assert.Equal(2, reference.WorldEpoch));
        Assert.Equal(0, world.Count(Exited));
        Assert.Equal(1, tick.Entered);
        Assert.Equal(2, world.Count(Entered));
    }

    [Fact]
    public void AClientMakesNoReadAndPublishesNothing()
    {
        using var world = TriggerZoneWorld.Start();
        world.Authority = false;
        world.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0");
        world.Player("1", 0, 0, 0);
        var tick = world.Tick();
        Assert.Equal("idle", tick.Status);
        Assert.Equal("not-authoritative", tick.Code);
        Assert.Empty(world.Published);
        Assert.Empty(world.ZoneModule.Inside("z1"));
    }

    [Fact]
    public void AnUnobservedWorldPublishesNoEdge()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0");
        var player = world.Player("1", 0, 0, 0);
        Assert.Equal(1, world.Tick().Entered);

        // A candidate whose snapshot could not be read makes the whole inspection a partial answer: the membership
        // the last complete tick established stands, and no exit is invented for it.
        world.StopReading(player);
        var refused = world.Tick();
        Assert.Equal("refused", refused.Status);
        Assert.Equal("entity-query-incomplete", refused.Code);
        Assert.Equal(0, world.Count(Exited));
        Assert.Single(world.ZoneModule.Inside("z1"));

        world.Move(player, 9, 0, 0);
        Assert.Equal(1, world.Tick().Exited);
    }

    [Fact]
    public void AnUnavailableKindKeepsItsTargetsMembership()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0", who: "enemy");
        // The enemy kind's own provider cannot enumerate: the module reads that as a refusal by name and never as
        // an empty world, so nothing is published and nothing leaves the table.
        world.EnemiesEnumerate = false;
        Assert.Equal(0, world.Tick().Entered);
        Assert.Contains(world.Reports, message => message.StartsWith("trigger-zone-targets: gtfo.enemy", StringComparison.Ordinal));

        // The kind answers, the body enters, and a later refusal of that kind is not an exit: nothing was observed
        // to have left.
        world.Enemy("9", 0, 0, 0);
        world.EnemiesEnumerate = true;
        Assert.Equal(1, world.Tick().Entered);
        world.EnemiesEnumerate = false;
        Assert.Equal("complete", world.Tick().Status);
        Assert.Equal(0, world.Count(Exited));
        Assert.Single(world.ZoneModule.Inside("z1"));
    }

    [Fact]
    public void TheTickIsBoundedByItsZoneAndTargetBudgets()
    {
        using var world = TriggerZoneWorld.Start();
        var many = new StringBuilder("{\"schemaVersion\":1,\"zones\":[");
        for (var index = 0; index < TriggerZoneModule.MaximumZonesPerTick + 1; index++)
        {
            if (index > 0) many.Append(',');
            many.Append("{\"id\":\"z").Append(index)
                .Append("\",\"level\":\"31:A:0\",\"room\":").Append(TriggerZoneWorld.RoomJson())
                .Append(",\"shape\":\"box\",\"size\":[1,1,1],\"position\":[0,0,0],\"rotation\":[0,0,0,1]}");
        }
        world.Load(many.Append("]}").ToString());
        world.Player("1", 0, 0, 0);
        var bounded = world.Tick();
        Assert.Equal("complete", bounded.Status);
        Assert.Equal(TriggerZoneModule.MaximumZonesPerTick, bounded.Zones);
        Assert.Contains(world.Reports, message => message.StartsWith("trigger-zone-tick-budget", StringComparison.Ordinal));

        // The candidate set past the kernel's own ceiling is refused by name, not truncated: the tick publishes
        // nothing and no target is read by a position the module did not get.
        using var crowded = TriggerZoneWorld.Start();
        crowded.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0");
        for (var index = 0; index <= TriggerZoneModule.MaximumTargetsPerTick; index++) crowded.Player(index.ToString(), 0, 0, 0);
        var refused = crowded.Tick();
        Assert.Equal("complete", refused.Status);
        Assert.Equal(0, refused.Targets);
        Assert.Empty(crowded.Published);
        Assert.Contains(crowded.Reports, message => message.StartsWith("trigger-zone-targets: gtfo.player answered entity-query-budget", StringComparison.Ordinal));
    }

    [Fact]
    public void APlanMountedOnOneZoneReceivesOnlyThatZonesEdges()
    {
        using var world = TriggerZoneWorld.Start();
        world.Load($$"""
        {"schemaVersion":1,"zones":[
          {"id":"a","level":"31:A:0","room":{{TriggerZoneWorld.RoomJson()}},"shape":"box","size":[4,4,4],"position":[0,0,0],"rotation":[0,0,0,1]},
          {"id":"b","level":"31:A:0","room":{{TriggerZoneWorld.RoomJson()}},"shape":"box","size":[4,4,4],"position":[20,0,0],"rotation":[0,0,0,1]}
        ]}
        """);
        TriggerZonePlan.Mount(world, "zone-a", TriggerZoneContract.EnteredCapability, "a");
        var body = world.Player("1", 0, 0, 0);
        // The body walks into zone A: the edge is A's, so the kernel dispatches it into the mount on A.
        var entered = world.TickOutcome();
        Assert.Equal(1, entered.Judgment.Entered);
        Assert.Equal(1, entered.Advance.EventsProcessed);

        // The same body walks into zone B: that is B's entry and A's exit, and neither is A's edge, so the mount on
        // A is handed nothing.
        world.Move(body, 20, 0, 0);
        var elsewhere = world.TickOutcome();
        Assert.Equal(1, elsewhere.Judgment.Entered);
        Assert.Equal(1, elsewhere.Judgment.Exited);
        Assert.Equal(0, elsewhere.Advance.EventsProcessed);

        // Walking back into A is A's own edge again, and it is handed to the mount once more.
        world.Move(body, 0, 0, 0);
        Assert.Equal(1, world.TickOutcome().Advance.EventsProcessed);
    }

    [Fact]
    public void AZoneNobodyMountedPublishesNoBehaviour()
    {
        using var world = TriggerZoneWorld.Start();
        world.LoadOne("a", "box", "4, 4, 4", "0, 0, 0");
        // The mount names a zone this install does not declare: the address parses, the matcher finds no zone
        // behind it, and the edge reaches no plan.        TriggerZonePlan.Mount(world, "missing", TriggerZoneContract.EnteredCapability, "missing");
        world.Player("1", 0, 0, 0);
        var entered = world.TickOutcome();
        Assert.Equal(1, entered.Judgment.Entered);
        Assert.Equal(0, entered.Advance.EventsProcessed);
        // The edge itself was published: a zone nobody listens to is still a zone somebody walked into.
        Assert.Equal(1, world.Count(Entered));
    }

    [Fact]
    public void DisposeStopsJudging()
    {
        var world = TriggerZoneWorld.Start();
        try
        {
            world.LoadOne("z1", "box", "4, 4, 4", "0, 0, 0");
            world.Player("1", 0, 0, 0);
            Assert.Equal(1, world.Tick().Entered);
            world.ZoneModule.Dispose();
            var tick = world.Tick();
            Assert.Equal("idle", tick.Status);
            Assert.Equal("module-disposed", tick.Code);
            Assert.Equal(1, world.Count(Entered));
        }
        finally { world.Dispose(); }
    }

    /// <summary>The zone entity of one published fact, as the subject the plan's mount is matched against.</summary>
    private static string ZoneOf(RuntimeEvent fact) => fact.Outputs.GetProperty("zone").GetProperty("id").GetString()!;

    /// <summary>One zone entry with the fixture's standard room, so a case about a shape or a size writes only the
    /// fields it is about.</summary>
    private static string OneJson(string id, string shape, string size, string position, string rotation)
        => OneJsonWithRoom(id, TriggerZoneWorld.RoomJson(), shape, size, position, rotation);

    /// <summary>The same entry with a room locator text the case is about: a locator the grammar refuses, or one
    /// that names a room the case wants the resolver to answer for.</summary>
    private static string OneJsonWithRoom(string id, string room, string shape = "box", string size = "4, 4, 4",
        string position = "0, 0, 0", string rotation = "0, 0, 0, 1")
        => "{\"schemaVersion\":1,\"zones\":[{\"id\":\"" + id + "\",\"level\":\"31:A:0\",\"room\":" + room
            + ",\"shape\":\"" + shape + "\",\"size\":[" + size + "],\"position\":[" + position
            + "],\"rotation\":[" + rotation + "]}]}";

    private static void Refuses(string document, string code)
    {
        Assert.False(TriggerZoneManifest.TryParse(document, out var zones, out var refused, out var reason),
            "the document was accepted: " + document);
        Assert.Equal(code, refused);
        Assert.Null(zones);
        Assert.False(string.IsNullOrWhiteSpace(reason));
    }
}
