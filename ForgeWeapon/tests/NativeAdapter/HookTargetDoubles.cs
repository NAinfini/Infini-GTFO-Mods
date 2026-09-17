// The second half of the game doubles: the bodies the device, melee, attack-instance and reload families patch.
// Same rule as `GameDoubles.cs` — names, namespaces and member kinds follow build 20403457 (dump.cs / interop),
// behaviour is synthetic and NOT game-verified. They live in their own file only because the first file predates
// these families.
//
// A type declared here is deliberately in the global namespace where the production source reads it unqualified
// with a `using Gear;`/`using Player;` in scope: a member of the global namespace is found before an imported one,
// which is what keeps the interop assembly's own type of the same name out of this compile.

/// <summary>One agent handle inside a replicated damage packet. The game exposes it as a value whose own `TryGet`
/// answers whether this process still holds the agent, which is why the remote half reads the attacker through it
/// rather than off the packet directly.</summary>
public struct pAgent
{
    public Agents.Agent? Target;
    public bool TryGet(out Agents.Agent? agent)
    {
        agent = Target;
        return agent != null;
    }
}

/// <summary>The master-side melee packet `Dam_EnemyDamageBase.ReceiveMeleeDamage` is handed: the attacker and the
/// limb. The packed damage field is deliberately not modelled — the remote half measures the damage from the
/// receiver's own health instead of decoding that field.</summary>
public struct pFullDamageData
{
    public pAgent source;
    public int limbID;
}

/// <summary>The holder whose per-frame update the sight state is read from: `WieldedItem` is the item the fact is
/// about, `ItemAimTrigger` is the state the aim row is published from, and `m_owner` is the player it is published
/// for. The game declares it in the global namespace, beside the weapon types rather than inside `Gear`.</summary>
public class FirstPersonItemHolder : UnityObjectDouble
{
    public global::ItemEquippable? WieldedItem;
    public Player.PlayerAgent? m_owner;
    public bool ItemAimTrigger;
    public void Update() { }
}

/// <summary>The enemy damage receiver a replicated melee hit lands on. The health either side of the body is the
/// damage that really landed, and the owner is the enemy life the hit belongs to.</summary>
public class Dam_EnemyDamageBase : UnityObjectDouble
{
    public float Health;
    public Enemies.EnemyAgent? Owner;
    public void ReceiveMeleeDamage(pFullDamageData data) { }
}

/// <summary>The sentry's own firing component. `m_core` is the world instance the placement table tracks and
/// `Ammo` is the value the firing pair is measured across.</summary>
public class SentryGunInstance_Firing_Bullets : UnityObjectDouble
{
    public SentryGunInstance? m_core;
    public void UpdateFireMaster() { }
}

/// <summary>The mine's own trigger component, reached through its core the same way the sentry's is.</summary>
public class MineDeployerInstance_Detonate_Explosive : UnityObjectDouble
{
    public MineDeployerInstance? m_core;
    public void TriggerDetonate() { }
}

/// <summary>The hand-held glue gun: an equipment life like any other tool, with the two launch bodies the fired
/// row is published from.</summary>
public class GlueGun : ItemEquippable
{
    public void FireBurst() { }
    public void FireSingle() { }
}

/// <summary>The two deployables in the player's hands. They are what the placement kind is read from while the
/// device is still carried, before any world instance exists.</summary>
public class SentryGunFirstPerson : ItemEquippable { }
public class MineDeployerFirstPerson : ItemEquippable { }

namespace UnityEngine
{
    /// <summary>The engine clock. The glue gun's launch dedupe reads the frame it happened in, which is the only
    /// member of it this package uses.</summary>
    public static class Time
    {
        public static int frameCount;
    }
}

namespace Gear
{
    /// <summary>The melee state a swing carries. The two charge-hit members and the two plain hit members are the
    /// ones the `charged` port is read from; the rest of the game's set is deliberately not modelled.</summary>
    public enum eMeleeWeaponState
    {
        None = 0, AttackHitLeft = 1, AttackHitRight = 2, AttackChargeHitLeft = 3, AttackChargeHitRight = 4
    }

    /// <summary>The first-person melee swing: the local player's own attack entry and the per-target damage entry
    /// both hooks are declared on, plus the damage the swing fixed before that entry ran.</summary>
    public class MeleeWeaponFirstPerson : global::Weapon
    {
        public float m_damageToDeal;
        public eMeleeWeaponState CurrentStateName;
        public void DoTriggerAttack() { }
        public void OnAttackHitDone() { }
        public void DoAttackDamage(MeleeWeaponDamageData data, bool isPush) { }
    }

    /// <summary>The third-person swing this machine runs for somebody else, whose damage is the body's own
    /// argument.</summary>
    public class MeleeWeaponThirdPerson : global::Weapon
    {
        public void DoAttackDamage(UnityEngine.GameObject damageGO, float damage) { }
    }

    /// <summary>The melee weapon's own charge state machine: the object whose life is the charge. `m_weapon` is the
    /// weapon the charge belongs to, `m_elapsed` and `m_maxDamageTime` are the two numbers the charge proportion is
    /// read from, and the four bodies are the ones the charge hooks are declared on.</summary>
    public class MWS_Base : UnityObjectDouble
    {
        public MeleeWeaponFirstPerson? m_weapon;
        public float m_elapsed;
        public float m_maxDamageTime;
        public virtual void Enter() { }
        public virtual void Exit() { }
        public virtual void Update() { }
    }

    public sealed class MWS_ChargeUp : MWS_Base
    {
        public void OnChargeupRelease() { }
    }

    /// <summary>What the first-person damage entry was handed: the object the swing landed on. Only that one
    /// member is read, and the charge is read off the weapon's own state instead.</summary>
    public sealed class MeleeWeaponDamageData
    {
        public UnityEngine.GameObject? damageGO;
    }

    /// <summary>The two firing archetypes the empty-clip and burst-sequence hooks are declared on. Both were set up
    /// on the weapon they hold, and both declare the same three firing bodies.</summary>
    public abstract class FiringArchetypeDouble : UnityObjectDouble
    {
        public BulletWeapon? m_weapon;
        /// <summary>The ranged charge the archetype's own update runs: the flag the fact is read from and the
        /// elapsed timer the ratio is measured against.</summary>
        public bool m_inChargeup;
        public float m_chargeupTimer;
        public float ChargeupDelay() => 1f;
        public void OnFireShotEmptyClip() { }
        public void OnStartFiring() { }
        public void OnStopFiring() { }
    }

    public sealed class BWA_Auto : FiringArchetypeDouble { }
    /// <summary>The two burst archetypes carry the burst length field the other classes do not have, which is the
    /// field an instance override writes and restores.</summary>
    public sealed class BWA_Burst : FiringArchetypeDouble { public int m_burstMax = -1; }
    public sealed class BWA_SemiBurst : FiringArchetypeDouble { public int m_burstMax = -1; }
}

namespace Player
{
    /// <summary>One slot's pool. `BulletsInPack` is the rounds the pack holds — the readback the refill and
    /// transfer rows are measured with.</summary>
    public sealed class InventorySlotAmmo
    {
        public int BulletsInPack;
    }

    /// <summary>The per-player ammunition storage the reload family reads and the two clip/storage setters the
    /// reload hooks are declared on. It names the backpack whose wielded item the transfer is measured from.</summary>
    public sealed class PlayerAmmoStorage : UnityObjectDouble
    {
        public PlayerBackpack? m_playerBackpack;
        public void SetClipAmmoInSlot(InventorySlot slot, int clip) { }
        public void SetStorageData(InventorySlot slot) { }
        public InventorySlotAmmo? GetInventorySlotAmmo(InventorySlot slot) => null;
        /// <summary>The one body that clamps a pool at both ends, which is why the ammunition row spends through
        /// it rather than writing a count.</summary>
        public void UpdateBulletsInPack(Player.AmmoType type, int delta) { }
    }
}
