using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;

namespace ForgeMap.Tests.PlayerFacts;

/// <summary>The player-domain facts and values this slice adds, exercised through the real kernel and a real
/// provider registration: the damage fact, the low-health and infection events, the ping and the two acquisition
/// events, the seven value answers, and the three write actions' refusal paths and result rows.
///
/// Every publication goes through the registration, so a payload port the capability does not declare is refused
/// by the kernel and the case sees no fact at all; the port values themselves are asserted on the row builders
/// the half publishes with, which is the same pair of halves the shipped text is written from.</summary>
public sealed class PlayerFactsTests
{
    // ---- damage (`forge.trigger.combat.damage_applied` for gtfo.player) ----------------------------------

    [Fact]
    public void damage_publishes_the_amount_the_receiver_accepted()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn();
        var shooter = world.Spawn();

        Require(world.State.DamageApplied(victim.Damage, 12.5f, shooter.Agent, PlayerStateContract.DamageKindDirect),
            "The accepted damage did not publish.");
        Require(world.State.PublishedFacts == 1, "The damage fact count differs.");
        Require(world.State.Journal.Single().StartsWith("player.damage_applied id=", StringComparison.Ordinal),
            "The damage fact was not journaled: " + string.Join(" | ", world.State.Journal));
    }

    [Fact]
    public void damage_payload_carries_the_catalog_ports_and_the_requested_friendly_fire_port()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn();
        var shooter = world.Spawn();
        var payload = PlayerStateContract.DamageAppliedPayload(victim.Reference, shooter.Reference, 12.5, PlayerStateContract.DamageKindDirect, true);

        Require(payload.GetProperty("target").GetProperty("id").GetString() == victim.Reference.Id, "The target port differs.");
        Require(payload.GetProperty("source").GetProperty("id").GetString() == shooter.Reference.Id, "The source port differs.");
        Require(Math.Abs(payload.GetProperty("amount").GetDouble() - 12.5) < 0.0001, "The amount port differs.");
        Require(payload.GetProperty("damage_kind").GetInt32() == PlayerStateContract.DamageKindDirect, "The damage kind port differs.");
        Require(payload.GetProperty("friendly_fire").GetBoolean(), "The friendly fire port differs.");
        // A port this half cannot observe is left out entirely: an unnamed kind and an unnamed attacker are not
        // the same claim as an observed zero or an observed nobody.
        var bare = PlayerStateContract.DamageAppliedPayload(victim.Reference, null, 1, null, null);
        Require(!bare.TryGetProperty("damage_kind", out _), "An unnamed kind was written as a port.");
        Require(!bare.TryGetProperty("friendly_fire", out _), "An unnamed attacker was written as a friendly-fire answer.");
        Require(bare.GetProperty("source").ValueKind == JsonValueKind.Null, "A nullable source was not written as null.");
    }

    [Fact]
    public void damage_names_an_enemy_attacker_through_the_kind_that_owns_it()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn();
        var enemy = world.Enemy();

        Require(world.State.DamageApplied(victim.Damage, 3f, enemy, PlayerStateContract.DamageKindExplosion), "The enemy attack did not publish.");
        // The fact is about the victim and carries the attacker the enemy domain resolved; the friendly-fire port
        // is a derivation from that answer, which is why it needs the attacker to be named at all.
        var payload = PlayerStateContract.DamageAppliedPayload(victim.Reference, enemy.Reference, 3, PlayerStateContract.DamageKindExplosion, false);
        Require(!payload.GetProperty("friendly_fire").GetBoolean(), "An enemy attacker read as friendly fire.");
    }

    [Fact]
    public void damage_publishes_nothing_for_an_unnamed_life_or_a_refused_application()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn();
        var stranger = new PlayerAgent { Pointer = (IntPtr)9999 };

        Require(!world.State.DamageApplied(stranger, 5f, null, PlayerStateContract.DamageKindDirect), "A damage about an unrecorded life published.");
        Require(!world.State.DamageApplied(victim.Damage, 0f, null, PlayerStateContract.DamageKindDirect), "A zero damage published.");
        Require(!world.State.DamageApplied(victim.Damage, float.NaN, null, PlayerStateContract.DamageKindDirect), "A non-finite damage published.");
        Require(world.State.PublishedFacts == 0, "A refused damage published: " + string.Join(" | ", world.State.Journal));
    }

    [Fact]
    public void damage_does_not_publish_on_a_machine_that_may_not_publish()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn();
        world.Source.CanPublish = false;

        Require(!world.State.DamageApplied(victim.Damage, 5f, null, PlayerStateContract.DamageKindDirect), "A client published a host fact.");
        Require(world.State.PublishedFacts == 0, "A client published a host fact.");
    }

    // ---- low health and infection ------------------------------------------------------------------------

    [Fact]
    public void low_health_publishes_the_player_the_game_named()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();

        Require(world.State.LowHealth(life.Agent), "The low-health event did not publish.");
        Require(world.State.Journal.Single().Contains("player.low_health", StringComparison.Ordinal), "The fact name differs.");
    }

    [Fact]
    public void infection_changed_carries_the_value_and_the_delta_of_the_write()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn(infection: 0.1f);

        // The half reads the value the receiver holds now; the write's own before-value is what the hook passes.
        Require(world.State.InfectionChanged(life.Damage, 0.04f), "The infection write did not publish.");
        var payload = PlayerStateContract.InfectionChangedPayload(life.Reference, 0.1, 0.06);
        Require(Math.Abs(payload.GetProperty("value").GetDouble() - 0.1) < 0.0001, "The value port differs.");
        Require(Math.Abs(payload.GetProperty("delta").GetDouble() - 0.06) < 0.0001, "The delta port differs.");

        Require(!world.State.InfectionChanged(life.Damage, 0.1f), "A write that changed nothing published.");
        Require(world.State.PublishedFacts == 1, "An unchanged infection published a second fact.");
    }

    [Fact]
    public void one_fact_of_one_life_claims_one_transition_number_per_transition()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();

        Require(world.State.DamageApplied(life.Damage, 4f, null, PlayerStateContract.DamageKindDirect), "The first damage did not publish.");
        Require(world.State.DamageApplied(life.Damage, 4f, null, PlayerStateContract.DamageKindDirect), "The second damage did not publish.");
        Require(world.State.PublishedFacts == 2, "Two accepted damages published " + world.State.PublishedFacts + " facts.");
        Require(world.State.Journal.Any(line => line.Contains("transition=0 ", StringComparison.Ordinal))
            && world.State.Journal.Any(line => line.Contains("transition=1 ", StringComparison.Ordinal)),
            "The transition numbers differ: " + string.Join(" | ", world.State.Journal));
    }

    [Fact]
    public void a_repeated_report_of_one_transition_is_not_dispatched_twice()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();
        Require(world.State.DamageApplied(life.Damage, 4f, null, PlayerStateContract.DamageKindDirect), "The damage did not publish.");
        // A world change drops this half's own table, so the next report starts at transition 0 again; in a world
        // that has not changed, that is the id the kernel's ledger already holds, and the kernel answers it
        // `duplicate` instead of dispatching a second event. A real checkpoint reload moves the world epoch, which
        // is what makes the id new — the game-in check list carries that one.
        world.State.BeginWorld();
        Require(!world.State.DamageApplied(life.Damage, 4f, null, PlayerStateContract.DamageKindDirect),
            "A repeated report of one transition was dispatched twice.");
        Require(world.State.PublishedFacts == 1, "The repeated report published.");
    }

    // ---- ping and acquisition ----------------------------------------------------------------------------

    [Fact]
    public void ping_publishes_once_per_ping_and_carries_the_position()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();
        var position = new double[] { 4, 5, 6 };

        Require(world.Events.Ping(life.Agent, position, null), "The ping did not publish.");
        // The game reaches two entries for one ping; the second one in the same tick at the same position is the
        // same ping and publishes nothing.
        Require(!world.Events.Ping(life.Agent, position, null), "The second entry published a second ping.");
        Require(world.Events.Ping(life.Agent, new double[] { 4, 5, 7 }, null), "A ping at another position did not publish.");
        Require(world.Events.PublishedFacts == 2, "The ping count differs: " + string.Join(" | ", world.Events.Journal));

        var payload = PlayerEventContract.PingPayload(life.Reference, position, null);
        Require(payload.GetProperty("position").EnumerateArray().Select(value => value.GetDouble()).SequenceEqual(position),
            "The ping position port differs.");
        Require(!payload.TryGetProperty("target", out _), "A ping with no nameable target wrote a target port.");
    }

    [Fact]
    public void ping_refuses_a_position_the_game_did_not_produce()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();

        Require(!world.Events.Ping(life.Agent, new double[] { 1, double.NaN, 3 }, null), "A non-finite ping published.");
        Require(!world.Events.Ping(life.Agent, null, null), "A ping without a position published.");
        Require(world.Events.PublishedFacts == 0, "A refused ping published.");
    }

    [Fact]
    public void supply_and_pickup_publish_their_catalog_kind()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();

        Require(world.Events.SupplyUsed(life.Agent, "disinfection"), "The supply use did not publish.");
        Require(world.Events.ItemPickedUp(life.Agent, "keycard"), "The pickup did not publish.");
        Require(!world.Events.SupplyUsed(life.Agent, "medigun"), "A kind outside the vocabulary published.");
        Require(world.Events.PublishedFacts == 2, "The acquisition fact count differs.");

        Require(PlayerEventContract.SupplyKind("tool_refill") == "tool_refill", "A declared supply kind was refused.");
        Require(PlayerEventContract.PickupKind("commodity_medium") == "commodity_medium", "A declared pickup kind was refused.");
        Require(PlayerEventContract.SupplyKinds.Count == 4 && PlayerEventContract.PickupKinds.Count == 9, "The kind vocabularies differ.");
    }

    [Fact]
    public void acquisition_does_not_publish_from_a_life_the_identity_no_longer_holds()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();
        world.Source.Lives.Clear();

        Require(!world.Events.SupplyUsed(life.Agent, "medikit"), "A supply use by an unrecorded life published.");
        Require(!world.Events.ItemPickedUp(life.Agent, "medikit"), "A pickup by an unrecorded life published.");
        Require(world.Events.PublishedFacts == 0, "A stale life published.");
    }

    // ---- value rows --------------------------------------------------------------------------------------

    [Fact]
    public void health_answers_the_snapshot_reading_and_refuses_an_absent_one()
    {
        var withHealth = Snapshot(health: 42, maximum: 100);
        var payload = PlayerValueReads.AnswerHealth(withHealth);
        Require(Math.Abs(payload.GetProperty("value").GetDouble() - 42) < 0.0001, "The health value differs.");
        Require(Math.Abs(payload.GetProperty("maximum").GetDouble() - 100) < 0.0001, "The health maximum differs.");
        Require(Math.Abs(payload.GetProperty("fraction").GetDouble() - 0.42) < 0.0001, "The health fraction differs.");

        var refusal = Refusal(() => PlayerValueReads.AnswerHealth(Snapshot(health: null, maximum: null)));
        Require(refusal == PlayerValueReads.HealthUnavailableCode, "A snapshot without health was answered: " + refusal);
    }

    [Fact]
    public void downed_answers_the_life_state_it_was_given()
    {
        Require(PlayerValueReads.AnswerDowned(Snapshot(lifeState: "downed")).GetProperty("value").GetBoolean(), "A downed life read as standing.");
        Require(!PlayerValueReads.AnswerDowned(Snapshot(lifeState: "alive")).GetProperty("value").GetBoolean(), "An upright life read as downed.");
        var dead = PlayerValueReads.AnswerDowned(Snapshot(lifeState: "dead"));
        Require(!dead.GetProperty("alive").GetBoolean(), "A dead life read as alive.");
    }

    [Fact]
    public void position_answers_three_coordinates_or_refuses()
    {
        var payload = PlayerValueReads.AnswerPosition(Snapshot());
        Require(payload.GetProperty("position").EnumerateArray().Select(value => value.GetDouble()).SequenceEqual(new double[] { 1, 2, 3 }),
            "The position port differs.");
    }

    [Fact]
    public void the_native_answered_values_carry_their_refusal_codes_through()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();
        world.Values.Infection = 0.35;
        world.Values.Wielded = life.Reference;
        world.Values.Ammo = new PlayerAmmo(3, 12, 44);
        world.Values.Carried = life.Reference;

        Require(Math.Abs(PlayerValueReads.AnswerInfection(world.Values, life.Reference, Snapshot()).GetProperty("value").GetDouble() - 0.35) < 0.0001,
            "The infection value differs.");
        Require(PlayerValueReads.AnswerWieldedGear(world.Values, life.Reference, Snapshot()).GetProperty("equipment").GetProperty("id").GetString()
            == life.Reference.Id, "The wielded gear entity differs.");
        var ammo = PlayerValueReads.AnswerAmmo(world.Values, life.Reference, Snapshot());
        Require(ammo.GetProperty("clip").GetInt32() == 3 && ammo.GetProperty("clip_max").GetInt32() == 12 && ammo.GetProperty("reserve").GetInt32() == 44,
            "The ammunition ports differ.");

        world.Values.AmmoReadable = false;
        Require(Refusal(() => PlayerValueReads.AnswerAmmo(world.Values, life.Reference, Snapshot())) == "no-wielded-gear",
            "An unreadable clip was answered.");
        Require(Refusal(() => PlayerValueReads.AnswerAmmo(null, life.Reference, Snapshot())) == PlayerValueReads.SourceUnavailableCode,
            "A missing source was answered.");
    }

    [Fact]
    public void a_value_read_requires_a_source_and_a_snapshot_of_the_declared_shape()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();
        Require(PlayerValueReads.AnswerCarriedItem(world.Values, life.Reference, Snapshot()).GetProperty("item").ValueKind == JsonValueKind.Null,
            "A player carrying nothing did not answer an observed absence.");
        world.Values.CarriedReadable = false;
        world.Values.CarriedCode = "backpack-unavailable";
        Require(Refusal(() => PlayerValueReads.AnswerCarriedItem(world.Values, life.Reference, Snapshot())) == "backpack-unavailable",
            "An unreadable backpack was answered.");
    }

    [Fact]
    public void the_tool_row_answers_the_class_pool_and_the_pocket_stacks_it_was_given()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();
        world.Values.Tool = new PlayerTool(life.Reference, 4, 12, 5, 2);

        var answer = PlayerValueReads.AnswerTool(world.Values, life.Reference, Snapshot());
        Require(answer.GetProperty("item").GetProperty("id").GetString() == life.Reference.Id,
            "The held item the tool numbers belong to differs.");
        Require(answer.GetProperty("ammo").GetInt32() == 4 && answer.GetProperty("ammo_max").GetInt32() == 12,
            "The class-ammunition pool differs.");
        Require(answer.GetProperty("count").GetInt32() == 5 && answer.GetProperty("stacks").GetInt32() == 2,
            "The consumable stack ports differ.");
    }

    [Fact]
    public void the_tool_row_refuses_a_player_holding_nothing_and_a_missing_source()
    {
        using var world = new PlayerFactsWorld();
        var life = world.Spawn();
        world.Values.ToolReadable = false;
        Require(Refusal(() => PlayerValueReads.AnswerTool(world.Values, life.Reference, Snapshot())) == "no-wielded-gear",
            "A player holding no pool was answered.");
        Require(Refusal(() => PlayerValueReads.AnswerTool(null, life.Reference, Snapshot())) == PlayerValueReads.SourceUnavailableCode,
            "A missing source was answered.");
    }

    /// <summary>The tool row is registered like every other value row: the row's own capability text, its binding,
    /// its support line and its evaluator name all come from the contract tables the fixture registers, so a row
    /// that lost its handler, its shape or its ports fails here rather than at a plan's load.</summary>
    [Fact]
    public void the_tool_row_is_declared_and_bound()
    {
        Require(PlayerStateContract.ValueRows().Any(row => row.GetType().GetProperty("id")!.GetValue(row) as string
            == PlayerStateContract.ToolValueCapability), "The tool row is not declared.");
        Require(PlayerStateContract.ValueShapes().ContainsKey(PlayerStateContract.ToolValueHandler),
            "The tool row has no handler shape.");
        Require(PlayerValueReads.Evaluators().ContainsKey(PlayerStateContract.ToolValueHandler),
            "The tool row has no evaluator.");
        Require(PlayerStateContract.ValueBindings().Any(binding => binding.GetType().GetProperty("capabilityId")!.GetValue(binding) as string
            == PlayerStateContract.ToolValueCapability), "The tool row is not bound.");
    }

    // ---- actions -----------------------------------------------------------------------------------------

    [Fact]
    public void damage_action_commits_the_amount_the_receiver_landed()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn(health: 100f);
        var shooter = world.Spawn();

        var result = PlayerCommandActions.Damage(
            Frame(("mitigation_policy", "receiver_rules")),
            Frame(("targets", new[] { victim.Reference }), ("source", shooter.Reference), ("amount", 25.0), ("damage_kind", 0)));

        Require(result.Status == CommandStatuses.Succeeded, "The damage was not committed: " + result.Code);
        Require(victim.Damage.Health == 75f, "The receiver's health differs: " + victim.Damage.Health);
        var row = result.Outputs.GetProperty("results").EnumerateArray().Single();
        Require(row.GetProperty("target").GetProperty("id").GetString() == victim.Reference.Id, "The result row's target differs.");
        Require(Math.Abs(row.GetProperty("amount").GetDouble() - 25) < 0.0001, "The result row's amount differs.");
        Require(row.GetProperty("target_count").GetInt32() == 1, "The result row's target count differs.");
    }

    [Fact]
    public void damage_action_refuses_what_the_entry_cannot_carry()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn();
        var shooter = world.Spawn();
        var inputs = Frame(("targets", new[] { victim.Reference }), ("source", shooter.Reference), ("amount", 5.0), ("damage_kind", 0));

        Require(PlayerCommandActions.Damage(Frame(("mitigation_policy", "ignore_armor")), inputs).Code == PlayerCommandActions.MitigationCode,
            "An armour-ignoring request was accepted.");
        Require(PlayerCommandActions.Damage(Frame(("mitigation_policy", "receiver_rules")),
            Frame(("targets", new[] { victim.Reference }), ("source", shooter.Reference), ("amount", 5.0), ("damage_kind", 3))).Code
            == PlayerCommandActions.DamageKindCode, "A damage kind this entry cannot carry was accepted.");
        Require(PlayerCommandActions.Damage(Frame(("mitigation_policy", "receiver_rules")),
            Frame(("targets", new[] { victim.Reference }), ("source", shooter.Reference), ("amount", 5.0), ("damage_kind", 0), ("limb", 1))).Code
            == PlayerCommandActions.LimbCode, "A limb index was accepted.");
        Require(PlayerCommandActions.Damage(Frame(("mitigation_policy", "receiver_rules")),
            Frame(("targets", new[] { victim.Reference }), ("source", shooter.Reference), ("amount", 0.0), ("damage_kind", 0))).Code
            == PlayerCommandActions.AmountRangeCode, "A zero amount was accepted.");
        Require(PlayerCommandActions.Damage(Frame(("mitigation_policy", "receiver_rules")),
            Frame(("targets", new[] { victim.Reference }), ("amount", 5.0), ("damage_kind", 0))).Code
            == PlayerCommandActions.SourceCode, "A request without a source was accepted.");
        Require(victim.Damage.Health == 100f, "A refused request still wrote: " + string.Join(",", victim.Damage.Calls));
    }

    [Fact]
    public void revive_action_submits_the_game_action_and_refuses_what_it_cannot_carry()
    {
        using var world = new PlayerFactsWorld();
        var fallen = world.Spawn();
        var rescuer = world.Spawn();
        world.Identity.SetLife(fallen.Reference, alive: true, downed: true);
        PlayerAgent? revived = null;
        // The game's own revive action is what clears the downed state on every machine, so the double does what
        // the native body's receive half does: the life leaves the downed state.
        Agents.AgentReplicatedActions.Revive = (target, source, where) =>
        {
            revived = target;
            world.Identity.SetLife(fallen.Reference, alive: true, downed: false);
        };
        try
        {
            var result = PlayerCommandActions.Revive(
                Frame(("interrupt_policy", "cancel"), ("cost_policy", "none")),
                Frame(("targets", new[] { fallen.Reference }), ("source", rescuer.Reference)));
            Require(result.Status == CommandStatuses.Succeeded, "The revive was not committed: " + result.Code);
            Require(revived == fallen.Agent, "The revive did not name the downed player.");

            Require(PlayerCommandActions.Revive(Frame(("interrupt_policy", "cancel"), ("cost_policy", "charge")),
                Frame(("targets", new[] { fallen.Reference }), ("source", rescuer.Reference))).Code == PlayerCommandActions.CostPolicyCode,
                "A charging revive was accepted.");
            Require(PlayerCommandActions.Revive(Frame(("interrupt_policy", "cancel"), ("cost_policy", "none")),
                Frame(("targets", new[] { fallen.Reference }), ("source", rescuer.Reference), ("duration", 30))).Code == PlayerCommandActions.DurationCode,
                "A revive with an authored duration was accepted.");
            Require(PlayerCommandActions.Revive(Frame(("interrupt_policy", "cancel"), ("cost_policy", "none")),
                Frame(("targets", new[] { fallen.Reference }), ("source", rescuer.Reference), ("restored_health", 50.0))).Code
                == PlayerCommandActions.RestoredHealthCode, "A revive with an authored health was accepted.");
            Require(PlayerCommandActions.Revive(Frame(("interrupt_policy", "cancel"), ("cost_policy", "none")),
                Frame(("targets", new[] { fallen.Reference }))).Code == PlayerCommandActions.SourceCode, "A revive without a reviver was accepted.");
        }
        finally { Agents.AgentReplicatedActions.Revive = null; }
    }

    [Fact]
    public void revive_action_refuses_a_life_that_is_not_downed()
    {
        using var world = new PlayerFactsWorld();
        var standing = world.Spawn();
        var rescuer = world.Spawn();

        var result = PlayerCommandActions.Revive(Frame(("interrupt_policy", "cancel"), ("cost_policy", "none")),
            Frame(("targets", new[] { standing.Reference }), ("source", rescuer.Reference)));
        Require(result.Status == CommandStatuses.Rejected, "A standing player was revived.");
        Require(result.Outputs.GetProperty("results").EnumerateArray().Single().GetProperty("code").GetString() == PlayerCommandActions.NotDownedCode,
            "The refusal code differs.");
    }

    [Fact]
    public void down_action_submits_the_revive_policy_and_refuses_a_life_already_down()
    {
        using var world = new PlayerFactsWorld();
        var standing = world.Spawn();

        var result = PlayerCommandActions.Down(Frame(("revive_policy", "denied")), Frame(("targets", new[] { standing.Reference })));
        Require(result.Status == CommandStatuses.Succeeded, "The down was not committed: " + result.Code);
        Require(standing.Damage.Calls.Contains("dead:False"), "The revive policy did not reach the native entry.");

        var again = PlayerCommandActions.Down(Frame(("revive_policy", "allowed")), Frame(("targets", new[] { standing.Reference })));
        Require(again.Outputs.GetProperty("results").EnumerateArray().Single().GetProperty("code").GetString() == PlayerCommandActions.AlreadyDownCode,
            "A second down was accepted.");

        var unknownPolicy = Refusal(() => PlayerCommandActions.Down(Frame(("revive_policy", "maybe")), Frame(("targets", new[] { standing.Reference }))));
        Require(unknownPolicy == PlayerCommandActions.RevivePolicyCode, "An unknown revive policy was accepted: " + unknownPolicy);
    }

    [Fact]
    public void actions_refuse_a_command_from_a_machine_that_may_not_commit()
    {
        using var world = new PlayerFactsWorld();
        var victim = world.Spawn();
        var shooter = world.Spawn();
        world.Identity.Authoritative = false;

        Require(PlayerCommandActions.Damage(Frame(("mitigation_policy", "receiver_rules")),
            Frame(("targets", new[] { victim.Reference }), ("source", shooter.Reference), ("amount", 5.0), ("damage_kind", 0))).Code
            == PlayerCommandActions.AuthorityCode, "A client command was executed.");
        Require(victim.Damage.Health == 100f, "A client command wrote.");
    }

    // ---- helpers -----------------------------------------------------------------------------------------

    private static RuntimeEntitySnapshot Snapshot(string lifeState = "alive", double? health = 42, double? maximum = 100)
        => new(new EntityReference("gtfo.player:1", 1, 1), "gtfo.player", "player", lifeState,
            Array.Empty<string>(), Array.Empty<string>(), new double[] { 1, 2, 3 }, health, maximum);

    private static JsonElement Frame(params (string Port, object? Value)[] ports)
    {
        var fields = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (port, value) in ports) fields[port] = value;
        return RuntimeJson.From(fields);
    }

    private static string Refusal(Action action)
    {
        try { action(); }
        catch (RuntimeContractException error) { return error.Code; }
        return "";
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Xunit.Sdk.XunitException(message);
    }
}
