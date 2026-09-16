using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Agents;
using ForgeRuntime.Framework;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>One row of `forge.result.combat.damage`, in the schema's own column order: the four fixed columns, the
/// amount the receiver landed, and how many recipients the command had. The two multi-word columns carry the
/// schema's own spelling, because the row is the schema's and not a C# name of it.</summary>
internal sealed record PlayerDamageRow(EntityReference Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    double Amount, [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>One row of `forge.result.combat.revive`: the four fixed columns, the revive duration the game's own
/// interaction reported, and the recipient count.</summary>
internal sealed record PlayerReviveRow(EntityReference Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    int Duration, [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>One row of `forge.result.player.down`: the four fixed columns and the recipient count.</summary>
internal sealed record PlayerDownRow(EntityReference Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>The three write actions of the player domain. Each one is a host action that ends in a game entry
/// point which replicates by itself, so this half never writes a field a client would have to guess at:
///
/// - damage submits the receiver's own `BulletDamage` — the packet-sending half of the game's damage path — with
///   the attacker the plan named and the victim's own position. The amount the game lands is read back from the
///   receiver's health, so the row reports what happened and not what was asked for. `damage_kind` is refused
///   unless it names the one kind this entry carries, `limb` is refused because the entry has no limb argument,
///   and `mitigation_policy` is refused unless it is `receiver_rules`: the game's own rules are the only mitigation
///   this path can apply.
/// - revive submits `AgentReplicatedActions.PlayerReviveAction`, the game's own host-authoritative revive action,
///   from the reviver the plan named to the downed target. `duration` and `restored_health` are refused by name:
///   the game owns the revive interaction's length and the health it restores, and a plan that asked for either
///   would be promised something this build cannot keep.
/// - down submits the receiver's `SendSetDead` with the row's one structural parameter as its argument. The
///   argument is the game's own `allowRevive` flag, so the two behaviours the game has are two authored choices.
///
/// Every refusal happens before a write, and every write is confirmed by reading the life back: an effect that
/// cannot be observed is reported as an unknown commit, never as a success.</summary>
internal static class PlayerCommandActions
{
    internal const string AuthorityCode = "authority-or-phase";
    internal const string KindCode = "unsupported-recipient";
    internal const string StaleCode = "stale-or-unsupported-recipient";
    internal const string NoTargetsCode = "no-targets";
    internal const string TooManyTargetsCode = "too-many-targets";
    internal const string AmountRangeCode = "amount-out-of-range";
    internal const string MitigationCode = "mitigation-policy-unsupported";
    internal const string DamageKindCode = "damage-kind-unsupported";
    internal const string LimbCode = "limb-unsupported";
    internal const string SourceCode = "source-missing";
    internal const string ReceiverCode = "missing-health-receiver";
    internal const string NotAliveCode = "not-alive";
    internal const string NotDownedCode = "not-downed";
    internal const string AlreadyDownCode = "already-down";
    internal const string DurationCode = "duration-unsupported";
    internal const string RestoredHealthCode = "restored-health-unsupported";
    internal const string CostPolicyCode = "cost-policy-unsupported";
    internal const string RevivePolicyCode = "revive-policy-unsupported";
    internal const string CommitExceptionCode = "native-commit-exception";
    internal const string ReadbackExceptionCode = "readback-exception";
    internal const string DamageUnknownCode = "damage-not-observed";
    internal const string ReviveUnknownCode = "revive-not-observed";
    internal const string DownUnknownCode = "down-not-observed";

    /// <summary>The one mitigation policy the game's own rules can express.</summary>
    internal const string ReceiverRulesPolicy = "receiver_rules";
    /// <summary>The one revive cost policy this action can keep: the game's revive action charges nothing, and a
    /// plan that asked it to charge or consume something would not be honoured.</summary>
    internal const string NoCostPolicy = "none";

    internal const double MinimumAmount = 0.000001;
    internal const double MaximumAmount = 1000000;

    /// <summary>The handler table this provider's registration composes.</summary>
    internal static IReadOnlyDictionary<string, CommandHandler> Handlers() => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    {
        [ForgeMap.PlayerCommandContract.DamageHandlerName] = Damage,
        [ForgeMap.PlayerCommandContract.ReviveHandlerName] = Revive,
        [ForgeMap.PlayerCommandContract.DownHandlerName] = Down
    };

    // ---- forge.action.combat.damage ----------------------------------------------------------------------

    internal static CommandResult Damage(CommandContext context) => Damage(context.Parameters, context.Inputs);

    /// <summary>The same handler over the two halves of the request it reads, so a case can drive it without a
    /// dispatch.</summary>
    internal static CommandResult Damage(JsonElement parameters, JsonElement inputs)
    {
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        if (parameters.GetProperty("mitigation_policy").GetString() != ReceiverRulesPolicy)
            return CommandResult.Rejected(MitigationCode);
        // An enum port's value is the member's index in the declared set, which is how the enemy provider reads
        // the same input. The one kind this path can carry is the bullet entry the catalog spells `direct`.
        if (!inputs.TryGetProperty("damage_kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.Number
            || !kindValue.TryGetInt32(out int kind) || kind != ForgeMap.PlayerStateContract.DamageKindDirect)
            return CommandResult.Rejected(DamageKindCode);
        if (Present(inputs, "limb")) return CommandResult.Rejected(LimbCode);
        if (!TryAmount(inputs, out double amount)) return CommandResult.Rejected(AmountRangeCode);
        if (!TryAgent(players, inputs, "source", out var source)) return CommandResult.Rejected(SourceCode);
        var targets = Targets(inputs, "targets");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        var outcomes = new List<PlayerDamageOutcome>(targets.Length);
        foreach (var target in targets) outcomes.Add(DamageOne(players, target, source, amount));
        var outputs = RuntimeJson.From(new { results = DamageRows(outcomes) });
        return Aggregate(outcomes, outputs, "damage");
    }

    private static PlayerDamageOutcome DamageOne(PlayerIdentityModule players, EntityReference target, PlayerAgent source, double amount)
    {
        if (!IsKind(target)) return RefuseDamage(target, KindCode);
        if (!players.CanCommit) return RefuseDamage(target, AuthorityCode);
        if (!PlayerHealthReceiver.TryRead(target, out var before, out var code)) return RefuseDamage(target, code);
        if (!before.Alive) return RefuseDamage(target, NotAliveCode);
        var agent = players.CurrentAgent(target);
        if (agent == null || agent.Pointer != before.AgentPointer) return RefuseDamage(target, StaleCode);
        Vector3 position;
        try { position = agent.Position; }
        catch (Exception) { return RefuseDamage(target, StaleCode); }
        // The receiver is resolved once more before the write: a life replaced since the preflight is refused
        // rather than written through.
        if (!PlayerHealthReceiver.TryRead(target, out var current, out var recode) || current != before)
            return RefuseDamage(target, recode.Length == 0 ? StaleCode : recode);

        try
        {
            var damage = players.CurrentAgent(target)?.Damage;
            if (damage == null || damage.Pointer != before.ReceiverPointer) return RefuseDamage(target, ReceiverCode);
            damage.BulletDamage((float)amount, source, position, Vector3.zero, Vector3.zero);
        }
        catch (Exception) { return UnknownDamage(target, CommitExceptionCode); }

        if (!PlayerHealthReceiver.TryRead(target, out var after, out _)) return UnknownDamage(target, ReadbackExceptionCode);
        if (after.AgentPointer != before.AgentPointer || after.ReceiverPointer != before.ReceiverPointer)
            return UnknownDamage(target, ReadbackExceptionCode);
        double landed = before.Health - after.Health;
        // A damage that landed nothing is not a committed damage: the receiver may have refused it, or applied a
        // smaller amount than the game's own float encoding can hold. Both are unknown, never success.
        if (!(landed > 0)) return UnknownDamage(target, DamageUnknownCode);
        return CommittedDamage(target, landed);
    }

    // ---- forge.action.combat.revive ----------------------------------------------------------------------

    internal static CommandResult Revive(CommandContext context) => Revive(context.Parameters, context.Inputs);

    /// <summary>The same handler over the two halves of the request it reads.</summary>
    internal static CommandResult Revive(JsonElement parameters, JsonElement inputs)
    {
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        _ = parameters.GetProperty("interrupt_policy").GetString();
        if (parameters.GetProperty("cost_policy").GetString() != NoCostPolicy)
            return CommandResult.Rejected(CostPolicyCode);
        // The game owns the revive interaction's length and the health it restores. A request that carries either
        // is refused rather than answered with a revive that ignores what it asked for.
        if (Present(inputs, "duration")) return CommandResult.Rejected(DurationCode);
        if (Present(inputs, "restored_health")) return CommandResult.Rejected(RestoredHealthCode);
        if (!TryAgent(players, inputs, "source", out var source)) return CommandResult.Rejected(SourceCode);
        var targets = Targets(inputs, "targets");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        var outcomes = new List<PlayerReviveOutcome>(targets.Length);
        foreach (var target in targets) outcomes.Add(ReviveOne(players, target, source));
        var outputs = RuntimeJson.From(new { results = ReviveRows(outcomes) });
        return Aggregate(outcomes, outputs, "revive");
    }

    private static PlayerReviveOutcome ReviveOne(PlayerIdentityModule players, EntityReference target, PlayerAgent source)
    {
        if (!IsKind(target)) return RefuseRevive(target, KindCode);
        if (!players.CanCommit) return RefuseRevive(target, AuthorityCode);
        if (players.LifeOf(target) is not { } life) return RefuseRevive(target, StaleCode);
        if (!life.Alive) return RefuseRevive(target, NotAliveCode);
        // A revive is only meaningful for a life the game itself has put in the downed state.
        if (!life.Downed) return RefuseRevive(target, NotDownedCode);
        var agent = players.CurrentAgent(target);
        if (agent == null) return RefuseRevive(target, StaleCode);
        var pointer = agent.Pointer;
        Vector3 where;
        try { where = agent.Position; }
        catch (Exception) { return RefuseRevive(target, StaleCode); }

        try { AgentReplicatedActions.PlayerReviveAction(agent, source, where); }
        catch (Exception) { return UnknownRevive(target, CommitExceptionCode); }

        if (players.CurrentAgent(target)?.Pointer != pointer) return UnknownRevive(target, ReadbackExceptionCode);
        if (players.LifeOf(target) is not { } after) return UnknownRevive(target, ReadbackExceptionCode);
        if (after.Downed) return UnknownRevive(target, ReviveUnknownCode);
        return CommittedRevive(target);
    }

    // ---- forge.action.player.down ------------------------------------------------------------------------

    internal static CommandResult Down(CommandContext context) => Down(context.Parameters, context.Inputs);

    /// <summary>The same handler over the two halves of the request it reads.</summary>
    internal static CommandResult Down(JsonElement parameters, JsonElement inputs)
    {
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        bool allowRevive;
        switch (parameters.GetProperty("revive_policy").GetString())
        {
            case "allowed": allowRevive = true; break;
            case "denied": allowRevive = false; break;
            default: throw new RuntimeContractException(RevivePolicyCode, "Unknown revive policy.");
        }
        var targets = Targets(inputs, "targets");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        var outcomes = new List<PlayerDownOutcome>(targets.Length);
        foreach (var target in targets) outcomes.Add(DownOne(players, target, allowRevive));
        var outputs = RuntimeJson.From(new { results = DownRows(outcomes) });
        return Aggregate(outcomes, outputs, "down");
    }

    private static PlayerDownOutcome DownOne(PlayerIdentityModule players, EntityReference target, bool allowRevive)
    {
        if (!IsKind(target)) return RefuseDown(target, KindCode);
        if (!players.CanCommit) return RefuseDown(target, AuthorityCode);
        if (players.LifeOf(target) is not { } life) return RefuseDown(target, StaleCode);
        if (!life.Alive || life.Downed) return RefuseDown(target, AlreadyDownCode);
        if (!PlayerHealthReceiver.TryRead(target, out var before, out var code)) return RefuseDown(target, code);

        try
        {
            var damage = players.CurrentAgent(target)?.Damage;
            if (damage == null || damage.Pointer != before.ReceiverPointer) return RefuseDown(target, ReceiverCode);
            damage.SendSetDead(allowRevive);
        }
        catch (Exception) { return UnknownDown(target, CommitExceptionCode); }

        // The confirmation is the life's own state and not the health receiver: the game's dead transition makes
        // the receiver unreadable on purpose (its read refuses a life that is no longer alive), so a readback
        // through health would report every successful down as unknown.
        if (players.CurrentAgent(target)?.Pointer != before.AgentPointer) return UnknownDown(target, ReadbackExceptionCode);
        if (players.LifeOf(target) is not { } after) return UnknownDown(target, ReadbackExceptionCode);
        if (after.Alive && !after.Downed) return UnknownDown(target, DownUnknownCode);
        return CommittedDown(target);
    }

    // ---- shared ------------------------------------------------------------------------------------------

    private readonly record struct PlayerDamageOutcome(EntityReference Target, string Status, string CommitState,
        string Code, double Amount);

    private readonly record struct PlayerReviveOutcome(EntityReference Target, string Status, string CommitState,
        string Code);

    private readonly record struct PlayerDownOutcome(EntityReference Target, string Status, string CommitState,
        string Code);

    private static PlayerDamageOutcome CommittedDamage(EntityReference target, double amount)
        => new(target, CommandStatuses.Succeeded, CommitStates.Confirmed, "committed", amount);

    private static PlayerReviveOutcome CommittedRevive(EntityReference target)
        => new(target, CommandStatuses.Succeeded, CommitStates.Confirmed, "committed");

    private static PlayerDownOutcome CommittedDown(EntityReference target)
        => new(target, CommandStatuses.Succeeded, CommitStates.Confirmed, "committed");

    private static PlayerDamageOutcome RefuseDamage(EntityReference target, string code)
        => new(target, CommandStatuses.Rejected, CommitStates.None, code, 0);

    private static PlayerDamageOutcome UnknownDamage(EntityReference target, string code)
        => new(target, CommandStatuses.Failed, CommitStates.Unknown, code, 0);

    private static PlayerReviveOutcome RefuseRevive(EntityReference target, string code)
        => new(target, CommandStatuses.Rejected, CommitStates.None, code);

    private static PlayerReviveOutcome UnknownRevive(EntityReference target, string code)
        => new(target, CommandStatuses.Failed, CommitStates.Unknown, code);

    private static PlayerDownOutcome RefuseDown(EntityReference target, string code)
        => new(target, CommandStatuses.Rejected, CommitStates.None, code);

    private static PlayerDownOutcome UnknownDown(EntityReference target, string code)
        => new(target, CommandStatuses.Failed, CommitStates.Unknown, code);

    private static IReadOnlyList<PlayerDamageRow> DamageRows(IReadOnlyList<PlayerDamageOutcome> outcomes)
    {
        var rows = new List<PlayerDamageRow>(outcomes.Count);
        foreach (var outcome in outcomes)
            rows.Add(new PlayerDamageRow(outcome.Target, outcome.Status, outcome.CommitState, outcome.Code,
                outcome.Amount, outcomes.Count));
        return rows;
    }

    private static IReadOnlyList<PlayerReviveRow> ReviveRows(IReadOnlyList<PlayerReviveOutcome> outcomes)
    {
        var rows = new List<PlayerReviveRow>(outcomes.Count);
        foreach (var outcome in outcomes)
            rows.Add(new PlayerReviveRow(outcome.Target, outcome.Status, outcome.CommitState, outcome.Code, 0, outcomes.Count));
        return rows;
    }

    private static IReadOnlyList<PlayerDownRow> DownRows(IReadOnlyList<PlayerDownOutcome> outcomes)
    {
        var rows = new List<PlayerDownRow>(outcomes.Count);
        foreach (var outcome in outcomes)
            rows.Add(new PlayerDownRow(outcome.Target, outcome.Status, outcome.CommitState, outcome.Code, outcomes.Count));
        return rows;
    }

    /// <summary>The command-level conclusion of a run of recipients: every recipient confirmed is a success, none
    /// confirmed is a rejection when nothing was submitted or a failure when a commit is unknown, and anything in
    /// between is partial with the weaker commit state.</summary>
    private static CommandResult Aggregate<T>(IReadOnlyList<T> outcomes, JsonElement outputs, string name)
        where T : struct
    {
        int committed = 0, unknown = 0;
        foreach (var outcome in outcomes)
        {
            string state = State(outcome);
            if (state == CommitStates.Confirmed) committed++;
            else if (state == CommitStates.Unknown) unknown++;
        }
        if (committed == outcomes.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(outcomes, name + "-all-rejected"), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(outcomes, name + "-all-unknown"), "", outputs);
    }

    private static string State<T>(T outcome) where T : struct => outcome switch
    {
        PlayerDamageOutcome damage => damage.CommitState,
        PlayerReviveOutcome revive => revive.CommitState,
        PlayerDownOutcome down => down.CommitState,
        _ => CommitStates.None
    };

    private static string Code<T>(T outcome) where T : struct => outcome switch
    {
        PlayerDamageOutcome damage => damage.Code,
        PlayerReviveOutcome revive => revive.Code,
        PlayerDownOutcome down => down.Code,
        _ => ""
    };

    private static string Single<T>(IReadOnlyList<T> outcomes, string fallback) where T : struct
    {
        if (outcomes.Count == 1) return Code(outcomes[0]);
        var first = Code(outcomes[0]);
        foreach (var outcome in outcomes) if (Code(outcome) != first) return fallback;
        return first;
    }

    /// <summary>One recipient collection from the request frame, in the plan's own order and with no dedupe: a
    /// repeated reference is two rows, exactly as it is two recipients.</summary>
    private static EntityReference[] Targets(JsonElement inputs, string port)
        => inputs.GetProperty(port).EnumerateArray().Select(RuntimeJson.Entity).ToArray();

    /// <summary>The player an entity port names, resolved to the agent this process may write through. A port that
    /// is absent, is not this provider's kind, or names a life the identity no longer holds is refused by the
    /// caller with its own code.</summary>
    private static bool TryAgent(PlayerIdentityModule players, JsonElement inputs, string port, out PlayerAgent agent)
    {
        agent = null!;
        if (!inputs.TryGetProperty(port, out var value) || value.ValueKind != JsonValueKind.Object) return false;
        var reference = RuntimeJson.Entity(value);
        if (!IsKind(reference)) return false;
        if (players.CurrentAgent(reference) is not { } found) return false;
        agent = found;
        return true;
    }

    /// <summary>Whether the reference names an entity of this provider's kind. The prefix is the identity half's
    /// own, so a reference from another domain is refused before any read.</summary>
    private static bool IsKind(EntityReference reference)
        => reference.Id.StartsWith(PlayerIdentityModule.EntityKind + ":", StringComparison.Ordinal);

    private static bool Present(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null;

    private static bool TryAmount(JsonElement inputs, out double amount)
    {
        amount = 0;
        if (!inputs.TryGetProperty("amount", out var value) || value.ValueKind != JsonValueKind.Number) return false;
        if (!value.TryGetDouble(out double number) || !double.IsFinite(number)) return false;
        if (number < MinimumAmount || number > MaximumAmount) return false;
        amount = number;
        return true;
    }
}
