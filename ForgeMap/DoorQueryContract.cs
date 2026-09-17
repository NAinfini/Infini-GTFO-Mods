using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `v-door` row: the read-only door state (`forge.query.map.door_state`). It is the value half of
/// the door rows this provider already publishes — the five states the checklist names, derived from the door's
/// own native status and lock component, and read on demand by a `query` step.
///
/// Naming and family follow `forge.query.environment.state`, the other value row of this package: the id spells
/// the `query` tier the plan node runs in, while the capability's `kind` is `modifier`, which is the
/// value-producing family the registry registers evaluators for. The id says `query` because the step an author
/// places is a query step; the two names are different fields and neither is derived from the other.
///
/// Two of the five states are the ones the previous batch could not answer and this one must:
///
/// - **要扫描** is a door held by an unsolved chained puzzle: `eDoorStatus.Closed_LockedWithChainedPuzzle` (5),
///   `Closed_LockedWithChainedPuzzle_Alarm` (4) and `Closed_LockedWithBulkheadDC` (15). It is derived, never
///   stored: the status is the only thing read.
/// - **被打破** is `eDoorStatus.Destroyed` (11). A weak door reads the same status, which is what makes the two
///   door kinds one vocabulary; the weak door itself is reached through `forge.trigger.interaction.door_broken`
///   because it has no map address.
///
/// The two detailed readings travel beside the coarse state rather than replacing it: `detail` is the native
/// `door_state` member name and `locked` is the lock component's own answer, so a plan that needs the exact
/// status is not forced to infer it back from the five-member derivation. Every reading is the door's own
/// native member — nothing here is a Forge-side state ledger.</summary>
public static class DoorQueryContract
{
    public const string CapabilityId = "forge.query.map.door_state";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.query.map.door_state";
    public const string HandlerName = "gtfo.map.door_state";

    /// <summary>The permission the row's own read needs. It is the map-object namespace's read permission, which
    /// is what every door observation of this package already declares.</summary>
    public const string Permission = MapObjectContract.MapObjectReadPermission;

    /// <summary>The refusal an input that names no door this provider owns gets. The trigger rows of this package
    /// answer for one entity kind, and this row answers for the same one.</summary>
    public const string InputKindCode = "door-state-input-kind";
    /// <summary>The refusal a door reference the world no longer answers for gets, reported by name rather than
    /// answered as a status the door is not in.</summary>
    public const string InputStaleCode = "door-state-input-stale";
    /// <summary>The refusal an address that is not a door address gets. A terminal reference is a map object too,
    /// and reading it as a door would answer with a state the terminal never had.</summary>
    public const string InputAddressCode = "door-state-input-address";
    /// <summary>The refusal a door whose own status value is outside `eDoorStatus` gets. A value no member names
    /// is refused instead of being reported as the nearest state.</summary>
    public const string StatusCode = "door-state-status";

    /// <summary>The one shape of this handler: the door the read is about, and the four facts it answers.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("door").Outputs("state", "detail", "locked", "key");

    /// <summary>The row, spelled the way a registration declares it: kind `state`, which is the value row's own
    /// kind — an on-demand read about the world — with execution `query`, one entity input and four read-only
    /// outputs. It declares the world read it performs, exactly as the environment row does.</summary>
    public const string CapabilityRowJson = """
    {
      "id": "forge.query.map.door_state",
      "owner": "forge.module.gtfo.map",
      "kind": "state",
      "label": "门状态",
      "version": "1.0.0",
      "parameters": { "description": "读一扇门的状态：关、开、上锁、要扫描、被打破。" },
      "graph": {
        "domains": ["map", "room", "logic"],
        "execution": "query",
        "inputs": [
          { "entityKinds": ["gtfo.map_object"], "id": "door", "type": "entity" }
        ],
        "outputs": [
          { "id": "state", "type": "enum", "schema": "door_query_state" },
          { "id": "detail", "type": "enum", "schema": "door_state" },
          { "id": "locked", "type": "boolean" },
          { "id": "key", "type": "string" }
        ],
        "parameters": [],
        "reads": ["world"]
      }
    }
    """;

    /// <summary>The binding row: an on-demand `query` binding, which is the `observe` role in this runtime — the
    /// same role the environment row's evaluator binding carries.</summary>
    public const string BindingRowJson = """
    {
      "id": "forge.module.gtfo.map.binding.query.map.door_state",
      "capabilityId": "forge.query.map.door_state",
      "providerId": "forge.module.gtfo.map",
      "handler": "gtfo.map.door_state",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The `door_query_state` vocabulary, in member order, because the port publishes the member index
    /// and not the name. The five states are the checklist's own and the order is the checklist's own. The set is
    /// declared here and offered to the framework and the catalog by the integration batch: this runtime refuses
    /// an enum port whose set is not registered, so the row and the set travel together.</summary>
    public static readonly string[] States = { "closed", "open", "locked", "needs_scan", "broken" };

    public const int Closed = 0, Open = 1, Locked = 2, NeedsScan = 3, Broken = 4;

    /// <summary>The member name one declared index spells, for the evidence and for a test.</summary>
    public static string StateName(int index) => index switch
    {
        Closed => "closed",
        Open => "open",
        Locked => "locked",
        NeedsScan => "needs_scan",
        Broken => "broken",
        _ => throw new RuntimeContractException(StatusCode, "Not a door query state index: " + index)
    };

    /// <summary>The five states derived from one `eDoorStatus` value, and nothing else. This is the whole
    /// derivation and the only place a native status is classified for the value row, so the row and the
    /// weak-door trigger cannot describe one door two different ways:
    ///
    /// - `Destroyed` (11) is broken.
    /// - The three unsolved-puzzle statuses (4, 5, 15) need a scan before the door opens.
    /// - `Open` (10) and `Opening` (16) are open.
    /// - The locked statuses (3, 6, 7) are locked.
    /// - Everything else — `None`, `Closed`, `Closed_BrokenCantOpen`, `ChainedPuzzleActivated`, `Unlocked`,
    ///   `GluedMax` and the two stuck states — is closed. `Unlocked` is an unlocked but still shut door, which
    ///   is closed for the author's five-state question; the exact native value stays readable through `detail`.
    ///
    /// A status outside the enum is refused rather than mapped to the nearest member.</summary>
    public static int StateOf(int status) => status switch
    {
        11 => Broken,
        4 or 5 or 15 => NeedsScan,
        10 or 16 => Open,
        3 or 6 or 7 => Locked,
        0 or 1 or 2 or 8 or 9 or 12 or 13 or 14 => Closed,
        _ => throw new RuntimeContractException(StatusCode, "Unknown door status value: " + status)
    };

    /// <summary>One door as the evaluator reads it: the native status, whether a lock holds the door, and the key
    /// it currently wants. It is the same three readings the door observation half publishes, handed over as a
    /// value because the game-independent assembly this contract lives in holds no game type.</summary>
    public readonly record struct DoorSample(int Status, bool Locked, string? Key);

    /// <summary>The one read this row needs from the world: the door a reference names right now, or null when
    /// this world does not answer for it. The game-bound half supplies it; a registration that carries none
    /// answers every read with a refusal instead of a state, which is what keeps a row that cannot be read from
    /// looking like a door that read as closed.</summary>
    public sealed record DoorReaders(Func<EntityReference, DoorSample?> Read);

    /// <summary>The evaluator, keyed by handler name: what a registration adds to its evaluator table beside the
    /// selector's. The input is refused by name when it names no door this provider owns, and a door whose own
    /// status does not read is refused rather than answered with a state the door was never in.</summary>
    public static EvaluatorHandler Evaluator(DoorReaders readers)
    {
        ArgumentNullException.ThrowIfNull(readers);
        return context => Evaluate(context, readers);
    }

    private static JsonElement Evaluate(EvaluationContext context, DoorReaders readers)
    {
        var door = RuntimeJson.Entity(Input(context, "door"));
        if (RuntimeJson.KindOf(door.Id) != MapObjectModule.EntityKind)
            throw new RuntimeContractException(InputKindCode, "This provider reads the state of a map object, not of " + door.Id);
        if (MapObjectDoorAddress.TryParse(door.Id[(MapObjectModule.EntityKind.Length + 1)..]) == null)
            throw new RuntimeContractException(InputAddressCode, "The input is not a door address: " + door.Id);
        var sample = readers.Read(door) ?? throw new RuntimeContractException(InputStaleCode, "The door is not current: " + door.Id);
        int state = StateOf(sample.Status);
        // A port whose value the world could not produce is left out rather than written as a null: a door that
        // wants no key and a door whose key could not be read are different facts, and only the second is an
        // absent reading. The answer is built port by port for that reason.
        var answer = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["state"] = state,
            ["detail"] = MapObjectDoorStatus.Name(sample.Status),
            ["locked"] = sample.Locked
        };
        if (!string.IsNullOrEmpty(sample.Key)) answer["key"] = sample.Key;
        return RuntimeJson.From(answer);
    }

    /// <summary>One required input port of the resolved frame, refused by name when the plan left it out: an
    /// absent door answered as "closed" would read exactly like a door that really is closed.</summary>
    private static JsonElement Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", port);
}
