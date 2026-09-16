using System;
using Agents;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native.Observation;

/// <summary>Build-specific, read-only reading of one native enemy attack state. The native attack sequence is
/// split by two members of `ES_EnemyAttackBase` (build 20403457): `OnAttackWindUp` is dispatched from the tail of
/// `DoStartAttack` at 0x17FC67A through vtable slot 33 (+0x340), and `OnAttackPerform` is dispatched from
/// `UpdateAttackTimingAndLineOfSight` at 0x17FCE83 through slot 35 (+0x360) or straight from `DoStartAttack`.
/// This observer never calls either member and never writes the world: it only reads which attack state belongs
/// to the tracked enemy and whether a reading is stable enough to name it.</summary>
internal static class EnemyAttackFactsObserver
{
    /// <summary>The native attack state read back in one stable sample: the enemy's own reference, the attack
    /// index the game wrote, and the native target agent. The target stays a native instance here — resolving it
    /// to a reference belongs to the module, because a kind no provider claims stays unresolved.</summary>
    internal sealed record Sample(EntityReference Reference, int AttackIndex, Agent? Target);

    /// <summary>Two readings of the same attack instance that are identical in every published value. The
    /// reference itself is validated by the caller; a reading whose owning enemy moved while it was taken is
    /// dropped instead of rebuilt.</summary>
    internal static bool TryRead(EnemyAgent enemy, EntityReference reference, ES_EnemyAttackBase attack,
        int attackIndex, out Sample? sample)
    {
        sample = null;
        RuntimeEntityReferences.Validate(reference);
        if (!TryReadOnce(enemy, reference, attack, attackIndex, out var first)) return false;
        if (!TryReadOnce(enemy, reference, attack, attackIndex, out var second)) return false;
        if (first != second) return false;
        sample = first;
        return true;
    }

    private static bool TryReadOnce(EnemyAgent enemy, EntityReference reference, ES_EnemyAttackBase attack,
        int attackIndex, out Sample? sample)
    {
        sample = null;
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup || !enemy.Alive) return false;
        var pointer = enemy.Pointer; ushort id = enemy.GlobalID;
        if (reference.Id != "gtfo.enemy:" + id.ToString(System.Globalization.CultureInfo.InvariantCulture)) return false;
        if (attack == null || attack.Pointer == IntPtr.Zero) return false;
        var ai = enemy.AI;
        // The attack state carries the AI it was set up with, so a state belonging to another enemy is not this
        // enemy's attack even when both native pointers are live.
        if (ai == null || ai.m_enemyAgent == null || ai.m_enemyAgent.Pointer != pointer) return false;
        if (!ReferenceEquals(attack.m_ai, ai)) return false;
        var target = attack.m_attackTarget;
        // The native getter may re-enter; anything it can invalidate is re-read before the reading is returned.
        var index = attack.m_lastAttackIndex;
        if (index != attackIndex) return false;
        if (enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup || !enemy.Alive) return false;
        if (!ReferenceEquals(attack.m_ai, ai) || attack.m_lastAttackIndex != index) return false;
        sample = new(reference, index, target);
        return true;
    }
}
