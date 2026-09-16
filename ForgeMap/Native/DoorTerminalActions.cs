using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The two door decision members of `LevelGeneration.LG_Door_Sync` this half writes through:
/// `AttemptDoorInteraction(eDoorInteractionType, float, float, Vector3, Agent)` is the door's synchronized
/// interaction entry, and `SetLockedNoKey` is the member of the native `eDoorInteractionType` enum the entry
/// switches on. The enum is declared here with the two members this half really uses rather than as a copy of
/// the whole native enum, and the names are the game's own.</summary>
internal enum DoorInteractionType
{
    /// <summary>`eDoorInteractionType.Unlock` (5): the door releases its lock.</summary>
    Unlock = 5,
    /// <summary>`eDoorInteractionType.SetLockedNoKey` (3): the door is locked with no key, which is the lock
    /// `eProgressionPuzzleType.Locked_No_Key = 3` and `eDoorStatus.Closed_LockedWithNoKey` name.</summary>
    SetLockedNoKey = 3
}

/// <summary>The execute half of the two door lock rows: `forge.action.map.door_lock` and
/// `forge.action.map.door_unlock`. Both act on the doors a plan named in the one `gtfo.map_object` namespace,
/// through the native entry points the readback half already reads — no second door table, no Forge-side lock
/// ledger, no handle.
///
/// The lock kinds are the game's own setups. `LG_SecurityDoor_Locks.SetupSimpleLock()` is what a door's own
/// level generation gives a plain locked door, and `LG_SecurityDoor_Locks.SetupAsLockedNoKey(LocalizedText)` is
/// the setup the game's own generator lock uses; both are the door's replicated lock component, so a lock
/// written here is the game's lock and survives exactly as long as the game's own does. The generator case the
/// node list asks for is a group of doors put under `no_key` and released later by `door_unlock`, which is the
/// same pair of native setups with no extra state.
///
/// What this half refuses, and why:
///
/// - `key_policy: "specific"` needs a native `GateKeyItem` instance, and the only provider that could resolve
///   one is the item-resource half (`forge.resource.item`), which this runtime does not register. The member is
///   declared so an authored plan compiles, and the handler refuses it by name.
/// - `consume_key: "yes"` asks for a key to be spent. The unlock interaction carries no key and the evidence
///   for this build establishes no native entry that spends one, so the request is refused rather than served
///   for free under a code that says a key was spent.
/// - A door the level did not make a zone's entrance — a weak door, a node door, a decorative door — has no
///   address in this provider's grammar, so it cannot be named by a plan and never reaches this half.</summary>
internal sealed class DoorTerminalActions
{
    /// <summary>The one recipient kind both rows answer for, and the one category of it these rows act on.</summary>
    internal const string Kind = "gtfo.map_object";
    private const string Prefix = Kind + ":";
    private const string DoorCategory = "door";

    internal const string AuthorityCode = "authority-or-phase";
    internal const string KindCode = "door-unsupported-recipient";
    internal const string StaleCode = "door-stale";
    internal const string MismatchCode = "door-identity-mismatch";
    internal const string TargetsCode = "too-many-targets";
    internal const string NotAttemptedCode = "not-attempted-after-unknown-commit";
    internal const string CommitExceptionCode = "native-commit-exception";
    internal const string NoTargetsCode = "door-no-targets";
    internal const string PolicyCode = "door-unknown-key-policy";
    internal const string KeyItemUnavailableCode = "door-key-item-provider-unavailable";
    internal const string ConsumeKeyUnsupportedCode = "door-consume-key-unsupported";
    internal const string AlreadyLockedCode = "door-already-locked";
    internal const string NotLockedCode = "door-not-locked";
    internal const string LockedCode = "door-lock-issued";
    internal const string UnlockedCode = "door-unlock-issued";
    internal const string NoLockComponentCode = "door-no-lock-component";
    internal const string NoSyncComponentCode = "door-no-sync-component";

    /// <summary>The native instance behind one recipient reference, or null when this world cannot resolve it.
    /// It is a delegate so a registration hands over the session's own lookup and a test can hand over a table;
    /// the caller still verifies the answer against the kernel before anything is written.</summary>
    internal delegate object? Resolver(EntityReference reference);

    /// <summary>One row of either result, in the row order the catalog declares: the four fixed columns and the
    /// row's own `target_count`, under the wire names the catalog spells.</summary>
    private sealed record DoorRow(EntityReference Target, string Status,
        [property: System.Text.Json.Serialization.JsonPropertyName("committed")] string CommitState, string Code,
        [property: System.Text.Json.Serialization.JsonPropertyName("target_count")] int TargetCount);

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _ready;
    private readonly Resolver _resolve;
    private readonly Action<string> _report;

    internal DoorTerminalActions(RuntimeKernel kernel, Func<bool> ready, Resolver resolve, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _ready = ready ?? throw new ArgumentNullException(nameof(ready));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    internal int Submitted { get; private set; }
    internal int Refused { get; private set; }
    internal int Unknown { get; private set; }

    /// <summary>Whether this side may write native door state at all. A door's own interaction entry is a
    /// synchronized write the host makes and every peer applies, which is what both rows' `host` execution says.</summary>
    internal bool Authoritative()
        => _ready() && SNetwork.SNet.IsMaster && _kernel.Lifecycle.StartupState == RuntimeStartupState.Ready
            && _kernel.Lifecycle.IsHost == true;

    /// <summary>One door behind one recipient reference, checked the same way for both rows: the reference has to
    /// name this world first, then the kernel is the authority on whether it is still current in that world, the
    /// address text is read with the door category's own grammar, and the instance the address names has to be
    /// the instance the kernel resolves the reference back to. A door that no longer reads as the address it was
    /// addressed with is refused rather than written to.
    ///
    /// The world check comes first and is this half's own: `EntityReference` carries the world it was built in,
    /// and a reference from a previous world still resolves to the same address text, so without this check a
    /// plan holding an old reference would silently lock the door the *new* world put at that address.</summary>
    internal LG_SecurityDoor? Resolve(EntityReference? reference, out MapObjectReference address, out string refusal)
    {
        address = null!;
        refusal = "";
        if (reference == null || !IsDoor(reference)) { refusal = KindCode; return null; }
        if (reference.WorldEpoch != _kernel.WorldEpoch) { refusal = StaleCode; return null; }
        if (!_kernel.IsEntityCurrent(reference)) { refusal = StaleCode; return null; }
        if (Address(reference) is not { } parsed) { refusal = KindCode; return null; }
        object? instance;
        try { instance = _resolve(reference); }
        catch (Exception error)
        {
            _report("map.door-resolve-exception: " + error.GetType().Name);
            refusal = StaleCode;
            return null;
        }
        if (instance is not LG_SecurityDoor door) { refusal = StaleCode; return null; }
        if (_kernel.ResolveEntityInstance(Kind, door) is not { } verified || verified != reference)
        { refusal = MismatchCode; return null; }
        if (!DoorObservation.IsCurrentAddress(door, parsed)) { refusal = StaleCode; return null; }
        address = parsed;
        return door;
    }

    /// <summary>Puts one door back under its own lock. The lock kind is the row's one structural choice and is
    /// checked before anything is written: `none` and `any` are the two setups this build has, `specific` needs
    /// the item-resource provider it does not have, and a value outside the catalog's three members is refused
    /// as the unknown policy it is.</summary>
    internal (string Status, string CommitState, string Code) Lock(LG_SecurityDoor door, MapObjectReference address,
        string? policy)
    {
        var outcome = LockOutcome(door, address, policy);
        if (outcome.Commit == MapActionCommit.Issued) { Submitted++; return ("succeeded", CommitStates.Confirmed, outcome.Code); }
        if (outcome.Code == CommitExceptionCode) { Unknown++; return ("failed", CommitStates.Unknown, outcome.Code); }
        if (outcome.Commit == MapActionCommit.AlreadyInState) { Refused++; return ("rejected", CommitStates.None, outcome.Code); }
        Refused++;
        return ("rejected", CommitStates.None, outcome.Code);
    }

    private MapActionOutcome LockOutcome(LG_SecurityDoor door, MapObjectReference address, string? policy)
    {
        if (policy is not ("none" or "any" or "specific")) return MapActionOutcome.Refused(PolicyCode);
        if (policy == "specific") return MapActionOutcome.Refused(KeyItemUnavailableCode);
        if (door == null || door.WasCollected) return MapActionOutcome.Refused(StaleCode);
        if (!DoorObservation.IsCurrentAddress(door, address)) return MapActionOutcome.Refused(StaleCode);
        if (door.m_locks?.TryCast<LG_SecurityDoor_Locks>() is not { } locks || locks.WasCollected)
            return MapActionOutcome.Refused(NoLockComponentCode);
        int status = (int)door.LastStatus;
        bool wantsNoKey = policy == "none";
        // The lock the request asks for is read from the door's own status before anything is written: a door
        // already in that state is left alone rather than re-locked, which is also what keeps a repeated request
        // from re-running a setup the level's own generation already ran.
        bool lockedNoKey = status == (int)eDoorStatus.Closed_LockedWithNoKey;
        if (wantsNoKey && lockedNoKey) return MapActionOutcome.AlreadyInState(AlreadyLockedCode);
        if (!wantsNoKey && MapObjectDoorStatus.IsLocked(status)) return MapActionOutcome.AlreadyInState(AlreadyLockedCode);
        try
        {
            if (wantsNoKey) locks.SetupAsLockedNoKey(new Localization.LocalizedText());
            // The simple lock is the door's own setup: the lock component it holds is only the state the setup
            // writes, so the entry point is the door's and not a method of the component.
            else door.SetupSimpleLock();
        }
        catch (Exception error)
        {
            // The call entered the door's own lock setup; whether it had already written the lock component is
            // not observable from here, so the commit stays unknown.
            _report("map.door-lock-exception: " + error.GetType().Name);
            return MapActionOutcome.Refused(CommitExceptionCode);
        }
        return MapActionOutcome.Issued(LockedCode);
    }

    /// <summary>Releases one door's lock through the door's own synchronized interaction entry. `consume_key`
    /// is checked first: the native entry takes no key, so a request that asks for one to be spent is refused
    /// rather than served for free.</summary>
    internal (string Status, string CommitState, string Code) Unlock(LG_SecurityDoor door, MapObjectReference address,
        string? consumeKey)
    {
        var outcome = UnlockOutcome(door, address, consumeKey);
        if (outcome.Commit == MapActionCommit.Issued) { Submitted++; return ("succeeded", CommitStates.Confirmed, outcome.Code); }
        if (outcome.Code == CommitExceptionCode) { Unknown++; return ("failed", CommitStates.Unknown, outcome.Code); }
        if (outcome.Commit == MapActionCommit.AlreadyInState) { Refused++; return ("rejected", CommitStates.None, outcome.Code); }
        Refused++;
        return ("rejected", CommitStates.None, outcome.Code);
    }

    private MapActionOutcome UnlockOutcome(LG_SecurityDoor door, MapObjectReference address, string? consumeKey)
    {
        if (consumeKey is not ("no" or "yes")) return MapActionOutcome.Refused(PolicyCode);
        if (consumeKey == "yes") return MapActionOutcome.Refused(ConsumeKeyUnsupportedCode);
        if (door == null || door.WasCollected) return MapActionOutcome.Refused(StaleCode);
        if (!DoorObservation.IsCurrentAddress(door, address)) return MapActionOutcome.Refused(StaleCode);
        int status = (int)door.LastStatus;
        if (status == (int)eDoorStatus.Unlocked || status == (int)eDoorStatus.Open || status == (int)eDoorStatus.Opening)
            return MapActionOutcome.AlreadyInState(NotLockedCode);
        // The door's own sync slot is an interface in the interop assembly, so the cast is how the concrete
        // component is reached, exactly as the lock slot is reached.
        if (door.m_sync?.TryCast<LG_Door_Sync>() is not { } sync || sync.WasCollected)
            return MapActionOutcome.Refused(NoSyncComponentCode);
        try
        {
            // The entry's two float arguments and its position are the interaction payload the game's own
            // interaction would carry; nothing about an unlock reads them, and the source agent is absent because
            // an unlock requested by a plan has no player behind it.
            sync.AttemptDoorInteraction((eDoorInteractionType)(byte)DoorInteractionType.Unlock, 0f, 0f, door.transform.position, null);
        }
        catch (Exception error)
        {
            _report("map.door-unlock-exception: " + error.GetType().Name);
            return MapActionOutcome.Refused(CommitExceptionCode);
        }
        return MapActionOutcome.Issued(UnlockedCode);
    }

    /// <summary>Both rows' handler body. The whole request is checked before the first door, because a request
    /// that cannot be carried out as asked must not half-apply.</summary>
    internal CommandResult Execute(JsonElement inputs, JsonElement parameters, bool unlock)
    {
        if (!Authoritative()) return CommandResult.Rejected(AuthorityCode);
        var policy = unlock ? Text(parameters, "consume_key") : Text(parameters, "key_policy");
        var doors = Recipients(inputs);
        if (doors.Length > CommandResult.MaximumFacts) return CommandResult.Rejected(TargetsCode);
        if (doors.Length == 0) return CommandResult.Rejected(NoTargetsCode);
        var rows = new List<DoorRow>(doors.Length);
        bool stop = false;
        foreach (var target in doors)
        {
            if (stop)
            {
                rows.Add(new DoorRow(target, "rejected", CommitStates.None, NotAttemptedCode, doors.Length));
                continue;
            }
            var door = Resolve(target, out var address, out var refusal);
            if (door == null)
            {
                rows.Add(new DoorRow(target, "rejected", CommitStates.None, refusal, doors.Length));
                continue;
            }
            var (status, commit, code) = unlock
                ? Unlock(door, address, policy)
                : Lock(door, address, policy);
            if (commit == CommitStates.Unknown) stop = true;
            rows.Add(new DoorRow(target, status, commit, code, doors.Length));
        }
        return Aggregate(rows);
    }

    internal CommandResult HandleLock(CommandContext context) => Execute(context.Inputs, context.Parameters, unlock: false);

    internal CommandResult HandleUnlock(CommandContext context) => Execute(context.Inputs, context.Parameters, unlock: true);

    /// <summary>Whether a reference names a door of the one map-object namespace. Only the address's own first
    /// segment is read here; the full grammar is the category's own parser's job, compared as text so a category
    /// spelled with different case is refused rather than accepted and then failed by the parser.</summary>
    internal static bool IsDoor(EntityReference reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return false;
        int slash = id.IndexOf('/', Prefix.Length);
        return slash > Prefix.Length
            && string.CompareOrdinal(id, Prefix.Length, DoorCategory, 0, slash - Prefix.Length) == 0;
    }

    /// <summary>The address a door reference carries, read back with the door category's own grammar.</summary>
    internal static MapObjectReference? Address(EntityReference? reference)
    {
        var id = reference?.Id;
        if (id == null || !id.StartsWith(Prefix, StringComparison.Ordinal)) return null;
        return MapObjectDoorAddress.TryParse(id[Prefix.Length..]);
    }

    private static CommandResult Aggregate(IReadOnlyList<DoorRow> rows)
    {
        var outputs = RuntimeJson.From(new { results = rows });
        int committed = 0, unknown = 0;
        foreach (var row in rows)
        {
            if (row.CommitState == CommitStates.Confirmed) committed++;
            else if (row.CommitState == CommitStates.Unknown) unknown++;
        }
        if (committed == rows.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0) return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(rows, "door-all-rejected"), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(rows, "door-all-unknown"), "", outputs);
    }

    private static string Single(IReadOnlyList<DoorRow> rows, string fallback)
    {
        if (rows.Count == 1) return rows[0].Code;
        var first = rows[0].Code;
        foreach (var row in rows) if (row.Code != first) return fallback;
        return first;
    }

    private static EntityReference[] Recipients(JsonElement inputs)
        => inputs.TryGetProperty("doors", out var value) && value.ValueKind == JsonValueKind.Array
            ? value.EnumerateArray().Select(RuntimeJson.Entity).ToArray()
            : Array.Empty<EntityReference>();

    private static string? Text(JsonElement bag, string port)
        => bag.TryGetProperty(port, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
