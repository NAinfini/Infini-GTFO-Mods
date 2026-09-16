using System;
using Agents;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native.Observation;

/// <summary>Build-specific, read-only AI behaviour sample of one registered enemy. It never requests a
/// transition, never writes the world and never infers allegiance; every value is read back from the native
/// behaviour and locomotion objects of the same life and from the AI's own target, and a sample is dropped rather
/// than returned when the instance identity moves while it is being read.</summary>
internal static class EnemyBehaviorFactsObserver
{
    internal sealed record Sample(EntityReference Reference, int BehaviourState, bool HasValidTarget,
        AgentTarget? Target, int ScoutScreamPhase);

    private sealed record Reading(IntPtr EnemyPointer, ushort EnemyId, IntPtr BehaviourPointer,
        int BehaviourState, bool HasValidTarget, AgentTarget? Target, int ScoutScreamPhase);

    internal static Sample? Read(EnemyAgent enemy, EntityReference reference)
    {
        RuntimeEntityReferences.Validate(reference);
        var first = ReadOnce(enemy);
        if (first == null || first != ReadOnce(enemy)) return null;
        if (reference.Id != "gtfo.enemy:" + first.EnemyId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return null;
        return new(reference, first.BehaviourState, first.HasValidTarget, first.Target, first.ScoutScreamPhase);
    }

    private static Reading? ReadOnce(EnemyAgent enemy)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup || !enemy.Alive) return null;
        var pointer = enemy.Pointer; ushort id = enemy.GlobalID;
        var ai = enemy.AI;
        if (ai == null || ai.m_enemyAgent == null || ai.m_enemyAgent.Pointer != pointer) return null;
        var behaviour = ai.m_behaviour;
        var locomotion = ai.m_locomotion;
        if (behaviour == null || locomotion == null) return null;
        if (behaviour.m_ai == null || behaviour.m_ai.Pointer != ai.Pointer) return null;
        int state = (int)behaviour.m_currentStateName;
        // A native getter may re-enter; anything it can invalidate is re-read before the sample is returned.
        bool hasTarget = TryTarget(ai, out var target);
        int phase = ScoutScreamPhase(locomotion);
        if (enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup || !enemy.Alive) return null;
        return new(pointer, id, behaviour.Pointer, state, hasTarget, target, phase);
    }

    private static bool TryTarget(EnemyAI ai, out AgentTarget? target)
    {
        target = null;
        bool valid;
        try { valid = ai.IsTargetValid; } catch (Exception) { return false; }
        if (!valid) return false;
        try { target = ai.Target; } catch (Exception) { target = null; }
        return true;
    }

    /// <summary>The scout scream phase index while the scout scream state is active, and -1 otherwise: the
    /// saved state object survives after its exit, so the state machine's own current state decides.</summary>
    private static int ScoutScreamPhase(EnemyLocomotion locomotion)
    {
        if ((int)locomotion.CurrentStateEnum != ScoutScreamState) return -1;
        var scream = locomotion.ScoutScream;
        if (scream == null) return -1;
        int phase = (int)scream.m_state;
        return phase is >= 0 and <= MaximumScoutScreamPhase ? phase : -1;
    }

    /// <summary>ES_StateEnum.ScoutScream; the wire value below is ES_ScoutScream.ScoutScreamState.</summary>
    private const int ScoutScreamState = 13;
    private const int MaximumScoutScreamPhase = 4;
}
