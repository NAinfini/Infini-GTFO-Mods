using System;
using BepInEx;

namespace ForgeMap.Native;

/// <summary>D-013 transition: on plugin Load, discover the one `BepInEx/plugins/*/forge/maps/` package and write
/// one bounded diagnostic per plan. Static G0-G6 checks only: nothing is generated, no game state is read or
/// written, no provider is registered and no network is touched. The `forge.log.v1` records for the same events
/// (`plan.loaded` / `plan.rejected`, Runtime-owned) wait for the Runtime sink plus the Map `Logging.Level` from
/// D-007, so these BepInEx log lines are the diagnostic until that wiring lands.</summary>
internal static class MapPlanDiagnostics
{
    // Each line is bounded so a broken or hostile package tree cannot flood the log.
    internal const int MaxCharacters = 512;
    internal const string PackageRejected = "map.package-rejected";
    internal const string PlanRejected = "map.plan-rejected";
    internal const string PlanAccepted = "map.plan-accepted";

    // Called once per plugin Load; there is no hot reload and no per-tick path.
    internal static void Report(Action<string> info, Action<string> error)
    {
        ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(error);
        var discovery = AssemblyPlanDiscovery.Discover(Paths.PluginPath, "plugins");
        if (discovery.Rejection is { } rejection)
        {
            error(Bound(PackageRejected + " code=" + rejection.Code + " path=" + rejection.Path));
            return;
        }
        foreach (var plan in discovery.Plans)
        {
            if (plan.Passed)
                info(Bound(PlanAccepted + " plan=" + plan.PlanId + " path=" + plan.Path
                    + " blockers=" + string.Join(",", plan.Blockers)));
            else
                error(Bound(PlanRejected + " plan=" + (plan.PlanId ?? "-") + " code=" + plan.Code
                    + " path=" + plan.Path + " at=" + plan.ErrorPath));
        }
    }

    internal static string Bound(string line) =>
        line.Length <= MaxCharacters ? line : line[..(MaxCharacters - 3)] + "...";
}
