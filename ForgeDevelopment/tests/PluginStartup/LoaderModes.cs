using ForgeRuntime;
using Native = ForgeDevelopment.Native;

// Loader and host boundaries for the three install states the game can be in. Only the managed
// decision and bootstrap are covered here: assembly scanning, IL2CPP/Harmony patching and the real
// chainloader are not executed, so the in-game result must still be confirmed by hand.
internal static class LoaderModes
{
    private static Dictionary<string, string> Registry(params (string Guid, string Version)[] plugins)
    {
        var registry = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (guid, version) in plugins) registry[guid] = version;
        return registry;
    }

    private static LoadDecision Consider(Dictionary<string, string> registry) =>
        ChainloaderDouble.Consider(new[] { typeof(Native.Plugin) }, registry).Single().Decision;

    private static Dictionary<string, string> HostInstalled(params (string Guid, string Version)[] extra) =>
        Registry(new[] { ("NAinfini.ForgeRuntime", ForgeRuntime.Plugin.PluginVersion) }.Concat(extra).ToArray());

    internal static void Run(string[] startupOrder)
    {
        Probe.Case("loader: an absent Development assembly contributes no plugin", () =>
        {
            Probe.That(ChainloaderDouble.Consider(Array.Empty<Type>(), HostInstalled()).Count == 0 &&
                Probe.Calls.Count == 0, "an uninstalled plugin still produced loader work");
        });
        Probe.Case("loader: Development declares one hard host dependency and one soft tweaks dependency", () =>
        {
            var declared = DeclaredDependency.Of(typeof(Native.Plugin));
            var hard = declared.Where(dependency => !dependency.Soft).ToArray();
            var soft = declared.Where(dependency => dependency.Soft).ToArray();
            Probe.That(hard.Length == 1 && hard[0].Guid == "NAinfini.ForgeRuntime" && hard[0].Version != null,
                "the host dependency is not a single pinned requirement: " + string.Join(",", declared.Select(d => d.Guid)));
            Probe.That(soft.Length == 1 && soft[0].Guid == "NAinfini.InfiniTweaks",
                "InfiniTweaks is not the only soft dependency: " + string.Join(",", declared.Select(d => d.Guid)));
        });
        Probe.Case("loader: a missing host stops Development before Load", () =>
        {
            var decision = Consider(Registry(("NAinfini.InfiniTweaks", "2.5.0")));
            Probe.That(!decision.Loaded && decision.Reason.Contains("NAinfini.ForgeRuntime"),
                "a missing host was accepted by the loader double: " + decision.Reason);
            if (decision.Loaded) new Native.Plugin().Load();
            Probe.That(Probe.Calls.Count == 0, "a skipped plugin performed diagnostic work: " + string.Join(",", Probe.Calls));
        });
        Probe.Case("loader: an older host does not satisfy the declared requirement", () =>
        {
            var decision = Consider(Registry(("NAinfini.ForgeRuntime", "1.1.9")));
            Probe.That(!decision.Loaded && decision.Reason.Contains("1.1.9"),
                "a host below the declared requirement was accepted: " + decision.Reason);
        });
        Probe.Case("loader: the declared requirement means what the release set relies on", () =>
        {
            // The floor form is read from the shipped attribute: only a range lets a rebuilt host
            // satisfy Development without editing the plugin, an exact pin must reject it.
            var declared = DeclaredDependency.Of(typeof(Native.Plugin)).Single(dependency => !dependency.Soft).Version!;
            Probe.That(ChainloaderDouble.Satisfies(declared, ForgeRuntime.Plugin.PluginVersion),
                "the installed host does not satisfy Development's own requirement " + declared);
            Probe.That(ChainloaderDouble.Satisfies(declared, "1.3.0") == declared.TrimStart().StartsWith(">=", StringComparison.Ordinal),
                "a newer host and requirement " + declared + " disagree with the declared form");
        });
        Probe.Case("host: having the plugin type without Load starts nothing", () =>
        {
            System.Runtime.CompilerServices.RuntimeHelpers.RunClassConstructor(typeof(Native.Plugin).TypeHandle);
            _ = new Native.Plugin();
            Probe.That(Probe.Calls.Count == 0, "type load or construction started diagnostics: " + string.Join(",", Probe.Calls));
        });
        Probe.Case("host: installed but disabled stays inactive through the loader path", () =>
        {
            var decision = Consider(HostInstalled(("NAinfini.InfiniTweaks", "2.5.0")));
            Probe.That(decision.Loaded, "the loader double refused an installed plugin: " + decision.Reason);
            Probe.Plugin(RuntimeMode.Play).Load();
            Probe.That(Probe.Calls.SequenceEqual(new[] { "log:inactive" }),
                "disabled mode touched settings, hooks or collectors: " + string.Join(",", Probe.Calls));
        });
        Probe.Case("host: no InfiniTweaks assembly at all is accepted", () =>
        {
            var decision = Consider(HostInstalled());
            Probe.That(decision.Loaded, "the soft dependency blocked startup: " + decision.Reason);
            Probe.Plugin(RuntimeMode.Authoring).Load();
            Probe.That(Probe.Calls.SequenceEqual(startupOrder), "startup order changed: " + string.Join(",", Probe.Calls));
        });
        Probe.Case("host: Authoring through the loader path starts one collector set", () =>
        {
            var decision = Consider(HostInstalled(("NAinfini.InfiniTweaks", "2.5.0")));
            Probe.That(decision.Loaded, "the loader double refused an installed plugin: " + decision.Reason);
            Probe.Plugin(RuntimeMode.Authoring).Load();
            Probe.That(Probe.Calls.SequenceEqual(startupOrder), "startup order changed: " + string.Join(",", Probe.Calls));
            Probe.That(Probe.Components.Count == 4, "Authoring did not create the authoring monitor, the two experiment components and the performance collector");
        });
        // Process-wide: after this case no "InfiniTweaks without InfiniTweaks.Telemetry" case can run.
        Probe.Case("host: a pre-2.5.0 InfiniTweaks refuses startup", () =>
        {
            Probe.InstallLegacyTweaks();
            var plugin = Probe.Plugin(RuntimeMode.Authoring);
            var error = Probe.LoadError(plugin);
            Probe.That(error is InvalidOperationException && error.Message.Contains("2.5.0"),
                "an older InfiniTweaks was not refused: " + error);
            Probe.That(Probe.Calls.SequenceEqual(new[] { "settings:bind", "settings:authoring", "settings:recorder" }),
                "the refused startup touched hooks or collectors: " + string.Join(",", Probe.Calls));
            Probe.That(Probe.LoadError(plugin) is InvalidOperationException,
                "a refused startup could be retried into an active one");
        });
    }
}
