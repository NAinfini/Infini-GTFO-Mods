using System.Globalization;
using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using GameData;
using HarmonyLib;
using LevelGeneration;
using Player;
using SNetwork;
using Host = ForgeRuntime.Plugin;
using MapPlugin = ForgeMap.Native.Plugin;

namespace ForgeMap.Tests.MapNativeAdapter;

// Production Map native plugin, session, hooks and player identity + compiled ForgeMap + compiled SDK.
// Native state is a managed double: every spawn, despawn and relink below is synthetic and NOT game-verified.
// Lookup values are synthetic Steam64-shaped numbers; the privacy cases prove none of them reaches any output.
public sealed class MapNativeAdapterTests
{
    // Every case starts from the fixture's own zero: the register, the patches, the agent list and the host
    // state are process-wide static, and an xUnit instance is created per case.
    public MapNativeAdapterTests() => Reset();

    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static void Throws(string? code, Action action)
    {
        try { action(); }
        catch (RuntimeContractException error) when (code != null) { Require(error.Code == code, "Expected " + code + "; got " + error.Code); return; }
        catch (Exception) when (code == null) { return; }
        throw new Exception("Expected rejection " + (code ?? "(any)"));
    }

    // Every log, warning and lookup result this suite observes; the privacy gate refuses an account lookup in any of them.
    private static readonly List<string> Outputs = new();

    private static void Reset()
    {
        var session = MapPlugin.Session;
        if (session != null)
        {
            // Fixture isolation only, after the case has recorded its production result.
            try { session.Module.Dispose(); session.Dispose(); } catch { }
            typeof(MapPlugin).GetProperty("Session", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, null);
        }
        Harmony.Reset(); PlayerManager.Reset(); SNet.IsMaster = true;
        LG_LevelBuilder.Current = null; ZoneIndex.Reset(); World = 0;
        RundownManager.Reset();
        Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = null;
    }

    // Each case gets a world of its own: the zone table is keyed by the world epoch, so a case that reused an
    // earlier epoch would keep reading the earlier case's level.
    private static long World;

    private static RuntimeKernel Kernel()
    {
        var kernel = new RuntimeKernel(new("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        kernel.BeginWorld(++World);
        // The canonical contract providers the host registers as builtins before any package loads: the Map
        // provider binds capability ids it does not own (`forge.trigger.interaction.lock_state`,
        // `forge.trigger.interaction.terminal_result`, `forge.trigger.session.expedition_ended` and the one
        // combat action the player half implements), and the runtime refuses a binding whose capability was
        // never declared. The same two calls `ForgeRuntime.GameBindings.GameRuntimeBridge` makes for the host.
        kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        return kernel;
    }

    private static RuntimeModule Other(string id, bool playerResolver) => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
        playerResolver ? new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.player"] = _ => true } : null);

    /// <summary>A module that claims the Map provider identity with nothing else: what a second Map package
    /// would look like, and the one registration the runtime refuses on the provider itself.</summary>
    private static RuntimeModule RivalMapProvider() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = ModuleDefinition.ProviderId, kind = "native", version = "9.9.9", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText(), new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());

    private static MapPluginSession Start(RuntimeKernel kernel, List<string> info, List<string> warn, Action? install = null, Action? remove = null)
        => MapPluginSession.Start(kernel, RuntimeLogLevel.Off, m => { Outputs.Add(m); warn.Add(m); }, m => { Outputs.Add(m); info.Add(m); },
            install ?? (() => { }), remove ?? (() => { }));

    private static (SNet_Player Player, PlayerAgent Agent) Spawn(ulong lookup, bool bot = false)
    {
        // A spawn takes the next free slot, and the agent and the player report the same one, exactly as the
        // two native reads do for a linked player.
        int slot = PlayerManager.PlayerAgentsInLevel.Count;
        var player = new SNet_Player { Lookup = lookup, IsBot = bot, SlotIndex = slot };
        var agent = new PlayerAgent { Owner = player, PlayerSlotIndex = slot };
        player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        PlayerManager.PlayerAgentsInLevel.Add(agent);
        return (player, agent);
    }

    private static PlayerAgent Replace(SNet_Player player, PlayerAgent old)
    {
        var agent = new PlayerAgent { Owner = player };
        var list = PlayerManager.PlayerAgentsInLevel; list[list.IndexOf(old)] = agent;
        old.Destroyed = true; player.PlayerAgent = new SNet_IPlayerAgent { Target = agent };
        return agent;
    }

    private static void Despawn(SNet_Player player, PlayerAgent agent)
    {
        PlayerManager.PlayerAgentsInLevel.Remove(agent); player.PlayerAgent = null;
    }

    // Entity IDs are per-world numbers in first-recorded order, never the account key.
    private static EntityReference Ref(long number, long world, long life) => new("gtfo.player:" + number.ToString(CultureInfo.InvariantCulture), world, life);
    private static void Read(MapPluginSession session) => session.Guard(module => module.Reconcile());

    /// <summary>Whether the identity half logged nothing about a life. The session's other halves report what they
    /// loaded while it starts, so the gate is on the lines this half owns and not on the whole channel.</summary>
    private static bool NoLifeLog(List<string> info) => !info.Any(l => l.StartsWith("map.player-", StringComparison.Ordinal));

    /// <summary>Runs one action on a thread of its own and rethrows what it threw. The pool cannot be trusted to
    /// hand out a different thread, and a wrong-thread rejection that never left the owning thread proves nothing.</summary>
    private static void OffThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.Start(); thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void Hook(Type hook) => hook.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { new PlayerManager() });
    /// <summary>Invokes a postfix with the arguments Harmony hands it: the patched instance, or the parameters
    /// the patched member itself carried.</summary>
    private static void Hook(Type hook, params object[] arguments) => hook.GetMethod("Postfix", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, arguments);
    private const ulong A = 76561198000000001, B = 76561198000000002, Bot = 4242424242;
    private static readonly ulong[] fixtureLookups = { A, B, Bot, 76561198000000003, 76561198000000004, 76561198000000005, 76561198000000010, 76561198000000011, 76561198000000012, 76561198000000013, 76561198000000099, 76561198000000100 };
    private static bool Leaks(string text) => fixtureLookups.Any(l => text.Contains(l.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal));

    private static void Privacy(string detail)
    {
        // The privacy gate fails the case that leaked, not a separate aggregate case.
        if (Outputs.Any(Leaks)) throw new Exception("An account lookup reached a log or warning: " + detail);
    }

    [Fact]
    public void module_provider_and_player_namespace_only()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new();
        using var session = Start(kernel, info, warn);
        using (var doc = JsonDocument.Parse(kernel.ExportManifest()))
        {
            var registry = doc.RootElement.GetProperty("registry");
            // The kernel's registry carries every registered provider, the canonical contract providers
            // included; this package owns exactly one of them.
            Require(registry.GetProperty("providers").EnumerateArray()
                .Count(p => p.GetProperty("id").GetString() == ModuleDefinition.ProviderId) == 1,
                "Map registered a provider count other than its one definition.");
            // The Map provider's own rows: the selector query binding the player half answers, the map-object
            // interaction rows this package observes, the session row for the end of an expedition, and the one
            // execute binding the player half implements. Every declared capability has exactly one binding, the
            // selector is the one query binding with a `targets` output, and the canonical heal action is the
            // one execution this provider declares.
            var capabilities = registry.GetProperty("capabilities").EnumerateArray()
                .Where(c => c.GetProperty("owner").GetString() == ModuleDefinition.ProviderId).ToArray();
            var bindings = registry.GetProperty("bindings").EnumerateArray().ToArray();
            var selector = capabilities.Single(c => c.GetProperty("id").GetString() == PlayerSelector.CapabilityId);
            Require(selector.GetProperty("kind").GetString() == "selector"
                && selector.GetProperty("graph").GetProperty("execution").GetString() == "query"
                && selector.GetProperty("graph").GetProperty("outputs")[0].GetProperty("id").GetString() == "targets",
                "The Map provider declared a capability other than the player selector.");
            var declared = capabilities.Select(c => c.GetProperty("id").GetString()!).ToArray();
            Require(bindings.All(b => b.GetProperty("providerId").GetString() == ModuleDefinition.ProviderId)
                && declared.All(id => bindings.Count(b => b.GetProperty("capabilityId").GetString() == id) == 1),
                "A declared capability has no binding, or a binding names a capability that was not declared.");
            // Every declared capability has its one binding, and the canonical heal action is bound here as this
            // provider's execute row: the package declares many more execute rows than the identity half's own,
            // so what is checked is the pairing and the heal row's own shape, not a row count.
            Require(bindings.Count(b => b.GetProperty("capabilityId").GetString() == PlayerHealthContract.CapabilityId
                    && b.GetProperty("role").GetString() == "execute") == 1,
                "The Map provider does not bind the canonical heal action as its one execution of that capability.");
            var selectorBinding = bindings.Single(b => b.GetProperty("id").GetString() == PlayerSelector.BindingId);
            Require(selectorBinding.GetProperty("capabilityId").GetString() == PlayerSelector.CapabilityId
                && selectorBinding.GetProperty("handler").GetString() == PlayerSelector.HandlerName
                && selectorBinding.GetProperty("role").GetString() == "observe"
                && selectorBinding.GetProperty("status").GetString() == "implemented",
                "The player selector binding is not registered as its own capability's observation.");
            // The heal row binds the canonical capability this provider does not own, with the handler the
            // native half supplies; the capability's own row is the contract provider's, so this provider must
            // not have declared a second copy of it.
            var heal = bindings.Single(b => b.GetProperty("id").GetString() == PlayerHealthContract.BindingId);
            Require(heal.GetProperty("capabilityId").GetString() == PlayerHealthContract.CapabilityId
                && heal.GetProperty("handler").GetString() == PlayerHealthContract.HandlerName
                && heal.GetProperty("role").GetString() == "execute"
                && heal.GetProperty("status").GetString() == "implemented"
                && !declared.Contains(PlayerHealthContract.CapabilityId),
                "The player heal binding is not the canonical heal action's own execution: " + heal);
            Require(doc.RootElement.GetProperty("bindingSupport").EnumerateArray()
                .Any(r => r.GetProperty("bindingId").GetString() == PlayerHealthContract.BindingId),
                "The player heal binding carries no support row.");
            Require(doc.RootElement.GetProperty("bindingSupport").EnumerateArray()
                .Single(r => r.GetProperty("bindingId").GetString() == PlayerSelector.BindingId)
                .GetProperty("bindingId").GetString() == PlayerSelector.BindingId,
                "The player selector binding carries no support row.");
        }
        Throws("entity-namespace-conflict", () => kernel.RegisterModule(Other("test.player_namespace", true), RuntimeLogLevel.Off));
        Throws("entity-instance-resolver-owner", () => kernel.RegisterModule(Other("test.player_lookup", false) with
            { EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>> { ["gtfo.player"] = _ => null } }, RuntimeLogLevel.Off));
    }

    [Fact]
    public void session_duplicate_map_provider_before_hooks()
    {
        var kernel = Kernel(); using var existing = kernel.RegisterModule(RivalMapProvider(), RuntimeLogLevel.Off);
        string before = kernel.ExportManifest(); int installs = 0, removes = 0;
        Throws(null, () => Start(kernel, new(), new(), () => installs++, () => removes++));
        Require(installs == 0 && removes == 0 && kernel.ExportManifest() == before, "Duplicate provider touched hooks or ownership.");
    }

    [Fact]
    public void session_foreign_player_namespace_before_hooks()
    {
        var kernel = Kernel(); using var other = kernel.RegisterModule(Other("test.player_owner", true), RuntimeLogLevel.Off);
        string before = kernel.ExportManifest(); int installs = 0;
        Throws("entity-namespace-conflict", () => Start(kernel, new(), new(), () => installs++));
        Require(installs == 0 && kernel.ExportManifest() == before, "Namespace conflict touched hooks or ownership.");
    }

    [Fact]
    public void session_install_failure_rolls_back()
    {
        var kernel = Kernel(); string before = kernel.ExportManifest(); int removes = 0; var primary = new IOException("patch");
        try { Start(kernel, new(), new(), () => throw primary, () => removes++); throw new Exception("Expected install failure."); }
        catch (IOException error) { Require(ReferenceEquals(error, primary), "Install failure was replaced."); }
        Require(removes == 1 && kernel.ExportManifest() == before, "Partial registration or hooks survived.");
        using var again = Start(kernel, new(), new());
        Require(again.Module.IsRegistered, "Rolled-back provider could not register again.");
    }

    [Fact]
    public void session_cleanup_failure_preserves_cause()
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
    }

    [Fact]
    public void session_registration_window()
    {
        var kernel = Kernel(); kernel.StartRuntime(() => { }); int calls = 0;
        Throws(null, () => Start(kernel, new(), new(), () => calls++, () => calls++));
        Require(calls == 0, "Late registration touched hooks.");
    }

    [Fact]
    public void identity_host_spawn_records_life_without_gameplay_gate()
    {
        // Elevator spawns happen in the same world before InLevel; the module reads no gameplay gate.
        var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { }); Spawn(A); Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 1)) && session.Module.Count == 1, "Spawned host player was not recorded.");
        Require(info.Where(l => l.StartsWith("map.player-", StringComparison.Ordinal))
                .SequenceEqual(new[] { "map.player-life-started id=gtfo.player:1 world=1 life=1 bot=false" }),
            "Life start log differs: " + string.Join(" | ", info));
        Require(warn.Count == 0, "Linked player reported a warning.");
        Privacy(nameof(identity_host_spawn_records_life_without_gameplay_gate));
    }

    [Fact]
    public void identity_requires_ready_runtime_and_master()
    {
        var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
        Spawn(A); Read(session);
        Require(session.Module.Count == 0 && NoLifeLog(info), "Recorded before Runtime was Ready.");
        kernel.StartRuntime(() => { }); SNet.IsMaster = false; Read(session);
        Require(session.Module.Count == 0 && NoLifeLog(info), "A client allocated a player life.");
        SNet.IsMaster = true; Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 1)), "Gated readbacks consumed an entity number or life epoch.");
    }

    [Fact]
    public void identity_repeat_readback_keeps_life()
    {
        // Downed, revive and heal do not replace the agent in this model; repeated readbacks must not allocate.
        var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
        kernel.StartRuntime(() => { }); Spawn(A); Read(session); Read(session);
        var other = Spawn(B); Read(session); Despawn(other.Player, other.Agent); Read(session); Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 1)) && info.Count(l => l.Contains("id=gtfo.player:1 ", StringComparison.Ordinal)) == 1,
            "Repeated readbacks changed an unchanged life.");
    }

    [Fact]
    public void identity_new_agent_instance_is_new_life()
    {
        var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
        kernel.StartRuntime(() => { }); var a = Spawn(A); Read(session);
        Replace(a.Player, a.Agent);
        Require(!session.Module.IsCurrent(Ref(1, 1, 1)), "Old life stayed current after its agent was replaced.");
        Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 2)) && !session.Module.IsCurrent(Ref(1, 1, 1)), "Replacement agent did not allocate a new life.");
        Require(info.Contains("map.player-life-ended id=gtfo.player:1 world=1 life=1 reason=replaced")
            && info.Contains("map.player-life-started id=gtfo.player:1 world=1 life=2 bot=false"), "Replacement logs differ: " + string.Join(" | ", info));
    }

    [Fact]
    public void identity_despawn_retires_and_respawn_keeps_number_with_new_life()
    {
        var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
        kernel.StartRuntime(() => { }); var a = Spawn(A); Spawn(B); Read(session);
        Despawn(a.Player, a.Agent); Read(session);
        Require(session.Module.Count == 1 && !session.Module.IsCurrent(Ref(1, 1, 1)) && session.Module.IsCurrent(Ref(2, 1, 2))
            && info.Contains("map.player-life-ended id=gtfo.player:1 world=1 life=1 reason=despawned"), "Despawn did not retire exactly that life.");
        var again = new PlayerAgent { Owner = a.Player }; a.Player.PlayerAgent = new SNet_IPlayerAgent { Target = again };
        PlayerManager.PlayerAgentsInLevel.Add(again); Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 3)) && !session.Module.IsCurrent(Ref(1, 1, 1)), "Respawn reused a life or changed the per-world number.");
    }

    [Fact]
    public void identity_world_change_clears_and_renumbers()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new());
        kernel.StartRuntime(() => { }); Spawn(B); Spawn(A); Read(session);
        kernel.BeginWorld(2);
        Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)) && !session.Module.IsCurrent(Ref(2, 1, 2)),
            "World change did not clear player lives.");
        PlayerManager.PlayerAgentsInLevel.Reverse(); Read(session);
        Require(session.Module.IsCurrent(Ref(1, 2, 3)) && session.Module.IsCurrent(Ref(2, 2, 4)) && !session.Module.IsCurrent(Ref(1, 1, 1)),
            "New world did not allocate fresh numbers and lives.");
    }

    [Fact]
    public void identity_runtime_stop_clears_and_stops_recording()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new());
        kernel.StartRuntime(() => { }); Spawn(A); Read(session);
        kernel.StopRuntime();
        Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)), "Stop left a player life current.");
        session.Module.Reconcile(); Require(session.Module.Count == 0, "Stopped Runtime recorded a player life.");
    }

    /// <summary>The level identity the `level` mount compares against is read from the game's own expedition:
    /// the rundown block it loaded plus the active tier and zero-based index, in the reference spelling, with
    /// the game's own key logged beside it. Both members are read once per level, and the reader answers null
    /// rather than throwing when the game cannot name the active expedition.</summary>
    [Fact]
    public void level_identity_reads_the_active_expedition_and_maps_its_tier()
    {
        List<string> info = new();
        Globals.Global.RundownIdToLoad = 31; RundownManager.ActiveExpeditionUniqueKey = "Local_31_TierA_0";
        RundownManager.Active = new pActiveExpedition { tier = eRundownTier.TierA, expeditionIndex = 0 };
        Require(LevelIdentity.Read(info.Add) == MapLevelReference.TryParse("31:A:0"), "The active expedition did not read as `31:A:0`.");
        Require(info.Count == 1 && info[0].EndsWith("activeKey=Local_31_TierA_0", StringComparison.Ordinal)
            && info[0].Contains("31:A:0", StringComparison.Ordinal), "The level read was not recorded with the game's own key: " + string.Join(" | ", info));

        // Every tier letter is the game's own ordinal, and the index is the one the expedition reports.
        foreach (var (tier, letter) in new[] { (eRundownTier.TierB, 'B'), (eRundownTier.TierC, 'C'), (eRundownTier.TierD, 'D'), (eRundownTier.TierE, 'E') })
        {
            RundownManager.Active = new pActiveExpedition { tier = tier, expeditionIndex = 2 };
            Require(LevelIdentity.Read(info.Add) == MapLevelReference.TryParse("31:" + letter + ":2"),
                "Tier " + tier + " index 2 did not read as `31:" + letter + ":2`.");
        }

        // A tier outside the five letters, and a read the game cannot serve, both answer null.
        RundownManager.Active = new pActiveExpedition { tier = eRundownTier.Surface, expeditionIndex = 0 };
        Require(LevelIdentity.Read(info.Add) == null, "A surface tier named a level identity.");
        RundownManager.ThrowOnRead = true;
        Require(LevelIdentity.Read(info.Add) == null && info[^1].Contains("<unreadable:", StringComparison.Ordinal),
            "An unreadable expedition did not answer null with a bounded record: " + info[^1]);
    }

    [Fact]
    public void identity_bot_follows_same_rules()
    {
        var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
        kernel.StartRuntime(() => { }); var bot = Spawn(Bot, bot: true); Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 1)) && info.Contains("map.player-life-started id=gtfo.player:1 world=1 life=1 bot=true"), "Bot was not recorded like a player.");
        Replace(bot.Player, bot.Agent); Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 2)) && !session.Module.IsCurrent(Ref(1, 1, 1)), "Bot replacement did not follow player life rules.");
    }

    [Fact]
    public void identity_late_joiner_does_not_touch_existing_lives()
    {
        var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
        kernel.StartRuntime(() => { }); Spawn(A); Read(session); Spawn(B); Read(session);
        Require(session.Module.IsCurrent(Ref(1, 1, 1)) && session.Module.IsCurrent(Ref(2, 1, 2))
            && !info.Any(l => l.StartsWith("map.player-life-ended", StringComparison.Ordinal)), "Late joiner changed an existing life.");
    }

    [Fact]
    public void identity_unresolved_owner_stays_unrecorded()
    {
        var kernel = Kernel(); List<string> warn = new(); using var session = Start(kernel, new(), warn);
        kernel.StartRuntime(() => { });
        PlayerManager.PlayerAgentsInLevel.Add(new PlayerAgent { Owner = null });
        var stray = Spawn(B); var elsewhere = new PlayerAgent { Owner = stray.Player }; stray.Player.PlayerAgent = new SNet_IPlayerAgent { Target = elsewhere };
        Read(session); Read(session);
        Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)), "An unresolved or unlinked owner was recorded.");
        Require(warn.Count == 2 && warn.All(w => w.StartsWith("map.player-owner-unresolved", StringComparison.Ordinal)), "Unresolved owners were not reported exactly once each.");
    }

    [Fact]
    public void identity_duplicate_key_records_neither()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { }); Spawn(A); Read(session); Spawn(A); Read(session); Read(session);
        Require(session.Module.Count == 0 && !session.Module.IsCurrent(Ref(1, 1, 1)) && !session.Module.IsCurrent(Ref(1, 1, 2))
            && info.Contains("map.player-life-ended id=gtfo.player:1 world=1 life=1 reason=key-conflict"), "Conflicting key kept a life.");
        Require(warn.Count(w => w.StartsWith("map.player-key-conflict", StringComparison.Ordinal)) == 1, "Key conflict was not reported exactly once.");
    }

    [Fact]
    public void identity_current_check_rereads_native_links()
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
    }

    [Fact]
    public void identity_forged_references_rejected()
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
    }

    [Fact]
    public void identity_wrong_thread_rejected()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        Spawn(A); Read(session);
        Throws("wrong-thread", () => OffThread(() => session.Module.IsCurrent(Ref(1, 1, 1))));
        Throws("wrong-thread", () => OffThread(session.Module.Reconcile));
        Require(session.Module.IsCurrent(Ref(1, 1, 1)), "Off-thread access changed identity state.");
    }

    [Fact]
    public void instance_sdk_lookup_returns_recorded_life()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { }); var a = Spawn(A); var bot = Spawn(Bot, bot: true); Read(session);
        int logs = info.Count + warn.Count;
        Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1)
            && kernel.ResolveEntityInstance("gtfo.player", bot.Player) == Ref(2, 1, 2), "SDK lookup did not return the recorded lives.");
        Require(kernel.IsEntityCurrent(Ref(1, 1, 1)) && info.Count + warn.Count == logs && session.Module.Count == 2,
            "Lookup logged or changed identity state.");
    }

    [Fact]
    public void instance_lookup_never_allocates_a_life()
    {
        var kernel = Kernel(); List<string> info = new(); using var session = Start(kernel, info, new());
        kernel.StartRuntime(() => { }); var a = Spawn(A);
        Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == null && session.Module.Count == 0 && NoLifeLog(info),
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
    }

    [Fact]
    public void instance_observe_gate_and_native_type()
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
    }

    [Fact]
    public void instance_lookup_rereads_native_links()
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
    }

    [Fact]
    public void instance_stop_and_wrong_thread()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        var a = Spawn(A); Read(session);
        Throws("wrong-thread", () => OffThread(() => session.Module.ResolveInstance(a.Player)));
        Throws("wrong-thread", () => OffThread(() => kernel.ResolveEntityInstance("gtfo.player", a.Player)));
        Require(kernel.ResolveEntityInstance("gtfo.player", a.Player) == Ref(1, 1, 1), "Off-thread lookup changed identity state.");
        kernel.StopRuntime();
        Require(session.Module.ResolveInstance(a.Player) == null, "Stopped Map resolved a player.");
        Throws("runtime-not-ready", () => kernel.ResolveEntityInstance("gtfo.player", a.Player));
    }

    [Fact]
    public void observation_reports_position_health_life_and_slot()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        var a = Spawn(A); var bot = Spawn(Bot, bot: true); var fallen = Spawn(B);
        a.Agent.Position = new UnityEngine.Vector3(1.5f, -2.25f, 3f); a.Player.SlotIndex = 2; a.Agent.PlayerSlotIndex = 2;
        a.Player.IsLocal = true; a.Player.IsMaster = true;
        a.Agent.Damage = new Dam_PlayerDamageBase { Owner = a.Agent, IsSetup = true, Health = 75.5f, HealthMax = 100f };
        bot.Agent.Position = new UnityEngine.Vector3(-4f, 0f, 0.5f); bot.Player.SlotIndex = 3; bot.Agent.PlayerSlotIndex = 3;
        bot.Agent.Alive = false;
        fallen.Agent.Position = new UnityEngine.Vector3(8f, 1f, -8f);
        fallen.Agent.Locomotion = new PlayerLocomotion { m_currentStateEnum = PlayerLocomotion.PLOC_State.Downed };
        fallen.Agent.Damage = new Dam_PlayerDamageBase { Owner = fallen.Agent, IsSetup = true, Health = 12f, HealthMax = 40f };
        Read(session);

        var query = kernel.InspectEntities(new[] { Ref(1, 1, 1), Ref(2, 1, 2), Ref(3, 1, 3) });
        Require(query.IsComplete && query.Code == "entities-observed" && query.Items.Count == 3, "Observation was not complete: " + query.Code);
        var living = query.Items[0].Snapshot!;
        Require(living.Kind == "gtfo.player" && living.Faction == "player" && living.LifeState == "alive"
            && living.Position.SequenceEqual(new[] { 1.5, -2.25, 3.0 }), "Living player snapshot differs.");
        Require(living.Tags.SequenceEqual(new[] { "player.local", "player.host", "player.slot-2", "hp.75.50", "hp.max.100.00" }),
            "Living player tags differ: " + string.Join(",", living.Tags));
        // The one recipient capability this provider serves for a player, advertised exactly where the health
        // reader can also serve it: a verified receiver on a living agent. A dead player and a destroyed or
        // foreign receiver advertise nothing instead of claiming a heal they would have to refuse.
        Require(living.Receives.SequenceEqual(new[] { "health.heal" }),
            "Living player observation did not advertise the one recipient capability it serves: " + string.Join(",", living.Receives));
        var dead = query.Items[1].Snapshot!;
        Require(dead.LifeState == "dead" && dead.Tags.SequenceEqual(new[] { "player.bot", "player.slot-3" })
            && !dead.Tags.Any(tag => tag.StartsWith("hp", StringComparison.Ordinal))
            && dead.Receives.Count == 0, "Dead player snapshot differs: " + string.Join(",", dead.Tags));
        var downed = query.Items[2].Snapshot!;
        Require(downed.LifeState == "downed" && downed.Tags.SequenceEqual(new[] { "player.slot-2", "hp.12.00", "hp.max.40.00" })
            && downed.Position.SequenceEqual(new[] { 8.0, 1.0, -8.0 })
            // Downed is alive in the native health model, so the receiver is still readable and the heal is
            // still accepted; this observer never claims it revives anyone.
            && downed.Receives.SequenceEqual(new[] { "health.heal" }), "Downed player snapshot differs: " + string.Join(",", downed.Tags));
    }

    [Fact]
    public void observation_refuses_stale_identity_and_missing_health_receiver()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        var a = Spawn(A); Read(session);
        a.Agent.Damage = new Dam_PlayerDamageBase { Owner = a.Agent, IsSetup = true, Health = 10f, HealthMax = 10f };
        Require(kernel.InspectEntities(new[] { Ref(1, 1, 1) }).IsComplete, "Fixture player was not observable.");
        // A health receiver that does not belong to this agent, or an impossible range, is not reported at all.
        a.Agent.Damage.Owner = null;
        Require(kernel.InspectEntities(new[] { Ref(1, 1, 1) }).Items[0].Snapshot!.Tags.SequenceEqual(new[] { "player.slot-0" }),
            "A foreign health receiver was reported as readable health.");
        a.Agent.Damage.Owner = a.Agent; a.Agent.Damage.Health = 99f;
        Require(kernel.InspectEntities(new[] { Ref(1, 1, 1) }).Items[0].Snapshot!.Tags.SequenceEqual(new[] { "player.slot-0" }),
            "Health above the maximum was reported.");
        // The player's own slot and the agent's slot are two native reads: a disagreement is not a snapshot.
        a.Agent.Damage.Health = 10f; a.Agent.PlayerSlotIndex = 1;
        Require(!kernel.InspectEntities(new[] { Ref(1, 1, 1) }).IsComplete, "A slot disagreement was observed.");
        a.Agent.PlayerSlotIndex = 0;

        Despawn(a.Player, a.Agent); Read(session);
        var gone = kernel.InspectEntities(new[] { Ref(1, 1, 1) });
        Require(!gone.IsComplete && gone.Items[0].Code == "stale-entity" && gone.Items[0].Snapshot == null,
            "A despawned player identity was observed: " + gone.Items[0].Code);

        var live = Spawn(A); Read(session);
        var replaced = Replace(live.Player, live.Agent); Read(session);
        var stale = kernel.InspectEntities(new[] { Ref(1, 1, 1) });
        Require(!stale.IsComplete && stale.Items[0].Code == "stale-entity" && stale.Items[0].Snapshot == null,
            "A replaced life was observed: " + stale.Items[0].Code);
        replaced.Damage = new Dam_PlayerDamageBase { Owner = replaced, IsSetup = true, Health = 5f, HealthMax = 5f };
        // The despawned life, the respawned one and the replacement are three lives of the same player number.
        Require(kernel.InspectEntities(new[] { Ref(1, 1, 3) }).IsComplete, "Replacement life was not observable: " + replaced.Pointer);
        var forged = kernel.InspectEntities(new[] { new EntityReference("gtfo.player:999", 1, 1) });
        Require(!forged.IsComplete && forged.Items[0].Code == "stale-entity", "A forged player reference was observed: " + forged.Items[0].Code);
    }

    [Fact]
    public void player_candidates_are_every_current_life_in_id_order()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        // Every fixture lookup but the last, which the destroyed-agent case below spawns: eleven lives, so the
        // `id` ordinal order of `gtfo.player:<n>` is not the order the lives were minted in —
        // `gtfo.player:10` sorts between `:1` and `:2`, and the source answer must show that.
        for (int i = 0; i < fixtureLookups.Length - 1; i++) Spawn(fixtureLookups[i], bot: i == 2);
        Read(session);
        var candidates = kernel.EnumerateEntityCandidates(PlayerIdentityModule.EntityKind);
        Require(candidates.Status == "complete" && candidates.Code == "entities-enumerated" && candidates.Items.Count == 11,
            "The player candidate source did not answer the recorded lives: " + candidates.Status + ":" + candidates.Code + " n=" + candidates.Items.Count);
        var expected = new[] { 1, 10, 11, 2, 3, 4, 5, 6, 7, 8, 9 }.Select(number => Ref(number, 1, number)).ToArray();
        Require(candidates.Items.Select(item => item.Reference).SequenceEqual(expected),
            "The candidate set was not the recorded lives in id order: " + string.Join(",", candidates.Items.Select(item => item.Reference.Id)));
        Require(!Leaks(string.Join("|", candidates.Items.Select(item => item.Reference.Id))),
            "A candidate id carried an account lookup.");
        // Identity is the source's gate: a destroyed agent is not enumerated even before the next readback.
        var gone = Spawn(fixtureLookups[^1]); Read(session);
        Require(session.Module.Count == 12, "Fixture life was not recorded.");
        gone.Agent.Destroyed = true;
        Require(kernel.EnumerateEntityCandidates(PlayerIdentityModule.EntityKind).Items.Select(item => item.Reference.Id)
            .SequenceEqual(expected.Select(reference => reference.Id)),
            "A destroyed player life stayed in the candidate set.");
    }

    [Fact]
    public void player_candidates_answer_empty_when_the_world_holds_no_life()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        Spawn(A); Read(session);
        Require(kernel.EnumerateEntityCandidates(PlayerIdentityModule.EntityKind).Items.Count == 1, "The recorded life was not enumerated.");
        // A world change clears the module's table. The kind is still exposed, so the answer is an empty set —
        // never the refusal a source that cannot be read reports.
        kernel.BeginWorld(2);
        var cleared = kernel.EnumerateEntityCandidates(PlayerIdentityModule.EntityKind);
        Require(cleared.Status == "complete" && cleared.Code == "entities-enumerated" && cleared.Items.Count == 0,
            "An empty player set was not answered as an empty set: " + cleared.Status + ":" + cleared.Code);
    }

    [Fact]
    public void player_candidates_report_a_released_module_as_a_failed_read()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        Spawn(A); Read(session);
        session.Module.Dispose();
        // The module is gone but the kind's owner is still registered: the source refuses by name and the kernel
        // reports it, so a step never reads the released module's empty table as "there are no players".
        var answer = kernel.EnumerateEntityCandidates(PlayerIdentityModule.EntityKind);
        Require(answer.Status == "rejected" && answer.Code == "entity-candidates-failed" && answer.Items.Count == 0,
            "A released player module answered a candidate read: " + answer.Status + ":" + answer.Code);
    }

    [Fact]
    public void observation_query_budget_is_explicit()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        Spawn(A); Spawn(B); Read(session);
        var references = new[] { Ref(1, 1, 1), Ref(2, 1, 2) };
        for (int i = 0; i < 64; i++)
            Require(kernel.InspectEntities(references).IsComplete, "Query budget ran out early at " + i + ".");
        var exceeded = kernel.InspectEntities(references);
        Require(exceeded.Status == "rejected" && exceeded.Code == "entity-query-tick-budget" && exceeded.Items.Count == 0,
            "Per-tick query budget was not reported explicitly: " + exceeded.Code);
        var oversized = new EntityReference[RuntimeKernel.MaximumEntityReferencesPerQuery + 1];
        Array.Fill(oversized, Ref(1, 1, 1));
        Require(kernel.InspectEntities(oversized).Code == "entity-query-budget", "Per-query reference budget was not enforced.");
    }

    [Fact]
    public void privacy_instance_lookup_results_and_errors_exclude_account_lookup()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn); kernel.StartRuntime(() => { });
        var a = Spawn(A); var bot = Spawn(Bot, bot: true); Read(session);
        var texts = new List<string>();
        foreach (var player in new[] { a.Player, bot.Player, new SNet_Player { Lookup = B } })
            texts.Add(kernel.ResolveEntityInstance("gtfo.player", player)?.ToString() ?? "null");
        void Capture(Action action) { try { action(); texts.Add("no-error"); } catch (Exception error) { texts.Add(error.ToString()); } }
        Capture(() => OffThread(() => session.Module.ResolveInstance(a.Player)));
        Capture(() => OffThread(() => kernel.ResolveEntityInstance("gtfo.player", a.Player)));
        kernel.StopRuntime(); Capture(() => kernel.ResolveEntityInstance("gtfo.player", a.Player));
        Require(texts.Count == 6 && texts[0].Contains("gtfo.player:1", StringComparison.Ordinal) && texts[2] == "null"
            && texts.Skip(3).All(t => t.Contains("RuntimeContractException", StringComparison.Ordinal)), "Scenario differs: " + texts.Count);
        Outputs.AddRange(texts);
        Require(!texts.Concat(info).Concat(warn).Any(Leaks), "An account lookup reached an instance lookup result, error or log.");
    }

    [Fact]
    public void privacy_entity_ids_logs_and_manifest_exclude_account_lookup()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { });
        var a = Spawn(A); Spawn(Bot, bot: true); Read(session);
        Replace(a.Player, a.Agent); Read(session);
        Spawn(B); Spawn(B); PlayerManager.PlayerAgentsInLevel.Add(new PlayerAgent { Owner = null }); Read(session);
        var ids = info.SelectMany(l => l.Split(' ')).Where(t => t.StartsWith("id=", StringComparison.Ordinal))
            .Distinct().ToArray();
        Require(ids.Length > 0 && ids.All(id => id is "id=gtfo.player:1" or "id=gtfo.player:2"), "Entity IDs are not per-world numbers: " + ids.Length);
        Require(session.Module.IsCurrent(Ref(1, 1, 3)) && session.Module.IsCurrent(Ref(2, 1, 2)), "Scenario lives differ.");
        Require(warn.Count == 2, "Scenario warnings differ.");
        Require(!info.Concat(warn).Append(kernel.ExportManifest()).Any(Leaks), "An account lookup reached an entity ID, log, warning or manifest.");
    }

    [Fact]
    public void session_native_fault_latches_and_clears()
    {
        var kernel = Kernel(); List<string> warn = new(); using var session = Start(kernel, new(), warn); kernel.StartRuntime(() => { });
        Spawn(A); Read(session); PlayerManager.ThrowOnRead = true; Read(session);
        Require(session.Faulted && !session.Module.IsCurrent(Ref(1, 1, 1)) && warn.Count == 1
            && warn[0].StartsWith("Map player identity disabled until restart", StringComparison.Ordinal), "Native read failure did not latch and clear.");
        PlayerManager.ThrowOnRead = false; Read(session);
        Require(session.Module.Count == 0 && kernel.StartupState == RuntimeStartupState.Ready, "Faulted session recorded again or stopped Runtime.");
    }

    [Fact]
    public void session_reporter_failure_cannot_escape()
    {
        var kernel = Kernel();
        using var session = MapPluginSession.Start(kernel, RuntimeLogLevel.Off, _ => throw new IOException("reporter"), _ => { }, () => { }, () => { });
        kernel.StartRuntime(() => { }); session.Guard(_ => throw new InvalidOperationException("native callback"));
        Require(session.Faulted && session.LastReporterFailure == "IOException", "Reporter failure escaped the guard.");
    }

    [Fact]
    public void session_fault_diagnostic_is_bounded_and_once()
    {
        List<string> warn = new(); using var session = Start(Kernel(), new(), warn);
        session.Guard(_ => throw new Exception(new string('x', 20000))); session.Guard(_ => throw new Exception("must not run"));
        Require(session.LastFault?.Length <= 2048 && warn.Count == 1 && warn[0].Length < 4096, "Unbounded or repeated failure reporting.");
    }

    [Fact]
    public void session_wrong_thread_dispose_keeps_ownership()
    {
        var kernel = Kernel(); int removes = 0; var session = Start(kernel, new(), new(), remove: () => removes++);
        try
        {
            Throws(null, () => OffThread(session.Dispose));
            Require(removes == 0 && session.Module.IsRegistered && !session.Faulted, "Off-thread Dispose changed ownership.");
            session.Dispose(); Require(removes == 1 && !session.Module.IsRegistered, "Legal cleanup could not be retried.");
        }
        finally { session.Dispose(); }
    }

    [Fact]
    public void session_dispose_unregisters_before_unpatch()
    {
        var kernel = Kernel(); MapPluginSession? session = null; bool registeredAtUnpatch = true; int removes = 0;
        session = Start(kernel, new(), new(), remove: () => { removes++; registeredAtUnpatch = session!.Module.IsRegistered; });
        session.Dispose(); session.Dispose();
        using var doc = JsonDocument.Parse(kernel.ExportManifest());
        // The kernel keeps the canonical contract providers this fixture registered; the Map provider is the one
        // the session must have taken with it.
        Require(removes == 1 && !registeredAtUnpatch
            && doc.RootElement.GetProperty("registry").GetProperty("providers").EnumerateArray()
                .All(p => p.GetProperty("id").GetString() != ModuleDefinition.ProviderId),
            "Dispose unpatched before unregistering or left the provider.");
    }

    [Fact]
    public void session_unpatch_failure_remains_inert()
    {
        var kernel = Kernel(); int removes = 0, callbacks = 0;
        var session = Start(kernel, new(), new(), remove: () => { removes++; throw new IOException("native cleanup"); });
        Throws(null, session.Dispose); session.Guard(_ => callbacks++); session.Dispose();
        Require(removes == 1 && callbacks == 0 && !session.Module.IsRegistered && session.Faulted, "Failed unpatch left executable ownership.");
    }

    [Fact]
    public void hooks_exact_set_and_no_session_noop()
    {
        Require(MapNativeHooks.Types.SequenceEqual(new[]
        {
            typeof(PlayerSpawnedReadback), typeof(PlayerDespawnedReadback),
            typeof(DoorStateReadback), typeof(TerminalStateReadback),
            typeof(ExpeditionEndedReadback),
            typeof(DownedStateReadback), typeof(SyncedDownedStateReadback), typeof(RevivedStateReadback),
            typeof(ReviveInteractionReadback), typeof(PlayerDiedReadback), typeof(PlayerWarpedReadback),
            typeof(PlayerRespawnedReadback),
            typeof(PlayerDamageAccepted), typeof(PlayerInfectionWritten),
            typeof(PlayerBulletDamageKind), typeof(PlayerProjectileDamageKind), typeof(PlayerMeleeDamageKind),
            typeof(PlayerExplosionDamageKind), typeof(PlayerFallDamageKind), typeof(PlayerFireDamageKind),
            typeof(PlayerStickyDamageKind), typeof(PlayerParasiteDamageKind), typeof(PlayerPushDamageKind),
            typeof(PlayerGameEventPosted), typeof(PlayerPingMarkerSet),
            typeof(TerminalCommandReadbackFixed), typeof(WeakLockBrokenReadback), typeof(WeakDoorAttackedReadback),
            typeof(WeakDoorBrokenReadback),
            typeof(ScanProgressReadback), typeof(ScanStateReadback), typeof(GeneratorCellReadback),
            typeof(GeneratorClusterStateReadback), typeof(ResourceContainerStateReadback), typeof(ItemPickupReadback),
            typeof(ExpeditionStartedReadback), typeof(ObjectiveStatusReadback), typeof(ReactorWaveReadback),
            typeof(HsuSampledReadback), typeof(CheckpointRestoredReadback),
            typeof(ZoneEnteredReadback), typeof(PortalWarpedReadback),
            typeof(TriggerZoneTick),
            typeof(TeammateOverheadRender), typeof(TeammateOverheadRemoved), typeof(TeammateOverheadVisibility)
        }), "Unexpected hook set.");
        Spawn(A); Hook(typeof(PlayerSpawnedReadback)); Hook(typeof(PlayerDespawnedReadback));
    }

    [Fact]
    public void zone_resolver_answers_the_zone_a_players_course_node_belongs_to()
    {
        var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
        try
        {
            plugin.Load();
            var session = MapPlugin.Session!;
            kernel.StartRuntime(() => { });
            var world = new SyntheticLevel(kernel);
            var start = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_0);
            var upper = world.Zone(1, LG_LayerType.SecondaryLayer, eLocalZoneIndex.Zone_3);
            var a = Spawn(A); a.Agent.CourseNode = new AIGraph.AIG_CourseNode { m_zone = upper };
            Read(session);
            var reference = kernel.ResolveEntityInstance("gtfo.player", a.Player);
            Require(reference != null, "The spawned player life was not recorded.");

            var answer = kernel.ZoneOfEntity(reference!);
            Require(answer.Answered && answer.Code == EntityZoneResolution.InZoneCode
                && answer.Zone?.Id == "gtfo.zone:1:1:3",
                "The zone of the player's own course node was not answered: " + answer.Code + " " + answer.Zone?.Id);

            // The same world answered the other zone as its own address, so the answer is the node's zone and
            // not a constant.
            a.Agent.CourseNode = new AIGraph.AIG_CourseNode { m_zone = start };
            var moved = kernel.ZoneOfEntity(reference!);
            Require(moved.Answered && moved.Zone?.Id == "gtfo.zone:0:0:0",
                "A player moved to the level's start zone was not answered there: " + moved.Code + " " + moved.Zone?.Id);
        }
        finally { Outputs.AddRange(plugin.Log.Infos); Outputs.AddRange(plugin.Log.Warnings); Privacy(nameof(zone_resolver_answers_the_zone_a_players_course_node_belongs_to)); }
    }

    [Fact]
    public void zone_resolver_answers_outside_when_the_level_holds_no_zone_for_the_node()
    {
        var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
        try
        {
            plugin.Load();
            var session = MapPlugin.Session!;
            kernel.StartRuntime(() => { });
            var world = new SyntheticLevel(kernel);
            var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1);
            var a = Spawn(A); a.Agent.CourseNode = new AIGraph.AIG_CourseNode { m_zone = zone };
            Read(session);
            var reference = kernel.ResolveEntityInstance("gtfo.player", a.Player);
            Require(reference != null, "The spawned player life was not recorded.");

            // A zone the level no longer holds is a placement this provider cannot make, which is the answer the
            // kernel reports as "outside every zone" and not as a refusal: a selector can exclude it by name.
            var absent = new LG_Zone { m_layer = new LG_Layer { m_type = LG_LayerType.MainLayer }, LocalIndex = eLocalZoneIndex.Zone_5 };
            a.Agent.CourseNode = new AIGraph.AIG_CourseNode { m_zone = absent };
            var answer = kernel.ZoneOfEntity(reference!);
            Require(answer.Answered && answer.Zone == null && answer.Code == EntityZoneResolution.OutsideCode,
                "A zone the level does not hold was not answered as outside: " + answer.Code);
        }
        finally { Outputs.AddRange(plugin.Log.Infos); Outputs.AddRange(plugin.Log.Warnings); Privacy(nameof(zone_resolver_answers_outside_when_the_level_holds_no_zone_for_the_node)); }
    }

    [Fact]
    public void zone_resolver_refuses_a_world_that_holds_no_zone_table()
    {
        var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
        try
        {
            plugin.Load();
            var session = MapPlugin.Session!;
            kernel.StartRuntime(() => { });
            var a = Spawn(A); a.Agent.CourseNode = new AIGraph.AIG_CourseNode();
            Read(session);
            var reference = kernel.ResolveEntityInstance("gtfo.player", a.Player);
            Require(reference != null, "The spawned player life was not recorded.");

            // No level was built at all, so there is no zone table to read: the read did not happen, and it is
            // refused in the provider's own words rather than answered as a player standing nowhere.
            LG_LevelBuilder.Current = null;
            var answer = kernel.ZoneOfEntity(reference!);
            Require(!answer.Answered && answer.Zone == null && answer.Code == ZoneSelectorContract.AnchorUnavailableCode,
                "A world with no zone table was not refused by its own code: " + answer.Code);
        }
        finally { Outputs.AddRange(plugin.Log.Infos); Outputs.AddRange(plugin.Log.Warnings); Privacy(nameof(zone_resolver_refuses_a_world_that_holds_no_zone_table)); }
    }

    [Fact]
    public void zone_resolver_refuses_a_life_whose_course_node_does_not_read()
    {
        var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
        try
        {
            plugin.Load();
            var session = MapPlugin.Session!;
            kernel.StartRuntime(() => { });
            var world = new SyntheticLevel(kernel);
            var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_0);
            var a = Spawn(A); a.Agent.CourseNode = new AIGraph.AIG_CourseNode { m_zone = zone };
            Read(session);
            var reference = kernel.ResolveEntityInstance("gtfo.player", a.Player);
            Require(reference != null, "The spawned player life was not recorded.");

            // A life with no course node at all, and one whose node cannot be read, are the same failure: the
            // node is what places the life, so neither is answered as a player standing in no zone.
            a.Agent.CourseNode = null;
            var missing = kernel.ZoneOfEntity(reference!);
            Require(!missing.Answered && missing.Code == ZoneSelectorContract.AnchorNodeMissingCode,
                "A life with no course node was not refused: " + missing.Code);

            a.Agent.CourseNode = new ThrowingCourseNode();
            var unreadable = kernel.ZoneOfEntity(reference!);
            Require(!unreadable.Answered && unreadable.Code == ZoneSelectorContract.AnchorNodeMissingCode,
                "A course node that did not read was not refused: " + unreadable.Code);
        }
        finally { Outputs.AddRange(plugin.Log.Infos); Outputs.AddRange(plugin.Log.Warnings); Privacy(nameof(zone_resolver_refuses_a_life_whose_course_node_does_not_read)); }
    }

    /// <summary>A course node whose own zone read fails: the game's node that was torn down under a live life
    /// reads exactly this way, and the provider has to refuse the read rather than answer "no zone".</summary>
    private sealed class ThrowingCourseNode : AIGraph.AIG_CourseNode
    {
        internal ThrowingCourseNode() => ZoneThrows = true;
    }

    /// <summary>The expedition readback: the game's own end state reaches the session half, which hands it to the
    /// kernel, and a client's identical callback stops at the hook. No plan subscribes here, so a host fact is
    /// ignored by the kernel: what the case proves is that the doubled native callback is accepted, that the
    /// state's own value is what travels (not a name or a boolean), and that a client publishes nothing at all.
    /// The dispatch and dedup answers are asserted where a plan consumes them, in the managed observation suite.
    [Fact]
    public void expedition_ended_readback_publishes_for_the_host_only()
    {
        var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
        try
        {
            plugin.Load();
            var session = MapPlugin.Session!;
            kernel.StartRuntime(() => { });
            var mapObjects = session.MapObjects!;
            Hook(typeof(ExpeditionEndedReadback), ExpeditionEndState.Success);
            Require(session.LastFault == null && plugin.Log.Warnings.Count == 0 && mapObjects.Expeditions.PublishedFacts == 0,
                "The expedition readback refused a readable end state, or queued a fact no plan consumes. warnings="
                + string.Join(" | ", plugin.Log.Warnings));
            SNet.IsMaster = false;
            Hook(typeof(ExpeditionEndedReadback), ExpeditionEndState.Abort);
            Require(session.LastFault == null && plugin.Log.Warnings.Count == 0 && mapObjects.Expeditions.PublishedFacts == 0,
                "A client published an expedition fact. warnings=" + string.Join(" | ", plugin.Log.Warnings));
        }
        finally { Outputs.AddRange(plugin.Log.Infos); Outputs.AddRange(plugin.Log.Warnings); Privacy(nameof(expedition_ended_readback_publishes_for_the_host_only)); }
    }

    [Fact]
    public void map_object_readbacks_read_the_doubled_natives_and_publish_for_the_host_only()
    {
        var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
        try
        {
            plugin.Load();
            var session = MapPlugin.Session!;
            kernel.StartRuntime(() => { });
            // A played level: three zones across two layers, each zone entered through its own source gate, and
            // one terminal in the start zone. The addresses are read from that zone list.
            var world = new SyntheticLevel(kernel);
            var start = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_0);
            var middle = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_4);
            var upper = world.Zone(0, LG_LayerType.SecondaryLayer, eLocalZoneIndex.Zone_1);
            var door = world.Entrance(middle, eDoorStatus.Closed);
            var terminal = world.Terminal(start, TERM_State.Awake);
            Require(kernel.ResolveEntityInstance("gtfo.map_object", door)?.Id == "gtfo.map_object:door/0/0/4/security",
                "The door's own entrance zone did not address it.");
            Require(kernel.ResolveEntityInstance("gtfo.map_object", terminal)?.Id == "gtfo.map_object:terminal/0/0/0/0",
                "The terminal's own zone and placement did not address it.");
            var locks = door.m_locks!.TryCast<LG_SecurityDoor_Locks>()!;
            // One lock callback is one door status and one lock reading, the terminal's own state callback reads
            // the terminal, and the command entry names the command. No plan subscribes in this case, so a host
            // fact is handed to the kernel and ignored there: what these hooks prove is that the production
            // readers accept the doubled natives — a refused instance or a failed read is reported, and a throw
            // disables the session, so a clean run is the assertion. The one-fact-per-state-change counts are
            // asserted where a plan consumes them, in the managed observation suite.
            Hook(typeof(DoorStateReadback), locks, default(pDoorState));
            Hook(typeof(DoorStateReadback), locks, default(pDoorState));
            Hook(typeof(TerminalCommandReadbackFixed), terminal.SyncID, TERM_Command.ViewSecurityLog, "", "", "");
            terminal.CurrentStateName = TERM_State.DataMining; Hook(typeof(TerminalStateReadback), terminal);
            terminal.CurrentStateName = TERM_State.Hacked; Hook(typeof(TerminalStateReadback), terminal);
            door.LastStatus = eDoorStatus.Unlocked; Hook(typeof(DoorStateReadback), locks, default(pDoorState));
            Require(session.LastFault == null && plugin.Log.Warnings.Count == 0 && session.MapObjects!.PublishedFacts == 0,
                "A map-object readback refused a readable instance, or queued a fact no plan consumes. warnings=" + string.Join(" | ", plugin.Log.Warnings));
            // A client runs the same callbacks and returns before reading anything: a door that could not be
            // addressed at all is not even reported, and no fact reaches the module's own authority gate.
            SNet.IsMaster = false;
            world.Forget(upper); Hook(typeof(DoorStateReadback), locks, default(pDoorState));
            terminal.CurrentStateName = TERM_State.PlayerInteracting; Hook(typeof(TerminalStateReadback), terminal);
            Hook(typeof(TerminalCommandReadbackFixed), terminal.SyncID, TERM_Command.Open, "", "", "");
            Require(session.LastFault == null && plugin.Log.Warnings.Count == 0 && session.MapObjects!.PublishedFacts == 0,
                "A client read or published map-object state. warnings=" + string.Join(" | ", plugin.Log.Warnings));
        }
        finally { Outputs.AddRange(plugin.Log.Infos); Outputs.AddRange(plugin.Log.Warnings); Privacy(nameof(map_object_readbacks_read_the_doubled_natives_and_publish_for_the_host_only)); }
    }

    [Fact]
    public void zone_index_addresses_only_the_entrance_of_a_zone()
    {
        var kernel = Host.Runtime = Kernel();
        try
        {
            var world = new SyntheticLevel(kernel);
            // The zone the level starts in has no entrance gate; the other two are entered through their own
            // source gates, and one of those entrances is a bulkhead layer transition.
            var start = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_0);
            var middle = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_4);
            var upper = world.Zone(1, LG_LayerType.SecondaryLayer, eLocalZoneIndex.Zone_1);
            var entrance = world.Entrance(middle);
            var bulkhead = world.Entrance(upper, eDoorStatus.Closed, eSecurityDoorType.Bulkhead);
            // A door the level did not spawn as any zone's entrance.
            var loose = world.LooseDoor(eDoorStatus.Closed);
            Require(world.AddressOf(entrance) == "door/0/0/4/security", "The middle zone's entrance was not addressed.");
            Require(ZoneIndex.Current(kernel.WorldEpoch).ZoneOfDoor(entrance) == middle,
                "The entrance door did not resolve back to its own zone.");
            Require(ZoneIndex.Entrance(start) == null, "The zone the level starts in reported an entrance gate.");
            Require(world.AddressOf(loose) == null, "A door that is no zone's entrance was addressed.");
            Require(world.AddressOf(bulkhead) == null, "A bulkhead transition door was addressed as a zone entrance.");
        }
        finally { Outputs.Add("zone_index_addresses_only_the_entrance_of_a_zone"); }
    }

    [Fact]
    public void zone_index_tells_two_terminals_of_one_zone_apart_by_their_placement()
    {
        var kernel = Host.Runtime = Kernel();
        try
        {
            var world = new SyntheticLevel(kernel);
            var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2);
            var first = world.Terminal(zone, TERM_State.Awake);
            var second = world.Terminal(zone, TERM_State.Awake);
            Require(world.AddressOf(first) == "terminal/0/0/2/0" && world.AddressOf(second) == "terminal/0/0/2/1",
                "Two terminals of one zone did not take their own placement index.");
            Require(!ReferenceEquals(first, second), "The zone's terminal list lost one of its terminals.");
        }
        finally { Outputs.Add("zone_index_tells_two_terminals_of_one_zone_apart_by_their_placement"); }
    }

    [Fact]
    public void zone_index_refuses_coordinates_it_cannot_read()
    {
        var kernel = Host.Runtime = Kernel();
        try
        {
            var world = new SyntheticLevel(kernel);
            var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_3);
            var door = world.Entrance(zone);
            var terminal = world.Terminal(zone, TERM_State.Awake);
            Require(world.AddressOf(door) == "door/0/0/3/security" && world.AddressOf(terminal) == "terminal/0/0/3/0",
                "A readable zone was not addressed.");
            // The zone's own layer object does not read: no coordinate is invented from the door, and the
            // terminal standing in the same zone is unaddressed for the same cause.
            zone.m_layer = null;
            Require(world.AddressOf(door) == null && world.AddressOf(terminal) == null,
                "A zone whose layer did not read still produced an address.");
            // A terminal the zone does not list is not the zone's placement, whatever it reports about itself.
            zone.m_layer = new LG_Layer { m_type = LG_LayerType.MainLayer };
            Require(world.AddressOf(terminal) == "terminal/0/0/3/0", "The restored coordinate did not address the terminal.");
            zone.TerminalsSpawnedInZone = new List<LG_ComputerTerminal>();
            Require(world.AddressOf(terminal) == null, "A terminal the zone does not list was addressed.");
        }
        finally { Outputs.Add("zone_index_refuses_coordinates_it_cannot_read"); }
    }

    [Fact]
    public void zone_index_refuses_a_zone_that_declares_specific_terminal_spawns()
    {
        var kernel = Host.Runtime = Kernel();
        try
        {
            var world = new SyntheticLevel(kernel);
            var zone = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_4);
            var terminal = world.Terminal(zone, TERM_State.Awake);
            Require(world.AddressOf(terminal) == "terminal/0/0/4/0", "A clean zone did not address its terminal.");
            // The zone's data block declares a spawn outside its placement list, so the placement order is not
            // the zone's whole terminal order and no terminal of that zone is addressed.
            zone.m_settings!.m_zoneData!.SpecificTerminalSpawnDatas!.Add(new GameData.SpecificTerminalSpawnData());
            Require(world.AddressOf(terminal) == null, "A zone with specific terminal spawns still addressed a terminal.");
        }
        finally { Outputs.Add("zone_index_refuses_a_zone_that_declares_specific_terminal_spawns"); }
    }

    [Fact]
    public void map_object_sources_report_each_refusal_class_once()
    {
        var kernel = Host.Runtime = Kernel();
        try
        {
            var world = new SyntheticLevel(kernel);
            var reports = new List<string>();
            var doors = new DoorSource(reports.Add);
            var terminals = new TerminalSource(reports.Add);
            // Two doors that are no zone's entrance and two bulkhead transitions: a diagnostic names the class
            // the object belongs to, not the object, so neither pair is reported twice and none is addressed.
            var loose = new[] { world.LooseDoor(eDoorStatus.Closed), world.LooseDoor(eDoorStatus.Open) };
            var bulkheads = new[]
            {
                world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_1), eDoorStatus.Closed,
                    eSecurityDoorType.Bulkhead),
                world.Entrance(world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_2), eDoorStatus.Closed,
                    eSecurityDoorType.Bulkhead)
            };
            // Two zones that declare specific terminal spawns, one terminal each: the placement order is not the
            // whole terminal order of either zone, so the class is reported once for both zones.
            var first = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_3);
            var second = world.Zone(0, LG_LayerType.MainLayer, eLocalZoneIndex.Zone_4);
            first.m_settings!.m_zoneData!.SpecificTerminalSpawnDatas!.Add(new GameData.SpecificTerminalSpawnData());
            second.m_settings!.m_zoneData!.SpecificTerminalSpawnDatas!.Add(new GameData.SpecificTerminalSpawnData());
            var specific = new[] { world.Terminal(first, TERM_State.Awake), world.Terminal(second, TERM_State.Awake) };
            Require(loose.Concat(bulkheads).All(door => doors.TryAddress(door) == null), "A refused door was addressed.");
            Require(specific.All(terminal => terminals.TryAddress(terminal) == null), "A refused terminal was addressed.");
            Require(reports.Count == 3, "Refusal classes were not reported once each: " + string.Join(" | ", reports));
            Require(reports.Count(row => row.Contains("not the entrance gate of a zone", StringComparison.Ordinal)) == 1
                && reports.Count(row => row.Contains("bulkhead layer transition", StringComparison.Ordinal)) == 1
                && reports.Count(row => row.Contains("declares specific terminal spawns", StringComparison.Ordinal)) == 1,
                "A refusal class was reported for another class: " + string.Join(" | ", reports));
        }
        finally { Outputs.Add("map_object_sources_report_each_refusal_class_once"); }
    }

    [Fact]
    public void plugin_off()
    {
        Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Off; Host.Runtime = null;
        new MapPlugin().Load();
        Require(MapPlugin.Session == null && Harmony.Patches == 0 && Harmony.Unpatches == 0, "Off activated native work.");
    }

    [Fact]
    public void plugin_missing_runtime()
    {
        Host.Runtime = null; var plugin = new MapPlugin(); Throws(null, plugin.Load); Throws(null, plugin.Load);
        Require(Harmony.Patches == 0 && MapPlugin.Session == null, "Unavailable host still installed patches.");
    }

    [Fact]
    public void plugin_suspended_host()
    {
        Host.ConfiguredMode = ForgeRuntime.RuntimeMode.Play; Host.Runtime = Kernel();
        Host.Suspension = "I-DIAG-EXAMPLE"; string before = Host.Runtime.ExportManifest();
        var plugin = new MapPlugin(); plugin.Load();
        Require(Harmony.Patches == 0 && Harmony.Unpatches == 0 && MapPlugin.Session == null && Host.Runtime.ExportManifest() == before
            && plugin.Log.Errors.Count == 1 && plugin.Log.Errors[0].Contains("I-DIAG-EXAMPLE", StringComparison.Ordinal),
            "Suspended host installed native work or hid its reason.");
        Host.Suspension = null;
    }

    [Fact]
    public void plugin_invalid_log_level()
    {
        Host.Runtime = Kernel(); var plugin = new MapPlugin(); plugin.Config.Preset["Logging.Level"] = "verbose"; Throws(null, plugin.Load);
        Require(MapPlugin.Session == null && Harmony.Patches == 0, "Malformed Logging.Level still installed native work.");
    }

    [Fact]
    public void plugin_existing_map_provider_conflict()
    {
        Host.Runtime = Kernel(); using var existing = Host.Runtime.RegisterModule(RivalMapProvider(), RuntimeLogLevel.Off);
        string before = Host.Runtime.ExportManifest(); Throws(null, new MapPlugin().Load);
        Require(Harmony.Patches == 0 && Harmony.Unpatches == 0 && MapPlugin.Session == null && Host.Runtime.ExportManifest() == before,
            "Plugin patched or removed a provider it did not own.");
    }

    [Fact]
    public void plugin_partial_hook_failure()
    {
        Host.Runtime = Kernel(); string before = Host.Runtime.ExportManifest();
        // The second patch of the five fails, so the install stops there and must not leave the first one.
        Harmony.FailPatchAt = 2;
        Throws(null, new MapPlugin().Load);
        Require(Harmony.Patches == 2 && Harmony.Unpatches == 1 && MapPlugin.Session == null && Host.Runtime.ExportManifest() == before,
            "Partial native load survived rollback.");
    }

    [Fact]
    public void plugin_log_failure_rollback()
    {
        Host.Runtime = Kernel(); string before = Host.Runtime.ExportManifest();
        // The failure is raised by the line Load writes after the session started, so what rolls back is a load
        // that already installed its patches and its registration.
        var plugin = new MapPlugin(); plugin.Log.ThrowOn = "Forge Map registered"; Throws(null, plugin.Load);
        Require(Harmony.Patches == MapNativeHooks.Types.Count && Harmony.Unpatches == 1 && MapPlugin.Session == null && Host.Runtime.ExportManifest() == before,
            "Post-registration failure leaked the module.");
    }

    [Fact]
    public void plugin_success_hooks_drive_identity_no_hot_reload()
    {
        var kernel = Host.Runtime = Kernel(); var plugin = new MapPlugin();
        try
        {
            plugin.Load();
            var session = MapPlugin.Session;
            Require(session?.Module.IsRegistered == true && Harmony.Patches == MapNativeHooks.Types.Count, "Map native module was not installed.");
            Throws(null, plugin.Load);
            Require(Harmony.Patches == MapNativeHooks.Types.Count && !plugin.Unload(), "Repeated Load or hot unload changed native lifetime.");
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
        finally { Outputs.AddRange(plugin.Log.Infos); Outputs.AddRange(plugin.Log.Warnings); Privacy(nameof(plugin_success_hooks_drive_identity_no_hot_reload)); }
    }

    [Fact]
    public void privacy_no_account_lookup_in_any_output()
    {
        // Aggregate gate over every log, warning and lookup result the suite captured; each case also gates its own.
        Require(Outputs.Count > 0, "No outputs were captured.");
        Require(!Outputs.Any(Leaks), "An account lookup reached a log, warning or lookup result.");
    }

    // ---- player health action (`forge.action.combat.heal` for `gtfo.player`) ------------------------------
    //
    // The production handler is invoked the way the kernel invokes it: one recipient collection, the required
    // source reference, the amount, the optional ceiling and the one structural policy. The command context's
    // constructor is the SDK's own and internal, so it is created through its non-public signature exactly as
    // the enemy package's audit does. Native state is the doubles above and NOT game-verified.

    /// <summary>The recipient collection of one heal command, plus the first row's own JSON.</summary>
    private static CommandResult Heal(EntityReference[] targets, double amount = 5, double? cap = null,
        string policy = "clamp", EntityReference? source = null)
    {
        var origin = new RuntimeEvent("test.player.heal", PlayerHealthContract.BindingId, 1, 0, "test.player.scope", RuntimeJson.EmptyObject);
        var inputs = new Dictionary<string, object?> { ["targets"] = targets, ["source"] = source ?? targets[0], ["amount"] = amount };
        if (cap.HasValue) inputs["cap"] = cap.Value;
        var context = (CommandContext)Activator.CreateInstance(typeof(CommandContext), BindingFlags.Instance | BindingFlags.NonPublic, null,
            new object[] { origin, 0L, "test.player.command", "test.player.plan", "test.player.resource", "1", "test.player.node",
                RuntimeJson.From(new { overheal_policy = policy }), RuntimeJson.From(inputs), true }, null)!;
        return PlayerHealthAction.Execute(context);
    }

    private static JsonElement[] HealRows(CommandResult result) => result.Outputs.GetProperty("results").EnumerateArray().ToArray();
    private static JsonElement HealRow(CommandResult result) => HealRows(result).Single();

    /// <summary>One spawned player with a readable, set-up health receiver, and the life the readback recorded
    /// for it. The reference comes back from the kernel's own instance lookup, never from a guessed number.</summary>
    private static (SNet_Player Player, PlayerAgent Agent, Dam_PlayerDamageBase Damage, EntityReference Reference) Receiver(
        RuntimeKernel kernel, MapPluginSession session, ulong lookup, float health, float maximum)
    {
        var spawned = Spawn(lookup);
        spawned.Agent.Damage = new Dam_PlayerDamageBase { Owner = spawned.Agent, IsSetup = true, Health = health, HealthMax = maximum };
        Read(session);
        return (spawned.Player, spawned.Agent, spawned.Agent.Damage,
            kernel.ResolveEntityInstance(PlayerIdentityModule.EntityKind, spawned.Player)!);
    }

    [Fact]
    public void player_heal_commits_only_player_lives()
    {
        var kernel = Kernel(); List<string> info = new(), warn = new(); using var session = Start(kernel, info, warn);
        kernel.StartRuntime(() => { });
        var a = Receiver(kernel, session, A, 40f, 100f);
        var b = Receiver(kernel, session, B, 10f, 50f);
        Require(kernel.InspectEntities(new[] { a.Reference, b.Reference }).IsComplete, "Fixture players were not observable.");

        // One player life: the write lands on the receiver the readback verified, and the row's amount is what
        // that readback showed, not the requested five.
        var single = Heal(new[] { a.Reference });
        Require(single.Status == "succeeded" && single.CommitState == "confirmed" && a.Damage.Sends == 1
            && a.Damage.Health == 45f, "A player heal did not commit: " + single.Status + " " + single.Code + " " + HealRow(single));
        var row = HealRow(single);
        Require(row.GetProperty("status").GetString() == "committed" && row.GetProperty("committed").GetString() == CommitStates.Confirmed
            && row.GetProperty("code").GetString() == "committed" && row.GetProperty("amount").GetDouble() == 5
            && row.GetProperty("target_count").GetInt32() == 1
            && row.GetProperty("target").GetProperty("id").GetString() == a.Reference.Id,
            "The heal row is not the canonical row shape: " + row);

        // Two targets: one row each, in the plan's own order, and the count is the command's.
        var both = Heal(new[] { a.Reference, b.Reference }, amount: 100);
        Require(both.Status == "succeeded" && HealRows(both).Length == 2
            && HealRows(both)[1].GetProperty("target").GetProperty("id").GetString() == b.Reference.Id
            && HealRows(both)[0].GetProperty("target_count").GetInt32() == 2
            && a.Damage.Health == 100f && b.Damage.Health == 50f,
            "A two-target player heal did not commit both: " + both.Status + " " + string.Join(",", HealRows(both).Select(r => r.ToString())));

        // A reference this provider does not own is refused by name; the enemy domain's kind, another
        // provider's entity id for a player, a stale life and a forged number all take the same refusal.
        var enemy = new EntityReference("gtfo.enemy:7", 1, 1);
        var foreign = new EntityReference("gtfo.other:1", 1, 1);
        foreach (var target in new[] { enemy, foreign, a.Reference with { LifeEpoch = 9 }, a.Reference with { WorldEpoch = 9 }, Ref(99, 1, 1) })
        {
            int sends = a.Damage.Sends;
            var refused = Heal(new[] { target });
            // A refused recipient still gets its own row, in the canonical shape, with its own commit state and
            // the code that names the refusal; only a command refused before any target is read has no rows.
            var refusedRow = HealRows(refused).Single();
            Require(refused.Status == "rejected" && refused.Code == "stale-or-unsupported-recipient"
                && a.Damage.Sends == sends && refusedRow.GetProperty("status").GetString() == "rejected"
                && refusedRow.GetProperty("committed").GetString() == CommitStates.None
                && refusedRow.GetProperty("code").GetString() == "stale-or-unsupported-recipient"
                && refusedRow.GetProperty("amount").GetDouble() == 0,
                "A foreign recipient was not refused: " + target.Id + " " + refused.Status + " " + refused.Code + " " + refusedRow);
        }
        Privacy(nameof(player_heal_commits_only_player_lives));
    }

    [Fact]
    public void player_heal_refuses_what_the_player_receiver_refuses()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        var a = Receiver(kernel, session, A, 40f, 100f);
        var dead = Receiver(kernel, session, B, 0f, 100f);
        dead.Agent.Alive = false;
        var unset = Receiver(kernel, session, 76561198000000003, 40f, 100f);
        unset.Damage.IsSetup = false;
        var foreign = Receiver(kernel, session, 76561198000000004, 40f, 100f);
        foreign.Damage.Owner = null;
        var broken = Receiver(kernel, session, 76561198000000005, 40f, 100f);
        broken.Damage.Health = 200f;

        var cases = new (EntityReference Target, string Code)[]
        {
            (dead.Reference, "not-alive"),
            (unset.Reference, "missing-health-receiver"),
            (foreign.Reference, "health-receiver-owner-mismatch"),
            (broken.Reference, "invalid-health-state")
        };
        foreach (var (target, code) in cases)
        {
            var refused = Heal(new[] { target });
            Require(refused.Status == "rejected" && refused.Code == code && refused.CommitState == "none",
                "Expected " + code + " for a refused player; got " + refused.Status + " " + refused.Code);
        }
        Require(dead.Damage.Sends == 0 && unset.Damage.Sends == 0 && foreign.Damage.Sends == 0 && broken.Damage.Sends == 0,
            "A refused player receiver was written to.");

        // A life this provider no longer holds — despawned or replaced — takes the identity refusal instead of
        // being retargeted onto whoever holds the number now.
        Despawn(a.Player, a.Agent); Read(session);
        Require(Heal(new[] { a.Reference }).Code == "stale-or-unsupported-recipient", "A despawned player life was healed.");

        // The host is the only authority that may submit: the native write entry applies nothing on a client,
        // so the command is refused before any read.
        SNet.IsMaster = false;
        Require(Heal(new[] { dead.Reference }).Code == "authority-or-phase", "A client submitted a player heal.");
        SNet.IsMaster = true;
        Privacy(nameof(player_heal_refuses_what_the_player_receiver_refuses));
    }

    [Fact]
    public void player_heal_policies_amount_and_target_budget()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        var full = Receiver(kernel, session, A, 100f, 100f);
        var low = Receiver(kernel, session, B, 40f, 100f);
        var small = Receiver(kernel, session, 76561198000000003, 10f, 20f);

        // Clamp stops at the ceiling and a target already there is a committed no-op: nothing is submitted and
        // no change is claimed.
        Require(full.Damage.Sends == 0 && low.Damage.Sends == 0, "A fixture receiver was written before the case ran.");
        var clamped = Heal(new[] { full.Reference, low.Reference }, amount: 10);
        Require(clamped.Status == "succeeded" && full.Damage.Sends == 0 && full.Damage.Health == 100f
            && low.Damage.Health == 50f && HealRows(clamped)[0].GetProperty("amount").GetDouble() == 0,
            "A clamped player heal claimed a change it did not make: " + HealRows(clamped)[0]);

        // `cap` lowers the ceiling below the receiver's own maximum for every target.
        var capped = Heal(new[] { small.Reference }, amount: 100, cap: 15);
        Require(capped.Status == "succeeded" && small.Damage.Health == 15f
            && HealRow(capped).GetProperty("amount").GetDouble() == 5, "A capped player heal did not stop at the cap.");

        // `discard` refuses the whole target when the amount would overflow, and never submits.
        int sends = small.Damage.Sends;
        var discarded = Heal(new[] { small.Reference }, amount: 100, policy: "discard");
        Require(discarded.Status == "rejected" && discarded.Code == "would-overheal" && small.Damage.Sends == sends,
            "A discarded player heal was submitted: " + discarded.Status + " " + discarded.Code);

        // `overheal` is structurally unsupported for the same reason it is for enemies: the receiver's own
        // encoding is measured against the maximum and holds no value above it.
        Require(Heal(new[] { small.Reference }, policy: "overheal").Code == "overheal-unsupported", "Overheal was accepted.");

        Require(Heal(new[] { small.Reference }, amount: 0).Code == "amount-out-of-range"
            && Heal(new[] { small.Reference }, amount: -5).Code == "amount-out-of-range"
            && Heal(new[] { small.Reference }, amount: 1000001).Code == "amount-out-of-range",
            "An out-of-range amount was accepted.");
        Require(Heal(new[] { small.Reference }, amount: 5, cap: 0).Code == "invalid-cap", "A non-positive cap was accepted.");

        // One row is written per target, so the result budget is the row budget.
        var many = Enumerable.Range(0, CommandResult.MaximumFacts + 1).Select(i => Ref(9000 + i, 1, 1)).ToArray();
        Require(Heal(many).Code == "too-many-targets", "An oversized player heal was accepted.");
        Privacy(nameof(player_heal_policies_amount_and_target_budget));
    }

    [Fact]
    public void player_heal_reports_unknown_commit_when_the_write_cannot_be_read_back()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        var a = Receiver(kernel, session, A, 40f, 100f);
        var b = Receiver(kernel, session, B, 40f, 100f);
        var c = Receiver(kernel, session, 76561198000000003, 40f, 100f);

        // A throwing native call is an unknown commit: the packet or the local application may already have
        // happened, so the row must not claim success or failure.
        a.Damage.Commit = _ => throw new IOException("synthetic submit exception");
        var thrown = Heal(new[] { a.Reference });
        Require(thrown.Status == "failed" && thrown.CommitState == "unknown" && thrown.Code == "native-commit-exception"
            && a.Damage.Sends == 1 && a.Damage.Health == 40f, "A throwing player commit was not unknown: " + HealRow(thrown));

        // A receiver replaced by the write, and a readback that moved the wrong way, are both unknown too.
        b.Damage.Commit = value => { b.Damage.Health = value; b.Agent.Damage = new Dam_PlayerDamageBase { Owner = b.Agent, IsSetup = true, Health = value, HealthMax = 100f }; };
        var replaced = Heal(new[] { b.Reference });
        Require(replaced.Status == "failed" && replaced.Code == "receiver-changed-during-commit" && b.Damage.Sends == 1
            && HealRow(replaced).GetProperty("committed").GetString() == "unknown", "A replaced receiver was not unknown: " + HealRow(replaced));

        c.Damage.Commit = _ => c.Damage.Health = 10f;
        var dropped = Heal(new[] { c.Reference });
        Require(dropped.Status == "failed" && dropped.Code == "unexpected-health-readback" && c.Damage.Sends == 1,
            "A health loss during a player heal was not unknown: " + HealRow(dropped));

        // An unknown row stops the walk and every target after it is reported as not attempted, so one command
        // never writes through a state it could not confirm.
        var after = Receiver(kernel, session, 76561198000000004, 40f, 100f);
        a.Damage.Commit = _ => throw new IOException("synthetic submit exception");
        var stopped = Heal(new[] { a.Reference, after.Reference });
        Require(stopped.Status == "failed" && stopped.CommitState == "unknown" && HealRows(stopped).Length == 2
            && HealRows(stopped)[1].GetProperty("code").GetString() == "not-attempted-after-unknown-commit"
            && after.Damage.Sends == 0, "A command continued after an unknown player commit: " + HealRows(stopped)[1]);
        Privacy(nameof(player_heal_reports_unknown_commit_when_the_write_cannot_be_read_back));
    }

    [Fact]
    public void player_heal_commits_one_of_two_targets_as_partial()
    {
        var kernel = Kernel(); using var session = Start(kernel, new(), new()); kernel.StartRuntime(() => { });
        var a = Receiver(kernel, session, A, 40f, 100f);
        var dead = Receiver(kernel, session, B, 0f, 100f);
        dead.Agent.Alive = false;
        var result = Heal(new[] { a.Reference, dead.Reference });
        Require(result.Status == "partial" && result.CommitState == CommitStates.Confirmed
            && HealRows(result)[0].GetProperty("status").GetString() == "committed"
            && HealRows(result)[1].GetProperty("status").GetString() == "rejected"
            && HealRows(result)[1].GetProperty("code").GetString() == "not-alive",
            "A partially committed player heal was not reported as such: " + string.Join(",", HealRows(result).Select(r => r.ToString())));
    }
}
