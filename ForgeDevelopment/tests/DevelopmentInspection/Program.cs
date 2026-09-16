using System.Text.Json;
using ForgeDevelopment.Native;
using LevelGeneration;

var checks = 0;
var failures = 0;
var output = Path.Combine(Path.GetTempPath(), "forge-development-d1-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
void Check(bool condition, string name)
{
    checks++;
    if (!condition) { failures++; Console.Error.WriteLine("FAIL: " + name); }
}
ProjectObjectDeclaration[] Declarations() => new ProjectObjectDeclaration[]
{
    new("expedition", "zone-a", new ProjectZoneLocator(10, 0, 0, 1)),
    new("expedition", "room-a", new ProjectRoomLocator("zone-a", new ProjectRoomScope(0, 0, 1), new("room", new string('a', 64)), "Assets/Rooms/room.prefab"))
};
ProjectInspectionSession Session(long epoch = 7, ProjectSourceVerification source = ProjectSourceVerification.Matched, long? tick = 12)
{
    RuntimeDiagnostics.WorldEpoch = epoch;
    RuntimeDiagnostics.SimulationTick = tick;
    return new(new DiagnosticsReport("run-" + epoch), new ProjectObjectReferenceScan(Declarations(), epoch, tick, source), epoch);
}
(LG_Floor Floor, LG_Zone Zone, LG_Geomorph Geo) Floor()
{
    Builder.LevelGenExpedition = new();
    var floor = new LG_Floor();
    var layer = new LG_Layer { m_dimension = floor.MainDimension };
    floor.MainDimension!.Layers.Add(layer);
    var zone = new LG_Zone { Layer = layer, LocalIndex = 1, m_settings = new LG_ZoneSettings { m_zoneData = new GameData.ExpeditionZoneData() } };
    layer.m_zones.Add(zone);
    floor.allZones!.Add(zone);
    var geo = new LG_Geomorph { m_zone = zone };
    var area = new LG_Area { m_zone = zone, m_geomorph = geo, UID = 5 };
    geo.m_areas = new[] { area };
    zone.m_areas!.Add(area);
    Builder.CurrentFloor = floor;
    return (floor, zone, geo);
}
void Drain(ProjectInspectionSession session)
{
    var iterations = 0;
    foreach (var step in WorldInspection.Capture(session))
        if (++iterations > 10000) throw new Exception("Unexpected unbounded iterator.");
}
JsonDocument Export(ProjectInspectionSession session, string name) =>
    JsonDocument.Parse(File.ReadAllBytes(session.Report.Export(Path.Combine(output, name + ".json"), "test")));
try
{
    var full = Session();
    var world = Floor();
    Drain(full);
    var snapshot = full.Scan!.Snapshot();
    Check(full.IsClosed && !full.IsPartial && snapshot.ScanStatus == ProjectScanStatus.Complete, "normal native traversal completes");
    Check(snapshot.WorldEpoch == 7 && snapshot.SimulationTick == 12, "snapshot uses Runtime world and tick");
    Check(snapshot.Groups[0].Status == ProjectReferenceStatus.Matched, "zone uses dimension/layer/local index and native owner");
    Check(snapshot.Groups[1].Status == ProjectReferenceStatus.Unverified && snapshot.Groups[1].ReasonCode == ProjectReferenceReason.CreationContextUnverified, "unknown prefab provenance never becomes a room match");
    Check(snapshot.Groups[1].ObservedCandidateCount == 0, "display prefab name is not an Assets identity");
    using (var json = Export(full, "complete"))
    {
        var doc = json.RootElement;
        Check(doc.GetProperty("format").GetString() == "gtfo-forge-diagnostics-report" && !doc.TryGetProperty("schemaVersion", out _), "current report format only");
        Check(doc.GetProperty("objectReferences").GetProperty("scanStatus").GetString() == "complete", "report is attached to completed scan");
        Check(doc.GetProperty("playability").GetString() == "not_assessed", "structural observation is not playability proof");
    }
    full.Cancel(13, "cleanup");
    Check(full.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Complete, "cleanup preserves already-completed scan evidence");

    var missing = Session(); Builder.CurrentFloor = null; Drain(missing);
    Check(missing.IsPartial && missing.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Partial, "missing floor is partial, not complete");
    Check(missing.Scan!.Snapshot().Groups.All(g => g.Status == ProjectReferenceStatus.Unverified), "missing floor cannot prove an author object absent");
    var empty = Session(); world = Floor(); world.Floor.allZones!.Clear(); Drain(empty);
    Check(empty.IsPartial, "empty zone collection is partial");
    var nullZone = Session(); world = Floor(); world.Floor.allZones!.Add(null); Drain(nullZone);
    Check(nullZone.IsPartial && nullZone.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Partial, "null zone reduces coverage");
    var nullArea = Session(); world = Floor(); world.Zone.m_areas!.Add(null); Drain(nullArea);
    Check(nullArea.IsPartial, "null area reduces coverage");
    var noGeomorph = Session(); world = Floor(); world.Zone.m_areas![0]!.m_geomorph = null; Drain(noGeomorph);
    Check(noGeomorph.IsPartial, "missing area geomorph reduces coverage");
    var noZoneData = Session(); world = Floor(); world.Zone.m_settings = null; Drain(noZoneData);
    Check(noZoneData.IsPartial, "a zone without its own data block is unverified, not an invented policy");
    var respawn = Session(); world = Floor();
    world.Zone.m_settings!.m_zoneData = new GameData.ExpeditionZoneData
    {
        EnemyRespawning = true, EnemyRespawnRequireOtherZone = true, EnemyRespawnRoomDistance = 2,
        EnemyRespawnTimeInterval = 900f, EnemyRespawnCountMultiplier = 2.5f,
        EnemyRespawnExcludeList = new() { 11u, 22u, 33u }, HealthMulti = 1.5f
    };
    Drain(respawn);
    using (var json = Export(respawn, "zone-respawn"))
    {
        var events = json.RootElement.GetProperty("events").EnumerateArray().ToList();
        Check(events.Any(e => e.GetProperty("stage").GetString() == "zone_respawn_policy"), "respawn policy is its own observation");
        var policy = events.Single(e => e.GetProperty("stage").GetString() == "zone_respawn_policy");
        var fields = policy.GetProperty("fields");
        Check(fields.GetProperty("enabled").GetString() == "True" && fields.GetProperty("courseNodeDistance").GetString() == "2"
            && fields.GetProperty("intervalSeconds").GetString() == "900" && fields.GetProperty("countPercent").GetString() == "250"
            && fields.GetProperty("excludeCount").GetString() == "3" && fields.GetProperty("healthMulti").GetString() == "1.5",
            "read values are the zone's own authored data");
        var verdict = json.RootElement.GetProperty("checks").EnumerateArray()
            .Single(row => row.GetProperty("kind").GetString() == "zone_enemy_respawn_policy");
        Check(verdict.GetProperty("status").GetString() == "enabled" && verdict.GetProperty("detail").GetString()!.Contains("count=250%"),
            "the enabled policy is the check's own status, not a pass/fail claim");
    }
    var failedRead = Session(); world = Floor(); world.Zone.ThrowOnPosition = true; Drain(failedRead);
    Check(failedRead.IsPartial, "caught native read exception reduces scan completeness");
    using (var json = Export(failedRead, "read-error"))
        Check(json.RootElement.GetProperty("issues").EnumerateArray().Any(i => i.GetProperty("type").GetString() == "diagnostic_error"), "read exception retains raw diagnostic issue");
    var unsupported = Session(); world = Floor(); world.Zone.DimensionIndex = eDimensionIndex.Dimension_1; Drain(unsupported);
    Check(unsupported.IsPartial && unsupported.Scan!.Snapshot().Groups[0].Status == ProjectReferenceStatus.Unverified, "non-main dimension is not assigned the main layout");
    var wrongOwner = Session(); world = Floor(); world.Zone.Layer!.m_zones.Clear(); Drain(wrongOwner);
    Check(wrongOwner.IsPartial, "zone absent from native layer ownership is unverified");

    var distinctDimension = Session(); world = Floor();
    world.Zone.Layer!.m_dimension = new Dimension();
    Drain(distinctDimension);
    Check(distinctDimension.IsPartial, "equal dimension labels with distinct native pointers are not the same owner");
    Check(distinctDimension.Scan!.Snapshot().Groups[0].Status == ProjectReferenceStatus.Unverified, "foreign native dimension cannot establish an authored zone match");
    var aliasDimension = Session(); world = Floor();
    world.Zone.Layer!.m_dimension = new Dimension { Pointer = world.Floor.MainDimension!.Pointer };
    Drain(aliasDimension);
    Check(!aliasDimension.IsPartial, "distinct managed wrappers of the same native dimension retain complete coverage");
    Check(aliasDimension.Scan!.Snapshot().Groups[0].Status == ProjectReferenceStatus.Matched, "native pointer identity, not wrapper reference equality, establishes dimension ownership");
    var foreignLayer = Session(); world = Floor();
    var impostorLayer = new LG_Layer { m_dimension = world.Floor.MainDimension };
    impostorLayer.m_zones.Add(world.Zone); world.Zone.Layer = impostorLayer;
    Drain(foreignLayer);
    Check(foreignLayer.IsPartial && foreignLayer.Scan!.Snapshot().Groups[0].Status == ProjectReferenceStatus.Unverified, "a same-type layer absent from the native dimension is not accepted");
    var duplicateLayer = Session(); world = Floor();
    world.Floor.MainDimension!.Layers.Add(world.Zone.Layer!); Drain(duplicateLayer);
    Check(duplicateLayer.IsPartial, "duplicate active native layers cannot prove unique layout context");

    var duplicateNames = Session(); world = Floor();
    var secondGeo = new LG_Geomorph { m_zone = world.Zone };
    var secondArea = new LG_Area { m_zone = world.Zone, m_geomorph = secondGeo, UID = 6 };
    secondGeo.m_areas = new[] { secondArea }; world.Zone.m_areas!.Add(secondArea);
    Drain(duplicateNames);
    using (var json = Export(duplicateNames, "same-names"))
        Check(json.RootElement.GetProperty("events").EnumerateArray().Count(e => e.GetProperty("stage").GetString() == "geomorph") == 2,
            "two geomorph instances sharing display path/name are both inspected");

    var late = Session(); world = Floor();
    using (var iterator = WorldInspection.Capture(late).GetEnumerator())
    {
        Check(iterator.MoveNext(), "inspection yields before completion");
        using var frozen = Export(late, "before-cancel");
        var frozenBytes = File.ReadAllBytes(Path.Combine(output, "before-cancel.json"));
        RuntimeDiagnostics.WorldEpoch = 8; RuntimeDiagnostics.SimulationTick = 999;
        Check(!iterator.MoveNext(), "world change stops old iterator");
        var cancelled = late.Scan!.Snapshot();
        Check(cancelled.ScanStatus == ProjectScanStatus.Cancelled && cancelled.WorldEpoch == 7, "old receipt is cancelled at old epoch");
        Check(cancelled.SimulationTick != 999, "new world tick is not copied into old receipt");
        late.Scan!.ObserveZone(new(999, 10, 0, 0, 1)); late.Scan!.Complete(1000);
        Check(late.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Cancelled, "late observe/complete cannot revive cancellation");
        Check(File.ReadAllBytes(Path.Combine(output, "before-cancel.json")).SequenceEqual(frozenBytes), "previous export bytes remain immutable");
        Check(frozen.RootElement.GetProperty("objectReferences").GetProperty("scanStatus").GetString() == "pending", "earlier snapshot retains earlier status");
    }
    var next = Session(8); world = Floor(); Drain(next);
    Check(next.Scan!.Snapshot().WorldEpoch == 8 && next.Scan!.Snapshot().Groups[0].ObservedCandidateCount == 1, "next world has only its own native candidates");
    var disposed = Session(); world = Floor();
    using (var iterator = WorldInspection.Capture(disposed).GetEnumerator()) Check(iterator.MoveNext(), "disposal fixture begins inspection");
    Check(disposed.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Cancelled, "early iterator disposal cancels its scan");
    var beforeStart = Session(); beforeStart.Cancel(null, "cancel-before-start"); world = Floor(); Drain(beforeStart);
    Check(beforeStart.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Cancelled, "cancel before Start cannot be revived");
    var unavailableEpoch = Session(0); world = Floor(); Drain(unavailableEpoch);
    Check(unavailableEpoch.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Rejected, "unknown Runtime epoch stays rejected");
    var rejected = Session(); rejected.Scan!.Reject(); world = Floor(); Drain(rejected);
    Check(rejected.IsPartial && rejected.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Rejected, "rejected project scan cannot become completed");
    var pendingSource = Session(source: ProjectSourceVerification.Pending); world = Floor(); Drain(pendingSource);
    Check(pendingSource.Scan!.Snapshot().Groups[0].ReasonCode == ProjectReferenceReason.SourcePending, "complete traversal does not imply verified source");
    pendingSource.Scan!.SetSourceVerification(ProjectSourceVerification.Mismatch);
    Check(pendingSource.Scan!.Snapshot().Groups[0].ReasonCode == ProjectReferenceReason.SourceMismatch, "source mismatch remains distinct from traversal status");

    var noTick = Session(tick: null); world = Floor(); Drain(noTick);
    Check(noTick.Scan!.Snapshot().SimulationTick == null && !noTick.IsPartial, "unavailable simulation tick stays null, not an invented zero");
    var noManifest = Session(source: ProjectSourceVerification.NotProvided); world = Floor(); Drain(noManifest);
    Check(noManifest.Scan!.Snapshot().Groups.All(g => g.Status == ProjectReferenceStatus.Unverified), "no declared source verification cannot create a match");
    var backwards = Session(); world = Floor(); RuntimeDiagnostics.SimulationTick = 10; Drain(backwards);
    Check(backwards.IsPartial, "simulation tick regression cannot produce full coverage");
    var overAreaBudget = Session(); world = Floor(); world.Geo.m_areas = new LG_Area?[ProjectObjectReferences.MaximumAreas + 1]; Drain(overAreaBudget);
    Check(overAreaBudget.IsPartial, "oversized native area collections are not silently treated as complete");

    var tuples = new ProjectObjectDeclaration[]
    {
        new("expedition", "reality", new ProjectZoneLocator(10, 0, 0, 1)),
        new("expedition", "other-dimension", new ProjectZoneLocator(11, 1, 0, 1)),
        new("expedition", "secondary", new ProjectZoneLocator(12, 0, 1, 1))
    };
    var tupleScan = new ProjectObjectReferenceScan(tuples, 9, 1, ProjectSourceVerification.Matched);
    tupleScan.Start(new[] { new ProjectLayoutKey(10,0,0), new ProjectLayoutKey(11,1,0), new ProjectLayoutKey(12,0,1) }, 1);
    tupleScan.ObserveZone(new(10,10,0,0,1)); tupleScan.ObserveZone(new(11,11,1,0,1)); tupleScan.ObserveZone(new(12,12,0,1,1));
    tupleScan.Complete(2);
    Check(tupleScan.Snapshot().Groups.All(g => g.Status == ProjectReferenceStatus.Matched && g.ObservedCandidateCount == 1), "same local index in different dimension/layer stays distinct");
    Check(tupleScan.Snapshot().Groups.Count == 3, "all three native tuple declarations remain present");
    NodeBackReferenceTests.Run(output, Check);
}
finally
{
    Directory.Delete(output, true);
}
Console.WriteLine($"Forge Development D1 inspection: {checks - failures}/{checks} passed");
return failures == 0 ? 0 : 1;
