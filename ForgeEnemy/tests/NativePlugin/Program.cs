using System.Text.Json;
using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using HarmonyLib;
using Host = ForgeRuntime.Plugin;
using NativePlugin = ForgeEnemy.Native.Plugin;

if (args.Length != 1) { Console.Error.WriteLine("Usage: NativePlugin <report.json>"); return 2; }
var checks = new List<object>(); int passed = 0, failed = 0;
void Case(string name, Action test)
{
    try { test(); passed++; checks.Add(new { name, passed = true }); }
    catch (Exception error) { failed++; checks.Add(new { name, passed = false, error = error.ToString() }); Console.Error.WriteLine(name + ": " + error.Message); }
    finally { Harmony.Reset(); }
}
void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }
void Throws(Action action) { try { action(); } catch { return; } throw new Exception("Expected rejection."); }
RuntimeKernel Kernel()
{
    var kernel = new RuntimeKernel(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
    kernel.BeginWorld(1); kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off); return kernel;
}
EnemyAgent Enemy()
{
    var actor = new EnemyAgent(); actor.Damage = new() { Owner = actor }; return actor;
}
// death_started -> record: exercises real plan loading and dispatch through the session without a heal step.
LocalPlan.Plan DeathPlan(RuntimeKernel kernel)
    => LocalPlan.Build(kernel, "test.plugin.death", EnemyModule.DeathStartedBinding, LocalPlan.RecordBinding, ("enemy", "target"));
RuntimeModule Dependency(string id) => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
{
    providers = new[] { new { id, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
    capabilities = Array.Empty<object>(),
    bindings = new[] { new { id = id + ".binding.read", capabilityId = "forge.trigger.combat.health_changed",
        providerId = id, handler = id + ".read", role = "observe", status = "implemented",
        dependencies = Array.Empty<string>(), requires = new[] { EnemyModule.HealthChangedBinding } } }
}).GetRawText(), new Dictionary<string, CommandHandler>(), new[]
{ new BindingSupport(id + ".binding.read", "implementation-only", Array.Empty<string>()) });
Case("session.duplicate-before-hooks", () =>
{
    var kernel = Kernel(); using var first = new EnemyModule(kernel, RuntimeLogLevel.Off, () => true, _ => { });
    string before = kernel.ExportManifest(); int installed = 0, removed = 0;
    Throws(() => EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => installed++, () => removed++));
    Require(installed == 0 && removed == 0 && kernel.ExportManifest() == before, "Duplicate changed existing ownership.");
});
Case("session.install-failure-rollback", () =>
{
    var kernel = Kernel(); string before = kernel.ExportManifest(); int removed = 0;
    Throws(() => EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => throw new IOException("patch"), () => removed++));
    Require(removed == 1 && kernel.ExportManifest() == before, "Partial registration or hooks survived.");
});
Case("session.cleanup-failure-preserves-cause", () =>
{
    var kernel = Kernel(); string before = kernel.ExportManifest(); var primary = new IOException("primary");
    try { EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => throw primary, () => throw new Exception("cleanup")); }
    catch (Exception error)
    {
        Require(ReferenceEquals(primary, error) && error.Data.Contains("ForgeEnemy.CleanupFailures"), "Primary failure was hidden.");
        Require(kernel.ExportManifest() == before, "Hook cleanup failure skipped provider cleanup."); return;
    }
    throw new Exception("Expected startup failure.");
});
Case("session.registration-window", () =>
{
    var kernel = Kernel(); kernel.StartRuntime(() => { }); int calls = 0;
    Throws(() => EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => calls++, () => calls++));
    Require(calls == 0, "Late registration touched hooks.");
});
Case("session.callback-and-reporter-fault", () =>
{
    var kernel = Kernel(); using var session = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true,
        _ => throw new IOException("reporter"), () => { }, () => { });
    kernel.StartRuntime(() => { });
    session.Guard(_ => throw new InvalidOperationException("native callback")); int callbacks = 0;
    session.Guard(_ => callbacks++);
    Require(session.Faulted && session.LastReporterFailure == "IOException" && callbacks == 0
        && kernel.StartupState == RuntimeStartupState.Ready, "Fault leaked or stopped unrelated Runtime work.");
});
Case("session.real-plan-and-world-cleanup", () =>
{
    var kernel = Kernel(); int removed = 0; var records = new List<CommandContext>();
    var session = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => { }, () => removed++);
    kernel.RegisterModule(LocalPlan.Recorder(records.Add), RuntimeLogLevel.Off);
    var plan = DeathPlan(kernel);
    var actor = Enemy(); var reference = session.Module.TrackSpawn(actor);
    kernel.StartRuntime(() => LocalPlan.Load(kernel, plan));
    var observed = session.Module.BeforeDeath(actor); actor.Alive = false;
    session.Module.AfterDeath(actor, observed); session.Module.AfterDeath(actor, observed);
    var tick = kernel.Advance(1, true);
    Require(observed != null && tick.Commands.Count == 1 && records.Count == 1, $"Plan was not executed exactly once: commands={tick.Commands.Count}; records={records.Count}.");
    kernel.BeginWorld(2); actor.Alive = true; var next = session.Module.TrackSpawn(actor);
    Require(next.WorldEpoch == 2 && next.LifeEpoch != reference.LifeEpoch, "World lifecycle did not retire the old life.");
    kernel.StopRuntime(); int called = 0; session.Guard(_ => called++);
    session.Dispose(); session.Dispose();
    Require(called == 0 && removed == 1 && !session.Module.IsRegistered, "Stop/dispose leaked execution or ownership.");
});
Case("plugin.off", () =>
{
    Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Off; Host.Runtime = null;
    new NativePlugin().Load();
    Require(NativePlugin.Session == null && Harmony.Patches == 0 && Harmony.Unpatches == 0, "Off activated native work.");
});
Case("plugin.missing-runtime", () =>
{
    Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = null;
    var plugin = new NativePlugin(); Throws(plugin.Load); Throws(plugin.Load);
    Require(Harmony.Patches == 0, "Unavailable host still installed patches.");
});
Case("plugin.invalid-log-level", () =>
{
    Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = Kernel();
    var plugin = new NativePlugin(); plugin.Config.Preset["Logging.Level"] = "verbose"; Throws(plugin.Load);
    Require(NativePlugin.Session == null && Harmony.Patches == 0, "Malformed Logging.Level still installed native work.");
});
Case("plugin.old-provider-conflict", () =>
{
    Host.Runtime = Kernel(); Host.CanExecuteGameplay = true;
    using var existing = new EnemyModule(Host.Runtime, RuntimeLogLevel.Off, () => true, _ => { });
    string before = Host.Runtime.ExportManifest(); Throws(new NativePlugin().Load);
    Require(Harmony.Patches == 0 && Harmony.Unpatches == 0 && Host.Runtime.ExportManifest() == before,
        "Native plugin patched or removed a provider it did not own.");
});
Case("plugin.partial-hook-failure", () =>
{
    Host.Runtime = Kernel(); string before = Host.Runtime.ExportManifest(); Harmony.FailPatchAt = 2;
    Throws(new NativePlugin().Load);
    Require(Harmony.Patches == 2 && Harmony.Unpatches == 1 && NativePlugin.Session == null
        && Host.Runtime.ExportManifest() == before, "Partial native load survived rollback.");
});
Case("plugin.log-failure-rollback", () =>
{
    Host.Runtime = Kernel(); string before = Host.Runtime.ExportManifest();
    var plugin = new NativePlugin(); plugin.Log.ThrowInfo = true; Throws(plugin.Load);
    Require(Harmony.Patches == 5 && Harmony.Unpatches == 1 && NativePlugin.Session == null
        && Host.Runtime.ExportManifest() == before, "Post-registration failure leaked the module.");
});
Case("session.readonly-error-data-keeps-primary", () =>
{
    var kernel = Kernel(); string before = kernel.ExportManifest(); var primary = new ReadOnlyDataFailure();
    Exception? caught = null;
    try { EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => throw primary, () => throw new IOException("unpatch")); }
    catch (Exception error) { caught = error; }
    Require(ReferenceEquals(primary, caught) && kernel.ExportManifest() == before, "Cleanup evidence replaced primary exception or left a provider.");
});
Case("session.message-getter-cannot-escape-guard", () =>
{
    var kernel = Kernel(); var logs = new List<string>();
    using var session = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, logs.Add, () => { }, () => { });
    session.Guard(_ => throw new MessageGetterFailure());
    Require(session.Faulted && session.LastFault != null && logs.Count == 1, "Native fault reporting escaped or lost the failure latch.");
});
Case("session.wrong-thread-callback-is-not-invoked", () =>
{
    var kernel = Kernel(); int calls = 0;
    using var session = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => { }, () => { });
    Throws(() => Task.Run(() => session.Guard(_ => calls++)).GetAwaiter().GetResult());
    Require(calls == 0 && !session.Faulted && session.Module.IsRegistered, "Off-thread guard invoked a callback or mutated the session.");
});
Case("session.wrong-thread-dispose-keeps-ownership", () =>
{
    var kernel = Kernel(); int removed = 0;
    var session = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => { }, () => removed++);
    try
    {
        Throws(() => Task.Run(session.Dispose).GetAwaiter().GetResult());
        Require(removed == 0 && session.Module.IsRegistered && !session.Faulted, "Off-thread Dispose removed native hooks or permanently disabled the session.");
        session.Dispose(); Require(removed == 1 && !session.Module.IsRegistered, "Legal cleanup could not be retried.");
    }
    finally { session.Module.Dispose(); session.Dispose(); }
});
Case("session.dispatch-dispose-rejects-before-unpatch", () =>
{
    var kernel = Kernel(); int removed = 0; Exception? rejection = null; EnemyPluginSession? dispatching = null;
    // The dispatched step tries to tear the session down from inside the kernel's dispatch.
    kernel.RegisterModule(LocalPlan.Recorder(_ => { try { dispatching!.Dispose(); } catch (Exception error) { rejection = error; } }), RuntimeLogLevel.Off);
    var session = dispatching = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => { }, () => removed++);
    try
    {
        var plan = DeathPlan(kernel);
        var actor = Enemy(); session.Module.TrackSpawn(actor);
        kernel.StartRuntime(() => LocalPlan.Load(kernel, plan));
        var death = session.Module.BeforeDeath(actor); actor.Alive = false; session.Module.AfterDeath(actor, death);
        var tick = kernel.Advance(1, true);
        Require(tick.Commands.Count == 1 && rejection is RuntimeContractException && removed == 0 && !session.Faulted && session.Module.IsRegistered,
            $"In-dispatch Dispose was not rejected before unpatching: commands={tick.Commands.Count}; rejection={rejection?.GetType().Name}; removed={removed}; faulted={session.Faulted}.");
        session.Dispose(); Require(removed == 1 && !session.Module.IsRegistered, "Post-dispatch cleanup failed.");
    }
    finally { session.Module.Dispose(); session.Dispose(); }
});
Case("plugin.cleanup-readonly-data-always-clears-session", () =>
{
    Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = Kernel(); Host.CanExecuteGameplay = true;
    string before = Host.Runtime.ExportManifest(); var primary = new ReadOnlyDataFailure();
    var plugin = new NativePlugin(); plugin.Log.InfoFailure = primary; Harmony.UnpatchFailure = new IOException("unpatch");
    Exception? caught = null;
    try
    {
        try { plugin.Load(); } catch (Exception error) { caught = error; }
        Require(ReferenceEquals(primary, caught) && NativePlugin.Session == null && Host.Runtime.ExportManifest() == before,
            "Post-load cleanup failure masked the original or left the static session published.");
    }
    finally
    {
        // Fixture isolation only, after assertions have recorded the production failure.
        Harmony.UnpatchFailure = null;
        NativePlugin.Session?.Module.Dispose(); NativePlugin.Session?.Dispose();
        typeof(NativePlugin).GetProperty("Session", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!.SetValue(null, null);
    }
});
Case("session.in-use-dispose-remains-retryable", () =>
{
    var kernel = Kernel(); int removed = 0;
    using var session = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => { }, () => removed++);
    using var dependency = kernel.RegisterModule(Dependency("test.enemy_dependency"), RuntimeLogLevel.Off);
    Throws(session.Dispose);
    Require(removed == 0 && !session.Faulted && session.Module.IsRegistered, "Rejected disposal removed hooks or changed a live session.");
    dependency.Dispose(); session.Dispose();
    Require(removed == 1 && !session.Module.IsRegistered, "Cleanup failed after dependent module left.");
});
Case("session.unpatch-failure-remains-inert", () =>
{
    var kernel = Kernel(); int removed = 0, callbacks = 0;
    var session = EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => { }, () => { }, () => { removed++; throw new IOException("native cleanup"); });
    Throws(session.Dispose); session.Guard(_ => callbacks++); session.Dispose();
    Require(removed == 1 && callbacks == 0 && !session.Module.IsRegistered && session.Faulted,
        "Failed unpatch left executable ownership or retried a native operation.");
});
Case("session.failed-start-residual-registration-is-inert", () =>
{
    var kernel = Kernel(); RuntimeModuleHandle? dependency = null; EnemyModule? orphan = null; int gateReads = 0;
    var primary = new IOException("install failed after dependency registration"); Exception? caught = null;
    try
    {
        try
        {
            EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => { gateReads++; return true; }, _ => { }, () =>
            {
                // Test-only access: inspect the otherwise unreachable module left by a failed rollback.
                var flags = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public;
                var registry = typeof(RuntimeKernel).GetField("registry", flags)!.GetValue(kernel)!;
                var handlers = (IDictionary<string, CommandHandler>)registry.GetType().GetField("Handlers", flags)!.GetValue(registry)!;
                orphan = (EnemyModule)handlers[EnemyModule.HealBinding].Target!;
                dependency = kernel.RegisterModule(Dependency("test.retained_dependency"), RuntimeLogLevel.Off); throw primary;
            }, () => { });
        }
        catch (Exception error) { caught = error; }
        Require(ReferenceEquals(primary, caught) && orphan?.IsRegistered == true, "Fixture did not retain the guarded failed-start module.");
        var before = gateReads; orphan!.BeforeDamage(Enemy().Damage);
        Require(gateReads == before, "Failed startup left a residual receiver with a live gameplay gate.");
    }
    finally { dependency?.Dispose(); orphan?.Dispose(); }
});
Case("session.throwing-error-data-and-reporter-keep-evidence", () =>
{
    var kernel = Kernel(); var primary = new DataGetterFailure(); Exception? caught = null;
    try { EnemyPluginSession.Start(kernel, RuntimeLogLevel.Off, () => true, _ => throw new IOException("reporter"),
        () => throw primary, () => throw new IOException("cleanup")); }
    catch (Exception error) { caught = error; }
    Require(ReferenceEquals(primary, caught) && EnemyPluginSession.LastCleanupDiagnostic?.Contains("attachment=") == true
        && EnemyPluginSession.LastCleanupDiagnostic.Contains("reporter="), "Failure evidence replaced the cause or lost bounded diagnostics.");
});
Case("session.null-message-is-safe", () =>
{
    using var session = EnemyPluginSession.Start(Kernel(), RuntimeLogLevel.Off, () => true, _ => { }, () => { }, () => { });
    session.Guard(_ => throw new NullMessageFailure());
    Require(session.Faulted && session.LastFault?.Contains(nameof(NullMessageFailure)) == true, "Null exception message escaped the guard.");
});
Case("session.fault-diagnostic-is-bounded-and-once", () =>
{
    var logs = new List<string>(); using var session = EnemyPluginSession.Start(Kernel(), RuntimeLogLevel.Off, () => true, logs.Add, () => { }, () => { });
    session.Guard(_ => throw new Exception(new string('x', 20000)));
    session.Guard(_ => throw new Exception("must not run"));
    Require(session.LastFault?.Length <= 2048 && logs.Count == 1 && logs[0].Length < 4096, "Unbounded or repeated failure reporting.");
});
Case("plugin.success-single-load-no-hot-reload", () =>
{
    Host.Runtime = Kernel(); var plugin = new NativePlugin(); plugin.Load();
    Require(NativePlugin.Session?.Module.IsRegistered == true && Harmony.Patches == 5, "Native module was not installed.");
    Throws(plugin.Load);
    Require(Harmony.Patches == 5 && !plugin.Unload(), "Repeated Load or hot unload changed native lifetime.");
    Host.Runtime.StartRuntime(() => { }); Host.Runtime.StopRuntime();
    NativePlugin.Session!.Dispose();
    Require(Harmony.Unpatches == 1 && !NativePlugin.Session.Module.IsRegistered, "Shutdown leaked registration.");
});
var result = new { verification = "production-plugin-session-and-receiver-source-with-loader-game-doubles",
    gameExecuted = false, multiplayerExecuted = false, passed, failed, checks };
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{passed + failed} Native plugin integration cases; no GTFO execution.");
return failed == 0 ? 0 : 1;
