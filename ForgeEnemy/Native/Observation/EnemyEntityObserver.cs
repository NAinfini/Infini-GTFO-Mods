using System;
using Agents;
using Enemies;
using ForgeRuntime.Framework;
using UnityEngine;
using UnityEngine.AI;

namespace ForgeEnemy.Native.Observation;

/// <summary>Read-only, explicit-instance native data. No world query or allegiance inference.
///
/// Every field of the frozen snapshot is read here, for the reason the frame was frozen with them: an author who
/// asks "how hurt is this enemy", "is it still asleep", "where is it standing" or "which type is it" is asking
/// about the one enemy a reference already names, and the answer has to come from that instance rather than from a
/// ledger. Each reading is optional on its own terms — a receiver that does not read, a navigation component that
/// is not there, a behaviour state outside the game's own enumeration — and an unreadable reading is published as
/// null so a consumer refuses instead of computing with a substituted zero.
///
/// The behaviour state is the one reading that is not a raw native number: the frame's `aiState` is a member name
/// of the shared `ai_state` set, so the native `EB_States` value is mapped here, in one table, and the mapping is
/// the same one `EnemyModule`'s behaviour facts publish as an index. A native member with no counterpart in the
/// shared set is `disabled`, which is what the set's own last member means for a state no author can act on.</summary>
internal static class EnemyEntityObserver
{
    private static readonly string[] HealReceiver = { "health.heal" };
    private sealed record Sample(IntPtr Pointer, ushort Id, bool Alive,
        float X, float Y, float Z, Receiver Health, Direction Facing, int Behaviour, double? Speed);
    private sealed record Receiver(IntPtr Pointer, IntPtr Owner, bool Setup,
        float Health, float Maximum);
    private sealed record Direction(float X, float Y, float Z);

    internal static RuntimeEntitySnapshot? Read(EnemyAgent enemy, EntityReference reference)
    {
        RuntimeEntityReferences.Validate(reference);
        var first = Capture(enemy);
        if (first == null || first != Capture(enemy)
            || reference.Id != "gtfo.enemy:" + first.Id.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
        var health = first.Health;
        bool canHeal = first.Alive && health.Pointer != IntPtr.Zero && health.Owner == first.Pointer
            && health.Setup && float.IsFinite(health.Health) && float.IsFinite(health.Maximum)
            && health.Health > 0 && health.Maximum > 0 && health.Health <= health.Maximum;
        double? healthReading = canHeal ? health.Health : null;
        double? maximumReading = canHeal ? health.Maximum : null;
        return new RuntimeEntitySnapshot(reference, "enemy", null, first.Alive ? "alive" : "dead",
            Array.Empty<string>(), canHeal ? HealReceiver : Array.Empty<string>(),
            new double[] { first.X, first.Y, first.Z },
            health: healthReading, healthMaximum: maximumReading,
            rotation: new double[] { first.Facing.X, first.Facing.Y, first.Facing.Z },
            aiState: AiState(first.Behaviour), speed: first.Speed);
    }

    /// <summary>The tag readings the `v-e-tagged` row answers: the enemy's own tag flag and the seconds of tag
    /// time it has left. Read through the same explicit-instance path the snapshot uses, and answered as an
    /// absence when either getter cannot be read.</summary>
    internal static TagReading? ReadTag(EnemyAgent enemy)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
        var pointer = enemy.Pointer;
        ushort id = enemy.GlobalID;
        bool tagged;
        float timer;
        try
        {
            tagged = enemy.IsTagged;
            timer = enemy.EnemyTaggedTimer;
        }
        catch (Exception) { return null; }
        if (!float.IsFinite(timer) || timer < 0f) timer = 0f;
        if (enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup) return null;
        return new TagReading(tagged, timer);
    }

    /// <summary>The glue volume the enemy's own damage receiver publishes, or null when the receiver cannot be
    /// read. This is the same reading `forge.trigger.enemy.glued` publishes as its `total`.</summary>
    internal static float? ReadGlueVolume(Dam_EnemyDamageBase? damage)
    {
        if (damage == null || damage.Pointer == IntPtr.Zero) return null;
        try
        {
            float volume = damage.AttachedGlueVolume;
            return float.IsFinite(volume) && volume >= 0f ? volume : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The zone the enemy stands in, as the three coordinates the framework's zone identity is written
    /// from: the enemy's own course node owns the zone, and the zone owns its index inside a layer of one
    /// dimension. Null when any link of that chain does not read, which leaves the zone unanswered instead of
    /// guessed.</summary>
    internal static (int Dimension, int Layer, int Zone)? ReadZone(EnemyAgent enemy)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
        try
        {
            var node = enemy.CourseNode;
            if (node == null) return null;
            var zone = node.m_zone;
            if (zone == null) return null;
            var layer = zone.m_layer;
            if (layer == null) return null;
            return ((int)zone.m_dimensionIndex, (int)layer.m_type, (int)zone.LocalIndex);
        }
        catch (Exception) { return null; }
    }

    internal readonly record struct TagReading(bool Tagged, float RemainingSeconds);

    private static Sample? Capture(EnemyAgent enemy)
    {
        if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
        var pointer = enemy.Pointer;
        ushort id = enemy.GlobalID;
        bool alive = enemy.Alive;
        var position = enemy.Position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z))
            return null;
        var damage = enemy.Damage;
        var health = damage == null || damage.Pointer == IntPtr.Zero
            ? new Receiver(IntPtr.Zero, IntPtr.Zero, false, 0, 0)
            : new Receiver(damage.Pointer, damage.Owner == null ? IntPtr.Zero : damage.Owner.Pointer,
                damage.IsSetup, damage.Health, damage.HealthMax);
        var facing = ReadFacing(enemy);
        int behaviour = ReadBehaviourState(enemy);
        double? speed = ReadSpeed(enemy);
        if (enemy == null || enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup)
            return null;
        return new Sample(pointer, id, alive, position.x, position.y, position.z, health, facing, behaviour, speed);
    }

    /// <summary>The direction the enemy faces, read from the agent's own forward vector. A non-finite component
    /// makes the whole reading absent rather than a direction nobody can normalize.</summary>
    private static Direction ReadFacing(EnemyAgent enemy)
    {
        try
        {
            var forward = enemy.Forward;
            if (float.IsFinite(forward.x) && float.IsFinite(forward.y) && float.IsFinite(forward.z))
                return new Direction(forward.x, forward.y, forward.z);
        }
        catch (Exception) { }
        return new Direction(0f, 0f, 0f);
    }

    /// <summary>The enemy's behaviour state, or -1 when no behaviour machine can be read: the frame then answers
    /// `disabled`, the one member of the shared set that means "no state an author can act on".</summary>
    private static int ReadBehaviourState(EnemyAgent enemy)
    {
        try
        {
            var behaviour = enemy.AI?.m_behaviour;
            return behaviour == null ? -1 : (int)behaviour.m_currentStateName;
        }
        catch (Exception) { return -1; }
    }

    /// <summary>The speed the enemy's own navigation component is set to. A component that is not there, or a
    /// value that is not a finite non-negative quantity, leaves the reading absent.</summary>
    private static double? ReadSpeed(EnemyAgent enemy)
    {
        try
        {
            INavigation? navigation = enemy.AI?.m_navMeshAgent;
            if (navigation == null) return null;
            double speed = navigation.speed;
            return double.IsFinite(speed) && speed >= 0 ? speed : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Native `EB_States` to the shared `ai_state` set, one entry per declared native member. The member
    /// names are the set's own spelling, so a consumer compares with the vocabulary it already has rather than
    /// with an index into a table only this file knows.</summary>
    internal static string AiState(int nativeState) => nativeState switch
    {
        0 => "hibernating",       // Hibernating
        1 => "patrolling",        // Patrolling
        2 => "investigating",     // Patrolling_Investigate
        3 => "idle",              // FollowingGroup
        4 => "idle",              // FollowingGroup_MoveToNode
        5 => "pursuing",          // InCombat
        6 => "pursuing",          // InCombat_MoveToPoint
        7 => "pursuing",          // InCombat_MoveToTarget
        8 => "pursuing",          // InCombat_MoveToNextNode
        9 => "pursuing",          // InCombat_MoveToNextNode_PathBlocked
        10 => "pursuing",         // InCombat_MoveToNextNode_PathOpen
        11 => "pursuing",         // InCombat_MoveToNextNode_DestroyDoor
        12 => "hibernating",      // SquidBoss_Hibernating
        13 => "waking",           // SquidBoss_Intro: the boss leaving its hibernation
        14 => "attacking",        // SquidBoss_Combat
        15 => "attacking",        // SquidBoss_Raging: a combat phase
        16 => "attacking",        // SquidBoss_Spawning: spawning adds is an attack
        17 => "recovering",       // SquidBoss_Cooldown
        18 => "recovering",       // SquidBoss_RageTransition
        19 => "dead",             // Dead
        20 => "attacking",        // InCombat_ChargedAttack
        21 => "pursuing",         // Incombat_GraphTraversal_Flyer
        22 => "pursuing",         // Incombat_FlyOutOfBoss_Flyer
        23 => "pursuing",         // InCombat_Dash
        24 => "attacking",        // InCombat_HeldPlayer
        25 => "attacking",        // InCombat_AfterHeldPlayer
        26 => "attacking",        // InCombat_Consume
        27 => "attacking",        // InCombat_SpitOut
        28 => "recovering",       // InCombat_Stagger
        _ => "disabled"           // no mapped native member is a state an author can act on
    };

    /// <summary>The two native members the `v-e-sleeping` row calls hibernating: `EB_States.Hibernating` and the
    /// squid boss's own hibernation. The same pair `forge.trigger.enemy.awakened` treats as the sleeping
    /// baseline, so the event and the value cannot disagree about what sleep is.</summary>
    internal static bool Hibernating(int nativeState) => nativeState is 0 or 12;
}
