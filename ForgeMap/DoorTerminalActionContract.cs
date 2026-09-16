using System;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two door lock rows the node list asks for under `a-door` — `forge.action.map.door_lock` and
/// `forge.action.map.door_unlock` — declared as this provider's own shapes. The package's existing
/// `DoorActionContract` deliberately leaves both rows unregistered, and its reason was the shape the catalog
/// used to carry: `door_lock` took a `forge.resource.item` key and emitted a `lease` handle, `door_unlock`
/// took that lease handle back, and neither an item-resource provider nor a lease-handle minter exists in
/// this runtime, so a bound handler could only refuse. That handle kind is gone — no native object stands
/// behind it (ruling 160.3) — so the catalog now names the doors themselves on both rows. The two rows are
/// reshaped here onto what this build really has, which is the rule the node-list review already set for
/// rows whose catalog shape the native side cannot honour ("改形需求写进 integration.json").
///
/// What the native side really has for a lock:
///
/// - `LevelGeneration.LG_SecurityDoor_Locks` carries the three setups the game itself uses — `SetupSimpleLock()`,
///   `SetupKeyItemLock(GateKeyItem)`, `SetupAsLockedNoKey(LocalizedText)` — and each of them returns the
///   `eDoorStatus` it wrote, so a lock is a native state and not a bookkeeping entry on the Forge side.
/// - `LevelGeneration.LG_SecurityDoor_Locks.OnApproached` also proves the lock component is the door's own
///   replicated component, so a lock this provider writes is the game's lock and not a shadow one.
///
/// What it really has for an unlock: `LevelGeneration.LG_Door_Sync.AttemptDoorInteraction(eDoorInteractionType,
/// float, float, Vector3, Agent)` is the door's synchronized interaction entry, and `eDoorInteractionType.Unlock`
/// is a member of the native enum (`eDoorInteractionType` in `Modules-ASM.dll`: `Open`, `SetLockedWithChainedPuzzle_Alarm`,
/// `SetLockedWithChainedPuzzle`, `SetLockedNoKey`, `ActivateChainedPuzzle`, `Unlock`, `Close`, `DoDamage`,
/// `SetGluedMaxEnabled`, `SetGluedMaxDisabled`, `SetGlueLevel`, `Approach`). `LG_Door_Sync.TryUnlockDoor(pDoorInteraction)`
/// is the private body that type dispatches into, and the interaction type is what it switches on, so the
/// interaction type — not the method name — is the evidence. `LG_SecurityDoor_Locks.SetupAsLockedNoKey` is the
/// same native setup the game's own generator lock uses (`eProgressionPuzzleType.Locked_No_Key = 3` maps to
/// `eDoorStatus.Closed_LockedWithNoKey`), which is what makes a generator-group door expressible with these two
/// rows and no Forge-side lock table.
///
/// Two members of the old shape are deliberately gone rather than carried as promises: the `specific` key policy
/// and the `consume_key` flag. Both need the item-resource half (`forge.resource.item`) that no provider
/// registers in this runtime. They stay **declared** so an authored plan that names them compiles, and the
/// handler refuses them by name at execution time, which is this package's existing rule for a port the native
/// side cannot honour.</summary>
public static class DoorTerminalActionContract
{
    public const string LockCapability = "forge.action.map.door_lock";
    public const string UnlockCapability = "forge.action.map.door_unlock";

    public const string LockPermission = "door.lock";
    public const string UnlockPermission = "door.lock";

    public const string LockHandlerName = "gtfo.map_object.door_lock";
    public const string UnlockHandlerName = "gtfo.map_object.door_unlock";

    /// <summary>The catalog's domain list for these rows: a door is a map object that rooms and logic graphs
    /// act on, and both rows name the same set the open and close rows already carry.</summary>
    internal static readonly string[] Domains = { "map", "room", "logic" };

    /// <summary>Internal binding ids, by capability: this provider's own namespace plus the capability's own
    /// suffix, exactly as the open and close rows spell theirs.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>The lock row's ports: the recipient collection and the one structural policy. The handle the old
    /// shape emitted is gone, because the lock a plan asked for is the door's own `eDoorStatus` and a handle to
    /// it would be Forge bookkeeping with nothing native behind it.</summary>
    public static readonly HandlerShape LockShape = new HandlerShape()
        .Inputs("doors").Outputs("result").Parameters("key_policy");

    /// <summary>The unlock row's ports: the recipient collection the plan wired in. The old shape's lease input
    /// is gone — the doors themselves are the recipients, which is also what the action recipient validator
    /// requires — and the one policy the row carries is declared here and refused by name at execution.</summary>
    public static readonly HandlerShape UnlockShape = new HandlerShape()
        .Inputs("doors").Outputs("result").Parameters("consume_key");

    internal static object Row(string capability, string label, string description, object[] inputs,
        object[] outputs, object[] parameters, string requirement)
        => new
        {
            id = capability, owner = ModuleDefinition.ProviderId, kind = "action", label, version = "1.0.0",
            parameters = new { description },
            graph = new
            {
                domains = Domains, execution = "host", inputs, outputs, parameters,
                recipients = new { input = "doors", target = "entity", cardinality = "many",
                    requires = new[] { requirement }, result = "result" }
            }
        };

    internal static object ResultPort(string schema, params object[] fields)
        => new { id = "result", type = "result", schema, fields };

    internal static object Port(string id, string type) => new { id, type };
    internal static object Many(string id, string type) => new { id, type, cardinality = "many" };
    internal static object Field(string id, string type) => new { id, type };
    internal static object EnumField(string id, string schema) => new { id, type = "enum", schema };
    internal static object Structural(string id, bool required, params string[] values)
        => new { id, type = "enum", role = "structural", required, values };

    /// <summary>The lock row: a door, or a whole generator group's worth of doors, is put back under a lock the
    /// game itself owns. `key_policy` keeps the catalog's three members, because those are the three lock kinds
    /// the native setups express; `any` with the value the game spells for a door lock, and `specific` declared
    /// but refused until an item-resource provider exists.</summary>
    public static object LockRow() => Row(LockCapability, "给门加锁", "给门加回它自己的锁。", new object[]
        {
            Port("in", "execution"),
            Many("doors", "entity")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.door_lock",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                Field("target_count", "integer"))
        },
        new object[] { Structural("key_policy", true, "none", "any", "specific") },
        LockPermission);

    /// <summary>The unlock row: the door's own synchronized unlock interaction is asked, which is the entry the
    /// game itself drives when a player unlocks a door. The `consume_key` policy is declared and refused: the
    /// interaction entry consumes nothing, and a request that asks for a key to be spent must be told it cannot
    /// have that rather than receive a free unlock under a code that says a key was spent.</summary>
    public static object UnlockRow() => Row(UnlockCapability, "解锁门", "解开门的锁。", new object[]
        {
            Port("in", "execution"),
            Many("doors", "entity")
        },
        new object[]
        {
            Port("next", "execution"),
            ResultPort("forge.result.map.door_unlock",
                Field("target", "entity"), EnumField("status", "execution_outcome"),
                EnumField("committed", "commit_state"), Field("code", "string"),
                Field("target_count", "integer"))
        },
        new object[] { Structural("consume_key", true, "no", "yes") },
        UnlockPermission);

    /// <summary>The two capability rows in the order this module declares them.</summary>
    public static object[] Rows() => new object[] { LockRow(), UnlockRow() };

    /// <summary>One execute binding row, spelled exactly as this package's other action rows spell theirs.</summary>
    public static object BindingRow(string capability, string handler) => new
    {
        id = Binding(capability), capabilityId = capability, providerId = ModuleDefinition.ProviderId, handler,
        role = "execute", status = "implemented", dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    /// <summary>The two binding rows in the same order as <see cref="Rows"/>.</summary>
    public static object[] Bindings() => new object[]
    {
        BindingRow(LockCapability, LockHandlerName),
        BindingRow(UnlockCapability, UnlockHandlerName)
    };

    /// <summary>One binding's registration support: the permission the row's own recipient contract names.</summary>
    public static BindingSupport Support(string capability, string permission)
        => new(Binding(capability), "implementation-only", new[] { permission });

    /// <summary>The two registration support rows, in the same order as <see cref="Bindings"/>.</summary>
    public static BindingSupport[] Supports() => new[]
    {
        Support(LockCapability, LockPermission),
        Support(UnlockCapability, UnlockPermission)
    };
}
