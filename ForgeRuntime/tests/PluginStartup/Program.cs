using ForgeRuntime;
using ForgeRuntime.GameBindings;

Probe.Case("Off starts no native services", () => {
    var p = Probe.Plugin(RuntimeMode.Off); p.Load();
    Probe.That(!Probe.Calls.Contains("host:init") && !Probe.Calls.Contains("harmony:new"), "Off initializes native services");
    Probe.That(Plugin.Runtime == null && !Plugin.CanExecuteGameplay && Probe.Components.Count == 0, "Off exposes gameplay");
});
foreach (var mode in new[] { RuntimeMode.Play, RuntimeMode.Authoring })
    Probe.Case(mode + " starts only the host", () => {
        var p = Probe.Plugin(mode); p.Load();
        Probe.That(Plugin.Runtime != null && Plugin.ConfiguredMode == mode && Probe.Calls.Contains("host:init"), "host was not published");
        Probe.That(Probe.Calls.Count(c => c.StartsWith("hook:type:")) == 1 && Probe.Calls.Contains("hook:type:FrameworkHook"), "host patched more than its framework hooks");
        Probe.That(Probe.Components.Count == 1 && Probe.Components[0] is FrameworkMonitor, "host created a diagnostic component");
        Probe.That(!p.Unload(), "native plugin incorrectly supports hot unloading");
    });
Probe.Case("invalid mode fails before native initialization", () => {
    var p = Probe.Plugin((RuntimeMode)77); var error = Probe.LoadError(p);
    Probe.That(error != null && !Probe.Calls.Contains("host:init"), "invalid mode crossed native startup boundary");
});
Probe.Case("Logging.Level defaults to error and reaches the host", () => {
    var p = Probe.Plugin(RuntimeMode.Play); p.Load();
    Probe.That(GameRuntimeBridge.LogLevel == ForgeRuntime.Framework.RuntimeLogLevel.Error, "default log level did not reach host initialization");
});
Probe.Case("configured Logging.Level reaches the host", () => {
    var p = Probe.Plugin(RuntimeMode.Authoring); p.Config.Preset["Logging.Level"] = "info"; p.Load();
    Probe.That(GameRuntimeBridge.LogLevel == ForgeRuntime.Framework.RuntimeLogLevel.Info, "configured log level did not reach host initialization");
});
Probe.Case("invalid Logging.Level fails before native initialization", () => {
    var p = Probe.Plugin(RuntimeMode.Play); p.Config.Preset["Logging.Level"] = "trace";
    Probe.That(Probe.LoadError(p) is InvalidOperationException && !Probe.Calls.Contains("harmony:new") && !Probe.Calls.Contains("host:init"),
        "trace or unknown log level crossed native startup boundary");
});
Probe.Case("successful Load cannot be repeated", () => {
    var p = Probe.Plugin(RuntimeMode.Play); p.Load(); var before = Probe.Calls.Count;
    Probe.That(Probe.LoadError(p) is InvalidOperationException && before == Probe.Calls.Count, "same plugin initialized twice");
});
Probe.Case("rollback continues through cleanup and logger errors", () => {
    var original = new IOException("last startup log failed");
    Probe.Faults["log:loaded"] = original;
    Probe.Faults["hook:unpatch"] = new IOException("unpatch failed");
    Probe.Faults["log:error"] = new IOException("logger failed");
    var p = Probe.Plugin(RuntimeMode.Play); var error = Probe.LoadError(p);
    Probe.That(ReferenceEquals(error, original), "cleanup replaced original startup exception");
    Probe.That(original.Data["ForgeRuntime.StartupCleanupFailures"] is AggregateException failures && failures.InnerExceptions.Count == 2, "cleanup/logger failures were not retained on original error");
    Probe.That(Probe.Calls.Contains("host:stop") && Probe.Components.Count == 1 && Probe.Components.All(c => c.Destroyed), "cleanup skipped host or created component");
    Probe.That(Plugin.Runtime == null && !Plugin.CanExecuteGameplay, "failed plugin exposes gameplay");
    Probe.That(Probe.Calls.IndexOf("host:stop") < Probe.Calls.IndexOf("hook:unpatch"), "native gate closes after potentially failing unpatch");
});
Probe.Case("partial host failure unwinds and blocks retry", () => {
    var original = new IOException("host initialization failed"); Probe.Faults["host:init"] = original;
    var p = Probe.Plugin(RuntimeMode.Play); var error = Probe.LoadError(p);
    Probe.That(ReferenceEquals(error, original) && Probe.Calls.Contains("host:stop") && Probe.Calls.Contains("hook:unpatch"), "partial host did not unwind");
    var count = Probe.Calls.Count;
    Probe.That(Probe.LoadError(p) is InvalidOperationException && count == Probe.Calls.Count, "failed Load retried native startup");
});
Probe.Case("failed cleanup cannot expose partially loaded host", () => {
    var original = new IOException("patch failed"); Probe.Faults["hook:patch"] = original;
    Probe.Faults["host:stop"] = new IOException("host stop failed");
    var p = Probe.Plugin(RuntimeMode.Authoring); var error = Probe.LoadError(p);
    Probe.That(ReferenceEquals(error, original), "cleanup error escaped instead of original exception");
    Probe.That(Probe.Calls.Contains("hook:unpatch"), "hook cleanup skipped after host stop failure");
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
    ((BepInEx.Configuration.ConfigEntry<string>)p.Config.Entries["Logging.Level"]).Value = "off";
    Probe.That(Plugin.ConfiguredMode == RuntimeMode.Play, "running mode silently changed");
    Probe.That(GameRuntimeBridge.LogLevel == ForgeRuntime.Framework.RuntimeLogLevel.Error, "running log level changed without restart");
    Probe.That(GameRuntimeBridge.Plan == "content/explicit.json" && GameRuntimeBridge.Grants == "gtfo.enemy.health.read", "running plan or grants changed without restart");
});
Probe.Case("unwritable exception Data preserves the startup exception", () => {
    var original = new UnwritableDataException(); Probe.Faults["hook:patch"] = original;
    Probe.Faults["hook:unpatch"] = new IOException("cleanup failed");
    var p = Probe.Plugin(RuntimeMode.Play);
    Probe.That(ReferenceEquals(Probe.LoadError(p), original), "error evidence attachment replaced original exception");
    Probe.That(Probe.Calls.Contains("host:stop") && Plugin.Runtime == null, "cleanup skipped with unwritable Data");
});
foreach (string stage in new[] { "harmony:new", "component:add:FrameworkMonitor", "hook:patch" })
    Probe.Case("startup failure at " + stage, () => {
        var original = new IOException(stage); Probe.Faults[stage] = original;
        var p = Probe.Plugin(RuntimeMode.Play);
        Probe.That(ReferenceEquals(Probe.LoadError(p), original), "startup exception replaced");
        Probe.That(Probe.Calls.Contains("host:stop") == (stage != "harmony:new"), "host cleanup did not match the attempted stages");
        Probe.That(Probe.Components.All(c => c.Destroyed) && Plugin.Runtime == null, "startup acquired resource leaked");
    });
Console.WriteLine($"Plugin bootstrap: {Probe.Checks} assertions passed; {Probe.Failures} scenarios failed.");
Console.WriteLine("Production Plugin.cs with managed BepInEx/Unity/Harmony doubles; no game injection.");
Environment.ExitCode = Probe.Failures == 0 ? 0 : 1;
