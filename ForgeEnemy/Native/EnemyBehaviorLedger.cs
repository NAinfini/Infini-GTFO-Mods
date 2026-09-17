using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>What the two behaviour actions can ask of the game, as one seam. The decision half of an action —
/// which ports it accepts, which requests it refuses, what each result row says — is game-independent and lives
/// in <see cref="AbilityDecision"/> and <see cref="NoiseDecision"/>; this is the half that touches the native
/// types, and its two implementations are the real bridge (`EnemyBehaviorNative`) and the focused suite's own.
///
/// A native answer is a plain value, never a game type: the decision half compares and reports it without ever
/// naming an `EnemyAgent`, an ability component or a noise struct, which is what keeps the policy under test
/// compilable without an IL2CPP runtime.
///
/// `Key` is the opaque identity of one tracked enemy life, the same text every other read in this package is
/// keyed by, and `Component` is the opaque identity of the ability component a trigger was submitted to.</summary>
internal interface IBehaviorPorts
{
    /// <summary>Whether this machine may write the world at all: the module's own authority gate, the startup
    /// phase and the host flag, in one answer.</summary>
    bool CanExecute { get; }

    /// <summary>Whether the module still tracks the life this key names, at this moment.</summary>
    bool IsCurrent(string key);

    /// <summary>Whether the enemy is alive, or false when the life is not one this module tracks.</summary>
    bool IsAlive(string key);

    /// <summary>Whether the enemy's own ability machine currently allows a trigger.</summary>
    bool CanTrigger(string key);

    /// <summary>The index of the requested ability in the enemy's own component table, or false when the enemy
    /// does not have that ability registered.</summary>
    bool TryAbilityIndex(string key, byte ability, out int index);

    /// <summary>Submits the game's own ability trigger at the position the lookup above answered and answers what
    /// it returned. `submitted` is false only when the attempt could not be made at all (the reference no longer
    /// names a live agent, the machine is gone); an attempt the game itself refused answers true with
    /// `triggered` false, which is a refusal the caller reports rather than an unknown.</summary>
    bool TryUseAbility(string key, byte ability, int index, out bool triggered);

    /// <summary>Reads back the component the trigger left running: its opaque identity and the machine's own
    /// `EnemyAbility.AbilityIsDone()` answer. False when the component can no longer be found, which is an unknown
    /// commit rather than a refusal.</summary>
    bool TryAbilityState(string key, byte ability, out IntPtr component, out bool done);

    /// <summary>Tells the module that the row for one enemy life is being dropped, with the machine's own
    /// `AbilityIsDone()` answer read back for it at that moment and the end path's reason. Whether the end is the
    /// interruption the `forge.trigger.enemy.ability_interrupted` row carries — an ability the machine still
    /// reports as unfinished — is the module's call, because publishing is a kernel fact and this half names no
    /// kernel type. The reason is one of the members
    /// <see cref="ForgeEnemy.EnemyAbilityInterruptedContract"/> declares.</summary>
    void AbilityDropped(RunningBehavior dropped, bool finished, string reason);

    /// <summary>Submits the game's own noise event. The node the native struct needs is resolved from the
    /// position inside the bridge, and the answer says whether one was found.</summary>
    bool TryEmitNoise(string key, (double X, double Y, double Z) position, double radius);
}

/// <summary>One in-flight ability, as the ledger holds it: the enemy life, the ability kind and the native
/// component the trigger was submitted to.</summary>
internal sealed record RunningBehavior(string EnemyKey, byte Ability, IntPtr Component, long WorldEpoch);

/// <summary>The provider-side table of in-flight enemy behaviours: what an enemy's own ability machine is running,
/// keyed by the enemy life it belongs to. One row per enemy at most, because the game itself keeps one
/// `EnemyAbilities.m_activeAbility` per agent — a second entry for the same life would be a second answer to a
/// question the native machine answers once.
///
/// The table exists because an in-flight ability is the only enemy behaviour this provider can both observe and
/// end: this provider submitted the trigger itself, so it knows which ability is running, and the row is what
/// tells a later end apart from a normal one. No native member says how an ability ended — the game's own
/// `EnemyAbility.AbilityIsDone()` is its whole answer, and the hitreact state machine that interrupts an ability
/// (`ES_HitreactBase.ActivateState`, the entry `forge.action.combat.stagger` and the combat family's
/// `attack_interrupt` submit) leaves the same answer behind as one that completed. A row still held while the
/// component reports itself unfinished was therefore ended by something other than the ability's own lifetime,
/// which is the interruption `forge.trigger.enemy.ability_interrupted` carries; the module publishes it, and a
/// row the machine reports as finished publishes nothing.
///
/// An entry is never timed. Nothing here counts ticks or expires entries, because no native member gives an
/// ability a duration: a row ends when a later trigger replaces it, when the enemy despawns, or when the world
/// ends. An expiry the provider invented would be a claim the native machine does not make, so the table has none
/// and this slice does not use the kernel's per-tick module hook.
///
/// The world epoch is part of the key rather than a field to compare: a world change clears the table, and a key
/// that names an older epoch cannot name a row here at all.</summary>
internal sealed class EnemyBehaviorLedger
{
    private readonly Dictionary<(long WorldEpoch, string Key), RunningBehavior> _entries = new();

    /// <summary>The ability one enemy life is currently running, or null.</summary>
    internal RunningBehavior? Find(string enemyKey, long worldEpoch)
        => _entries.TryGetValue(Key(enemyKey, worldEpoch), out var entry) ? entry : null;

    /// <summary>Replaces the row for one enemy life. The native machine holds one active ability per agent, so
    /// the newest submitted ability is the one that is running.</summary>
    internal void Start(RunningBehavior entry) => _entries[Key(entry.EnemyKey, entry.WorldEpoch)] = entry;

    /// <summary>Drops one enemy life's row and answers whether one was there. What the end was is the caller's to
    /// report: the decision half tells the module through <see cref="IBehaviorPorts.AbilityDropped"/>, and the
    /// despawn path reads its own row back before it drops it.</summary>
    internal bool End(string enemyKey, long worldEpoch) => _entries.Remove(Key(enemyKey, worldEpoch));

    internal void Clear() => _entries.Clear();

    internal int Count => _entries.Count;

    private static (long WorldEpoch, string Key) Key(string enemyKey, long worldEpoch)
    {
        if (enemyKey == null) throw new ArgumentException("An enemy key is required.", nameof(enemyKey));
        return (worldEpoch, enemyKey);
    }
}

/// <summary>The `ability` resource kind this provider owns: an author names one of the game's own
/// `Agents.AgentAbility` members, and the action resolves it against the target enemy's own registered ability
/// components. The resource identifies the ability <em>kind</em> the native enum names, never a component
/// instance: `EnemyAbilities.AbilityComps` is indexed per enemy and per index, so an id that carried an index
/// would name a different component on every enemy type that lays its components out differently.
///
/// `AgentAbility.None` (0) is not a resource: it is the machine's own "nothing is active" value, not an ability
/// an author can ask for, and the table below never answers it.</summary>
internal static class EnemyAbilityResources
{
    internal const string Kind = "ability";

    /// <summary>`AgentAbility.SpawnChildren`, the kind a birthing component is registered under. The profile
    /// half asks for the component by this kind, so the value lives here, next to the table that names it, rather
    /// than being spelled as a literal at the call site.</summary>
    internal const byte SpawnChildrenAbility = 9;

    /// <summary>`AgentAbility` in declaration order, minus `None`. The id is the member's own name in lowercase
    /// snake case, which is stable across builds because it is the native member name, and the pair is one table
    /// so the resource an author writes and the value the action submits cannot disagree.</summary>
    private static readonly (string Id, byte Ability)[] Table =
    {
        ("melee", 1), ("ranged", 2), ("alarm", 3), ("defensive", 4), ("healing", 5),
        ("group_enhance", 6), ("detection", 7), ("door_breaker", 8), ("spawn_children", SpawnChildrenAbility)
    };

    /// <summary>The provider's two answers to the kernel's resource table: every ability kind this provider owns,
    /// and one id resolved to its reference. An id outside the table is null, which the kernel reports as
    /// `stale-resource` rather than silently answering a different ability.</summary>
    internal static RuntimeResourceProvider Provider { get; } = RuntimeResourceProvider.Of(Enumerate, Resolve);

    internal static IReadOnlyList<ResourceRef> Enumerate()
    {
        var references = new ResourceRef[Table.Length];
        for (int i = 0; i < Table.Length; i++) references[i] = new ResourceRef(Kind, Table[i].Id);
        return references;
    }

    internal static ResourceRef? Resolve(string id)
    {
        foreach (var entry in Table) if (entry.Id == id) return new ResourceRef(Kind, entry.Id);
        return null;
    }

    /// <summary>The native member behind one resolved reference. A reference of another kind, or an id this
    /// provider does not own, is refused by the caller instead of being guessed at.</summary>
    internal static bool TryAbility(ResourceRef reference, out byte ability)
    {
        ability = 0;
        if (reference == null || reference.ResourceKind != Kind) return false;
        foreach (var entry in Table)
        {
            if (entry.Id != reference.ResourceId) continue;
            ability = entry.Ability;
            return true;
        }
        return false;
    }

    /// <summary>The other direction, for a fact that carries the ability it is about: the reference one native
    /// member belongs to. A byte outside the table is not a resource this provider owns, and the caller publishes
    /// nothing rather than inventing an id for it.</summary>
    internal static bool TryReference(byte ability, out ResourceRef reference)
    {
        foreach (var entry in Table)
        {
            if (entry.Ability != ability) continue;
            reference = new ResourceRef(Kind, entry.Id);
            return true;
        }
        reference = null!;
        return false;
    }
}
