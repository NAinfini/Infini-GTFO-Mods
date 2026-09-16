using System.Text.Json;
using AIGraph;
using ForgeDevelopment.Native;
using Gear;
using LevelGeneration;
using UnityEngine;

// Back-reference assertions for the CourseNode, item and terminal relations WorldInspection records.
// Every native object here is a managed double, so this covers the production inspection contract and
// never claims that GTFO generated such a floor.
internal static class NodeBackReferenceTests
{
    private const uint LayoutId = 10;
    private static int _files;

    private sealed record Inspection(
        List<(string Kind, string Subject, string Status, string Detail)> Checks,
        List<(string Stage, string Subject, Dictionary<string, string> Fields)> Events,
        bool ScanComplete);

    internal static void Run(string output, Action<bool, string> check)
    {
        string Status(Inspection inspection, string kind) => inspection.Checks.Single(row => row.Kind == kind).Status;
        string Detail(Inspection inspection, string kind) => inspection.Checks.Single(row => row.Kind == kind).Detail;
        Dictionary<string, string> Fields(Inspection inspection, string stage) =>
            inspection.Events.Single(entry => entry.Stage == stage).Fields;

        var consistent = Inspect(output, "node-consistent", (_, _, _, _) => { });
        check(Status(consistent, "zone_course_nodes") == "observed" &&
            Detail(consistent, "zone_course_nodes").Contains("Observed 1 registered course nodes.", StringComparison.Ordinal),
            "a zone with a registered CourseNode is observed with its count");
        check(Status(consistent, "area_course_node") == "observed" &&
            Detail(consistent, "area_course_node").Contains("nodeAreaMatches=True", StringComparison.Ordinal) &&
            Detail(consistent, "area_course_node").Contains("zoneRegistersNode=True", StringComparison.Ordinal),
            "a CourseNode owned by its area and registered in its zone is observed");
        check(Status(consistent, "area_navigation_data") == "observed", "a present navigation holder is observed");
        check(Status(consistent, "area_navmesh_sample") == "sample_failed",
            "a failed NavMesh sample is recorded as a failed sample, not as proof of unreachability");
        check(consistent.ScanComplete, "node inspection alone must not degrade the project scan coverage");
        var areaFields = Fields(consistent, "area");
        check(areaFields["courseNode"] == "same-display-path#node-77" && areaFields["courseNodeStatus"] == "observed" &&
            areaFields["navigationHolder"] == "present",
            "the area event carries the node back-reference and its status: " + areaFields["courseNode"]);

        var invalid = Inspect(output, "node-invalid", (_, _, _, node) => node.IsValid = false);
        check(Status(invalid, "area_course_node") == "mismatch" &&
            Detail(invalid, "area_course_node").Contains("valid=False", StringComparison.Ordinal),
            "an invalid CourseNode is a mismatch, never an observation");
        check(Fields(invalid, "area")["courseNodeStatus"] == "invalid", "an invalid node is reported as invalid, not missing");

        var foreignArea = Inspect(output, "node-foreign-area", (_, zone, _, node) =>
        {
            var other = new LG_Area { m_zone = zone, UID = 9 };
            node.m_area = other;
        });
        check(Status(foreignArea, "area_course_node") == "mismatch" &&
            Detail(foreignArea, "area_course_node").Contains("nodeAreaMatches=False", StringComparison.Ordinal),
            "a node owned by another area is a mismatch");

        var unregistered = Inspect(output, "node-unregistered", (_, zone, _, _) => zone.m_courseNodes!.Clear());
        check(Status(unregistered, "area_course_node") == "mismatch" &&
            Detail(unregistered, "area_course_node").Contains("zoneRegistersNode=False", StringComparison.Ordinal),
            "a node absent from its zone's registration list is a mismatch");
        check(Status(unregistered, "zone_course_nodes") == "missing_data",
            "clearing the zone registration is recorded as missing node data");

        var noHolder = Inspect(output, "node-no-navigation", (_, _, _, node) => node.m_navigationInfoHolder = null);
        check(Status(noHolder, "area_navigation_data") == "missing_data",
            "a node without a navigation holder is missing_data, not observed");

        var nodeFree = Inspect(output, "area-without-node", (_, _, area, _) => area.m_courseNode = null);
        check(Status(nodeFree, "area_course_node") == "missing_data" &&
            nodeFree.Checks.All(row => row.Kind != "area_navmesh_sample"),
            "an area without a node is missing_data and no navigation sample is attempted");

        var item = new ItemInLevel { ItemDataBlock = new ItemData { persistentID = 4242, inventorySlot = 2 } };
        var itemBound = Inspect(output, "item-bound", (_, _, _, node) =>
        {
            item.CourseNode = node;
            node.m_itemsInNode.Add(item);
        }, item);
        check(Status(itemBound, "item_course_node") == "observed", "an item bound to a valid node is observed");
        var itemFields = Fields(itemBound, "item");
        check(itemFields["courseNode"] == "same-display-path#node-77" && itemFields["nodeContainsItem"] == "True" &&
            itemFields["itemDataId"] == "4242" && itemFields["inventorySlot"] == "2" &&
            itemFields["authority"] == "host" && itemFields["resourcePackType"] == "not_resource_pack",
            "the item event carries its node back-reference and native item identity");

        var itemUnlisted = Inspect(output, "item-unlisted", (_, _, _, node) => item.CourseNode = node, item);
        check(Status(itemUnlisted, "item_course_node") == "observed" && Fields(itemUnlisted, "item")["nodeContainsItem"] == "False",
            "a node that does not list the item is recorded without becoming a mismatch");
        var itemWithoutNode = Inspect(output, "item-without-node", (_, _, _, _) => item.CourseNode = null, item);
        check(Status(itemWithoutNode, "item_course_node") == "missing_data",
            "an item without a node is missing_data, never an inferred placement");
        var itemWrongParent = Inspect(output, "item-foreign-parent", (_, zone, _, node) =>
        {
            item.CourseNode = node;
            item.ParentComponent = new LG_Area { m_zone = zone, UID = 11 };
        }, item);
        check(Status(itemWrongParent, "item_course_node") == "mismatch" &&
            Detail(itemWrongParent, "item_course_node").Contains("parentAreaMatches=False", StringComparison.Ordinal),
            "an item whose node does not match its parent area is a mismatch");

        var terminalItem = new LG_GenericTerminalItem { TerminalItemKey = "KEY_1", ShowInFloorInventory = true };
        var terminalItemBound = Inspect(output, "terminal-item-bound", (_, _, _, node) => terminalItem.SpawnNode = node, terminalItem);
        check(Status(terminalItemBound, "terminal_item_node") == "observed" &&
            Fields(terminalItemBound, "terminal_item")["spawnNode"] == "same-display-path#node-77",
            "a terminal item records its spawn node back-reference");
        var terminalItemUnbound = Inspect(output, "terminal-item-unbound", (_, _, _, _) => terminalItem.SpawnNode = null, terminalItem);
        check(Status(terminalItemUnbound, "terminal_item_node") == "missing_data",
            "a terminal item without a spawn node is missing_data");

        var terminal = new LG_ComputerTerminal { SyncID = 5, PublicName = "TERMINAL_A", m_isSetup = true, IsRegistered = true };
        var terminalBound = Inspect(output, "terminal-bound", (_, zone, _, node) =>
        {
            terminal.SpawnNode = node;
            zone.TerminalsSpawnedInZone.Add(terminal);
            LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager
            { m_terminals = new Dictionary<int, LG_ComputerTerminal> { [5] = terminal } };
        }, terminal);
        check(Status(terminalBound, "terminal_setup") == "observed",
            "a terminal registered in its zone and manager with a valid node is observed");
        check(Fields(terminalBound, "terminal")["zoneRegistersTerminal"] == "True" &&
            Fields(terminalBound, "terminal")["managerMatches"] == "True",
            "the terminal event carries its registration facts");
        var terminalUnregistered = Inspect(output, "terminal-unregistered", (_, _, _, node) => terminal.SpawnNode = node, terminal);
        check(Status(terminalUnregistered, "terminal_setup") == "mismatch",
            "a terminal the zone does not register is a mismatch");
        var terminalWithoutNode = Inspect(output, "terminal-without-node", (_, zone, _, _) =>
        {
            terminal.SpawnNode = null;
            zone.TerminalsSpawnedInZone.Add(terminal);
            LG_ComputerTerminalManager.Current = new LG_ComputerTerminalManager
            { m_terminals = new Dictionary<int, LG_ComputerTerminal> { [5] = terminal } };
        }, terminal);
        check(Status(terminalWithoutNode, "terminal_setup") == "mismatch",
            "a terminal without a spawn node is a mismatch even when everything else is registered");
    }

    private static Inspection Inspect(string output, string name,
        Action<LG_Floor, LG_Zone, LG_Area, AIG_CourseNode> arrange, params Component[] floorComponents)
    {
        RuntimeDiagnostics.WorldEpoch = 7;
        RuntimeDiagnostics.SimulationTick = 12;
        LG_ComputerTerminalManager.Current = null;
        Builder.LevelGenExpedition = new ExpeditionData { LevelLayoutData = LayoutId };
        var floor = new LG_Floor();
        var layer = new LG_Layer { m_dimension = floor.MainDimension, m_type = LG_LayerType.MainLayer };
        floor.MainDimension!.Layers.Add(layer);
        var zone = new LG_Zone { Layer = layer, LocalIndex = 1, m_settings = new LG_ZoneSettings { m_zoneData = new GameData.ExpeditionZoneData() } };
        layer.m_zones.Add(zone);
        floor.allZones!.Add(zone);
        var node = new AIG_CourseNode { NodeID = 77, m_zone = zone, IsValid = true, m_navigationInfoHolder = new object() };
        var area = new LG_Area { m_zone = zone, m_courseNode = node, UID = 5 };
        node.m_area = area;
        zone.m_courseNodes!.Add(node);
        var geomorph = new LG_Geomorph { m_zone = zone, m_areas = new[] { area } };
        area.m_geomorph = geomorph;
        zone.m_areas!.Add(area);
        Builder.CurrentFloor = floor;
        arrange(floor, zone, area, node);
        floor.transform.gameObject.Components.AddRange(floorComponents);

        var report = new DiagnosticsReport(name);
        var scan = new ProjectObjectReferenceScan(new ProjectObjectDeclaration[]
        {
            new("expedition", "zone-a", new ProjectZoneLocator(LayoutId, 0, 0, 1))
        }, 7, 12, ProjectSourceVerification.Matched);
        var session = new ProjectInspectionSession(report, scan, 7);
        var iterations = 0;
        foreach (var _ in WorldInspection.Capture(session))
            if (++iterations > 10000) throw new Exception("Unexpected unbounded iterator.");
        var path = Path.Combine(output, name + "-" + Interlocked.Increment(ref _files) + ".json");
        using var json = JsonDocument.Parse(File.ReadAllBytes(report.Export(path, "test")));
        var checks = json.RootElement.GetProperty("checks").EnumerateArray()
            .Select(row => (row.GetProperty("kind").GetString()!, row.GetProperty("subject").GetString()!,
                row.GetProperty("status").GetString()!, row.GetProperty("detail").GetString()!)).ToList();
        var events = json.RootElement.GetProperty("events").EnumerateArray()
            .Select(row => (row.GetProperty("stage").GetString()!,
                row.GetProperty("subject").GetString()!,
                row.GetProperty("fields").EnumerateObject().ToDictionary(field => field.Name, field => field.Value.GetString()!)))
            .ToList();
        return new Inspection(checks, events, session.Scan!.Snapshot().ScanStatus == ProjectScanStatus.Complete);
    }
}
