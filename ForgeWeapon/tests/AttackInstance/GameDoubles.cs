using System;

namespace ForgeWeapon.Tests.AttackInstance
{
    /// <summary>The native identity the package keys every one of its tables by. A double carries its own
    /// pointer, so a case can tell two instances apart exactly as the game's are.</summary>
    internal static class PointerSource
    {
        private static long _next = 1000;
        internal static IntPtr Next() => new(++_next);
    }
}

/// <summary>The game's item root, declared where the game declares it: `Item` is a global type and not a `Gear`
/// one, which is why the production adapter names it unqualified. Only the identity the package keys its tables
/// by is here.</summary>
internal class Item
{
    internal Item() => Pointer = ForgeWeapon.Tests.AttackInstance.PointerSource.Next();
    internal IntPtr Pointer { get; }
}

/// <summary>The weapon root. Its `Fire` exists for the hook set's `nameof` binding and for
/// <see cref="NativeMemberTests"/>, which checks the member against this build's interop metadata.</summary>
internal class Weapon : Item
{
    internal virtual void Fire(bool resetRecoilSimilarity = true) { }
}

namespace Gear
{
    internal class ItemEquippable : Item { }

    internal class BulletWeapon : Weapon
    {
        /// <summary>The game's own burst length: the value the `burst_*` facts carry. A case sets it the way the
        /// archetype's setup would.</summary>
        internal int m_burstMax { get; set; }
        internal int m_burstCurrentCount { get; set; }
        internal bool IsCurrentlyBurstFiring { get; set; }

        internal override void Fire(bool resetRecoilSimilarity = true) { }
    }

    internal class BulletWeaponSynced : BulletWeapon { }

    internal class RifleWeapon : BulletWeapon { }

    internal class RifleWeaponSynced : BulletWeaponSynced { }

    internal class Shotgun : BulletWeapon
    {
        internal override void Fire(bool resetRecoilSimilarity = true) { }
    }

    internal class ShotgunSynced : BulletWeaponSynced
    {
        internal override void Fire(bool resetRecoilSimilarity = true) { }
    }

    /// <summary>The melee family: no `Fire` at all, one attack entry and one end-of-swing callback, which is what
    /// the melee half of the hook set patches.</summary>
    internal class MeleeWeaponFirstPerson : ItemEquippable
    {
        internal void DoTriggerAttack(bool charged = false) { }
        internal void OnAttackHitDone() { }
    }

    /// <summary>The archetype base every burst-capable behaviour derives from: the weapon it was set up on is what
    /// the empty-clip and burst hooks hand to the module.</summary>
    internal class BulletWeaponArchetype
    {
        /// <summary>The game's own member name, which is what the hooks read.</summary>
        internal BulletWeapon? m_weapon { get; set; }
    }

    /// <summary>The burst behaviour: it declares both ends of the sequence and the empty-clip path, which is what
    /// makes it the archetype the burst and dry-fire hooks are installed on.</summary>
    internal class BWA_Burst : BulletWeaponArchetype
    {
        internal int m_burstMax { get; set; }
        internal int m_burstCurrentCount { get; set; }
        internal virtual void OnStartFiring() { }
        internal virtual void OnStopFiring() { }
        internal virtual void OnFireShotEmptyClip() { }
    }

    /// <summary>The automatic behaviour: the second archetype that declares both sequence ends and its own
    /// empty-clip path.</summary>
    internal class BWA_Auto : BulletWeaponArchetype
    {
        internal virtual void OnStartFiring() { }
        internal virtual void OnStopFiring() { }
        internal virtual void OnFireShotEmptyClip() { }
    }

    /// <summary>The semi-burst behaviour derives from the semi behaviour and declares no sequence end of its own,
    /// which is why it carries no burst hook: a sequence it never opened has no end this build can see.</summary>
    internal class BWA_Semi : BulletWeaponArchetype { }

    internal class BWA_SemiBurst : BWA_Semi
    {
        internal int m_burstMax { get; set; }
        internal int m_semiBurstCurrentCount { get; set; }
    }
}
