using System;
using System.Collections.Generic;
using AIGraph;
using Enemies;
using ForgeRuntime.Framework;
using UnityEngine;

namespace ForgeEnemy.Native;

/// <summary>Host-authoritative enemy behaviour actions, submitted through the game's own entry points:
///
/// * `forge.action.enemy.ability` submits `EnemyAbilities.UseAbility(AgentAbility, index)` — the game's own
///   ability trigger, whose `bool` answer says whether it triggered and which then runs that ability component's
///   `Trigger()`/`DoTrigger()`. The author names the ability through the `ability` resource this provider owns
///   (<see cref="EnemyAbilityResources"/>); the index is read from the target enemy's own
///   `EnemyAbilities.AllComps` table, because the same ability kind sits at a different index on every enemy type.
/// * `forge.action.enemy.noise_emit` submits `NoiseManager.MakeNoise(NM_NoiseData)` — the game's only noise write
///   point. The struct carries the caller's position, the catalog's single radius, and the course node that
///   position belongs to, resolved with `AIG_CourseNode.TryGetCourseNode`; the game's own listening, occlusion and
///   propagation then run unchanged.
///
/// Both actions read their result back instead of claiming it: an ability whose native answer was false, or
/// whose machine does not report it running afterwards, is not reported as a committed one.
///
/// The rules themselves live in <see cref="AbilityDecision"/> / <see cref="NoiseDecision"/>, which name no game
/// type; this file is the bridge they are given, and the focused suite drives the same rules through its own
/// bridge.
///
/// `forge.action.enemy.investigate` is **not** implemented, and no half version of it is offered. Ruling 88
/// restates the design's reshaping of the row into a state request, and the design's own reason is that the game
/// has no "go and look at this position" API: the native behaviour machine is entered by
/// `EnemyBehaviour.ChangeState(EB_States)`, which is a different row — `forge.action.enemy.state_request`, owned
/// by the enemy control family, which also carries the handle recipient `investigate` never had. The reshaped row
/// leaves `state_request`'s ports nowhere to come from, so declaring an `investigate` row here would either
/// duplicate that row under a second id or advertise a `position`/`evidence_kind` request the game cannot carry.
/// See `ForgeEnemy/evidence/enemy-behavior-ledger.json`.
///
/// `forge.action.enemy.patrol`, `flee` and `threat_change` are deleted by ruling 88 and are not declared or
/// implemented anywhere in this package: no native entry point exists for a waypoint path, for a state machine
/// with no flee member, or for a threat table the game does not keep.</summary>
internal sealed partial class EnemyModule
{
    internal const string AbilityBinding = EnemyBehaviorContract.AbilityBinding;
    internal const string NoiseEmitBinding = EnemyBehaviorContract.NoiseEmitBinding;
    internal const string AbilityHandler = EnemyBehaviorContract.AbilityHandler;
    internal const string NoiseEmitHandler = EnemyBehaviorContract.NoiseEmitHandler;
    internal const string AbilityCapability = EnemyBehaviorContract.AbilityCapability;
    internal const string NoiseEmitCapability = EnemyBehaviorContract.NoiseEmitCapability;

    /// <summary>The two command handlers the contract's binding rows name, handed to the registration as one
    /// table so the binding, the handler and its shape are declared in one place.</summary>
    internal static Dictionary<string, CommandHandler> BehaviorHandlers(EnemyModule module)
        => new(StringComparer.Ordinal)
        {
            [AbilityHandler] = module.Ability,
            [NoiseEmitHandler] = module.NoiseEmit
        };

    /// <summary>`NM_NoiseData.type` this action submits: `NM_NoiseType.Detectable` (1). The catalog declares a
    /// noise a plan emits for the world to hear; `InstaDetect` (0) is the game's own instant-detection stimulus
    /// and is a different claim, while `Investigate` (2) and `PulseOnly` (3) are AI-side behaviours no author
    /// asked for.</summary>
    private const NM_NoiseType EmittedNoiseType = NM_NoiseType.Detectable;

    /// <summary>The dimension a plan's noise lives in. Every entity a plan can name is an agent in the reality
    /// dimension, and the other `eDimensionIndex` members are the level's alternate realities; a noise emitted at
    /// a world position belongs to the dimension the enemies that hear it are in.</summary>
    private const eDimensionIndex NoiseDimension = eDimensionIndex.Reality;

    /// <summary>How far from the requested position the course node may be sampled. The game's own node volumes
    /// are room-sized, so a radius under a metre would refuse a position on a node seam; this is the order of an
    /// agent's own footprint and is deliberately far below a room.</summary>
    private const float CourseNodeSampleDistance = 2f;

    /// <summary>`NM_NoiseData.yScale`: the vertical scale of the noise volume. The catalog declares one radius
    /// and no vertical extent, and a scale of 1 makes the vertical extent the same as the horizontal one, which
    /// is the only value the declared port can mean.</summary>
    private const float NoiseVerticalScale = 1f;
    /// <summary>`NM_NoiseData.includeToNeightbourAreas`: the catalog's own description is that the noise spreads,
    /// and a node-local stimulus would not leave the room it was made in.</summary>
    private const bool NoiseIncludesNeighbourAreas = true;
    /// <summary>`NM_NoiseData.raycastFirstNode`: the position is the caller's world point, not a sample the game
    /// has to trace back to a node; the node is resolved before the struct is built, so no first-node raycast is
    /// asked for.</summary>
    private const bool NoiseRaycastsFirstNode = false;

    /// <summary>The in-flight ability table. Owned by the module and keyed by enemy life and world epoch; the
    /// world change that empties every other per-life table here empties this one through <c>ClearWorld</c>, and a
    /// despawn drops its own row.</summary>
    private EnemyBehaviorLedger? _behaviors;
    private EnemyBehaviorLedger Behaviors => _behaviors ??= new EnemyBehaviorLedger();

    private CommandResult Ability(CommandContext context)
        => AbilityDecision.Run(context, new NativeBehaviorPorts(this), Behaviors, _kernel.WorldEpoch);

    private CommandResult NoiseEmit(CommandContext context)
        => NoiseDecision.Run(context, new NativeBehaviorPorts(this));

    /// <summary>Drops one enemy life's in-flight row, reporting the ability the despawn cut short. The readback is
    /// taken while the entry still resolves — the despawn path drops the row before it drops the entity — because
    /// a component the game no longer answers for proves nothing: an unreadable component publishes nothing rather
    /// than being claimed as an interruption, and neither does one the machine reports as finished. A module that
    /// never started an ability has no table to drop a row from, so the call stays a no-op rather than building
    /// one. The world's own teardown drops rows through <c>ClearBehaviors</c> and publishes nothing: a world going
    /// away is not an interruption of one ability.</summary>
    internal void ForgetBehavior(EntityReference enemy)
    {
        CheckThread();
        if (_behaviors == null) return;
        var running = _behaviors.Find(enemy.Id, enemy.WorldEpoch);
        if (running == null) return;
        _behaviors.End(enemy.Id, enemy.WorldEpoch);
        if (!TryAbilityComponent(Resolve(enemy), running.Ability, out _, out bool done)) return;
        ReportAbilityDropped(running, done, ForgeEnemy.EnemyAbilityInterruptedContract.ReasonDespawn);
    }

    /// <summary>The tracked life a key names, through the module's one entry table: the key is the entity
    /// reference's own id, so a reference from another world or another life cannot name an entry here.</summary>
    private Entry? ResolveKey(string key)
    {
        const string prefix = "gtfo.enemy:";
        if (key == null || !key.StartsWith(prefix, StringComparison.Ordinal)) return null;
        if (!ushort.TryParse(key.AsSpan(prefix.Length), System.Globalization.NumberStyles.None,
            System.Globalization.CultureInfo.InvariantCulture, out ushort id)) return null;
        return _entities.TryGetValue(id, out var entry)
            && entry.Reference.Id == key && Resolve(entry.Reference) != null ? entry : null;
    }

    /// <summary>The ability table of one tracked life, or null when the enemy cannot be read at all.</summary>
    private static EnemyAbilities? Abilities(Entry? entry)
    {
        if (entry == null) return null;
        try { return entry.Enemy.Abilities; }
        catch (Exception) { return null; }
    }

    /// <summary>The index of the requested ability in one enemy's own `AllComps` table. The scan is ordinal and
    /// takes the first component of that kind, which is the same pair `GetAbilities` reports for the ability, so
    /// the component the trigger reaches is the one the game enumerated.</summary>
    private static bool TryAbilityIndex(EnemyAbilities abilities, Agents.AgentAbility ability, out int index)
    {
        index = -1;
        var comps = abilities.AllComps;
        if (comps == null) return false;
        for (int i = 0; i < comps.Length; i++)
        {
            var component = comps[i];
            if (component == null || (byte)component.m_abilityType != (byte)ability) continue;
            index = i;
            return true;
        }
        return false;
    }

    /// <summary>The component one tracked life's own table holds for the requested ability, and the machine's
    /// `AbilityIsDone()` answer for it. False when the entry is gone, the ability is not registered on it, or the
    /// component cannot be reached, which is an unknown rather than an answer.</summary>
    private static bool TryAbilityComponent(Entry? entry, byte ability, out IntPtr component, out bool done)
    {
        component = IntPtr.Zero;
        done = false;
        var abilities = Abilities(entry);
        if (abilities == null) return false;
        try
        {
            if (!TryAbilityIndex(abilities, (Agents.AgentAbility)ability, out int index)) return false;
            var comps = abilities.AllComps;
            if (comps == null || index < 0 || index >= comps.Length) return false;
            var abilityComponent = comps[index];
            if (abilityComponent == null) return false;
            component = abilityComponent.Pointer;
            done = abilityComponent.AbilityIsDone();
            return true;
        }
        catch (Exception) { component = IntPtr.Zero; done = false; return false; }
    }

    /// <summary>Reports one ability end the ledger proved: the row is dropped while the machine's own
    /// `AbilityIsDone()` answer still says the ability is running. A row the machine reports as finished ended
    /// normally and publishes nothing, an ability whose native member is not one this provider owns is never
    /// published as an invented reference, and a component that could not be read was already dropped by the
    /// caller. Nothing here writes the world: the whole fact is a reading of a submission this provider already
    /// made.</summary>
    private void ReportAbilityDropped(RunningBehavior dropped, bool finished, string reason)
    {
        if (finished) return;
        if (!EnemyAbilityResources.TryReference(dropped.Ability, out var ability)) return;
        var entry = ResolveKey(dropped.EnemyKey);
        if (entry == null) return;
        PublishBehavior("gtfo.enemy.ability", ForgeEnemy.EnemyAbilityInterruptedContract.BindingId, entry.Reference,
            new Dictionary<string, object>(StringComparer.Ordinal)
            {
                ["enemy"] = entry.Reference,
                ["ability"] = ability,
                ["reason"] = reason
            });
    }

    /// <summary>Drops every in-flight row. The world epoch is part of every key, so this is the explicit end for
    /// a world that is being torn down rather than a comparison every later read would have to repeat. A module
    /// that never started an ability has no table, and this stays the no-op the teardown path can always call.</summary>
    internal void ClearBehaviors() { CheckThread(); _behaviors?.Clear(); }

    /// <summary>The real bridge: every member asks this module's own tables and then the game's, and no answer
    /// leaves this type as a game object.</summary>
    private sealed class NativeBehaviorPorts : IBehaviorPorts
    {
        private readonly EnemyModule _module;
        internal NativeBehaviorPorts(EnemyModule module) => _module = module;

        public bool CanExecute => _module.CanExecute;

        public bool IsCurrent(string key) => _module.ResolveKey(key) != null;

        public bool IsAlive(string key)
        {
            var entry = _module.ResolveKey(key);
            if (entry == null) return false;
            try { return entry.Enemy.Alive; }
            catch (Exception) { return false; }
        }

        public bool CanTrigger(string key)
        {
            var abilities = Abilities(_module.ResolveKey(key));
            if (abilities == null) return false;
            try { return abilities.CanTriggerAbilities; }
            catch (Exception) { return false; }
        }

        public bool TryAbilityIndex(string key, byte ability, out int index)
        {
            index = -1;
            var abilities = Abilities(_module.ResolveKey(key));
            if (abilities == null) return false;
            try { return EnemyModule.TryAbilityIndex(abilities, (Agents.AgentAbility)ability, out index); }
            catch (Exception) { index = -1; return false; }
        }

        public bool TryUseAbility(string key, byte ability, int index, out bool triggered)
        {
            triggered = false;
            var entry = _module.ResolveKey(key);
            var abilities = Abilities(entry);
            if (entry == null || abilities == null) return false;
            try { triggered = abilities.UseAbility((Agents.AgentAbility)ability, index); }
            catch (Exception) { return false; }
            return true;
        }

        public bool TryAbilityState(string key, byte ability, out IntPtr component, out bool done)
            => TryAbilityComponent(_module.ResolveKey(key), ability, out component, out done);

        public void AbilityDropped(RunningBehavior dropped, bool finished, string reason)
            => _module.ReportAbilityDropped(dropped, finished, reason);

        public bool TryEmitNoise(string key, (double X, double Y, double Z) position, double radius)
        {
            if (_module.ResolveKey(key) == null) return false;
            var point = new Vector3((float)position.X, (float)position.Y, (float)position.Z);
            if (!float.IsFinite(point.x) || !float.IsFinite(point.y) || !float.IsFinite(point.z)) return false;
            AIG_CourseNode? node;
            try
            {
                if (!AIG_CourseNode.TryGetCourseNode(NoiseDimension, point, CourseNodeSampleDistance, out node)
                    || node == null || node.Pointer == IntPtr.Zero) return false;
            }
            catch (Exception) { return false; }
            var data = new NM_NoiseData(null, point, (float)radius, (float)radius, NoiseVerticalScale, node,
                EmittedNoiseType, NoiseIncludesNeighbourAreas, NoiseRaycastsFirstNode);
            NoiseManager.MakeNoise(data);
            return true;
        }
    }
}
