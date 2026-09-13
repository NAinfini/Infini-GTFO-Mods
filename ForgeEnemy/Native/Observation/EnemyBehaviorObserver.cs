using System;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native.Observation;

/// <summary>Build-specific AI observation only; it does not request AI transitions or infer allegiance.</summary>
internal static class EnemyBehaviorObserver
{
    internal sealed record Snapshot(EntityReference Reference, int NativeState,
        bool HasValidTarget, int ActiveAbility, bool CanTriggerAbilities, bool Invisible);

    private sealed record Sample(IntPtr EnemyPointer, ushort EnemyId,
        int NativeState, bool HasValidTarget, int ActiveAbility,
        bool CanTriggerAbilities, bool Invisible);

    internal static Snapshot? Read(EnemyAgent enemy, EntityReference reference)
    {
        RuntimeEntityReferences.Validate(reference);
        var first = Capture(enemy);
        if (first == null || first != Capture(enemy)) return null;
        if (reference.Id != "gtfo.enemy:" + first.EnemyId.ToString(System.Globalization.CultureInfo.InvariantCulture))
            return null;
        return new(reference, first.NativeState, first.HasValidTarget,
            first.ActiveAbility, first.CanTriggerAbilities, first.Invisible);
    }

    private static Sample? Capture(EnemyAgent enemy)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup || !enemy.Alive) return null;
        var pointer = enemy.Pointer; ushort id = enemy.GlobalID;
        var ai = enemy.AI; var locomotion = enemy.Locomotion; var abilities = enemy.Abilities;
        if (ai == null || locomotion == null || abilities == null) return null;
        if (ai.m_enemyAgent == null || ai.m_enemyAgent.Pointer != pointer
            || locomotion.m_agent == null || locomotion.m_agent.Pointer != pointer
            || abilities.m_agent == null || abilities.m_agent.Pointer != pointer)
            return null;
        int state = (int)locomotion.CurrentStateEnum;
        bool hasTarget = enemy.m_hasValidTarget;
        int activeAbility = (int)abilities.ActiveAbility;
        bool canTrigger = abilities.CanTriggerAbilities;
        bool invisible = enemy.IsInvisible();
        if (enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup || !enemy.Alive)
            return null;
        return new(pointer, id, state, hasTarget, activeAbility, canTrigger, invisible);
    }
}
