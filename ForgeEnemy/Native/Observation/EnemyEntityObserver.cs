using System;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native.Observation;

/// <summary>Read-only, explicit-instance native data. No world query or allegiance inference.</summary>
internal static class EnemyEntityObserver
{
    private static readonly string[] HealReceiver = { "health.heal" };
    private sealed record Sample(IntPtr Pointer, ushort Id, bool Alive,
        float X, float Y, float Z, Receiver Health);
    private sealed record Receiver(IntPtr Pointer, IntPtr Owner, bool Setup,
        float Health, float Maximum);

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
        return new RuntimeEntitySnapshot(reference, "enemy", null, first.Alive ? "alive" : "dead",
            Array.Empty<string>(), canHeal ? HealReceiver : Array.Empty<string>(),
            new double[] { first.X, first.Y, first.Z });
    }

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
        if (enemy == null || enemy.Pointer != pointer || enemy.GlobalID != id || !enemy.IsSetup)
            return null;
        return new Sample(pointer, id, alive, position.x, position.y, position.z, health);
    }
}
