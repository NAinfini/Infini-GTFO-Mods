// Managed test doubles: tests run the production patch methods, not Unity or
// Harmony's IL2CPP detours. They cannot certify in-game rendering/networking.
using System.Reflection;

namespace UnityEngine
{
    public readonly record struct Color(float r, float g, float b, float a);
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
        public static float Angle(Vector3 a, Vector3 b) => 0;
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
        public HarmonyPatch(Type t, string name, Type[] args, ArgumentType[] variations) { }
    }
    public enum ArgumentType { Normal, Ref, Out }
    public class HarmonyFinalizer : Attribute { }
    public class HarmonyPrefix : Attribute { }
    public class HarmonyPostfix : Attribute { }
    public class HarmonyPriority : Attribute { public HarmonyPriority(int priority) { } }
    public static class Priority { public const int Last = 0; }
    public static class AccessTools
    {
        public static MethodInfo Method(Type t, string name) => t.GetMethod(name)!;
        public static MethodInfo DeclaredMethod(Type t, string name, Type[] args) => t.GetMethod(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly, args)!;
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
    public class NetworkOwner { public ulong Lookup; public bool IsLocal, IsBot; }
    public class PlayerAgent : Agents.Agent
    {
        public bool IsLocallyOwned;
        public NetworkOwner? Owner = new();
        public FPSCamera? FPSCamera;
        public PlayerStamina? Stamina;
        public bool Alive = true;
        public PlayerInventoryBase? Inventory = new();
        public PlaceNavMarkerOnGO? NavMarker;
        public HudDamage? Damage = new();
        public UnityEngine.Vector3 EyePosition;
    }
    public static class PlayerManager
    {
        public static PlayerAgent? Local;
        public static readonly List<PlayerAgent> PlayerAgentsInLevel = new();
        public static PlayerAgent? GetLocalPlayerAgent() => Local;
    }
}
public enum InventorySlot { Standard, Special, Class, ResourcePack, Consumable, Melee, GearStandard = Standard, GearSpecial = Special, GearClass = Class }
public enum eResourceContainerSpawnType { Health, Disinfection, AmmoWeapon, AmmoTool }
public class HudItemData { public bool GUIShowAmmoInfinite; public bool GUIShowAmmoTotalRel = true; }
public class HudItem { public string ArchetypeName = "Item"; public HudItemData? ItemDataBlock = new(); public T? TryCast<T>() where T : class => this as T; }
public class ItemEquippable : HudItem { }
public class HudDamage { public float Health = 1, Infection; public float GetHealthRel() => Health; }
public class HudAmmo { public float RelInPack; public int BulletsInPack; }
public class HudAmmoStorage { public HudAmmo StandardAmmo = new(), SpecialAmmo = new(), ClassAmmo = new(), ResourcePackAmmo = new(), ConsumableAmmo = new(); }
public class HudBackpackItem { public HudItem? Instance; }
public class HudBackpack
{
    public HudAmmoStorage AmmoStorage = new();
    public Dictionary<InventorySlot, HudBackpackItem> Items = new();
    public bool TryGetBackpackItem(InventorySlot slot, out HudBackpackItem? item) => Items.TryGetValue(slot, out item);
}
public enum eFocusState { FPS, Menu }
public enum InputAction { Aim }
public static class FocusStateManager { public static eFocusState CurrentState = eFocusState.FPS; }
public static class InputMapper { public static Func<InputAction, eFocusState, bool> GetButton = (_, _) => false; }
public class NavMarker
{
    public int SetterCalls, VisualChanges;
    public bool ComponentsActive = true, OnScreen = true;
    public void Project(bool value) { OnScreen = value; ComponentsActive = IsVisible && value; }
    public string Title = "";
    public float IconScale;
    public UnityEngine.Color Color;
    public void SetTitle(string value) { SetterCalls++; Title = value; }
    public void SetIconScale(float value) { SetterCalls++; IconScale = value; }
    public void SetColor(UnityEngine.Color value) { SetterCalls++; Color = value; }
    private static int _id;
    public IntPtr Pointer = (IntPtr)(++_id);
    public bool IsVisible = true;
    public void SetVisible(bool value) { SetterCalls++; InfiniTweaks.MarkerOwnership.Visibility(this, ref value); IsVisible = value; ComponentsActive = value && OnScreen; }
    public void OnDestroy() { }
    public float Alpha = 1;
    public eNavMarkerStyle Style;
    public NavMarkerOption Visible, Focus, Edge, Inactive;
    public void SetAlpha(float value) { SetterCalls++; if (ComponentsActive) Alpha = value; }
    public void SetStyle(eNavMarkerStyle style) { SetterCalls++; Style = style; Visible = NavMarkerOption.Loot; Color = default; Alpha = 1; IconScale = 1; Title = ""; }
    public void SetVisualStates(NavMarkerOption visible, NavMarkerOption focus, NavMarkerOption edge, NavMarkerOption inactive)
    { SetterCalls++; VisualChanges++; ComponentsActive = false; Visible = visible; Focus = focus; Edge = edge; Inactive = inactive; }
}
public class SentryGunInstance
{
    public Player.PlayerAgent? Owner;
    public float Ammo, AmmoMaxCap;
    public void OnSpawn() { }
    public void OnDespawn() { }
}
public class PlaceNavMarkerOnGO
{
    public void PlaceMarker() { }
    private static int _next;
    public IntPtr Pointer = (IntPtr)(++_next);
    public NavMarker? m_marker = new();
    public Player.PlayerAgent? Player;
    public HudBackpack? m_playerBackpack = new();
    public string m_extraInfo = "", m_nameToShow = "Teammate", DisplayName = "", DisplayExtra = "";
    public bool m_extraInfoVisible, m_isInfoDirty;
    public int UpdateCalls;
    public void UpdateName(string name, string extra) { DisplayName = name; DisplayExtra = extra; }
    public void OnDestroy() { }
    public void SetExtraInfoVisible(bool value)
    {
        object[] args = { this, value };
        typeof(InfiniTweaks.ResourceHud).GetMethod("ShowContext", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, args);
        m_extraInfoVisible = (bool)args[1];
    }
    public void UpdateExtraInfo()
    {
        UpdateCalls++;
        // Model native text submission BEFORE invoking the actual production postfix.
        m_extraInfo = m_extraInfoVisible ? "native old extra" : "";
        UpdateName(m_nameToShow, m_extraInfo);
        typeof(InfiniTweaks.ResourceHud).GetMethod("Render", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, new object[] { this });
    }
}
public class CameraController { public UnityEngine.Camera m_camera = new(); }
public class FPSCamera : CameraController { public void AddHitReact() { } }
public class PlayerInventoryBase
{
    public InventorySlot WieldedSlot;
    public HudItem? WieldedItem;
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
    public class ResourcePackFirstPerson : HudItem { public eResourceContainerSpawnType m_packType; }
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
    // The production collector is exercised above; Unity clone/layout behavior is
    // tested separately and is not simulated by this presentation sink.
    internal static class ResourceHudView
    {
        internal static float ResourceAlpha = 1;
        internal static bool Living;
        internal static void Show(PlaceNavMarkerOnGO owner, string text) => owner.DisplayExtra = text;
        internal static void Alpha(PlaceNavMarkerOnGO owner, float alpha, bool living) { ResourceAlpha = alpha; Living = living; }
        internal static void Remove(IntPtr owner) { }
        internal static void Clear() { ResourceAlpha = 1; Living = false; }
    }
    internal enum WeaponPreset { Disabled, OriginalR6, R8Current }
    internal class Entry<T> { public T Value; public Entry(T value) => Value = value; }
    internal static class Settings
    {
        public static Entry<float> RecoilMultiplier = new(1), AimPunchMultiplier = new(1), StaminaCostMultiplier = new(1);
        public static Entry<float> HudEmphasisScale = new(1.2f);
        public static Entry<bool> ResourceHud = new(true), HudBold = new(true), HudDynamicOpacity = new(true);
        public static Entry<float> HudAimOpacity = new(0.15f);
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

namespace UnityEngine.Profiling { public static class Profiler { private static bool _enabled; public static int Reads, Begins, Ends; public static bool enabled { get { Reads++; return _enabled; } set => _enabled = value; } public static void BeginSample(string name) => Begins++; public static void EndSample() => Ends++; } }
