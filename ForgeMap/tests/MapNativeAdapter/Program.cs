using System.Globalization;
using System.Reflection;
using System.Text.Json;
using BepInEx;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using HarmonyLib;
using Player;
using SNetwork;
using Host = ForgeRuntime.Plugin;
using MapPlugin = ForgeMap.Native.Plugin;

// Production Map native plugin, session, hooks and player identity + compiled ForgeMap + compiled SDK.
// Native state is a managed double: every spawn, despawn and relink below is synthetic and NOT game-verified.
// Lookup values are synthetic Steam64-shaped numbers; the privacy cases prove none of them reaches any output.
if (args.Length != 1) { Console.Error.WriteLine("Usage: MapNativeAdapter <report.json>"); return 2; }
var checks = new List<object>(); int passed = 0, failed = 0;
var outputs = new List<string>();
void Case(string name, Action test)
{
    try { test(); passed++; checks.Add(new { name, passed = true }); }
    catch (Exception error) { failed++; checks.Add(new { name, passed = false, error = error.ToString() }); Console.Error.WriteLine("FAIL " + name + ": " + error.Message); }
    finally { Reset(); }
}
void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }
void Throws(string? code, Action action)
{
    try { action(); }
    catch (RuntimeContractException error) when (code != null) { Require(error.Code == code, "Expected " + code + "; got " + error.Code); return; }
    catch (Exception) when (code == null) { return; }
    throw new Exception("Expected rejection " + (code ?? "(any)"));
}
void Reset()
{
    var session = MapPlugin.Session;
    if (session != null)
    {
        // Fixture isolation only, after the case has recorded its production result.
        try { session.Module.Dispose(); session.Dispose(); } catch { }
        typeof(MapPlugin).GetProperty("Session", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
    }
    Harmony.Reset(); PlayerManager.Reset(); SNet.IsMaster = true;
    Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = null;
}
RuntimeKernel Kernel()
{
    var kernel = new RuntimeKernel(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
    kernel.BeginWorld(1); return kernel;
}
RuntimeModule Other(string id, bool playerResolver) => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
{
    providers = new[] { new { id, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
    capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
}).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
    playerResolver ? new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.player"] = _ => true } : null);
MapPluginSession Start(RuntimeKernel kernel, List<string> info, List<string> warn, Action? install = null, Action? remove = null)
    => MapPluginSession.Start(kernel, RuntimeLogLevel.Off, m => { outputs.Add(m); warn.Add(m); }, m => { outputs.Add(m); info.Add(m); },
        install ?? (() => { }), remove ?? (() => { }));
(SNet_Player Player, PlayerAgent Agent) Spawn(ulong lookup, bool bot = false)
{
    var player = new SNet_Player { Lookup = lookup, IsBot = bot };
    var agent = new PlayerAgent { Owner = player };
    player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
    PlayerManager.PlayerAgentsInLevel.Add(agent);
    return (player, agent);
}
PlayerAgent Replace(SNet_Player player, PlayerAgent old)
{
    var agent = new PlayerAgent { Owner = player };
    var list = PlayerManager.PlayerAgentsInLevel; list[list.IndexOf(old)] = agent;
    old.Destroyed = true; player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
    return agent;
}
void Despawn(SNet_Player player, PlayerAgent agent)
{
    PlayerManager.PlayerAgentsInLevel.Remove(agent); player.PlayerAgent = null;
}
// Entity IDs are per-world numbers in first-recorded order, never the account key.
EntityReference Ref(long number, long world, long life) => new("gtfo.player:" + number.ToString(CultureInfo.InvariantCulture), world, life);
void Read(MapPluginSession session) => session.Guard(module => module.Reconcile());
void Hook(Type hook) => hook.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { new PlayerManager() });
const ulong A = 76561198000000001, B = 76561198000000002, Bot = 4242424242;
ulong[] fixtureLookups = { A, B, Bot, 76561198000000010, 76561198000000011, 76561198000000012, 76561198000000013, 76561198000000099 };
bool Leaks(string text) => fixtureLookups.Any(l => text.Contains(l.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

Case("module.provider-and-player-namespace-only", () =>
{
    var kernel = Kernel(); List<string> info = new(), warn = new();
    using var session = Start(kernel, info, warn);
    using (var doc = JsonDocument.Parse(kernel.ExportManifest()))
    {
        var registry = doc.RootElement.GetProperty("registry");
        Require(registry.GetProperty("providers").EnumerateArray().Select(p => p.GetProperty("id").GetString())
            .SequenceEqual(new[] { ModuleDefinition.ProviderId }), "Map registered a provider other than its one definition.");
        Require(registry.GetProperty("capabilities").GetArrayLength() == 0 && registry.GetProperty("bindings").GetArrayLength() == 0,
            "Player identity claimed capabilities or bindings.");
    }
    Throws("entity-namespace-conflict", () => kernel.RegisterModule(Other("test.player_namespace", true), RuntimeLogLevel.Off));
    Throws("entity-instance-resolver-owner", () => kernel.RegisterModule(Other("test.player_lookup", false) with
        { EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>> { ["gtfo.player"] = _ => null } }, RuntimeLogLevel.Off));
});
Case("session.duplicate-map-provider-before-hooks", () =>
{
    var kernel = Kernel(); using var existing = kernel.RegisterModule(ModuleDefinition.Create(), RuntimeLogLevel.Off);
    string before = kernel.ExportManifest(); int installs = 0, removes = 0;
    Throws(null, () => Start(kernel, new(), new(), () => installs++, () => removes++));
    Require(installs == 0 && removes == 0 && kernel.ExportManifest() == before, "Duplicate provider touched hooks or ownership.");
});
Case("session.foreign-player-namespace-before-hooks", () =>
{
    var kernel = Kernel(); using var other = kernel.RegisterModule(Other("test.player_owner", true), RuntimeLogLevel.Off);
    string before = kernel.ExportManifest(); int installs = 0;
    Throws("entity-namespace-conflict", () => Start(kernel, new(), new(), () => installs++));
    Require(installs == 0 && kernel.ExportManifest() == before, "Namespace conflict touched hooks or ownership.");
});
Case("session.install-failure-rolls-back", () =>
{
    var kernel = Kernel(); string before = kernel.ExportManifest(); int removes = 0; var primary = new IOException("patch");
    try { Start(kernel, new(), new(), () => throw primary, () => removes++); throw new Exception("Expected install failure."); }
    catch (IOException error) { Require(ReferenceEquals(error, primary), "Install failure was replaced."); }
    Require(removes == 1 && kernel.ExportManifest() == before, "Partial registration or hooks survived.");
    using var again = Start(kernel, new(), new());
    Require(again.Module.IsRegistered, "Rolled-back provider could not register again.");
});
Case("session.cleanup-failure-preserves-cause", () =>
{
    var kernel = Kernel(); string before = kernel.ExportManifest(); var primary = new IOException("primary");
    try { Start(kernel, new(), new(), () => throw primary, () => throw new InvalidOperationException("unpatch")); }
    catch (Exception error)
    {
        Require(ReferenceEquals(primary, error) && error.Data.Contains("ForgeMap.CleanupFailures")
            && kernel.ExportManifest() == before, "Cleanup failure hid the cause or skipped provider cleanup.");
        return;
    }
    throw new Exception("Expected startup failure.");
});
Case("session.registration-window", () =>
{
    var kernel = Kernel(); kernel.StartRuntime(() => { }); int calls = 0;
    Throws(null, () => Start(kernel, new(), new(), () => calls++, () => calls++));
    Require(calls == 0, "Late registration touched hooks.");
});
Case("identity.host-spawn-records-life-without-gameplay-gate", () =>
{
    // Elevator spawns happen in the same world before InLevel; the module reads no gameplay gate.
    var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
    kernel.StartRuntime(() => { }); Spawn(A); Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 1)) && session.Module.Count == 1, "Spawned host player was not recorded.");
    Require(info.SequenceEqual(new[] { "map.player-life-started id=gtfo.player:1 world=1 life=1 bot=false" }),
        "Life start log differs: " + string.Join(" | ", info));
    Require(warn.Count == 0, "Linked player reported a warning.");
});
Case("identity.requires-ready-runtime-and-master", () =>
{
    var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
    Spawn(A); Read(session);
    Require(session.Module.Count == 0 && info.Count == 0, "Recorded before Runtime was Ready.");
    kernel.StartRuntime(() => { }); SNet.IsMaster = false; Read(session);
    Require(session.Module.Count == 0 && info.Count == 0, "A client allocated a player life.");
    SNet.IsMaster = true; Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 1)), "Gated readbacks consumed an entity number or life epoch.");
});
Case("identity.repeat-readback-keeps-life", () =>
{
    // Downed, revive and heal do not replace the agent in this model; repeated readbacks must not allocate.
    var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
    kernel.StartRuntime(() => { }); Spawn(A); Read(session); Read(session);
    var other = Spawn(B); Read(session); Despawn(other.Player, other.Agent); Read(session); Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 1)) && info.Count(l => l.Contains("id=gtfo.player:1 ", StringComparison.Ordinal)) == 1,
        "Repeated readbacks changed an unchanged life.");
});
Case("identity.new-agent-instance-is-new-life", () =>
{
    var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
    kernel.StartRuntime(() => { }); var a = Spawn(A); Read(session);
    Replace(a.Player, a.Agent);
    Require(!session.Module.IsCurrent(Ref(1, 1, 1)), "Old life stayed current after its agent was replaced.");
    Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 2)) && !session.Module.IsCurrent(Ref(1, 1, 1)), "Replacement agent did not allocate a new life.");
    Require(info.Contains("map.player-life-ended id=gtfo.player:1 world=1 life=1 reason=replaced")
        && info.Contains("map.player-life-started id=gtfo.player:1 world=1 life=2 bot=false"), "Replacement logs differ: " + string.Join(" | ", info));
});
Case("identity.despawn-retires-and-respawn-keeps-number-with-new-life", () =>
{
    var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
    kernel.StartRuntime(() => { }); var a = Spawn(A); Spawn(B); Read(session);
    Despawn(a.Player, a.Agent); Read(session);
    Require(session.Module.Count == 1 && !session.Module.IsCurrent(Ref(1, 1, 1)) && session.Module.IsCurrent(Ref(2, 1, 2))
        && info.Contains("map.player-life-ended id=gtfo.player:1 world=1 life=1 reason=despawned"), "Despawn did not retire exactly that life.");
    var again = new PlayerAgent { Owner = a.Player }; a.Player.PlayerAgent = new SNet_IPlayerAgent { Target = again };
    PlayerManager.PlayerAgentsInLevel.Add(again); Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 3)) && !session.Module.IsCurrent(Ref(1, 1, 1)), "Respawn reused a life or changed the per-world number.");
});
Case("identity.world-change-clears-and-renumbers", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new());
    kernel.StartRuntime(() => { }); Spawn(B); Spawn(A); Read(session);
    kernel.BeginWorld(2);
    Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)) && !session.Module.IsCurrent(Ref(2, 1, 2)),
        "World change did not clear player lives.");
    PlayerManager.PlayerAgentsInLevel.Reverse(); Read(session);
    Require(session.Module.IsCurrent(Ref(1, 2, 3)) && session.Module.IsCurrent(Ref(2, 2, 4)) && !session.Module.IsCurrent(Ref(1, 1, 1)),
        "New world did not allocate fresh numbers and lives.");
});
Case("identity.runtime-stop-clears-and-stops-recording", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new());
    kernel.StartRuntime(() => { }); Spawn(A); Read(session);
    kernel.StopRuntime();
    Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)), "Stop left a player life current.");
    session.Module.Reconcile(); Require(session.Module.Count == 0, "Stopped Runtime recorded a player life.");
});
Case("identity.bot-follows-same-rules", () =>
{
    var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
    kernel.StartRuntime(() => { }); var bot = Spawn(Bot, bot: true); Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 1)) && info.Contains("map.player-life-started id=gtfo.player:1 world=1 life=1 bot=true"), "Bot was not recorded like a player.");
    Replace(bot.Player, bot.Agent); Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 2)) && !session.Module.IsCurrent(Ref(1, 1, 1)), "Bot replacement did not follow player life rules.");
});
Case("identity.late-joiner-does-not-touch-existing-lives", () =>
{
    var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
    kernel.StartRuntime(() => { }); Spawn(A); Read(session); Spawn(B); Read(session);
    Require(session.Module.IsCurrent(Ref(1, 1, 1)) && session.Module.IsCurrent(Ref(2, 1, 2))
        && !info.Any(l => l.StartsWith("map.player-life-ended", StringComparison.Ordinal)), "Late joiner changed an existing life.");
});
Case("identity.unresolved-owner-stays-unrecorded", () =>
{
    var kernel = Kernel(); List<string> warn = new(); using var session = Start(kernel, new(), warn);
    kernel.StartRuntime(() => { });
    PlayerManager.PlayerAgentsInLevel.Add(new PlayerAgent { Owner = null });
    var stray = Spawn(B); var elsewhere = new PlayerAgent { Owner = stray.Player }; stray.Player.PlayerAgent = new SNet_IPlayerAgent { Target = elsewhere };
    Read(session); Read(session);
    Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)), "An unresolved or unlinked owner was recorded.");
    Require(warn.Count == 2 && warn.All(w => w.StartsWith("map.player-owner-unresolved", StringComparison.Ordinal)), "Unresolved owners were not reported exactly once each.");
});
Case("identity.duplicate-key-records-neither", () =>
{
    var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
    kernel.StartRuntime(() => { }); Spawn(A); Read(session); Spawn(A); Read(session); Read(session);
    Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)) && !session.Module.IsCurrent(Ref(1, 1, 2))
        && info.Contains("map.player-life-ended id=gtfo.player:1 world=1 life=1 reason=key-conflict"), "Conflicting key kept a life.");
    Require(warn.Count(w => w.StartsWith("map.player-key-conflict", StringComparison.Ordinal)) == 1, "Key conflict was not reported exactly once.");
});
Case("identity.current-check-rereads-native-links", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
    var destroyed = Spawn(76561198000000010); var relinked = Spawn(76561198000000011); var relookup = Spawn(76561198000000012);
    var reowned = Spawn(76561198000000013); Read(session);
    Require(Enumerable.Range(1, 4).All(n => session.Module.IsCurrent(Ref(n, 1, n))), "Fixture lives were not recorded.");
    destroyed.Agent.Destroyed = true;
    relinked.Player.PlayerAgent = new SNet_IPlayerAgent { Target = new PlayerAgent { Owner = relinked.Player } };
    relookup.Player.Lookup = 76561198000000099;
    reowned.Agent.Owner = new SNet_Player { Lookup = 76561198000000013, PlayerAgent = new SNet_IPlayerAgent { Target = reowned.Agent } };
    Require(Enumerable.Range(1, 4).All(n => !session.Module.IsCurrent(Ref(n, 1, n))),
        "A destroyed, relinked, re-keyed or re-owned agent stayed current.");
});
Case("identity.forged-references-rejected", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
    Spawn(A); Read(session); var module = session.Module;
    Require(module.IsCurrent(Ref(1, 1, 1)), "Fixture life missing.");
    var forged = new[]
    {
        Ref(1, 2, 1), Ref(1, 1, 2), Ref(1, 1, 0), Ref(2, 1, 1),
        new EntityReference("gtfo.player:01", 1, 1), new EntityReference("gtfo.player:+1", 1, 1), new EntityReference("gtfo.player:-1", 1, 1),
        new EntityReference("gtfo.player: 1", 1, 1), new EntityReference("GTFO.player:1", 1, 1), new EntityReference("gtfo.enemy:1", 1, 1),
        new EntityReference("gtfo.player:", 1, 1),
        // The account key is never an entity ID, even for the exact player it belongs to.
        new EntityReference("gtfo.player:" + A.ToString(CultureInfo.InvariantCulture), 1, 1)
    };
    for (int i = 0; i < forged.Length; i++) Require(!module.IsCurrent(forged[i]), "Forged reference #" + i + " accepted.");
});
Case("identity.wrong-thread-rejected", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
    Spawn(A); Read(session);
    Throws("wrong-thread", () => Task.Run(() => session.Module.IsCurrent(Ref(1, 1, 1))).GetAwaiter().GetResult());
    Throws("wrong-thread", () => Task.Run(session.Module.Reconcile).GetAwaiter().GetResult());
    Require(session.Module.IsCurrent(Ref(1, 1, 1)), "Off-thread access changed identity state.");
});
Case("instance.sdk-lookup-returns-recorded-life", () =>
{
    var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
    kernel.StartRuntime(() => { }); var a = Spawn(A); var bot = Spawn(Bot, bot: true); Read(session);
    int logs = info.Count + warn.Count;
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1)
        && kernel.ResolveEntityInstance("gtfo.player", bot.Player) == Ref(2, 1, 2), "SDK lookup did not return the recorded lives.");
    Require(kernel.IsEntityCurrent(Ref(1, 1, 1)) && info.Count + warn.Count == logs && session.Module.Count == 2,
        "Lookup logged or changed identity state.");
});
Case("instance.lookup-never-allocates-a-life", () =>
{
    var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
    kernel.StartRuntime(() => { }); var a = Spawn(A);
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null && session.Module.Count == 0 && info.Count == 0,
        "Lookup recorded a player before the spawn readback.");
    Read(session);
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1), "Lookup consumed an entity number or life epoch.");
    Replace(a.Player, a.Agent);
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null && session.Module.Count == 1,
        "Lookup returned the replaced life or allocated the replacement.");
    Read(session);
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 2), "Replacement readback did not own the new life.");
    kernel.BeginWorld(2);
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null && session.Module.Count == 0, "Lookup survived or refilled a world change.");
});
Case("instance.observe-gate-and-native-type", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new()); var a = Spawn(A);
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null, "Registering Runtime resolved a player.");
    kernel.StartRuntime(() => { }); Read(session);
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1), "Fixture life missing.");
    SNet.IsMaster = false;
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null && session.Module.ResolveInstance(a.Player) == null,
        "A client resolved a host-allocated life.");
    SNet.IsMaster = true;
    var stranger = new SNet_Player { Lookup = A, PlayerAgent = new SNet_IPlayerAgent { Target = a.Agent } };
    foreach (var wrong in new object[] { a.Agent, a.Player.PlayerAgent!, "gtfo.player:1", 1L, stranger })
        Require(kernel.ResolveEntityInstance("gtfo.player", wrong) == null, "Lookup accepted " + wrong.GetType().Name + ".");
    a.Player.Destroyed = true;
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null, "Destroyed player resolved.");
    a.Player.Destroyed = false;
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1), "Gated lookups changed the recorded life.");
    session.Guard(_ => throw new InvalidOperationException("fault"));
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null, "Faulted session resolved a player.");
});
Case("instance.lookup-rereads-native-links", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
    var relinked = Spawn(76561198000000010); var relookup = Spawn(76561198000000011); var reowned = Spawn(76561198000000012); Read(session);
    var fixtures = new[] { relinked, relookup, reowned };
    Require(fixtures.Select((p, i) => kernel.ResolveEntityInstance("gtfo.player", p.Player) == Ref(i + 1, 1, i + 1)).All(x => x), "Fixture lives missing.");
    relinked.Player.PlayerAgent = new SNet_IPlayerAgent { Target = new PlayerAgent { Owner = relinked.Player } };
    relookup.Player.Lookup = 76561198000000099;
    reowned.Agent.Owner = new SNet_Player { Lookup = 76561198000000012, PlayerAgent = new SNet_IPlayerAgent { Target = reowned.Agent } };
    Require(fixtures.All(p => kernel.ResolveEntityInstance("gtfo.player", p.Player) == null) && session.Module.Count == 3,
        "A relinked, re-keyed or re-owned player resolved, or the lookup changed the table.");
});
Case("instance.stop-and-wrong-thread", () =>
{
    var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
    var a = Spawn(A); Read(session);
    Throws("wrong-thread", () => Task.Run(() => session.Module.ResolveInstance(a.Player)).GetAwaiter().GetResult());
    Throws("wrong-thread", () => Task.Run(() => kernel.ResolveEntityInstance("gtfo.player", a.Player)).GetAwaiter().GetResult());
    Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1), "Off-thread lookup changed identity state.");
    kernel.StopRuntime();
    Require(session.Module.ResolveInstance(a.Player) == null, "Stopped Map resolved a player.");
    Throws("runtime-not-ready", () => kernel.ResolveEntityInstance("gtfo.player", a.Player));
});
Case("privacy.instance-lookup-results-and-errors-exclude-account-lookup", () =>
{
    var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn); kernel.StartRuntime(() => { });
    var a = Spawn(A); var bot = Spawn(Bot, bot: true); Read(session);
    var texts = new List<string>();
    foreach (var player in new[] { a.Player, bot.Player, new SNet_Player { Lookup = B } })
        texts.Add(kernel.ResolveEntityInstance("gtfo.player", player)?.ToString() ?? "null");
    void Capture(Action action) { try { action(); texts.Add("no-error"); } catch (Exception error) { texts.Add(error.ToString()); } }
    Capture(() => Task.Run(() => session.Module.ResolveInstance(a.Player)).GetAwaiter().GetResult());
    Capture(() => Task.Run(() => kernel.ResolveEntityInstance("gtfo.player", a.Player)).GetAwaiter().GetResult());
    kernel.StopRuntime(); Capture(() => kernel.ResolveEntityInstance("gtfo.player", a.Player));
    Require(texts.Count == 6 && texts[0].Contains("gtfo.player:1", StringComparison.Ordinal) && texts[2] == "null"
        && texts.Skip(3).All(t => t.Contains("RuntimeContractException", StringComparison.Ordinal)), "Scenario differs: " + texts.Count);
    outputs.AddRange(texts);
    Require(!texts.Concat(info).Concat(warn).Any(Leaks), "An account lookup reached an instance lookup result, error or log.");
});
Case("privacy.entity-ids-logs-and-manifest-exclude-account-lookup", () =>
{
    var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
    kernel.StartRuntime(() => { });
    var a = Spawn(A); Spawn(Bot, bot: true); Read(session);
    Replace(a.Player, a.Agent); Read(session);
    Spawn(B); Spawn(B); PlayerManager.PlayerAgentsInLevel.Add(new PlayerAgent { Owner = null }); Read(session);
    var ids = info.Select(l => l.Split(' ')[1]).Distinct().ToArray();
    Require(ids.Length > 0 && ids.All(id => id is "id=gtfo.player:1" or "id=gtfo.player:2"), "Entity IDs are not per-world numbers: " + ids.Length);
    Require(session.Module.IsCurrent(Ref(1, 1, 3)) && session.Module.IsCurrent(Ref(2, 1, 2)), "Scenario lives differ.");
    Require(warn.Count == 2, "Scenario warnings differ.");
    Require(!info.Concat(warn).Append(kernel.ExportManifest()).Any(Leaks), "An account lookup reached an entity ID, log, warning or manifest.");
});
Case("session.native-fault-latches-and-clears", () =>
{
    var kernel = Kernel(); List<string> warn = new(); using var session = Start(kernel, new(), warn); kernel.StartRuntime(() => { });
    Spawn(A); Read(session); PlayerManager.ThrowOnRead = true; Read(session);
    Require(session.Faulted && !session.Module.IsCurrent(Ref(1, 1, 1)) && warn.Count == 1
        && warn[0].StartsWith("Map player identity disabled until restart", StringComparison.Ordinal), "Native read failure did not latch and clear.");
    PlayerManager.ThrowOnRead = false; Read(session);
    Require(session.Module.Count == 0 && kernel.StartupState == RuntimeStartupState.Ready, "Faulted session recorded again or stopped Runtime.");
});
Case("session.reporter-failure-cannot-escape", () =>
{
    var kernel = Kernel();
    using var session = MapPluginSession.Start(kernel, RuntimeLogLevel.Off, _ => throw new IOException("reporter"), _ => { }, () => { }, () => { });
    kernel.StartRuntime(() => { }); session.Guard(_ => throw new InvalidOperationException("native callback"));
    Require(session.Faulted && session.LastReporterFailure == "IOException", "Reporter failure escaped the guard.");
});
Case("session.fault-diagnostic-is-bounded-and-once", () =>
{
    List<string> warn = new(); using var session = Start(Kernel(), new(), warn);
    session.Guard(_ => throw new Exception(new string('x', 20000))); session.Guard(_ => throw new Exception("must not run"));
    Require(session.LastFault?.Length <= 2048 && warn.Count == 1 && warn[0].Length < 4096, "Unbounded or repeated failure reporting.");
});
Case("session.wrong-thread-dispose-keeps-ownership", () =>
{
    var kernel = Kernel(); int removes = 0; var session = Start(kernel, new(), new(), remove: () => removes++);
    try
    {
        Throws(null, () => Task.Run(session.Dispose).GetAwaiter().GetResult());
        Require(removes == 0 && session.Module.IsRegistered && !session.Faulted, "Off-thread Dispose changed ownership.");
        session.Dispose(); Require(removes == 1 && !session.Module.IsRegistered, "Legal cleanup could not be retried.");
    }
    finally { session.Dispose(); }
});
Case("session.dispose-unregisters-before-unpatch", () =>
{
    var kernel = Kernel(); MapPluginSession? session = null; bool registeredAtUnpatch = true; int removes = 0;
    session = Start(kernel, new(), new(), remove: () => { removes++; registeredAtUnpatch = session!.Module.IsRegistered; });
    session.Dispose(); session.Dispose();
    using var doc = JsonDocument.Parse(kernel.ExportManifest());
    Require(removes == 1 && !registeredAtUnpatch && doc.RootElement.GetProperty("registry").GetProperty("providers").GetArrayLength() == 0,
        "Dispose unpatched before unregistering or left the provider.");
});
Case("session.unpatch-failure-remains-inert", () =>
{
    var kernel = Kernel(); int removes = 0, callbacks = 0;
    var session = Start(kernel, new(), new(), remove: () => { removes++; throw new IOException("native cleanup"); });
    Throws(null, session.Dispose); session.Guard(_ => callbacks++); session.Dispose();
    Require(removes == 1 && callbacks == 0 && !session.Module.IsRegistered && session.Faulted, "Failed unpatch left executable ownership.");
});
Case("hooks.exact-set-and-no-session-noop", () =>
{
    Require(MapNativeHooks.Types.SequenceEqual(new[] { typeof(PlayerSpawnedReadback), typeof(PlayerDespawnedReadback) }), "Unexpected hook set.");
    Spawn(A); Hook(typeof(PlayerSpawnedReadback)); Hook(typeof(PlayerDespawnedReadback));
});
Case("plugin.off", () =>
{
    Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Off; Host.Runtime = null;
    new MapPlugin().Load();
    Require(MapPlugin.Session == null && Harmony.Patches == 0 && Harmony.Unpatches == 0, "Off activated native work.");
});
Case("plugin.missing-runtime", () =>
{
    Host.Runtime = null; var plugin = new MapPlugin(); Throws(null, plugin.Load); Throws(null, plugin.Load);
    Require(Harmony.Patches == 0 && MapPlugin.Session == null, "Unavailable host still installed patches.");
});
Case("plugin.invalid-log-level", () =>
{
    Host.Runtime = Kernel(); var plugin = new MapPlugin(); plugin.Config.Preset["Logging.Level"] = "verbose"; Throws(null, plugin.Load);
    Require(MapPlugin.Session == null && Harmony.Patches == 0, "Malformed Logging.Level still installed native work.");
});
Case("plugin.existing-map-provider-conflict", () =>
{
    Host.Runtime = Kernel(); using var existing = Host.Runtime.RegisterModule(ModuleDefinition.Create(), RuntimeLogLevel.Off);
    string before = Host.Runtime.ExportManifest(); Throws(null, new MapPlugin().Load);
    Require(Harmony.Patches == 0 && Harmony.Unpatches == 0 && MapPlugin.Session == null && Host.Runtime.ExportManifest() == before,
        "Plugin patched or removed a provider it did not own.");
});
Case("plugin.partial-hook-failure", () =>
{
    Host.Runtime = Kernel(); string before = Host.Runtime.ExportManifest(); Harmony.FailPatchAt = 2;
    Throws(null, new MapPlugin().Load);
    Require(Harmony.Patches == 2 && Harmony.Unpatches == 1 && MapPlugin.Session == null && Host.Runtime.ExportManifest() == before,
        "Partial native load survived rollback.");
});
Case("plugin.log-failure-rollback", () =>
{
    Host.Runtime = Kernel(); string before = Host.Runtime.ExportManifest();
    var plugin = new MapPlugin(); plugin.Log.ThrowInfo = true; Throws(null, plugin.Load);
    Require(Harmony.Patches == 2 && Harmony.Unpatches == 1 && MapPlugin.Session == null && Host.Runtime.ExportManifest() == before,
        "Post-registration failure leaked the module.");
});
Case("plugin.success-hooks-drive-identity-no-hot-reload", () =>
{
    var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
    try
    {
        plugin.Load();
        var session = MapPlugin.Session;
        Require(session?.Module.IsRegistered == true && Harmony.Patches == 2, "Map native module was not installed.");
        Throws(null, plugin.Load);
        Require(Harmony.Patches == 2 && !plugin.Unload(), "Repeated Load or hot unload changed native lifetime.");
        kernel.StartRuntime(() => { });
        var a = Spawn(A); Hook(typeof(PlayerSpawnedReadback));
        Require(session!.Module.IsCurrent(Ref(1, 1, 1)) && kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1)
            && plugin.Log.Infos.Contains("map.player-life-started id=gtfo.player:1 world=1 life=1 bot=false"), "Spawn postfix did not record the player.");
        Despawn(a.Player, a.Agent); Hook(typeof(PlayerDespawnedReadback));
        Require(!session.Module.IsCurrent(Ref(1, 1, 1))
            && plugin.Log.Infos.Contains("map.player-life-ended id=gtfo.player:1 world=1 life=1 reason=despawned"), "Despawn postfix did not retire the player.");
        kernel.StopRuntime(); session.Dispose();
        Require(Harmony.Unpatches == 1 && !session.Module.IsRegistered, "Shutdown leaked registration.");
    }
    finally { outputs.AddRange(plugin.Log.Infos); outputs.AddRange(plugin.Log.Warnings); }
});
// Aggregate privacy gate over every log and warning captured above and every recorded case result.
Case("privacy.no-account-lookup-in-any-output", () =>
{
    Require(outputs.Count > 0, "No outputs were captured.");
    Require(!outputs.Any(Leaks) && !Leaks(JsonSerializer.Serialize(checks)), "An account lookup reached a log, warning or case result.");
});

var result = new { verification = "production-map-native-sources-with-loader-game-doubles", gameExecuted = false,
    multiplayerExecuted = false, passed, failed, checks };
string serialized = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
if (Leaks(serialized)) { Console.Error.WriteLine("FAIL report contains an account lookup; not written."); return 1; }
string output = Path.GetFullPath(args[0]); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
File.WriteAllText(output, serialized);
Console.WriteLine($"{(failed == 0 ? "PASS" : "FAIL")} {passed}/{passed + failed} Map native player identity cases; no GTFO execution.");
return failed == 0 ? 0 : 1;
