using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.query.player.movement_state` row (ruling R1, generic-player batch): the movement state a
/// player's own locomotion machine is in, how long it has been there, and — when the plan named a set of members —
/// whether the live state is one of them.
///
/// The state is the native `PlayerLocomotion.PLOC_State` member itself, read from the agent's own
/// `m_currentStateEnum` (dump.cs 656698 lists the eighteen members and their order, 655466 the machine's own
/// property). Nothing here is a Forge-side ledger: the read is the state machine's current state at the moment the
/// query step runs, so a plan that asks while a body is mid-jump is answered with `jump` and not with the last
/// state this package happened to observe.
///
/// The row is the player value family's own kind and execution (`kind: state`, `execution: query`), but it does
/// not share `PlayerStateContract`'s evaluator table: those rows answer from the published
/// `RuntimeEntitySnapshot`, which carries a life's health, position and downed flag, and the locomotion state is
/// not part of it. The read is made from the agent the kernel's own reference resolves to, through the same
/// `PlayerIdentityModule` every other player row of this provider resolves through.
///
/// Two ports of the draft are deliberately narrower than the recipe's sketch:
///
/// - `since` is the seconds the native state has been held, computed from the machine's own `m_changeStateTime`
///   against the game clock. An evaluator runs without the kernel's tick — the read-only boundary carries no clock
///   — so a tick count could only be invented here, and an invented tick would be a second clock beside the one the
///   runtime advances.
/// - there is no `in_combat` port. The ruling asked this row for "whether the player is in combat"; the game has no
///   such player flag. `PlayerStamina` carries `StaminaRegen`, `StaminaRegenInCombat` and `StaminaRegenOutOfCombat`
///   (dump.cs 541450-541452) — three rates, not a state — and deriving the flag by comparing them would answer
///   "in combat" for every configuration whose two rates happen to be equal. The row answers what it can read and
///   the report lists the missing port.</summary>
public static class PlayerMovementStateContract
{
    public const string CapabilityId = "forge.query.player.movement_state";
    public const string HandlerName = "gtfo.player.movement_state";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.player.movement_state";

    /// <summary>The eighteen members of the native `PLOC_State` enum, in the enum's own order, spelled the way the
    /// card layer names them. An index in this array is the member's native value, which is what the row's own
    /// enum port carries and what the native read compares against.</summary>
    public static readonly string[] States =
    {
        "stand", "crouch", "run", "jump", "fall", "land", "stunned", "downed", "climb_ladder", "on_terminal",
        "melee", "empty", "grabbed_by_trap", "grabbed_by_tank", "testing", "in_elevator", "grabbed_by_pouncer",
        "stand_still"
    };

    /// <summary>The refusal an input that names something other than a player gets. The row reads one entity kind
    /// and says so instead of reporting a state no player had.</summary>
    public const string InputKindCode = "movement-state-input-kind";
    /// <summary>The refusal a player this provider no longer holds gets.</summary>
    public const string InputStaleCode = "movement-state-input-stale";
    /// <summary>The refusal a locomotion state outside the declared members gets: a native value no member names is
    /// reported as itself rather than mapped onto the nearest state.</summary>
    public const string StateCode = "movement-state-unknown";

    /// <summary>The one shape of the handler: the player the read is about, and the two ports it answers. The
    /// `set` parameter and the `in_set` output the row used to carry are deleted: a membership question is what
    /// the generic comparison cards answer, and this row answers state, not set membership.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("player").Outputs("state", "since");

    /// <summary>The capability row, spelled the way a registration declares it.</summary>
    public const string CapabilityRowJson = """
    {
      "id": "forge.query.player.movement_state",
      "owner": "forge.module.gtfo.map",
      "kind": "state",
      "label": "玩家移动状态",
      "version": "1.0.0",
      "parameters": { "description": "读一个玩家当前的原生移动状态，以及进入该状态以来的 tick 数。" },
      "graph": {
        "domains": ["map", "player", "logic"],
        "execution": "query",
        "inputs": [
          { "entityKinds": ["gtfo.player"], "id": "player", "type": "entity" }
        ],
        "outputs": [
          { "id": "state", "type": "enum", "schema": "player_movement_state" },
          { "id": "since", "type": "number", "unit": "tick" }
        ],
        "parameters": [],
        "reads": ["world"]
      }
    }
    """;

    /// <summary>The binding row: an on-demand `query` binding, which is the `observe` role in this runtime — the
    /// same role every value row of this provider carries.</summary>
    public const string BindingRowJson = """
    {
      "id": "forge.module.gtfo.map.binding.query.player.movement_state",
      "capabilityId": "forge.query.player.movement_state",
      "providerId": "forge.module.gtfo.map",
      "handler": "gtfo.player.movement_state",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The row's registration support: a read owns no object and writes nothing, so it declares no
    /// permission, exactly as the other player value rows do.</summary>
    public static BindingSupport Support() => new(BindingId, "implementation-only", Array.Empty<string>());

    public static object CapabilityRow() => RuntimeJson.Parse(CapabilityRowJson);

    public static object BindingRow() => RuntimeJson.Parse(BindingRowJson);

    /// <summary>One reading of a player's locomotion, as the native half answers it. `State` is the declared member
    /// name, `SinceSeconds` the seconds the native machine has held it, and `Index` the native value the name was
    /// resolved from — carried so the row can refuse a value no member names instead of reporting a guessed
    /// one.</summary>
    public readonly record struct MovementSample(string State, double SinceSeconds, int Index);

    /// <summary>The one native read this row needs, handed in by the half that can make it.</summary>
    public readonly record struct MovementReaders(Func<EntityReference, MovementSample?> Read);

    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [HandlerName] = Shape
    };

    /// <summary>The evaluator, built from the native read. A refusal is an exception carrying the row's own code:
    /// the runtime turns it into the query step's failure, which is what an author sees instead of a default
    /// answer.</summary>
    public static EvaluatorHandler Evaluator(MovementReaders readers) => context => Evaluate(context, readers);

    private static JsonElement Evaluate(EvaluationContext context, MovementReaders readers)
    {
        var player = RuntimeJson.Entity(Input(context, "player"));
        if (RuntimeJson.KindOf(player.Id) != PlayerStateContract.EntityKind)
            throw new RuntimeContractException(InputKindCode, "This provider reads the movement of a player, not of " + player.Id);
        var sample = readers.Read(player) ?? throw new RuntimeContractException(InputStaleCode, "The player is not current: " + player.Id);
        if (sample.Index < 0 || sample.Index >= States.Length || States[sample.Index] != sample.State)
            throw new RuntimeContractException(StateCode, "The native locomotion value is outside the declared members.");
        var answer = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["state"] = sample.State,
            ["since"] = sample.SinceSeconds
        };
        return RuntimeJson.From(answer);
    }

    /// <summary>One required input port of the resolved frame, refused by name when the plan left it out.</summary>
    private static JsonElement Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", port);
}
