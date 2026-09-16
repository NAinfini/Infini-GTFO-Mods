using System.Runtime.CompilerServices;
using Il2CppInterop.Runtime.InteropTypes;

// Doubles for the interop types this slice's native applier names. Names, namespaces and member kinds follow
// build 20403457 (dump.cs / interop Modules-ASM); behaviour is synthetic and NOT game-verified. This file is the
// slice's own, so adding a member here cannot disturb another suite's doubles.

public static class Pointers
{
    private static long _next = 0x10000;
    public static IntPtr Next() => new(Interlocked.Increment(ref _next));
}

/// <summary>The two-component value type the block carries inline; a struct is copied by assignment, which is
/// why the applier does not have to clone these the way it clones the recoil ranges.</summary>
public struct Vector2
{
    public float x;
    public float y;
    public Vector2(float x, float y) { this.x = x; this.y = y; }
}

/// <summary>The interop object base the game's own types derive from. `TryCast` is the interop idiom the
/// production code uses to read an object's real type out of a base-typed reference; the base class carries the
/// same member against the real interop metadata, so the double narrows it to this compile's own types.</summary>
public class Il2CppDouble : Il2CppObjectBase
{
    public Il2CppDouble() : base(Pointers.Next()) { }
    public new T? TryCast<T>() where T : class => this as T;
}

namespace GameData
{
    /// <summary>`GameData.MinMaxValue` is an il2cpp *object*, not an embedded pair of floats, which is why the
    /// applier has to clone it rather than assign the original's onto its own block.</summary>
    public class MinMaxValue : Il2CppDouble
    {
        public float Min;
        public float Max;
    }

    /// <summary>Only the members the applier reads and writes; the class defaults the native constructor writes
    /// are not modelled because the double's constructor is not the game's.</summary>
    public class ArchetypeDataBlock : Il2CppDouble
    {
        public int FireMode;
        public uint RecoilDataID;
        public int DamageBoosterEffect;
        public float Damage;
        public Vector2 DamageFalloff;
        public float StaggerDamageMulti;
        public float PrecisionDamageMulti;
        public int DefaultClipSize;
        public float DefaultReloadTime;
        public float CostOfBullet;
        public float ShotDelay;
        public float ShellCasingSize;
        public Vector2 ShellCasingSpeedRange;
        public bool PiercingBullets;
        public int PiercingDamageCountLimit;
        public float HipFireSpread;
        public float AimSpread;
        public float EquipTransitionTime;
        public float AimTransitionTime;
        public float BurstDelay;
        public int BurstShotCount;
        public int ShotgunBulletCount;
        public int ShotgunConeSize;
        public int ShotgunBulletSpread;
        public float SpecialChargetupTime;
        public float SpecialCooldownTime;
        public float SpecialSemiBurstCountTimeout;
        public float Sentry_StartFireDelay;
        public float Sentry_RotationSpeed;
        public float Sentry_DetectionMaxRange;
        public float Sentry_DetectionMaxAngle;
        public bool Sentry_FireTowardsTargetInsteadOfForward;
        public bool Sentry_ForceAimTowardsBody;
        public float Sentry_LongRangeThreshold;
        public float Sentry_ShortRangeThreshold;
        public bool Sentry_LegacyEnemyDetection;
        public bool Sentry_FireTagOnly;
        public bool Sentry_PrioTag;
        public float Sentry_StartFireDelayTagMulti;
        public float Sentry_RotationSpeedTagMulti;
        public float Sentry_DamageTagMulti;
        public float Sentry_StaggerDamageTagMulti;
        public float Sentry_CostOfBulletTagMulti;
        public float Sentry_ShotDelayTagMulti;
    }

    public class RecoilDataBlock : Il2CppDouble
    {
        public MinMaxValue? power;
        public float spring;
        public float dampening;
        public float hipFireCrosshairSizeDefault;
        public float hipFireCrosshairRecoilPop;
        public float hipFireCrosshairSizeMax;
        public MinMaxValue? horizontalScale;
        public MinMaxValue? verticalScale;
        public float directionalSimilarity;
        public float worldToViewSpaceBlendHorizontal;
        public float worldToViewSpaceBlendVertical;
        public float recoilPosImpulse;
        public float recoilPosShift;
        public float recoilPosShiftWeight;
        public float recoilPosStiffness;
        public float recoilPosDamping;
        public float recoilPosImpulseWeight;
        public float recoilCameraPosWeight;
        public float recoilAimingWeight;
        public float recoilRotImpulse;
        public float recoilRotStiffness;
        public float recoilRotDamping;
        public float recoilRotImpulseWeight;
        public float recoilCameraRotWeight;
        public float concussionIntensity;
        public float concussionFrequency;
        public float concussionDuration;
    }
}

/// <summary>The root of the gear hierarchy. In this build `Item` and `ItemEquippable` are declared in the global
/// namespace — only `Weapon` and everything below it live under `Gear` — which is why the applier names the gear
/// base through a fully qualified alias rather than importing the namespace.</summary>
public class Item : Il2CppDouble { }
public class ItemEquippable : Item { }

namespace Gear
{
    public class Weapon : ItemEquippable { }

    /// <summary>The archetype instance a weapon holds. `m_archetypeData` and `m_recoilData` are the two fields
    /// the applier swaps; `m_nextShotTimer` is the shot gate `cooldown_reset` zeroes.</summary>
    public class BulletWeaponArchetype : Il2CppDouble
    {
        public GameData.ArchetypeDataBlock? m_archetypeData;
        public GameData.RecoilDataBlock? m_recoilData;
        public float m_nextShotTimer;
        public float m_nextBurstTimer;
    }

    public class BWA_Auto : BulletWeaponArchetype { }
    public class BWA_Semi : BulletWeaponArchetype { }
    public class BWA_Burst : BulletWeaponArchetype { public int m_burstMax = -1; }
    public class BWA_SemiBurst : BWA_Semi { public int m_burstMax = -1; }

    public class BulletWeapon : Weapon
    {
        public BulletWeaponArchetype? m_archeType;
        public int m_burstMax;
        public int m_clip;
        public float m_lastFireTime;
    }

    public class Shotgun : BulletWeapon { }
    public class BulletWeaponSynced : BulletWeapon { }
}
