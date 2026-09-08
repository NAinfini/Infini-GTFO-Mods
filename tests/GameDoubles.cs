// Managed test doubles: tests run the production patch methods, not Unity or
// Harmony's IL2CPP detours. They cannot certify in-game rendering/networking.
using System.Reflection;

namespace UnityEngine
{
    public struct Vector2
    {
        public float x, y;
        public Vector2(float x, float y) { this.x = x; this.y = y; }
    }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float sqrMagnitude => x*x + y*y + z*z;
        public static Vector3 operator *(Vector3 a, float b) => new(a.x*b, a.y*b, a.z*b);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x+b.x, a.y+b.y, a.z+b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x-b.x, a.y-b.y, a.z-b.z);
    }
    public struct Quaternion { public static Quaternion LookRotation(Vector3 direction) => default; }
    public class Transform { public Vector3 position, forward; public Quaternion rotation; }
    public class Camera { public Transform transform = new(); }
    public class Light { public Transform transform = new(); }
    public static class Mathf
    {
        public static float Clamp(float v, float min, float max) => Math.Clamp(v, min, max);
        public static bool Approximately(float a, float b) => Math.Abs(a-b) < 0.000001f;
    }
}
namespace HarmonyLib
{
    [AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple=true)]
    public class HarmonyPatch : Attribute
    {
        public HarmonyPatch() { }
        public HarmonyPatch(Type t, string name) { }
    }
    public class HarmonyPrefix : Attribute { }
    public class HarmonyPostfix : Attribute { }
    public static class AccessTools
    {
        public static MethodInfo Method(Type t, string name) => t.GetMethod(name)!;
    }
}
namespace Agents
{
    public class Agent
    {
        private static int _id;
        public IntPtr Pointer = (IntPtr)(++_id);
        public T? TryCast<T>() where T : class => this as T;
    }
}
namespace Player
{
    public class NetworkOwner { public bool IsBot; }
    public class PlayerAgent : Agents.Agent
    {
        public bool IsLocallyOwned;
        public NetworkOwner? Owner = new();
        public FPSCamera? FPSCamera;
        public PlayerStamina? Stamina;
    }
}
public class CameraController { public UnityEngine.Camera m_camera = new(); }
public class FPSCamera : CameraController { public void AddHitReact() { } }
public class PlayerInventoryBase
{
    public Player.PlayerAgent? Owner;
    public CullingSystem.C_Light? m_flashlightCLight;
    public void UpdateFPSFlashlightAlignment() { }
}
namespace CullingSystem { public class C_Light { public UnityEngine.Light? m_unityLight; } }
public class Dam_PlayerDamageLimb
{
    public Agents.Agent? Target;
    public Agents.Agent? GetBaseAgent() => Target;
    public void BulletDamage() { }
}
public class PlayerStamina
{
    public Player.PlayerAgent? m_owner;
    public float Stamina = 1;
    public bool AllowRegen;
    public int ResetCalls;
    public struct ActionCost
    {
        public float baseStaminaCostInCombat, baseStaminaCostOutOfCombat;
        public bool resetRestingTimerInCombat, resetRestingTimerOutOfCombat;
    }
    public void ResetStamina() { Stamina = 1; ResetCalls++; }
    public void UseStamina(ActionCost cost, float dt) => Stamina = Math.Clamp(Stamina - cost.baseStaminaCostInCombat*dt, 0, 1);
    public void LateUpdate() { }
    public void UseJumpStamina() { }
    public void UseWeaponLightSwingStamina() { }
    public void UseWeaponChargedSwingStamina() { }
}
namespace Gear
{
    public class MeleeWeaponFirstPerson { public Player.PlayerAgent? Owner; }
    public class MWS_ChargeUp { public MeleeWeaponFirstPerson? m_weapon; public void Enter() { } }
}
namespace GTFO.API { public static class GameDataAPI { public static event Action? OnGameDataInitialized; public static void Raise() => OnGameDataInitialized?.Invoke(); } }
public enum eWeaponFireMode { Semi }
public enum AgentModifier { HELDamage, SniperDamage }
namespace GameData
{
    public class GameDataBlockBase<T> where T : GameDataBlockBase<T>
    {
        public uint persistentID;
        public string name = "";
        public bool internalEnabled = true;
        public static readonly List<T> Blocks = new();
        public static List<T> GetAllBlocks() => Blocks;
        public static T? GetBlock(uint id) => Blocks.Find(x => x.persistentID == id);
        public static T AddBlock(T block) { Blocks.Add(block); return block; }
    }
    public class FlashlightSettingsDataBlock : GameDataBlockBase<FlashlightSettingsDataBlock> { public float range, angle; }
    public class MinMaxValue { public float Min, Max; }
    public class ArchetypeDataBlock : GameDataBlockBase<ArchetypeDataBlock>
    {
        public eWeaponFireMode FireMode;
        public AgentModifier DamageBoosterEffect;
        public uint RecoilDataID;
        public float Damage, StaggerDamageMulti, PrecisionDamageMulti, DefaultReloadTime, CostOfBullet, ShotDelay;
        public UnityEngine.Vector2 DamageFalloff;
        public bool PiercingBullets;
        public int DefaultClipSize, PiercingDamageCountLimit;
        public float HipFireSpread, AimSpread, EquipTransitionTime, AimTransitionTime, BurstDelay;
        public int BurstShotCount, ShotgunBulletCount, ShotgunConeSize, ShotgunBulletSpread;
        public float SpecialChargetupTime, SpecialCooldownTime, SpecialSemiBurstCountTimeout;
    }
    public class RecoilDataBlock : GameDataBlockBase<RecoilDataBlock>
    {
        public MinMaxValue power = new(), horizontalScale = new(), verticalScale = new();
        public float spring, dampening, hipFireCrosshairSizeDefault, hipFireCrosshairRecoilPop, hipFireCrosshairSizeMax;
        public float directionalSimilarity, worldToViewSpaceBlendHorizontal, worldToViewSpaceBlendVertical;
        public UnityEngine.Vector3 recoilPosImpulse, recoilPosShift, recoilRotImpulse;
        public float recoilPosShiftWeight, recoilPosStiffness, recoilPosDamping, recoilPosImpulseWeight;
        public float recoilCameraPosWeight, recoilAimingWeight, recoilRotStiffness, recoilRotDamping;
        public float recoilRotImpulseWeight, recoilCameraRotWeight, concussionIntensity, concussionFrequency, concussionDuration;
    }
}
namespace InfiniTweaks
{
    internal enum WeaponPreset { Disabled, OriginalR6, R8Current }
    internal class Entry<T> { public T Value; public Entry(T value) => Value = value; }
    internal static class Settings
    {
        public static Entry<float> RecoilMultiplier = new(1), AimPunchMultiplier = new(1), StaminaCostMultiplier = new(1);
        public static Entry<float> FlashlightRangeAdjustment = new(10), FlashlightAngleAdjustment = new(35);
        public static Entry<bool> TeammatesIgnoreBullets = new(false), EnableChargeRecovery = new(false);
        public static Entry<bool> RemoveMeleeCost = new(false), RemoveJumpCost = new(false), NoFlashlightSway = new(true);
        public static Entry<WeaponPreset> HelGunVersion = new(WeaponPreset.Disabled), SniperVersion = new(WeaponPreset.Disabled);
    }
    internal class Log
    {
        public void LogInfo(string text) { }
        public void LogWarning(string text) => throw new Exception(text);
    }
    internal static class Plugin { public static readonly Log PluginLog = new(); }
}
