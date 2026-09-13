using ForgeRuntime;
using ForgeRuntime.GameBindings;

Probe.Case("Off excludes diagnostics and native startup", () => {
    var p = Probe.Plugin(RuntimeMode.Off); p.Load();
    Probe.That(!Probe.Calls.Contains("diagnostics:bind"), "Off still binds diagnostic settings");
    Probe.That(!Probe.Calls.Contains("host:init") && !Probe.Calls.Contains("harmony:new"), "Off initializes native services");
    Probe.That(Plugin.Runtime == null && !Plugin.CanExecuteGameplay && Probe.Components.Count == 0, "Off exposes gameplay");
});
Probe.Case("Play independent of diagnostic binding failures", () => {
    Probe.Faults["diagnostics:bind"] = new Exception("diagnostic settings unavailable");
    var p = Probe.Plugin(RuntimeMode.Play); p.Load();
    Probe.That(Plugin.Runtime != null && Probe.Calls.Contains("host:init"), "Play failed to create host");
    Probe.That(!Probe.Calls.Contains("diagnostics:init") && !Probe.Calls.Contains("hook:type:DiagnosticHook"), "Play started diagnostics");
    Probe.That(Probe.Components.Count == 1 && Probe.Components[0] is FrameworkMonitor, "Play created diagnostic components");
});
Probe.Case("Authoring retains requested components", () => {
    var p = Probe.Plugin(RuntimeMode.Authoring); p.Load();
    Probe.That(Probe.Calls.Contains("diagnostics:bind") && Probe.Calls.Contains("diagnostics:init"), "Authoring diagnostics absent");
    Probe.That(Probe.Components.Count == 3 && Probe.Calls.Contains("hook:type:DiagnosticHook"), "Authoring components or hooks missing");
    Probe.That(!p.Unload(), "native plugin incorrectly supports hot unloading");
});
Probe.Case("invalid mode fails before native initialization", () => {
    var p = Probe.Plugin((RuntimeMode)77); var error = Probe.LoadError(p);
    Probe.That(error != null && !Probe.Calls.Contains("host:init"), "invalid mode crossed native startup boundary");
});
Probe.Case("successful Load cannot be repeated", () => {
    var p = Probe.Plugin(RuntimeMode.Play); p.Load(); var before = Probe.Calls.Count;
    Probe.That(Probe.LoadError(p) is InvalidOperationException && before == Probe.Calls.Count, "same plugin initialized twice");
});
Probe.Case("rollback continues through cleanup and logger errors", () => {
    var original = new IOException("authoring component failed");
    Probe.Faults["component:add:AuthoringMonitor"] = original;
    Probe.Faults["hook:unpatch"] = new IOException("unpatch failed");
    Probe.Faults["log:error"] = new IOException("logger failed");
    var p = Probe.Plugin(RuntimeMode.Authoring); var error = Probe.LoadError(p);
    Probe.That(ReferenceEquals(error, original), "cleanup replaced original startup exception");
    Probe.That(original.Data["ForgeRuntime.StartupCleanupFailures"] is AggregateException failures && failures.InnerExceptions.Count == 2, "cleanup/logger failures were not retained on original error");
    Probe.That(Probe.Calls.Contains("host:stop") && Probe.Calls.Contains("diagnostics:stop"), "cleanup skipped host or diagnostics");
    Probe.That(Probe.Components.All(c => c.Destroyed), "cleanup skipped already-created components");
    Probe.That(Plugin.Runtime == null && !Plugin.CanExecuteGameplay, "failed plugin exposes gameplay");
    Probe.That(Probe.Calls.IndexOf("host:stop") < Probe.Calls.IndexOf("hook:unpatch"), "native gate closes after potentially failing unpatch");
});
Probe.Case("last-stage failure cleans performance component", () => {
    var original = new IOException("last startup log failed"); Probe.Faults["log:loaded"] = original;
    var p = Probe.Plugin(RuntimeMode.Authoring); var error = Probe.LoadError(p);
    Probe.That(ReferenceEquals(error, original), "last-stage error changed");
    Probe.That(Probe.Components.Count == 3 && Probe.Components.All(c => c.Destroyed), "late startup failure leaked a component");
    Probe.That(Probe.Calls.Contains("diagnostics:stop") && Plugin.Runtime == null, "late failure kept host/diagnostics alive");
});
Probe.Case("partial host failure does not stop unstarted diagnostics", () => {
    var original = new IOException("host initialization failed"); Probe.Faults["host:init"] = original;
    var p = Probe.Plugin(RuntimeMode.Authoring); var error = Probe.LoadError(p);
    Probe.That(ReferenceEquals(error, original) && Probe.Calls.Contains("host:stop"), "partial host did not unwind");
    Probe.That(!Probe.Calls.Contains("diagnostics:stop"), "unstarted diagnostics were touched");
    var count = Probe.Calls.Count;
    Probe.That(Probe.LoadError(p) is InvalidOperationException && count == Probe.Calls.Count, "failed Load retried native startup");
});
Probe.Case("failed cleanup cannot expose partially loaded host", () => {
    var original = new IOException("patch failed"); Probe.Faults["hook:patch"] = original;
    Probe.Faults["host:stop"] = new IOException("host stop failed");
    Probe.Faults["diagnostics:stop"] = new IOException("diagnostic stop failed");
    var p = Probe.Plugin(RuntimeMode.Authoring); var error = Probe.LoadError(p);
    Probe.That(ReferenceEquals(error, original), "cleanup error escaped instead of original exception");
    Probe.That(Probe.Calls.Contains("hook:unpatch") && Probe.Calls.Contains("diagnostics:stop"), "cleanup stages skipped");
    Probe.That(Plugin.Runtime == null && !Plugin.CanExecuteGameplay, "partially loaded host published after failure");
});
Probe.Case("configuration edits do not change frozen startup selection", () => {
    var p = Probe.Plugin(RuntimeMode.Play);
    p.Config.Preset["Framework.PlanPath"] = "content/explicit.json";
    p.Config.Preset["Framework.AllowedPermissions"] = "gtfo.enemy.health.read";
    p.Load();
    ((BepInEx.Configuration.ConfigEntry<string>)p.Config.Entries["Runtime.Mode"]).Value = "Off";
    ((BepInEx.Configuration.ConfigEntry<string>)p.Config.Entries["Framework.PlanPath"]).Value = "other.json";
    ((BepInEx.Configuration.ConfigEntry<string>)p.Config.Entries["Framework.AllowedPermissions"]).Value = "gtfo.enemy.health.write";
    Probe.That(Plugin.ConfiguredMode == RuntimeMode.Play, "running mode silently changed");
    Probe.That(GameRuntimeBridge.Plan == "content/explicit.json" && GameRuntimeBridge.Grants == "gtfo.enemy.health.read", "running plan or grants changed without restart");
});
Probe.Case("diagnostic partial initialization unwinds", () => {
    var original = new IOException("diagnostic initialization failed"); Probe.Faults["diagnostics:init"] = original;
    var p = Probe.Plugin(RuntimeMode.Authoring);
    Probe.That(ReferenceEquals(Probe.LoadError(p), original), "diagnostic initialization exception changed");
    Probe.That(Probe.Calls.Contains("diagnostics:stop") && Probe.Calls.Contains("host:stop"), "partial diagnostic startup did not unwind");
    Probe.That(Probe.Components.Count == 0 && Plugin.Runtime == null, "partial diagnostic startup leaked host or component");
});
Probe.Case("unwritable exception Data preserves the startup exception", () => {
    var original = new UnwritableDataException(); Probe.Faults["hook:patch"] = original;
    Probe.Faults["hook:unpatch"] = new IOException("cleanup failed");
    var p = Probe.Plugin(RuntimeMode.Authoring);
    Probe.That(ReferenceEquals(Probe.LoadError(p), original), "error evidence attachment replaced original exception");
    Probe.That(Probe.Calls.Contains("diagnostics:stop") && Plugin.Runtime == null, "cleanup skipped with unwritable Data");
});
foreach (string stage in new[] { "component:add:FrameworkMonitor", "component:add:PerformanceMonitor", "hook:patch" })
    Probe.Case("startup failure at " + stage, () => {
        var original = new IOException(stage); Probe.Faults[stage] = original;
        var p = Probe.Plugin(RuntimeMode.Authoring);
        Probe.That(ReferenceEquals(Probe.LoadError(p), original), "startup exception replaced");
        Probe.That(Probe.Calls.Contains("host:stop") && Probe.Calls.Contains("diagnostics:stop"), "startup cleanup incomplete");
        Probe.That(Probe.Components.All(c => c.Destroyed) && Plugin.Runtime == null, "startup acquired resource leaked");
    });
Console.WriteLine($"Plugin bootstrap: {Probe.Checks} assertions passed; {Probe.Failures} scenarios failed.");
Console.WriteLine("Production Plugin.cs with managed BepInEx/Unity/Harmony doubles; no game injection.");
Environment.ExitCode = Probe.Failures == 0 ? 0 : 1;
