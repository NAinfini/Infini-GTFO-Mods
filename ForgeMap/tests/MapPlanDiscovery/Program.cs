using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeMap;
using ForgeMap.TestFixtures;

// Discovery of the G0 package layout (FORGE-FRAMEWORK.md section 3.2, D-013 transition): `plugins/*/forge/maps/`
// with static G0-G6 checks and per-plan diagnostics. Synthetic fixtures under the session temp directory only;
// no game, no bundle and no installed package is read, and an accepted plan is never a generation success.
var checks = new List<string>();
var failures = new List<string>();
var scratch = Path.Combine(Path.GetTempPath(), "map-plan-discovery-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(scratch);

void Check(string name, bool ok, string detail)
{
    if (ok) { checks.Add(name); Console.Error.WriteLine("PASS: " + name + " [" + detail + "]"); }
    else { failures.Add(name + ": " + detail); Console.Error.WriteLine("FAIL: " + name + " [" + detail + "]"); }
}

void CheckPlans(string name, AssemblyDiscovery discovery, params string[] expected)
{
    var actual = discovery.Plans.Select(plan => plan.Path + " " + (plan.Code ?? "ok") + " "
        + (plan.ErrorPath ?? "-") + " [" + string.Join(",", plan.Blockers) + "]").ToArray();
    Check(name, actual.SequenceEqual(expected), string.Join(" | ", actual));
}

string NewRoot(string name)
{
    var root = Path.Combine(scratch, name);
    Directory.CreateDirectory(root);
    return root;
}

// `descriptors` null leaves `forge/maps/` present but empty, which is the incomplete-layout case.
void WritePackage(string root, string package, string? descriptors, params (string Name, string Content)[] plans)
{
    if (descriptors is null) Directory.CreateDirectory(Path.Combine(root, package, "forge", "maps"));
    else PlanFixtures.WriteMapsFile(root, package, "rooms.descriptors.json", descriptors);
    foreach (var (name, content) in plans) PlanFixtures.WriteMapsFile(root, package, name, content);
}

static string Mutate(string planJson, Action<JsonObject> mutate)
{
    var plan = JsonNode.Parse(planJson)!.AsObject();
    mutate(plan);
    return plan.ToJsonString();
}

const string Maps = "plugins/pkg/forge/maps/";
const string Accepted = Maps + "alpha.assembly.json";
const string AcceptedBlockers = " [colliders,dimension-bounds-unknown,navigation,occlusion]";

try
{
    // ---- the plugins root itself
    var missing = Path.Combine(scratch, "missing");
    var noRoot = AssemblyPlanDiscovery.Discover(missing, "plugins");
    Check("root.missing-is-silent", noRoot.PackagePath is null && noRoot.Rejection is null && noRoot.Plans.Count == 0,
        "package=" + noRoot.PackagePath + " rejection=" + noRoot.Rejection);

    var empty = AssemblyPlanDiscovery.Discover(NewRoot("empty"), "plugins");
    Check("root.empty-is-silent", empty.PackagePath is null && empty.Rejection is null && empty.Plans.Count == 0,
        "plans=" + empty.Plans.Count);

    // A directory without `forge/maps/` and files directly under `plugins/` are never opened, reported or logged.
    var noPackage = NewRoot("no-package");
    Directory.CreateDirectory(Path.Combine(noPackage, "unrelated-mod", "BepInEx", "plugins"));
    File.WriteAllText(Path.Combine(noPackage, "stray.assembly.json"), PlanFixtures.LockedPlanJson("stray", 1, 1));
    var skipped = AssemblyPlanDiscovery.Discover(noPackage, "plugins");
    Check("package.without-maps-is-silent", skipped.PackagePath is null && skipped.Rejection is null && skipped.Plans.Count == 0,
        "package=" + skipped.PackagePath + " plans=" + skipped.Plans.Count);

    // ---- package layout
    var incomplete = NewRoot("incomplete");
    WritePackage(incomplete, "pkg", null);
    var incompleteResult = AssemblyPlanDiscovery.Discover(incomplete, "plugins");
    Check("package.maps-without-descriptors", incompleteResult.Rejection is { Code: "assembly.package-layout", Path: "plugins/pkg/forge/maps" }
        && incompleteResult.PackagePath == "pkg" && incompleteResult.Plans.Count == 0, "rejection=" + incompleteResult.Rejection);

    var twoPackages = NewRoot("two-packages");
    WritePackage(twoPackages, "one", PlanFixtures.DescriptorsJson());
    WritePackage(twoPackages, "two", PlanFixtures.DescriptorsJson());
    var twoResult = AssemblyPlanDiscovery.Discover(twoPackages, "plugins");
    Check("package.more-than-one-is-rejected", twoResult.Rejection is { Code: "assembly.package-layout", Path: "plugins" }
        && twoResult.Plans.Count == 0, "rejection=" + twoResult.Rejection);

    var descriptorsOnly = Setup("descriptors-only", PlanFixtures.DescriptorsJson());
    Check("package.descriptors-without-plans", descriptorsOnly.PackagePath == "pkg" && descriptorsOnly.Rejection is null
        && descriptorsOnly.Plans.Count == 0, "package=" + descriptorsOnly.PackagePath + " plans=" + descriptorsOnly.Plans.Count);

    // ---- one legal plan
    var accepted = Setup("accepted", PlanFixtures.DescriptorsJson(),
        ("alpha.assembly.json", PlanFixtures.LockedPlanJson("alpha", 3788602088, 12345)));
    Check("plan.accepted", accepted.PackagePath == "pkg" && accepted.Rejection is null && accepted.Plans.Count == 1
        && accepted.Plans[0].Passed && accepted.Plans[0].PlanId == "alpha",
        "package=" + accepted.PackagePath + " plans=" + accepted.Plans.Count);
    CheckPlans("plan.accepted-blockers", accepted, Accepted + " ok -" + AcceptedBlockers);

    // Files that are not `<planId>.assembly.json` are ignored, including the descriptor document itself and a
    // suffix that only differs by case (`plan-file` is about the plan's own name, not about every file).
    var withExtras = Path.Combine(scratch, "with-extras");
    WritePackage(withExtras, "pkg", PlanFixtures.DescriptorsJson(),
        ("alpha.assembly.json", PlanFixtures.LockedPlanJson("alpha", 1, 1)));
    PlanFixtures.WriteMapsFile(withExtras, "pkg", "notes.txt", "not a plan");
    PlanFixtures.WriteMapsFile(withExtras, "pkg", "beta.plan.json", PlanFixtures.LockedPlanJson("beta", 2, 2));
    PlanFixtures.WriteMapsFile(withExtras, "pkg", "gamma.ASSEMBLY.JSON", PlanFixtures.LockedPlanJson("gamma", 3, 3));
    CheckPlans("plan.non-plan-files-ignored", AssemblyPlanDiscovery.Discover(withExtras, "plugins"),
        Accepted + " ok -" + AcceptedBlockers);

    // ---- rejected plans, each with its contract code and schema path
    var noPlanId = Path.Combine(scratch, "no-plan-id");
    WritePackage(noPlanId, "pkg", PlanFixtures.DescriptorsJson(),
        ("noplanid.assembly.json", Mutate(PlanFixtures.LockedPlanJson("noplanid", 1, 1), plan => plan.Remove("planId"))));
    CheckPlans("plan.missing-plan-id", AssemblyPlanDiscovery.Discover(noPlanId, "plugins"),
        Maps + "noplanid.assembly.json assembly.plan-file " + Maps + "noplanid.assembly.json []");

    var renamed = Path.Combine(scratch, "renamed");
    WritePackage(renamed, "pkg", PlanFixtures.DescriptorsJson(),
        ("renamed.assembly.json", PlanFixtures.LockedPlanJson("alpha", 1, 1)));
    CheckPlans("plan.file-name-mismatch", AssemblyPlanDiscovery.Discover(renamed, "plugins"),
        Maps + "renamed.assembly.json assembly.plan-file " + Maps + "renamed.assembly.json []");

    var broken = Path.Combine(scratch, "broken-json");
    WritePackage(broken, "pkg", PlanFixtures.DescriptorsJson(), ("broken.assembly.json", "{ not json"));
    CheckPlans("plan.unparsable-document", AssemblyPlanDiscovery.Discover(broken, "plugins"),
        Maps + "broken.assembly.json assembly.schema $ []");

    var badSeed = Path.Combine(scratch, "bad-seed");
    WritePackage(badSeed, "pkg", PlanFixtures.DescriptorsJson(),
        ("alpha.assembly.json", PlanFixtures.LockedPlanJson("alpha", 1, 2147483648)));
    CheckPlans("plan.seed-out-of-range", AssemblyPlanDiscovery.Discover(badSeed, "plugins"),
        Accepted + " assembly.seed $.seed []");

    var badRotation = Path.Combine(scratch, "bad-rotation");
    WritePackage(badRotation, "pkg", PlanFixtures.DescriptorsJson(), ("alpha.assembly.json",
        Mutate(PlanFixtures.LockedPlanJson("alpha", 1, 1), plan =>
        {
            var placement = plan["placements"]!.AsArray()[0]!.AsObject();
            placement["transform"]!.AsObject()["rotation"] = new JsonArray(0, 0, 0, -1);
        })));
    CheckPlans("plan.rotation-not-canonical", AssemblyPlanDiscovery.Discover(badRotation, "plugins"),
        Accepted + " assembly.transform-rotation $.placements[0].transform.rotation []");

    var staleLock = Path.Combine(scratch, "stale-lock");
    WritePackage(staleLock, "pkg", PlanFixtures.DescriptorsJson(),
        ("alpha.assembly.json", PlanFixtures.PlanJson("alpha", 1, 1, new string('0', 64))));
    CheckPlans("plan.descriptor-lock-mismatch", AssemblyPlanDiscovery.Discover(staleLock, "plugins"),
        Accepted + " assembly.descriptor-lock $.descriptors.sha256 []");

    var badDescriptors = Path.Combine(scratch, "bad-descriptors");
    WritePackage(badDescriptors, "pkg", "{ not json", ("alpha.assembly.json", PlanFixtures.LockedPlanJson("alpha", 1, 1)));
    CheckPlans("package.descriptor-document-unparsable", AssemblyPlanDiscovery.Discover(badDescriptors, "plugins"),
        Accepted + " descriptor.schema $descriptors []");

    // ---- package-level duplicate rule: one plan per level layout
    var duplicate = Path.Combine(scratch, "duplicate-level-layout");
    WritePackage(duplicate, "pkg", PlanFixtures.DescriptorsJson(),
        ("zeta.assembly.json", PlanFixtures.LockedPlanJson("zeta", 7, 1)),
        ("alpha.assembly.json", PlanFixtures.LockedPlanJson("alpha", 7, 2)));
    CheckPlans("package.duplicate-level-layout-in-ordinal-order", AssemblyPlanDiscovery.Discover(duplicate, "plugins"),
        Accepted + " ok -" + AcceptedBlockers,
        Maps + "zeta.assembly.json assembly.duplicate-level-layout " + Maps + "zeta.assembly.json []");

    // Discovery reads the fixture tree only: a second pass reports the same plans in the same order.
    var again = AssemblyPlanDiscovery.Discover(duplicate, "plugins");
    Check("discovery.is-repeatable", again.Plans.Count == 2 && again.Plans[0].Path == Accepted
        && again.Plans[1].Code == "assembly.duplicate-level-layout", "plans=" + again.Plans.Count);

    AssemblyDiscovery Setup(string name, string descriptors, params (string Name, string Content)[] plans)
    {
        var root = Path.Combine(scratch, name);
        WritePackage(root, "pkg", descriptors, plans);
        return AssemblyPlanDiscovery.Discover(root, "plugins");
    }
}
finally
{
    if (Directory.Exists(scratch)) Directory.Delete(scratch, true);
}

Console.WriteLine(JsonSerializer.Serialize(new
{
    verification = "managed discovery over synthetic temp fixtures; static checks are not a generation success",
    nativeGameExecuted = false, passed = checks.Count, failed = failures.Count, checks, failures,
}, new JsonSerializerOptions { WriteIndented = true }));
return failures.Count == 0 ? 0 : 1;
