using System;
using Enemies;

namespace ForgeEnemy.Native;

/// <summary>The one native write path a damage row submits through, kept in its own type so the game assembly is
/// reached from exactly one audited member and the handler stays readable without it. The receiver probe compiles
/// this same source against a stand-in receiver, which is why the entry call needs no injection seam.</summary>
internal static class EnemyNativeWrite
{
    /// <summary>One bullet-damage submission with the exact argument list the frozen interop declares. The
    /// attacker is null: the source and instigator ports carry entity identities the kernel validated, and no
    /// provider can hand this assembly the native object behind another provider's reference, so no attribution
    /// is invented. The receiver tolerates the missing attacker — `ProcessReceivedDamage` passes it to
    /// `EnemyAgent.RegisterDamageInflictor`, whose body returns immediately for a null inflictor, and the
    /// explosion receive path reaches the same window from a packet that cannot name an attacker at all.
    /// Positions are zero because this action has no geometry port, the limb argument is the array position the
    /// declared id names (resolved by the caller), and the multipliers and gear category are the neutral values.</summary>
    internal static void ApplyDamage(Dam_EnemyDamageBase damage, int limbIndex, float requested)
        => damage.BulletDamage(requested, null, UnityEngine.Vector3.zero, UnityEngine.Vector3.zero,
            UnityEngine.Vector3.zero, false, limbIndex, 1f, 1f, 0u);

    /// <summary>Resolves the limb a hit should name. `-1` means no limb and needs no array at all; any other id
    /// must be one this receiver's limb array actually declares, and the index the entry point takes is that
    /// entry's position, which is not the id. An unreadable array or an id no limb declares answers false.</summary>
    internal static bool TryNameLimb(Dam_EnemyDamageBase damage, int limbId, out int limbIndex)
    {
        limbIndex = -1;
        try
        {
            if (limbId < 0) return true;
            var limbs = damage.DamageLimbs;
            if (limbs == null) return false;
            for (int index = 0; index < limbs.Length; index++)
            {
                var limb = limbs[index];
                if (limb == null || limb.m_limbID != limbId) continue;
                limbIndex = index;
                return true;
            }
            return false;
        }
        catch (Exception) { limbIndex = -1; return false; }
    }
}
