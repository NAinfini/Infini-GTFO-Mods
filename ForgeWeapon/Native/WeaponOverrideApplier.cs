using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
// The game's gear types are named through fully qualified aliases rather than imported: `Gear` is the namespace
// they live in and this file's own namespace is `ForgeWeapon.Native`, so an unqualified `Gear.X` would be looked
// for inside the package first. `ItemEquippable` is the root of the gear hierarchy in this build and lives in the
// global namespace, not in `Gear`.
using ArchetypeDataBlock = global::GameData.ArchetypeDataBlock;
using BulletWeaponArchetype = global::Gear.BulletWeaponArchetype;
using GearBase = global::ItemEquippable;
using MinMaxRange = global::GameData.MinMaxValue;
using RecoilDataBlock = global::GameData.RecoilDataBlock;
using WeaponBase = global::Gear.BulletWeapon;

namespace ForgeWeapon.Native;

/// <summary>
/// The one place in this package that changes a weapon's numbers: ruling 84's instance-level block replacement,
/// written the only way the shipped code allows.
///
/// The evidence (evidence/weapon-override-hooks.json) settles three facts this file is built on. First, rate of
/// fire, spread, burst length and damage are read out of `BulletWeaponArchetype.m_archetypeData` on every shot
/// and every frame, so changing what an instance points at changes its behaviour immediately. Second, exactly
/// two values are *not* read per shot: `m_recoilData`, resolved once in the archetype constructor from the
/// block's `RecoilDataID`, and `m_burstMax`, copied out of the block by the burst archetypes' own `Setup`. Both
/// are refreshed here, or a swapped block would give a weapon the new rate of fire and the old recoil. Third,
/// native rebuilds the archetype in exactly one chain — `OnGearSpawnComplete` — so an override survives until
/// the next gear spawn and is replayed there.
///
/// What this file will not do: it never writes the shared block (the clone is the instance's own), never builds a
/// Forge-only override that the player's own trigger would ignore, and never invents a value it could not read.
/// A field whose native destination the instance does not have is refused *before* the first write, so a refused
/// request leaves the weapon exactly as it was found.
/// </summary>
internal sealed class WeaponOverrideApplier : IWeaponOverrideSink
{
    /// <summary>One overridden instance: the archetype being written, the instance's own block and recoil block
    /// as they were found, and this instance's clones. `OriginalBurstMax` is the weapon's own burst limit, which
    /// the burst-count field also has to change and therefore also has to put back.</summary>
    private sealed class Applied
    {
        internal EntityReference Equipment = null!;
        internal WeaponBase Weapon = null!;
        internal BulletWeaponArchetype Archetype = null!;
        internal ArchetypeDataBlock OriginalBlock = null!;
        internal RecoilDataBlock? OriginalRecoil;
        internal int OriginalBurstMax;
        internal bool BurstPatched;
        internal ArchetypeDataBlock ClonedBlock = null!;
        internal RecoilDataBlock? ClonedRecoil;
    }

    private readonly Dictionary<string, Applied> _applied = new(StringComparer.Ordinal);
    private readonly Func<EntityReference, WeaponBase?> _weaponOf;
    private readonly Func<WeaponBase, EntityReference?> _equipmentOf;
    private readonly Action<string> _report;
    private readonly Action<string> _info;

    /// <summary>`weaponOf` is the equipment domain's own current-instance lookup for `gtfo.equipment`, and
    /// `equipmentOf` is the inverse for a native weapon. Neither is a second table: a weapon this package was
    /// never told about is answered with null and the request is refused as stale, which is what an instance
    /// that no longer exists deserves.</summary>
    internal WeaponOverrideApplier(Func<EntityReference, WeaponBase?> weaponOf,
        Func<WeaponBase, EntityReference?> equipmentOf, Action<string> report, Action<string> info)
    {
        _weaponOf = weaponOf ?? throw new ArgumentNullException(nameof(weaponOf));
        _equipmentOf = equipmentOf ?? throw new ArgumentNullException(nameof(equipmentOf));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
        Ledger = new WeaponOverrideLedger(this);
    }

    /// <summary>The ledger this applier writes for. The two are one feature: the ledger decides what is true and
    /// this file is the only code that makes it true on a weapon, so a caller can never hold one without the
    /// other.</summary>
    internal WeaponOverrideLedger Ledger { get; }

    /// <summary>Instances currently carrying a clone this machine wrote.</summary>
    internal int AppliedCount => _applied.Count;

    /// <summary>
    /// Writes one merged field set onto one instance. Every field is checked against the instance before the
    /// first write: an unknown name, a spread cone on a weapon whose fire path never reads one, a burst length on
    /// an archetype that has no such field, and a recoil value on an instance whose recoil block is missing are
    /// all refusals with their own code. The clones are made once and kept, so a second request on the same
    /// instance changes the clone it already owns instead of cloning again.
    /// </summary>
    public bool Apply(EntityReference equipment, IReadOnlyList<WeaponOverrideField> fields, out string code)
    {
        code = "";
        var weapon = _weaponOf(equipment);
        if (weapon == null || weapon.Pointer == IntPtr.Zero)
        {
            code = WeaponOverrideLedger.StaleEquipmentCode;
            return false;
        }
        var archetype = weapon.m_archeType;
        if (archetype == null || archetype.Pointer == IntPtr.Zero)
        {
            code = WeaponOverrideLedger.BlockMissingCode;
            return false;
        }
        var block = archetype.m_archetypeData;
        if (block == null || block.Pointer == IntPtr.Zero)
        {
            // The instance's own block is the template. A clone taken from nothing would be a weapon this
            // package invented, so the request is refused instead.
            code = WeaponOverrideLedger.BlockMissingCode;
            return false;
        }

        var key = equipment.Id ?? "";
        if (!_applied.TryGetValue(key, out var applied) || applied.Archetype.Pointer != archetype.Pointer)
        {
            applied = new Applied
            {
                Equipment = equipment,
                Weapon = weapon,
                Archetype = archetype,
                OriginalBlock = block,
                OriginalRecoil = archetype.m_recoilData,
                OriginalBurstMax = weapon.m_burstMax,
                ClonedBlock = CloneBlock(block)
            };
            _applied[key] = applied;
        }

        // The whole request is judged first. A write that happens before its own refusal check would leave the
        // instance in a state no native path produces, and the family a weapon belongs to is what decides which
        // of these destinations exist on it at all — the check itself is the one the action rows share.
        var wantsRecoil = false;
        var wantsBurst = false;
        var family = Family(weapon, applied.Archetype);
        foreach (var field in fields)
        {
            if (!WeaponActionRuntime.HasDestination(field.Name, family, out var recoil))
            {
                code = WeaponOverrideLedger.UnknownFieldCode;
                return false;
            }
            wantsRecoil |= recoil;
            wantsBurst |= field.Name == "burst_count";
        }
        if (wantsRecoil && (applied.OriginalRecoil == null || applied.OriginalRecoil.Pointer == IntPtr.Zero))
        {
            // A block whose RecoilDataID is not in the game's own recoil table resolves to no recoil block. The
            // instance has no recoil destination at all, so a recoil request is refused rather than written to a
            // field nothing reads.
            code = WeaponOverrideLedger.BlockMissingCode;
            return false;
        }
        if (wantsRecoil && !HasRecoilRange(applied.OriginalRecoil!, fields))
        {
            // The three values the recoil path reads through a pointer are objects of their own, and a block that
            // carries none of them has no destination for that value. The whole request is refused for the same
            // reason one missing field always is: the accepted part would be a half-applied weapon.
            code = WeaponOverrideLedger.BlockMissingCode;
            return false;
        }

        if (wantsRecoil && applied.ClonedRecoil == null)
            applied.ClonedRecoil = CloneRecoil(applied.OriginalRecoil!);

        foreach (var field in fields) Write(applied, field);

        // The two caches the native code does not re-read per shot. Recoil is resolved in the archetype's
        // constructor, so a clone without this assignment would keep the old kick; the burst length is copied by
        // the burst archetypes' Setup, so both the archetype's copy and the weapon's own limit are set here.
        if (applied.ClonedRecoil != null) applied.Archetype.m_recoilData = applied.ClonedRecoil;
        applied.Archetype.m_archetypeData = applied.ClonedBlock;
        if (wantsBurst)
        {
            SetBurstCount(applied.Archetype, applied.ClonedBlock.BurstShotCount);
            applied.Weapon.m_burstMax = applied.ClonedBlock.BurstShotCount;
            applied.BurstPatched = true;
        }
        ClampClip(applied);

        _info("weapon.override-applied equipment=" + key + " fields=" + fields.Count.ToString(CultureInfo.InvariantCulture));
        return true;
    }

    /// <summary>Puts one instance's own block back, and its two cached values with it. Restoring an instance this
    /// machine never overrode is a success with nothing to do.</summary>
    public bool Restore(EntityReference equipment, out string code)
    {
        code = "";
        if (equipment == null) return true;
        if (!_applied.TryGetValue(equipment.Id ?? "", out var applied)) return true;
        _applied.Remove(equipment.Id ?? "");
        var archetype = applied.Archetype;
        if (archetype == null || archetype.Pointer == IntPtr.Zero) return true;
        archetype.m_archetypeData = applied.OriginalBlock;
        if (applied.ClonedRecoil != null) archetype.m_recoilData = applied.OriginalRecoil;
        if (applied.BurstPatched)
        {
            SetBurstCount(archetype, applied.OriginalBlock.BurstShotCount);
            applied.Weapon.m_burstMax = applied.OriginalBurstMax;
        }
        ClampClip(applied);
        _info("weapon.override-restored equipment=" + (equipment.Id ?? ""));
        return true;
    }

    /// <summary>Every instance this machine overrode, put back. Returns how many were restored, so a world
    /// boundary can report whether anything had been written at all.</summary>
    public int RestoreAll()
    {
        if (_applied.Count == 0) return 0;
        var keys = new List<string>(_applied.Keys);
        var restored = 0;
        foreach (var key in keys)
        {
            if (!_applied.TryGetValue(key, out var applied)) continue;
            if (Restore(applied.Equipment, out _)) restored++;
        }
        return restored;
    }

    /// <summary>
    /// One gear finished spawning, which is the only chain in the shipped code that rebuilds an archetype. An
    /// instance this package already overrode has just lost its clone, so the accepted fields are written again
    /// from the ledger's own answer. Replay is idempotent because it writes the merged absolute set and not a
    /// delta, and it is not a second application path: it calls the same <see cref="Apply"/>.
    /// </summary>
    internal void OnGearSpawnComplete(GearBase? gear)
    {
        var weapon = gear?.TryCast<WeaponBase>();
        if (weapon == null) return;
        var equipment = _equipmentOf(weapon);
        if (equipment == null) return;
        var fields = Ledger.Pending(equipment);
        if (fields.Count == 0) return;
        // The rebuilt archetype carries the game's own block again, so the clone this instance used to hold is
        // no longer referenced by it; the cache entry is dropped first so the write clones from the new block.
        _applied.Remove(equipment.Id ?? "");
        if (!Apply(equipment, fields, out var code))
            _report("weapon.override-replay-refused equipment=" + equipment.Id + " code=" + code);
    }

    /// <summary>One field onto the instance's clone. Only names the check above accepted reach here, so the
    /// switch has no default that silently ignores a request.</summary>
    private static void Write(Applied applied, WeaponOverrideField field)
    {
        var block = applied.ClonedBlock;
        var recoil = applied.ClonedRecoil;
        switch (field.Name)
        {
            case "fire_rate":
                block.ShotDelay = (float)field.Value;
                break;
            case "spread_cone":
                // The cone is an integer count of degrees; the shipped read is a 32-bit load (`cmp r15d` family),
                // so the value is rounded here rather than written as a fraction nothing reads back.
                block.ShotgunConeSize = (int)Math.Round(field.Value, MidpointRounding.AwayFromZero);
                break;
            case "spread_movement_scale":
                block.HipFireSpread = (float)field.Value;
                break;
            case "spread_aim_scale":
                block.AimSpread = (float)field.Value;
                break;
            case "recoil_horizontal":
                if (recoil != null) recoil.horizontalScale!.Min = (float)field.Value;
                break;
            case "recoil_vertical":
                if (recoil != null) recoil.verticalScale!.Min = (float)field.Value;
                break;
            case "recoil_recovery":
                if (recoil != null) recoil.spring = (float)field.Value;
                break;
            case "recoil_camera_kick":
                if (recoil != null) recoil.power!.Min = (float)field.Value;
                break;
        }
    }

    /// <summary>
    /// A copy of one archetype block, written field by field. There is no clone helper to call: the only
    /// parameterless constructor the interop type has is the game's own, reached through `il2cpp_object_new` plus
    /// an invoke of that constructor, and `MemberwiseClone` is not reachable at all. The constructor is still the
    /// right starting point — it writes the class defaults the game expects onto the new object and touches no
    /// static table, so the clone is never registered, never marked dirty and triggers no reload.
    ///
    /// The copy is explicit and covers the values this package can read. Reference-typed fields
    /// (`PublicName`/`Description`, the two animation sequences) keep pointing at the same objects as the
    /// original, which is deliberate: they are shared, immutable in this package's hands, and copying them would
    /// mean inventing objects the game's own data tables do not know.
    /// </summary>
    private static ArchetypeDataBlock CloneBlock(ArchetypeDataBlock source)
    {
        var clone = Construct<ArchetypeDataBlock>(Il2CppClassPointerStore<ArchetypeDataBlock>.NativeClassPtr);
        clone.FireMode = source.FireMode;
        clone.RecoilDataID = source.RecoilDataID;
        clone.DamageBoosterEffect = source.DamageBoosterEffect;
        clone.Damage = source.Damage;
        clone.DamageFalloff = source.DamageFalloff;
        clone.StaggerDamageMulti = source.StaggerDamageMulti;
        clone.PrecisionDamageMulti = source.PrecisionDamageMulti;
        clone.DefaultClipSize = source.DefaultClipSize;
        clone.DefaultReloadTime = source.DefaultReloadTime;
        clone.CostOfBullet = source.CostOfBullet;
        clone.ShotDelay = source.ShotDelay;
        clone.ShellCasingSize = source.ShellCasingSize;
        clone.ShellCasingSpeedRange = source.ShellCasingSpeedRange;
        clone.PiercingBullets = source.PiercingBullets;
        clone.PiercingDamageCountLimit = source.PiercingDamageCountLimit;
        clone.HipFireSpread = source.HipFireSpread;
        clone.AimSpread = source.AimSpread;
        clone.EquipTransitionTime = source.EquipTransitionTime;
        clone.AimTransitionTime = source.AimTransitionTime;
        clone.BurstDelay = source.BurstDelay;
        clone.BurstShotCount = source.BurstShotCount;
        clone.ShotgunBulletCount = source.ShotgunBulletCount;
        clone.ShotgunConeSize = source.ShotgunConeSize;
        clone.ShotgunBulletSpread = source.ShotgunBulletSpread;
        clone.SpecialChargetupTime = source.SpecialChargetupTime;
        clone.SpecialCooldownTime = source.SpecialCooldownTime;
        clone.SpecialSemiBurstCountTimeout = source.SpecialSemiBurstCountTimeout;
        clone.Sentry_StartFireDelay = source.Sentry_StartFireDelay;
        clone.Sentry_RotationSpeed = source.Sentry_RotationSpeed;
        clone.Sentry_DetectionMaxRange = source.Sentry_DetectionMaxRange;
        clone.Sentry_DetectionMaxAngle = source.Sentry_DetectionMaxAngle;
        clone.Sentry_FireTowardsTargetInsteadOfForward = source.Sentry_FireTowardsTargetInsteadOfForward;
        clone.Sentry_ForceAimTowardsBody = source.Sentry_ForceAimTowardsBody;
        clone.Sentry_LongRangeThreshold = source.Sentry_LongRangeThreshold;
        clone.Sentry_ShortRangeThreshold = source.Sentry_ShortRangeThreshold;
        clone.Sentry_LegacyEnemyDetection = source.Sentry_LegacyEnemyDetection;
        clone.Sentry_FireTagOnly = source.Sentry_FireTagOnly;
        clone.Sentry_PrioTag = source.Sentry_PrioTag;
        clone.Sentry_StartFireDelayTagMulti = source.Sentry_StartFireDelayTagMulti;
        clone.Sentry_RotationSpeedTagMulti = source.Sentry_RotationSpeedTagMulti;
        clone.Sentry_DamageTagMulti = source.Sentry_DamageTagMulti;
        clone.Sentry_StaggerDamageTagMulti = source.Sentry_StaggerDamageTagMulti;
        clone.Sentry_CostOfBulletTagMulti = source.Sentry_CostOfBulletTagMulti;
        clone.Sentry_ShotDelayTagMulti = source.Sentry_ShotDelayTagMulti;
        return clone;
    }

    /// <summary>
    /// A copy of one recoil block. The three values the recoil path reads — `power`, `horizontalScale` and
    /// `verticalScale` — are `MinMaxValue` *objects*, not embedded numbers, so assigning the original's onto the
    /// clone would make both blocks share one range and every write here would reach back into the block the rest
    /// of the game is still using. They are cloned too, which is what makes an instance recoil change stay on the
    /// instance.
    /// </summary>
    private static RecoilDataBlock CloneRecoil(RecoilDataBlock source)
    {
        var clone = Construct<RecoilDataBlock>(Il2CppClassPointerStore<RecoilDataBlock>.NativeClassPtr);
        clone.power = CloneRange(source.power);
        clone.spring = source.spring;
        clone.dampening = source.dampening;
        clone.hipFireCrosshairSizeDefault = source.hipFireCrosshairSizeDefault;
        clone.hipFireCrosshairRecoilPop = source.hipFireCrosshairRecoilPop;
        clone.hipFireCrosshairSizeMax = source.hipFireCrosshairSizeMax;
        clone.horizontalScale = CloneRange(source.horizontalScale);
        clone.verticalScale = CloneRange(source.verticalScale);
        clone.directionalSimilarity = source.directionalSimilarity;
        clone.worldToViewSpaceBlendHorizontal = source.worldToViewSpaceBlendHorizontal;
        clone.worldToViewSpaceBlendVertical = source.worldToViewSpaceBlendVertical;
        clone.recoilPosImpulse = source.recoilPosImpulse;
        clone.recoilPosShift = source.recoilPosShift;
        clone.recoilPosShiftWeight = source.recoilPosShiftWeight;
        clone.recoilPosStiffness = source.recoilPosStiffness;
        clone.recoilPosDamping = source.recoilPosDamping;
        clone.recoilPosImpulseWeight = source.recoilPosImpulseWeight;
        clone.recoilCameraPosWeight = source.recoilCameraPosWeight;
        clone.recoilAimingWeight = source.recoilAimingWeight;
        clone.recoilRotImpulse = source.recoilRotImpulse;
        clone.recoilRotStiffness = source.recoilRotStiffness;
        clone.recoilRotDamping = source.recoilRotDamping;
        clone.recoilRotImpulseWeight = source.recoilRotImpulseWeight;
        clone.recoilCameraRotWeight = source.recoilCameraRotWeight;
        clone.concussionIntensity = source.concussionIntensity;
        clone.concussionFrequency = source.concussionFrequency;
        clone.concussionDuration = source.concussionDuration;
        return clone;
    }

    private static MinMaxRange? CloneRange(MinMaxRange? source)
    {
        if (source == null || source.Pointer == IntPtr.Zero) return null;
        var clone = Construct<MinMaxRange>(Il2CppClassPointerStore<MinMaxRange>.NativeClassPtr);
        clone.Min = source.Min;
        clone.Max = source.Max;
        return clone;
    }

    /// <summary>
    /// The one way this package makes an object of a native class whose interop type has no public parameterless
    /// constructor. `il2cpp_object_new` allocates through the game's own allocator and the invoke of that class's
    /// own constructor gives the object the defaults the game wrote into it; the constructor is what the shipped
    /// code itself calls, so the result is an ordinary instance of the class and not a hand-built layout.
    ///
    /// The method is `unsafe` because the interop's invoke signature takes a `void**`, which no safe signature
    /// can spell; the array is null because none of these constructors takes a parameter, and the exception slot
    /// is how a throw inside the native constructor is reported back. The class pointer is the caller's, so each
    /// of the three call sites names its own type instead of this method guessing from `typeof(T)`.
    /// </summary>
    private static unsafe T Construct<T>(IntPtr klass) where T : Il2CppObjectBase
    {
        var constructor = IL2CPP.GetIl2CppMethod(klass, false, ".ctor", "System.Void", Array.Empty<string>());
        if (constructor == IntPtr.Zero) throw new InvalidOperationException("A native GameData block has no parameterless constructor.");
        var pointer = IL2CPP.il2cpp_object_new(klass);
        if (pointer == IntPtr.Zero) throw new InvalidOperationException("A native GameData block could not be allocated.");
        var exception = IntPtr.Zero;
        IL2CPP.il2cpp_runtime_invoke(constructor, pointer, null, ref exception);
        if (exception != IntPtr.Zero) throw new InvalidOperationException("A native GameData block constructor threw.");
        return (T)Activator.CreateInstance(typeof(T), pointer)!;
    }

    /// <summary>Whether this archetype class carries the burst length field at all. `BWA_Burst` and
    /// `BWA_SemiBurst` declare `m_burstMax`; the other archetypes have no such field, and the request is refused
    /// for them rather than written to an offset that is a different member of a different class.</summary>
    private static WeaponActionRuntime.WeaponFamily Family(WeaponBase weapon, BulletWeaponArchetype archetype)
    {
        if (archetype.TryCast<global::Gear.BWA_Burst>() != null || archetype.TryCast<global::Gear.BWA_SemiBurst>() != null)
            return WeaponActionRuntime.WeaponFamily.Burst;
        return weapon.TryCast<global::Gear.Shotgun>() != null
            ? WeaponActionRuntime.WeaponFamily.Shotgun
            : WeaponActionRuntime.WeaponFamily.Bullet;
    }

    /// <summary>
    /// Whether the recoil block carries the objects the requested values are written through. `power`,
    /// `horizontalScale` and `verticalScale` are reference fields, so a block could in principle hold none of
    /// them; a request naming one of those values is refused rather than accepted with the value dropped.
    /// </summary>
    private static bool HasRecoilRange(RecoilDataBlock recoil, IReadOnlyList<WeaponOverrideField> fields)
    {
        foreach (var field in fields)
        {
            switch (field.Name)
            {
                case "recoil_horizontal":
                    if (recoil.horizontalScale == null || recoil.horizontalScale.Pointer == IntPtr.Zero) return false;
                    break;
                case "recoil_vertical":
                    if (recoil.verticalScale == null || recoil.verticalScale.Pointer == IntPtr.Zero) return false;
                    break;
                case "recoil_camera_kick":
                    if (recoil.power == null || recoil.power.Pointer == IntPtr.Zero) return false;
                    break;
            }
        }
        return true;
    }

    private static void SetBurstCount(BulletWeaponArchetype archetype, int count)
    {
        if (archetype.TryCast<global::Gear.BWA_Burst>() is { } burst) burst.m_burstMax = count;
        else if (archetype.TryCast<global::Gear.BWA_SemiBurst>() is { } semiBurst) semiBurst.m_burstMax = count;
    }

    /// <summary>
    /// A capacity decrease can leave the current clip above the new maximum: the capacity lives on the block and
    /// the count lives on the weapon, so the weapon is clamped to whichever block it now points at. A capacity
    /// increase needs no write — the archetype refills an empty clip from the block itself.
    /// </summary>
    private static void ClampClip(Applied applied)
    {
        var block = applied.Archetype.m_archetypeData;
        var capacity = block == null ? 0 : block.DefaultClipSize;
        if (capacity > 0 && applied.Weapon.m_clip > capacity) applied.Weapon.m_clip = capacity;
    }
}
