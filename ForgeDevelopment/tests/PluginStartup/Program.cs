using ForgeRuntime;
using DevelopmentSettings = ForgeDevelopment.Native.Settings;

string[] order = { "settings:bind", "settings:authoring", "diagnostics:init", "harmony:new", "hook:type:GenerationHook", "hook:patch",
    "component:add:AuthoringMonitor", "component:add:PerformanceMonitor", "log:loaded" };

foreach (var mode in new[] { RuntimeMode.Off, RuntimeMode.Play })
    Probe.Case(mode + " host mode keeps Development inactive", () => {
        var p = Probe.Plugin(mode); p.Load();
        Probe.That(Probe.Calls.SequenceEqual(new[] { "log:inactive" }), "inactive mode touched settings, hooks or collectors: " + string.Join(",", Probe.Calls));
        Probe.That(Probe.Components.Count == 0, "inactive mode created a collector");
    });
Probe.Case("Authoring without a published Runtime fails before diagnostic work", () => {
    var p = Probe.Plugin(RuntimeMode.Authoring, runtimeAvailable: false);
    Probe.That(Probe.LoadError(p) is InvalidOperationException && Probe.Calls.Count == 0, "Development started without a real Runtime");
});
Probe.Case("Authoring starts exactly one collector set", () => {
    var p = Probe.Plugin(RuntimeMode.Authoring); p.Load();
    Probe.That(Probe.Calls.SequenceEqual(order), "unexpected startup sequence: " + string.Join(",", Probe.Calls));
    Probe.That(!p.Unload(), "native plugin incorrectly supports hot unloading");
});
Probe.Case("performance logging opt-out skips only the performance collector", () => {
    DevelopmentSettings.PerformanceLogging.Value = false;
    var p = Probe.Plugin(RuntimeMode.Authoring); p.Load();
    Probe.That(Probe.Components.Count == 1 && Probe.Components[0] is ForgeDevelopment.Native.AuthoringMonitor, "performance opt-out changed authoring collection");
    Probe.That(Probe.Calls.Contains("diagnostics:init") && Probe.Calls.Contains("hook:patch"), "authoring diagnostics skipped");
});
Probe.Case("successful Load cannot be repeated", () => {
    var p = Probe.Plugin(RuntimeMode.Authoring); p.Load(); var before = Probe.Calls.Count;
    Probe.That(Probe.LoadError(p) is InvalidOperationException && before == Probe.Calls.Count, "same plugin initialized twice");
});
Probe.Case("inactive Load cannot be retried into an active one", () => {
    var p = Probe.Plugin(RuntimeMode.Play); p.Load();
    ForgeRuntime.Plugin.ConfiguredMode = RuntimeMode.Authoring;
    Probe.That(Probe.LoadError(p) is InvalidOperationException && !Probe.Calls.Contains("diagnostics:init"), "second Load enabled diagnostics");
});
foreach (var stage in order.Where(s => !s.StartsWith("hook:type:")))
    Probe.Case("startup failure at " + stage, () => {
        var original = new IOException(stage); Probe.Faults[stage] = original;
        var p = Probe.Plugin(RuntimeMode.Authoring);
        Probe.That(ReferenceEquals(Probe.LoadError(p), original), "startup exception replaced");
        int at = Array.IndexOf(order, stage);
        Probe.That(Probe.Calls.Contains("hook:unpatch") == (at > Array.IndexOf(order, "harmony:new")), "hook cleanup did not match created Harmony instance");
        Probe.That(Probe.Calls.Contains("diagnostics:stop") == (at >= Array.IndexOf(order, "diagnostics:init")), "diagnostic cleanup did not match attempted initialization");
        Probe.That(Probe.Components.All(c => c.Destroyed), "startup component leaked");
        if (Probe.Calls.Contains("diagnostics:stop"))
            Probe.That(Probe.Calls.IndexOf("diagnostics:stop") == Probe.Calls.Count - 1, "collectors stopped before hooks and components were removed");
    });
Probe.Case("one failing cleanup does not block the rest", () => {
    var original = new IOException("performance component failed");
    Probe.Faults["component:add:PerformanceMonitor"] = original;
    Probe.Faults["hook:unpatch"] = new IOException("unpatch failed");
    Probe.Faults["component:destroy:AuthoringMonitor"] = new IOException("destroy failed");
    Probe.Faults["log:error"] = new IOException("logger failed");
    var p = Probe.Plugin(RuntimeMode.Authoring);
    Probe.That(ReferenceEquals(Probe.LoadError(p), original), "cleanup replaced original startup exception");
    Probe.That(original.Data["ForgeDevelopment.StartupCleanupFailures"] is AggregateException failures && failures.InnerExceptions.Count == 4, "cleanup and reporter failures were not retained");
    Probe.That(Probe.Calls.Contains("diagnostics:stop"), "diagnostics kept running after earlier cleanup failures");
});
Probe.Case("unwritable exception Data preserves the startup exception", () => {
    var original = new UnwritableDataException(); Probe.Faults["hook:patch"] = original;
    Probe.Faults["hook:unpatch"] = new IOException("cleanup failed");
    var p = Probe.Plugin(RuntimeMode.Authoring);
    Probe.That(ReferenceEquals(Probe.LoadError(p), original), "error evidence attachment replaced original exception");
    Probe.That(Probe.Calls.Contains("diagnostics:stop"), "cleanup skipped with unwritable Data");
});
Console.WriteLine($"Development plugin bootstrap: {Probe.Checks} assertions passed; {Probe.Failures} scenarios failed.");
Console.WriteLine("Production Development Plugin.cs with managed host/BepInEx/Unity/Harmony doubles; no game injection.");
Environment.ExitCode = Probe.Failures == 0 ? 0 : 1;
