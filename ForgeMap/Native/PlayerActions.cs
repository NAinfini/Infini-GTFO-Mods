using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>What one recipient's attempt did, in the two columns every player action's result row carries: the
/// conclusion the row reports and the commit state it can honestly claim. `Amount` is the infection row's own
/// field and stays zero for an action that has none.</summary>
internal readonly record struct PlayerActionOutcome(EntityReference Target, string Status, string CommitState, string Code, double Amount);

/// <summary>One row of `forge.result.player.teleport`, in the schema's own column order: the four fixed columns,
/// then the command's recipient count.</summary>
internal sealed record PlayerTeleportRow(EntityReference Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>One row of `forge.result.player.infection_change`: the same four fixed columns, the amount the
/// readback showed, and the command's recipient count.</summary>
internal sealed record PlayerInfectionRow(EntityReference Target, string Status,
    [property: JsonPropertyName("committed")] string CommitState, string Code,
    [property: JsonPropertyName("amount")] double Amount,
    [property: JsonPropertyName("target_count")] int TargetCount);

/// <summary>The two player-domain actions this package submits, on build 20403457. Each one ends in a game entry
/// point that is itself the replication path, and each one is refused by name when the request asks for something
/// that entry point cannot carry:
///
/// - teleport submits the game's own warp request. `PlayerAgent.RequestWarpToSync` is the host entry whose
///   session half sends `PlayerSync.SendSyncWarp` to the other machines (received by `IncomingSyncWarp`), so one
///   host call is what every client sees. The landing point is not taken on trust: the game's own
///   `PlayerAgent.SampleWarpPosition` solves it for the destination, and the agent's own locomotion state must be
///   one of its `m_warpableStates`, which is the same gate the warp applies. The destination is asked for in the
///   level's reality dimension, because the catalog's row has no dimension port and the level geometry a
///   `vector3` destination names is the reality one. The write is confirmed by the agent's own position read
///   back, never by the call having been made: a warp the game declined leaves the position where it was, which
///   is an unknown commit rather than a success.
///
/// - infection change submits the player damage base's own `ModifyInfection` entry with `sync` set, which is the
///   player receiver's replicated write: the same call sends `m_receiveModifyInfectionPacket` to the other
///   machines. The requested operation maps to the native mode where the game has one (`Set`, `Add`) and is
///   computed as an absolute value where it does not (`subtract`), and the row reports the infection the
///   receiver's own readback showed. The catalog's `resistance` is refused by name: the native path owns
///   resistance through `AgentModifier.InfectionResistance`, and pre-scaling the amount here would apply it twice.
///
/// What neither action does is invent state: no field is written directly, no client-side value is guessed, and
/// every refusal carries the code that names why.</summary>
internal static class PlayerActions
{
    /// <summary>The catalog's carrier policy this action can keep: the native warp moves the agent and touches no
    /// inventory, which is exactly `keep`. The two policies that would move or store items belong to the
    /// inventory domain's paths and are refused by name here.</summary>
    internal const string KeepInventoryPolicy = "keep";

    /// <summary>The one dimension a positional teleport can mean: the level the destination was authored in.
    /// The catalog's row has no dimension port, so this is the only member a `vector3` destination can name.</summary>
    private static readonly eDimensionIndex WarpDimension = eDimensionIndex.Reality;

    /// <summary>The game's own warp options for an authored teleport: no bots are dragged along, no warp screen
    /// effect and no warp sound are played for a scripted move.</summary>
    private static readonly PlayerAgent.WarpOptions WarpEffects = PlayerAgent.WarpOptions.None;

    /// <summary>The distance within which the agent is read back as having arrived. A warp resolves colliders on
    /// the way in, so an exact bit-for-bit match is not what the native path promises; a position still outside
    /// this radius is a warp that did not happen, and the row says so instead of claiming one.</summary>
    internal const float WarpTolerance = 0.5f;

    /// <summary>The delta below which an infection readback is the value that was asked for. The native field is a
    /// float the receiver owns, so the write is confirmed by the value it stored, not by the call returning.</summary>
    internal const float InfectionTolerance = 0.001f;

    /// <summary>The bound on one infection request, in the receiver's own unit. It exists so a plan cannot ask for
    /// a value no infection model could hold; it is not a claim about the game's own ceiling.</summary>
    internal const double MaximumAmount = 1000;

    internal const string AuthorityCode = "authority-or-phase";
    internal const string KindCode = "unsupported-recipient";
    internal const string StaleCode = "stale-or-unsupported-recipient";
    internal const string NotAliveCode = "not-alive";
    internal const string NoTargetsCode = "no-targets";
    internal const string TooManyTargetsCode = "too-many-targets";
    internal const string InventoryPolicyCode = "inventory-policy-unsupported";
    internal const string AreaCode = "area-field-unavailable";
    internal const string DestinationCode = "destination-unavailable";
    internal const string DestinationInvalidCode = "destination-invalid";
    internal const string DimensionCode = "dimension-out-of-range";
    internal const string RotationCode = "rotation-missing";
    internal const string WarpStateCode = "warp-state-refused";
    internal const string WarpUnknownCode = "warp-not-observed";
    internal const string CommitExceptionCode = "native-commit-exception";
    internal const string ReadbackExceptionCode = "readback-exception";
    internal const string OperationCode = "operation-unsupported";
    internal const string SourceCode = "source-missing";
    internal const string AmountRangeCode = "amount-out-of-range";
    internal const string CapCode = "cap-out-of-range";
    internal const string ResistanceCode = "resistance-unsupported";
    internal const string ReceiverCode = "missing-infection-receiver";
    internal const string ReceiverMismatchCode = "infection-receiver-owner-mismatch";
    internal const string StateChangedCode = "state-changed-before-commit";
    internal const string InfectionReadbackCode = "unexpected-infection-readback";

    private const string PlayerPrefix = PlayerIdentityModule.EntityKind + ":";
    private static readonly string[] Operations = { "set", "add", "subtract" };

    /// <summary>The handler table this provider's registration composes: the two handler names its own contract
    /// declares, bound to the two entry points below.</summary>
    internal static IReadOnlyDictionary<string, CommandHandler> Handlers() => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
    {
        [ForgeMap.PlayerActionContract.TeleportHandlerName] = Teleport,
        [ForgeMap.PlayerActionContract.InfectionHandlerName] = Infection
    };

    /// <summary>The shape table of the same two handlers, read from the contract so the native half cannot
    /// describe a different port layout than the one the row is declared with.</summary>
    internal static IReadOnlyDictionary<string, HandlerShape> Shapes() => ForgeMap.PlayerActionContract.Shapes();

    // ---- forge.action.player.teleport --------------------------------------------------------------------

    /// <summary>The `forge.action.player.teleport` command handler. The request is checked once, before the first
    /// recipient: a policy this action cannot keep, an area it cannot verify and a destination it cannot solve
    /// refuse the whole command, because half a teleport is not a state the plan asked for.</summary>
    internal static CommandResult Teleport(CommandContext context) => Teleport(context.Parameters, context.Inputs);

    /// <summary>The same handler over the two halves of a request it actually reads. The command identity, the
    /// plan and the event behind it are the runtime's business, so the handler is written against the node's
    /// parameters and the request frame alone — which is also what makes it drivable without a dispatch.</summary>
    internal static CommandResult Teleport(JsonElement parameters, JsonElement inputs)
    {
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        if (parameters.GetProperty("inventory_policy").GetString() != KeepInventoryPolicy)
            return CommandResult.Rejected(InventoryPolicyCode);
        // The area port names a resource kind no provider in this build registers. A destination inside an area
        // the author constrained is a different request from a bare coordinate, so it is refused rather than
        // silently treated as the bare one.
        if (Present(inputs, "area")) return CommandResult.Rejected(AreaCode);
        if (!TryVector(inputs, "destination", out var x, out var y, out var z))
            return CommandResult.Rejected(DestinationInvalidCode);
        if (!TryVector(inputs, "rotation", out var rotationX, out var rotationY, out var rotationZ))
            return CommandResult.Rejected(RotationCode);
        var targets = Targets(inputs, "players");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        // The landing point is solved once per command by the game's own sampler; a destination it cannot answer
        // for is refused before any recipient is touched.
        Vector3 landing;
        try
        {
            if (!PlayerAgent.SampleWarpPosition(WarpDimension, new Vector3(x, y, z), out landing))
                return CommandResult.Rejected(DestinationCode);
        }
        catch (Exception) { return CommandResult.Rejected(DestinationCode); }
        if (!Finite(landing)) return CommandResult.Rejected(DestinationCode);
        var look = LookDirection(rotationX, rotationY, rotationZ);

        var outcomes = new List<PlayerActionOutcome>(targets.Length);
        foreach (var target in targets) outcomes.Add(WarpOne(players, target, WarpDimension, landing, look));
        var outputs = RuntimeJson.From(new { results = TeleportRows(outcomes) });
        return Aggregate(outcomes, outputs, "teleport");
    }

    /// <summary>One recipient's warp. Every step before the native call is a read the caller could re-check, and
    /// the agent is resolved again after the call: a life replaced in between leaves the effect of the write
    /// unknown rather than attributed to whoever holds the reference now. The dimension is the caller's, because
    /// the authored teleport and the dimension warp differ in exactly that: where the landing is read back.</summary>
    private static PlayerActionOutcome WarpOne(PlayerIdentityModule players, EntityReference target,
        eDimensionIndex dimension, Vector3 landing, Vector3 look)
    {
        if (!IsKind(target, PlayerIdentityModule.EntityKind)) return Refuse(target, KindCode);
        if (!players.CanCommit) return Refuse(target, AuthorityCode);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Refuse(target, StaleCode);
        var pointer = agent.Pointer;
        bool alive;
        PlayerLocomotion.PLOC_State state;
        var accepted = default(Il2CppSystem.Collections.Generic.HashSet<PlayerLocomotion.PLOC_State>);
        try
        {
            alive = agent.Alive;
            var locomotion = agent.Locomotion;
            accepted = agent.m_warpableStates;
            state = locomotion.m_currentStateEnum;
        }
        catch (Exception) { return Refuse(target, StaleCode); }
        if (!alive) return Refuse(target, NotAliveCode);
        // The game's own warp gate: a state outside the set the agent accepts a warp in is refused here by name
        // rather than submitted and left to be ignored.
        if (accepted == null || !accepted.Contains(state)) return Refuse(target, WarpStateCode);
        if (players.CurrentAgent(target)?.Pointer != pointer) return Refuse(target, StaleCode);

        try { agent.RequestWarpToSync(dimension, landing, look, WarpEffects); }
        catch (Exception) { return Unknown(target, CommitExceptionCode); }

        var after = players.CurrentAgent(target);
        if (after == null || after.Pointer != pointer)
        {
            // The life the request was made for is no longer the life this reference names; whether the warp
            // landed on it is not observable from here.
            return Unknown(target, ReadbackExceptionCode);
        }
        Vector3 position;
        try { position = after.Position; }
        catch (Exception) { return Unknown(target, ReadbackExceptionCode); }
        if (!Finite(position)) return Unknown(target, ReadbackExceptionCode);
        if (Distance(position, landing) > WarpTolerance) return Unknown(target, WarpUnknownCode);
        return Committed(target, 0);
    }

    // ---- forge.action.player.dimension (the authored-table shape) ------------------------------------------

    /// <summary>The `forge.action.player.dimension` command's per-recipient body: the EOS family's dimension warp,
    /// which is the authored teleport above with two differences — the destination dimension is the request's own
    /// instead of the level's reality, and every recipient gets its own landing from the request's two destination
    /// collections, in order and with the table reused from the top once it runs out. The row is one capability in
    /// two shapes: this body answers the shape that carries the table, and the level-event half answers the
    /// team-event shape. The table was a row of its own (`forge.action.player.dimension_warp`) until the merge.
    ///
    /// The table is the row's `positions` and `look_dirs` collections, zipped by index, so it is checked once for
    /// the whole command: a table this layer cannot pair exactly refuses the command instead of moving the
    /// recipients it happened to parse. Each landing is then solved by the game's own sampler for the destination
    /// dimension, which is the same gate the teleport row passes, and the readback is the one the teleport row
    /// uses — a warp the game declined leaves the agent where it was, and that is an unknown commit rather than a
    /// success.</summary>
    internal static CommandResult DimensionWarp(CommandContext context) => DimensionWarp(context.Inputs);

    /// <inheritdoc cref="Teleport(JsonElement, JsonElement)"/>
    internal static CommandResult DimensionWarp(JsonElement inputs)
    {
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        if (!inputs.TryGetProperty("dimension", out var dimensionElement)
            || dimensionElement.ValueKind != JsonValueKind.Number
            || !dimensionElement.TryGetInt32(out int dimension)
            || dimension < 0 || dimension >= (int)eDimensionIndex.MAX_COUNT)
            return CommandResult.Rejected(DimensionCode);
        if (!inputs.TryGetProperty("positions", out var positions)
            || !inputs.TryGetProperty("look_dirs", out var lookDirs))
            return CommandResult.Rejected(DimensionDestinations.InvalidCode);
        if (!DimensionDestinations.TryRead(positions, lookDirs, out var destinations, out var locationsCode))
            return CommandResult.Rejected(locationsCode);
        var targets = Targets(inputs, "players");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        var destinationDimension = (eDimensionIndex)dimension;
        var outcomes = new List<PlayerActionOutcome>(targets.Length);
        for (var index = 0; index < targets.Length; index++)
        {
            var destination = DimensionDestinations.At(destinations, index);
            var position = new Vector3(destination.Position[0], destination.Position[1], destination.Position[2]);
            Vector3 landing;
            try
            {
                if (!PlayerAgent.SampleWarpPosition(destinationDimension, position, out landing))
                {
                    outcomes.Add(Refuse(targets[index], DestinationCode));
                    continue;
                }
            }
            catch (Exception) { outcomes.Add(Refuse(targets[index], DestinationCode)); continue; }
            if (!Finite(landing)) { outcomes.Add(Refuse(targets[index], DestinationCode)); continue; }
            var look = new Vector3(destination.LookDirection[0], destination.LookDirection[1], destination.LookDirection[2]);
            outcomes.Add(WarpOne(players, targets[index], destinationDimension, landing, look));
        }
        var outputs = RuntimeJson.From(new { results = TeleportRows(outcomes) });
        return Aggregate(outcomes, outputs, "dimension-warp");
    }

    // ---- forge.action.player.infection_change ------------------------------------------------------------

    /// <summary>The `forge.action.player.infection_change` command handler. The operation and the amount are
    /// checked once for the command; the ceiling is applied per recipient, because it bounds the value that
    /// recipient's receiver ends up holding.</summary>
    internal static CommandResult Infection(CommandContext context) => Infection(context.Parameters, context.Inputs);

    /// <inheritdoc cref="Teleport(JsonElement, JsonElement)"/>
    internal static CommandResult Infection(JsonElement parameters, JsonElement inputs)
    {
        if (PlayerIdentityModule.Current is not { } players || !players.CanCommit)
            return CommandResult.Rejected(AuthorityCode);
        var operation = parameters.GetProperty("operation").GetString();
        if (operation == null || !Operations.Contains(operation, StringComparer.Ordinal))
            return CommandResult.Rejected(OperationCode);
        // The source is a required, kernel-validated entity reference; the native `pInfection` value carries no
        // source field, so nothing here invents one. The runtime validates the reference before dispatch.
        if (!inputs.TryGetProperty("source", out var source) || source.ValueKind != JsonValueKind.Object)
            return CommandResult.Rejected(SourceCode);
        _ = RuntimeJson.Entity(source);
        if (!inputs.TryGetProperty("amount", out var amountElement) || amountElement.ValueKind != JsonValueKind.Number
            || !amountElement.TryGetDouble(out double amount) || !double.IsFinite(amount) || amount < 0 || amount > MaximumAmount)
            return CommandResult.Rejected(AmountRangeCode);
        // The native path applies infection resistance itself (`AgentModifier.InfectionResistance`); scaling the
        // amount here as well would apply it twice, which is not what either number means.
        if (Present(inputs, "resistance")) return CommandResult.Rejected(ResistanceCode);
        double? cap = null;
        if (Present(inputs, "cap"))
        {
            if (!inputs.GetProperty("cap").TryGetDouble(out double capValue) || !double.IsFinite(capValue) || capValue <= 0)
                return CommandResult.Rejected(CapCode);
            cap = capValue;
        }
        var targets = Targets(inputs, "targets");
        if (targets.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TooManyTargetsCode);

        var outcomes = new List<PlayerActionOutcome>(targets.Length);
        foreach (var target in targets) outcomes.Add(InfectOne(players, target, operation, amount, cap));
        var outputs = RuntimeJson.From(new { results = InfectionRows(outcomes) });
        return Aggregate(outcomes, outputs, "infection");
    }

    /// <summary>One recipient's infection change. The receiver is read before the write, the value the operation
    /// asks for is computed from that read, the same receiver is proved again before anything is submitted, and
    /// the row reports what the receiver holds afterwards — never what was requested.</summary>
    private static PlayerActionOutcome InfectOne(PlayerIdentityModule players, EntityReference target, string operation, double amount, double? cap)
    {
        if (!IsKind(target, PlayerIdentityModule.EntityKind)) return Refuse(target, KindCode);
        if (!players.CanCommit) return Refuse(target, AuthorityCode);
        var agent = players.CurrentAgent(target);
        if (agent == null) return Refuse(target, StaleCode);
        var pointer = agent.Pointer;
        bool alive;
        Dam_PlayerDamageBase? damage;
        float current;
        try
        {
            alive = agent.Alive;
            damage = agent.Damage;
            if (damage == null || damage.Pointer == IntPtr.Zero || !damage.IsSetup) return Refuse(target, ReceiverCode);
            if (damage.Owner == null || damage.Owner.Pointer != pointer) return Refuse(target, ReceiverMismatchCode);
            current = damage.Infection;
        }
        catch (Exception) { return Refuse(target, ReceiverCode); }
        if (!alive) return Refuse(target, NotAliveCode);
        if (!float.IsFinite(current) || current < 0) return Refuse(target, ReceiverCode);

        // `add` is the one operation the native mode itself carries, so it is submitted as the delta it is; the
        // ceiling then bounds the delta by what is left under it, and an `add` never lowers a value already above
        // the ceiling. Every other operation is an absolute value the receiver should end up holding: `set` names
        // it, and `subtract` computes it because `pInfectionMode` has `Set` and `Add` only.
        double submitted, desired;
        if (operation == "add")
        {
            submitted = cap.HasValue ? Math.Max(0, Math.Min(amount, cap.Value - current)) : amount;
            desired = current + submitted;
        }
        else
        {
            desired = operation == "set" ? amount : Math.Max(0, current - amount);
            if (cap.HasValue) desired = Math.Min(desired, cap.Value);
            submitted = desired;
        }
        var mode = operation == "add" ? pInfectionMode.Add : pInfectionMode.Set;
        var value = new pInfection { amount = (float)submitted, mode = mode, effect = pInfectionEffect.None };

        // A native read can re-enter other mods, so the state the value was computed from is proved again before
        // the write; a change is a refusal, never a value applied to a stale read.
        if (!Reads(players, target, pointer, current, damage.Pointer)) return Refuse(target, StateChangedCode);

        try { damage.ModifyInfection(value, true, true); }
        catch (Exception) { return Unknown(target, CommitExceptionCode); }

        var after = players.CurrentAgent(target)?.Damage;
        if (after == null || after.Pointer != damage.Pointer || after.Owner == null || after.Owner.Pointer != pointer)
            return Unknown(target, ReadbackExceptionCode);
        float stored;
        try { stored = after.Infection; }
        catch (Exception) { return Unknown(target, ReadbackExceptionCode); }
        if (!float.IsFinite(stored)) return Unknown(target, ReadbackExceptionCode);
        if (Math.Abs(stored - desired) > InfectionTolerance) return Unknown(target, InfectionReadbackCode);
        return Committed(target, stored);
    }

    /// <summary>Proves the receiver the value was computed from is still the one the reference names: the same
    /// agent, the same damage base, and the infection value the computation started from.</summary>
    private static bool Reads(PlayerIdentityModule players, EntityReference target, IntPtr pointer, float current, IntPtr receiver)
    {
        var agent = players.CurrentAgent(target);
        var damage = agent?.Damage;
        return agent != null && agent.Pointer == pointer && damage != null && damage.Pointer == receiver
            && damage.Owner != null && damage.Owner.Pointer == pointer && damage.Infection == current;
    }

    // ---- shared ------------------------------------------------------------------------------------------

    /// <summary>The catalogue's `execution_outcome` members a per-recipient row can report, paired with the
    /// commit state that row may carry: a confirmed write is a success, a known-uncommitted refusal is a
    /// rejection, and a write whose effect cannot be read back is a failure with an unknown commit.</summary>
    private static PlayerActionOutcome Committed(EntityReference target, double amount)
        => new(target, CommandStatuses.Succeeded, CommitStates.Confirmed, "committed", amount);

    private static PlayerActionOutcome Refuse(EntityReference target, string code)
        => new(target, CommandStatuses.Rejected, CommitStates.None, code, 0);

    private static PlayerActionOutcome Unknown(EntityReference target, string code)
        => new(target, CommandStatuses.Failed, CommitStates.Unknown, code, 0);

    private static IReadOnlyList<PlayerTeleportRow> TeleportRows(IReadOnlyList<PlayerActionOutcome> outcomes)
    {
        var rows = new List<PlayerTeleportRow>(outcomes.Count);
        foreach (var outcome in outcomes)
            rows.Add(new PlayerTeleportRow(outcome.Target, outcome.Status, outcome.CommitState, outcome.Code, outcomes.Count));
        return rows;
    }

    private static IReadOnlyList<PlayerInfectionRow> InfectionRows(IReadOnlyList<PlayerActionOutcome> outcomes)
    {
        var rows = new List<PlayerInfectionRow>(outcomes.Count);
        foreach (var outcome in outcomes)
            rows.Add(new PlayerInfectionRow(outcome.Target, outcome.Status, outcome.CommitState, outcome.Code, outcome.Amount, outcomes.Count));
        return rows;
    }

    /// <summary>The command-level conclusion of a run of recipients, by the same rule the provider's other
    /// actions use: every recipient confirmed is a success, none confirmed is a rejection when nothing was
    /// submitted or a failure when a commit is unknown, and anything in between is partial with the weaker
    /// commit state. `name` is only used for the all-rejected and all-unknown fallback codes.</summary>
    private static CommandResult Aggregate(IReadOnlyList<PlayerActionOutcome> outcomes, JsonElement outputs, string name)
    {
        int committed = 0, unknown = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.CommitState == CommitStates.Confirmed) committed++;
            else if (outcome.CommitState == CommitStates.Unknown) unknown++;
        }
        if (committed == outcomes.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(outcomes, name + "-all-rejected"), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(outcomes, name + "-all-unknown"), "", outputs);
    }

    private static string Single(IReadOnlyList<PlayerActionOutcome> outcomes, string fallback)
    {
        if (outcomes.Count == 1) return outcomes[0].Code;
        var first = outcomes[0].Code;
        foreach (var outcome in outcomes) if (outcome.Code != first) return fallback;
        return first;
    }

    /// <summary>One recipient collection from the request frame, in the plan's own order and with no dedupe: a
    /// repeated reference is two rows, exactly as it is two recipients.</summary>
    private static EntityReference[] Targets(JsonElement inputs, string port)
        => inputs.GetProperty(port).EnumerateArray().Select(RuntimeJson.Entity).ToArray();

    /// <summary>The look direction a warp takes, from the catalog's `rotation` port. The port carries euler angles
    /// in degrees and the native entry takes a direction, so the forward vector is built with Unity's own euler
    /// order (Z, then X, then Y): roll cannot tilt a forward vector, pitch lifts it and yaw turns it.</summary>
    internal static Vector3 LookDirection(float x, float y, float z)
    {
        float pitch = DegreesToRadians(x), yaw = DegreesToRadians(y);
        float cosPitch = MathF.Cos(pitch);
        return new Vector3(MathF.Sin(yaw) * cosPitch, -MathF.Sin(pitch), MathF.Cos(yaw) * cosPitch);
    }

    private static float DegreesToRadians(float degrees) => degrees * (MathF.PI / 180f);

    private static bool TryVector(JsonElement inputs, string port, out float x, out float y, out float z)
    {
        x = y = z = 0;
        if (!inputs.TryGetProperty(port, out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 3)
            return false;
        var coordinates = value.EnumerateArray().ToArray();
        if (!TryCoordinate(coordinates[0], out x) || !TryCoordinate(coordinates[1], out y) || !TryCoordinate(coordinates[2], out z))
            return false;
        return true;
    }

    private static bool TryCoordinate(JsonElement value, out float result)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetDouble(out double number) || !double.IsFinite(number)) return false;
        // A coordinate the game's own float positions cannot hold is not a destination.
        if (number < -float.MaxValue || number > float.MaxValue) return false;
        result = (float)number;
        return true;
    }

    private static bool Finite(Vector3 value) => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z);

    private static float Distance(Vector3 left, Vector3 right)
    {
        float dx = left.x - right.x, dy = left.y - right.y, dz = left.z - right.z;
        return MathF.Sqrt(dx * dx + dy * dy + dz * dz);
    }

    private static bool IsKind(EntityReference reference, string kind)
        => reference.Id != null && reference.Id.StartsWith(kind + ":", StringComparison.Ordinal);

    /// <summary>An input the plan actually supplied: absent, or a present null, is the same "not asked for".</summary>
    private static bool Present(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value)
           && value.ValueKind != JsonValueKind.Null
           && value.ValueKind != JsonValueKind.Undefined;
}