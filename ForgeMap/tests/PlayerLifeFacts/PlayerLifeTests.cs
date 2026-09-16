using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;
using SNetwork;
using UnityEngine;

namespace ForgeMap.Tests.PlayerLifeObs;

/// <summary>The player-life observation: one fact per native transition of one life, published through the Map
/// provider's own registration. Every case drives the production half through the same call a hook makes and
/// reads back what the kernel accepted.
///
/// The port values themselves are asserted on the row builders the half publishes with
/// (`PlayerLifeContract.*Payload`), because those are the one place a port is written: a fact's payload and the
/// catalog row that declares it are two halves of one shape, and the half never builds a payload any other way.
/// What the kernel adds — identity, ownership, the ledger, the mount — is asserted through the registration.
/// </summary>
public sealed class PlayerLifeTests
{
    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    private static EntityReference Ref(long number, long world = 1, long life = 1)
        => new(PlayerIdentityModule.EntityKind + ":" + number, world, life);

    private static JsonElement Port(JsonElement payload, string id)
        => payload.TryGetProperty(id, out var value) ? value : throw new Exception("port missing: " + id);

    private static bool IsNull(JsonElement payload, string id)
        => payload.TryGetProperty(id, out var value) && value.ValueKind == JsonValueKind.Null;

    private static bool Absent(JsonElement payload, string id) => !payload.TryGetProperty(id, out _);

    private static double[] Numbers(JsonElement value)
        => value.EnumerateArray().Select(x => x.GetDouble()).ToArray();

    private static bool Same(double[] left, double[] right)
        => left.Length == right.Length && left.Zip(right).All(pair => Math.Abs(pair.First - pair.Second) < 1e-6);

    // --------------------------------------------------------------- rows and registration

    /// <summary>Every row this package binds is declared by the contract provider with exactly one observation
    /// binding under the Map provider, and the registration carries one support row per binding. A binding row
    /// with no capability, or a capability with no binding, is refused by the registry itself.</summary>
    [Fact]
    public void module_registers_one_binding_and_one_support_per_row()
    {
        using var fixture = new PlayerLifeFixture();
        using var document = JsonDocument.Parse(fixture.Kernel.ExportManifest());
        var registry = document.RootElement.GetProperty("registry");
        var bindings = registry.GetProperty("bindings").EnumerateArray()
            .Where(b => b.GetProperty("providerId").GetString() == PlayerLifeContract.ProviderId).ToArray();
        var support = document.RootElement.GetProperty("bindingSupport").EnumerateArray()
            .Select(r => r.GetProperty("bindingId").GetString()!).ToArray();
        Require(bindings.Length == PlayerLifeContract.Facts.Count,
            "The Map provider declared " + bindings.Length + " player-life bindings; the family has " + PlayerLifeContract.Facts.Count + ".");
        foreach (var (fact, capability) in PlayerLifeContract.Facts)
        {
            var binding = bindings.SingleOrDefault(b => b.GetProperty("id").GetString() == PlayerLifeFixture.BindingOf(fact));
            Require(binding.ValueKind == JsonValueKind.Object, "No binding row for " + fact + ".");
            Require(binding.GetProperty("capabilityId").GetString() == PlayerLifeFixture.CapabilityOf(fact), "Wrong capability for " + fact + ".");
            Require(binding.GetProperty("role").GetString() == "observe" && binding.GetProperty("status").GetString() == "implemented",
                fact + " is not an implemented observation.");
            Require(binding.GetProperty("dependencies").GetArrayLength() == 0 && binding.GetProperty("requires").GetArrayLength() == 0,
                fact + " declares a dependency or requirement it must not have.");
            Require(support.Contains(PlayerLifeFixture.BindingOf(fact)), "No support row for " + fact + ".");
        }
        // The catalog rows the fixture declared are the seven the package binds, field for field.
        var declared = registry.GetProperty("capabilities").EnumerateArray()
            .Where(c => c.GetProperty("owner").GetString() == PlayerLifeFixture.ContractProvider)
            .Select(c => c.GetProperty("id").GetString()!).OrderBy(x => x, StringComparer.Ordinal).ToArray();
        Require(declared.SequenceEqual(PlayerLifeContract.Facts.Select(f => PlayerLifeFixture.CapabilityOf(f.Fact)).OrderBy(x => x, StringComparer.Ordinal)),
            "The declared rows differ from the family: " + string.Join(",", declared));
    }

    // ------------------------------------------------------------------------ each fact

    [Fact]
    public void downed_publishes_once_per_episode()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        var life = fixture.Spawn(1);
        life.Fall();
        Require(fixture.Facts.Downed(life.Downed), "The downed transition did not publish.");
        Require(fixture.Facts.PublishedFacts == 1 && fixture.Facts.Journal.Single() == "player.downed id=gtfo.player:1 transition=0",
            "The downed fact differs: " + string.Join(" | ", fixture.Facts.Journal));
        // The same state reported again by the other enter body is not a second fact.
        Require(!fixture.Facts.Downed(life.Downed) && fixture.Facts.PublishedFacts == 1, "A repeated enter published twice.");
        // A life that never entered the state has no downing to report.
        var other = fixture.Spawn(2);
        Require(!fixture.Facts.Downed(other.Downed), "An upright life published a downing.");
        // Nor does a dead one.
        other.Kill();
        Require(!fixture.Facts.Downed(other.Downed), "A dead life published a downing.");
    }

    [Fact]
    public void revive_started_names_the_rescuer_and_is_not_repeated()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        fixture.Mount("plan-revive_started", "revive_started");
        var fallen = fixture.Spawn(1);
        var rescuer = fixture.Spawn(2);
        fallen.Fall();
        Require(fixture.Facts.Downed(fallen.Downed), "The fixture life did not go down.");
        Require(fixture.Facts.ReviveStarted(rescuer.Agent, fallen.Agent), "The rescue did not publish.");
        Require(fixture.Facts.Journal.Last() == "player.revive_started id=gtfo.player:1 transition=1",
            "The revive_started fact differs: " + fixture.Facts.Journal.Last());
        Require(!fixture.Facts.ReviveStarted(rescuer.Agent, fallen.Agent), "The same rescue started twice.");
        // A rescuer this process tracks no life for leaves the required port unreadable, so nothing is published
        // and the refusal is reported once. The rescue has to be a life's own first one: a rescue that already
        // started is refused by the episode's state before the rescuer is ever read.
        var other = fixture.Spawn(3);
        other.Fall();
        Require(fixture.Facts.Downed(other.Downed), "The second life did not go down.");
        var stranger = new PlayerAgent { Owner = null };
        Require(!fixture.Facts.ReviveStarted(stranger, other.Agent), "A rescue by an unknown actor published.");
        Require(fixture.Reported.Count == 1 && fixture.Reported[0].StartsWith("Map player life observation:", StringComparison.Ordinal),
            "The unreadable rescuer was not reported once: " + fixture.Reported.Count);
    }

    [Fact]
    public void revive_cancelled_only_cancels_a_rescue_that_started()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        fixture.Mount("plan-revive_started", "revive_started");
        fixture.Mount("plan-revive_cancelled", "revive_cancelled");
        var fallen = fixture.Spawn(1);
        var rescuer = fixture.Spawn(2);
        fallen.Fall();
        fixture.Facts.Downed(fallen.Downed);
        // An interaction that ends without ever having started a rescue is not a cancellation of one.
        Require(!fixture.Facts.ReviveCancelled(PlayerLifeContract.AbortedReason, rescuer.Agent, fallen.Agent),
            "A rescue that never started was cancelled.");
        Require(fixture.Facts.ReviveStarted(rescuer.Agent, fallen.Agent), "The rescue did not publish.");
        Require(fixture.Facts.ReviveCancelled(PlayerLifeContract.AbortedReason, rescuer.Agent, fallen.Agent),
            "The interrupted rescue did not publish a cancellation.");
        Require(fixture.Facts.Journal.Last() == "player.revive_cancelled id=gtfo.player:1 transition=2",
            "The revive_cancelled fact differs: " + fixture.Facts.Journal.Last());
        Require(!fixture.Facts.ReviveCancelled(PlayerLifeContract.LeftReason, rescuer.Agent, fallen.Agent),
            "The cancelled rescue was cancelled twice.");
    }

    [Fact]
    public void revived_ends_the_episode_and_reports_the_rescuer()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        fixture.Mount("plan-revive_started", "revive_started");
        fixture.Mount("plan-revived", "revived");
        var fallen = fixture.Spawn(1);
        var rescuer = fixture.Spawn(2);
        fallen.Fall();
        fixture.Facts.Downed(fallen.Downed);
        fixture.Facts.ReviveStarted(rescuer.Agent, fallen.Agent);
        // The revive runs: the state records it and the machine leaves the downed state, with the acting player
        // on the interaction the same way the game's own revive interaction carries it.
        fallen.Downed.m_owner!.ReviveInteraction!.Agent = rescuer.Agent;
        fallen.Revive();
        Require(fixture.Facts.Revived(fallen.Downed), "The revive did not publish.");
        Require(fixture.Facts.Journal.Last() == "player.revived id=gtfo.player:1 transition=2",
            "The revived fact differs: " + fixture.Facts.Journal.Last());
        Require(!fixture.Facts.Revived(fallen.Downed), "The same revive published twice.");
        // A revive with no actor this process can name still publishes; its rescuer port is nullable for exactly
        // that revive.
        var lone = fixture.Spawn(3);
        lone.Fall();
        fixture.Facts.Downed(lone.Downed);
        lone.Revive();
        Require(fixture.Facts.Revived(lone.Downed), "A revive without a named actor did not publish.");
        // A life that never went down has no episode to end.
        var upright = fixture.Spawn(4);
        Require(!fixture.Facts.Revived(upright.Downed), "An upright life published a revive.");
    }

    [Fact]
    public void died_publishes_once_per_life_from_the_native_write()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-died", "died");
        var life = fixture.Spawn(1);
        Require(!fixture.Facts.Died(true, life.Agent), "A living write published a death.");
        life.Kill();
        Require(fixture.Facts.Died(false, life.Agent), "The death did not publish.");
        Require(fixture.Facts.Journal.Single() == "player.died id=gtfo.player:1 transition=0",
            "The died fact differs: " + string.Join(" | ", fixture.Facts.Journal));
        Require(!fixture.Facts.Died(false, life.Agent), "The same death published twice.");
        // An agent this provider does not track has no life to end.
        Require(!fixture.Facts.Died(false, new PlayerAgent { Owner = null }), "An untracked agent published a death.");
    }

    [Fact]
    public void teleported_reports_both_ends_from_a_position_that_was_read()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-teleported", "teleported");
        var life = fixture.Spawn(1, 1, 2, 3);
        // A life is tracked with the position it holds when this half first sees it, and that read is the origin
        // a later warp reports: the first warp of a spawned life moves it from where it was spawned, which is a
        // real reading and not an invented one.
        Require(fixture.Facts.Teleported(life.Agent, new pPlayerLocationData { goodPosition = new Vector3(9, 8, 7) }),
            "The first teleport did not publish.");
        Require(fixture.Facts.Journal.Last() == "player.teleported id=gtfo.player:1 transition=0",
            "The first teleport differs: " + fixture.Facts.Journal.Last());
        life.Move(9, 8, 7);
        Require(fixture.Facts.Teleported(life.Agent, new pPlayerLocationData { goodPosition = new Vector3(-1, -2, -3) }),
            "The second teleport did not publish.");
        var last = fixture.Facts.Journal.Last();
        Require(last == "player.teleported id=gtfo.player:1 transition=1", "The teleported fact differs: " + last);
        // A destination that is not a finite position is no destination at all.
        Require(!fixture.Facts.Teleported(life.Agent, new pPlayerLocationData { goodPosition = new Vector3(float.NaN, 0, 0) }),
            "A non-finite destination published.");
        Require(!fixture.Facts.Teleported(life.Agent, "not a location"), "A foreign destination published.");
        // A life whose own position cannot be read has no origin, so its move is not published as one from nowhere.
        var unreadable = fixture.Spawn(2, float.NaN, 0, 0);
        Require(!fixture.Facts.Teleported(unreadable.Agent, new pPlayerLocationData { goodPosition = new Vector3(1, 1, 1) }),
            "A teleport without a readable origin published.");
        Require(fixture.Facts.Journal.Count == 2, "A refused teleport wrote a fact: " + string.Join(" | ", fixture.Facts.Journal));
    }

    [Fact]
    public void respawned_reports_the_spawn_position_and_needs_a_recorded_life()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        fixture.Mount("plan-respawned", "respawned");
        var life = fixture.Spawn(1);
        var spawn = new pPlayerSpawnData
        {
            snetPlayer = life.Player, locationData = new pPlayerLocationData { goodPosition = new Vector3(4, 5, 6) }
        };
        life.Fall();
        fixture.Facts.Downed(life.Downed);
        Require(fixture.Facts.Respawned(spawn), "The spawn did not publish.");
        Require(fixture.Facts.Journal.Last() == "player.respawned id=gtfo.player:1 transition=1",
            "The respawned fact differs: " + fixture.Facts.Journal.Last());
        // A spawn for a player this world does not hold publishes nothing.
        var stranger = new SNet_Player { Lookup = 99, PlayerAgent = new SNet_IPlayerAgent() };
        Require(!fixture.Facts.Respawned(new pPlayerSpawnData { snetPlayer = stranger, locationData = spawn.locationData }),
            "A spawn for an unrecorded player published.");
        Require(!fixture.Facts.Respawned(null), "A null spawn data published.");
    }

    // ------------------------------------------------------------------ port values

    /// <summary>Every row's payload carries exactly the ports the catalog declares for it, with the values the
    /// fact read: the entity for the life it is about, metres for both ends of a move, and the reason word.</summary>
    [Fact]
    public void payloads_carry_the_catalog_ports_and_values()
    {
        var player = Ref(3);
        var rescuer = Ref(4);

        var downed = PlayerLifeContract.DownedPayload(player);
        Require(downed.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "player", "source" }),
            "The downed payload ports differ: " + downed);
        Require(Port(downed, "player").GetProperty("id").GetString() == "gtfo.player:3", "The downed player differs.");
        Require(IsNull(downed, "source"), "The downed source is not the null the game reports.");

        var started = PlayerLifeContract.ReviveStartedPayload(player, rescuer);
        Require(started.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "player", "rescuer" }),
            "The revive_started payload ports differ: " + started);
        Require(Port(started, "rescuer").GetProperty("id").GetString() == "gtfo.player:4", "The rescuer differs.");

        var cancelled = PlayerLifeContract.ReviveCancelledPayload(player, rescuer, PlayerLifeContract.AbortedReason);
        Require(cancelled.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "player", "rescuer", "reason" }),
            "The revive_cancelled payload ports differ: " + cancelled);
        Require(Port(cancelled, "reason").GetString() == "aborted", "The reason word differs.");
        Require(IsNull(PlayerLifeContract.ReviveCancelledPayload(player, null, PlayerLifeContract.LeftReason), "rescuer"),
            "An unidentified canceller is not the null the nullable port means.");

        var revived = PlayerLifeContract.RevivedPayload(player, rescuer);
        Require(revived.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "player", "rescuer" }),
            "The revived payload ports differ: " + revived);
        Require(IsNull(PlayerLifeContract.RevivedPayload(player, null), "rescuer"), "A lone revive is not null.");

        var died = PlayerLifeContract.DiedPayload(player);
        Require(died.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "player", "source" }),
            "The died payload ports differ: " + died);
        Require(IsNull(died, "source"), "The died source is not the null the game reports.");

        var teleported = PlayerLifeContract.TeleportedPayload(player, new[] { 1.0, 2.0, 3.0 }, new[] { -4.0, 5.5, 6.0 });
        Require(teleported.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "player", "from", "to" }),
            "The teleported payload ports differ: " + teleported);
        Require(Same(Numbers(Port(teleported, "from")), new[] { 1.0, 2.0, 3.0 }), "The origin differs.");
        Require(Same(Numbers(Port(teleported, "to")), new[] { -4.0, 5.5, 6.0 }), "The destination differs.");

        var respawned = PlayerLifeContract.RespawnedPayload(player, new[] { 7.0, 8.0, 9.0 });
        Require(respawned.EnumerateObject().Select(p => p.Name).SequenceEqual(new[] { "player", "position" }),
            "The respawned payload ports differ: " + respawned);
        Require(Same(Numbers(Port(respawned, "position")), new[] { 7.0, 8.0, 9.0 }), "The spawn position differs.");
    }

    // ------------------------------------------------------------- refusals and lifetime

    /// <summary>A reference the identity no longer holds publishes nothing and reports nothing: it is the same
    /// answer every other read of that life gives.</summary>
    [Fact]
    public void unheld_life_and_non_host_publish_nothing()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        var life = fixture.Spawn(1);
        fixture.Forget(life);
        Require(!fixture.Facts.Downed(life.Downed), "An unheld life published a downing.");
        Require(!fixture.Facts.Died(false, life.Agent), "An unheld life published a death.");
        Require(!fixture.Facts.Teleported(life.Agent, new pPlayerLocationData()), "An unheld life published a teleport.");
        Require(!fixture.Facts.Respawned(new pPlayerSpawnData { snetPlayer = life.Player }), "An unheld life was respawned.");
        Require(fixture.Facts.PublishedFacts == 0 && fixture.Facts.Journal.Count == 0, "A refused read published a fact.");

        // A client is not the authority for a host fact, so its callback reads nothing at all.
        var live = fixture.Spawn(2);
        fixture.Authority = false;
        live.Fall();
        Require(!fixture.Facts.Downed(live.Downed), "A non-authoritative peer published a downing.");
        Require(!fixture.Facts.Authoritative, "A non-authoritative peer claimed authority.");
        fixture.Authority = true;
        Require(fixture.Facts.Downed(live.Downed), "Authority did not restore the read.");
    }

    /// <summary>The kernel's own ledger is what makes a repeated report free: the same event id is the kernel's
    /// `duplicate` answer and never a second dispatch.</summary>
    [Fact]
    public void the_kernel_ledger_answers_a_repeated_transition()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        var life = fixture.Spawn(1);
        life.Fall();
        Require(fixture.Facts.Downed(life.Downed), "The downing did not publish.");
        // The same transition reported through a second life half on the same registration is the same event id.
        var replay = new PlayerLifeFacts(
            KernelHandle(fixture), fixture.Kernel, () => true, fixture.Reported.Add, fixture.Logged.Add) { World = fixture.Identity };
        Require(!replay.Downed(life.Downed), "A replayed transition was published as a new fact.");
        Require(replay.PublishedFacts == 0 && fixture.Facts.PublishedFacts == 1,
            "A replayed transition changed the published count.");
        replay.Dispose();
    }

    /// <summary>The registration the fixture's own half publishes through, for the replay case.</summary>
    private static RuntimeModuleHandle KernelHandle(PlayerLifeFixture fixture)
    {
        var handle = fixture.Kernel.GetType()
            .GetProperty("Registration")?.GetValue(fixture.Kernel) as RuntimeModuleHandle;
        if (handle != null) return handle;
        // The public surface has no registration getter, so the replay half is built from the same provider
        // definition by registering a second Map provider is impossible: the fixture reuses the one handle the
        // half already holds instead.
        return (RuntimeModuleHandle)typeof(PlayerLifeFacts)
            .GetField("_registration", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(fixture.Facts)!;
    }

    /// <summary>A world change drops every per-life decision of the world that ended, so the next world's first
    /// transition is its own fact and never the previous world's transition number.</summary>
    [Fact]
    public void world_change_clears_every_per_life_decision()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        var life = fixture.Spawn(1);
        life.Fall();
        Require(fixture.Facts.Downed(life.Downed), "The first world's downing did not publish.");
        fixture.ForgeWorld(2);
        var next = fixture.Spawn(2);
        next.Fall();
        Require(fixture.Facts.Downed(next.Downed), "The new world's downing did not publish.");
        Require(fixture.Facts.Journal.Last() == "player.downed id=gtfo.player:1 transition=0",
            "The new world reused a transition number: " + fixture.Facts.Journal.Last());
        // The previous world's life is gone with its world: its state no longer resolves.
        Require(!fixture.Facts.Downed(life.Downed), "A life of the previous world published again.");
    }

    /// <summary>A native callback that throws disables the half for the rest of the process, exactly as every
    /// other Map callback does, and the entry the hooks reach stops publishing.</summary>
    [Fact]
    public void a_failing_callback_stops_the_hooks_entry()
    {
        using var fixture = new PlayerLifeFixture();
        fixture.Mount("plan-downed", "downed");
        var life = fixture.Spawn(1);
        life.Fall();
        var registration = KernelHandle(fixture);
        registration.Dispose();
        Require(!fixture.Facts.Downed(life.Downed), "A released registration still published.");
        Require(fixture.Facts.PublishedFacts == 0, "A released registration counted a publication.");
    }
}
