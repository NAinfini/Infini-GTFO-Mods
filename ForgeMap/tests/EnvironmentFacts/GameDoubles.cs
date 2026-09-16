using System;
using System.Collections.Generic;
using System.Globalization;

// The doubles every production source in this project is compiled against. Nothing here loads a GTFO assembly:
// each type is the shape the slice's own code names, and each static member is the one instance a case sets up.
// The types are deliberately in the game's own namespaces and carry the game's own member names, because the
// point of the project is to run the production sources unchanged.

namespace UnityEngine
{
    public struct Vector3
    {
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public float x, y, z;
        public bool IsDefault => x == 0f && y == 0f && z == 0f;
        public static Vector3 one => new(1f, 1f, 1f);
        public static bool operator ==(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public override bool Equals(object? other) => other is Vector3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
    }

    public struct Vector2
    {
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float x, y;
    }

    public struct Vector4
    {
        public Vector4(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
        public float x, y, z, w;
        public static Vector4 zero => new(0f, 0f, 0f, 0f);
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public static Quaternion identity => new() { w = 1f };
    }

    public struct Bounds
    {
        public Bounds(Vector3 center, Vector3 size) { this.center = center; this.size = size; }
        public Vector3 center, size;
        public Vector3 min => new(center.x - size.x / 2f, center.y - size.y / 2f, center.z - size.z / 2f);
    }

    public struct Color
    {
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
        public float r, g, b, a;
        public static Color white => new(1f, 1f, 1f, 1f);
        public bool SameAs(Color other) => r == other.r && g == other.g && b == other.b && a == other.a;
    }

    /// <summary>The one Unity object both HUD placements touch: object creation and destruction are recorded so a
    /// case can see that a readout or a teammate line was cloned and that it was destroyed with the world it
    /// belonged to.</summary>
    public class Object
    {
        internal static readonly List<GameObject> Created = new();
        internal static readonly List<Object> Destroyed = new();

        public static GameObject Instantiate(GameObject original, Transform parent)
        {
            var clone = new GameObject(original.name);
            clone.transform.parent = parent;
            var rect = new RectTransform { gameObject = clone, parent = parent };
            clone.Attach(rect);
            var text = new TMPro.TextMeshPro { gameObject = clone, transform = clone.transform, rectTransform = rect };
            clone.Attach(text);
            Created.Add(clone);
            return clone;
        }

        /// <summary>A clone of a component, which is how the teammate-overhead row builds its line. The game's own
        /// component already carries the game object, the transform and the rect it was built into, so the clone is
        /// one more object of the same shape and the recorded creation is what a case reads.</summary>
        public static T Instantiate<T>(T original, Transform parent) where T : Component, new()
        {
            var host = new GameObject(original.gameObject.name);
            var clone = new T { gameObject = host, transform = host.transform };
            var rect = new RectTransform { gameObject = host, parent = parent, pivot = original.RectPivot };
            host.Attach(clone);
            host.Attach(rect);
            if (clone is TMPro.TextMeshPro text) text.rectTransform = rect;
            Created.Add(host);
            return clone;
        }

        public static void Destroy(Object target) => Destroyed.Add(target);

        internal static void ForgetAll() { Created.Clear(); Destroyed.Clear(); }
    }

    public class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform = null!;
        public T? GetComponent<T>() where T : class => gameObject?.GetComponent<T>();
        /// <summary>The pivot a rect-carrying clone copies off the component it was cloned from.</summary>
        internal Vector2 RectPivot => gameObject?.GetComponent<RectTransform>()?.pivot ?? default;
    }

    public class Transform : Component
    {
        public Transform? parent;
        public Vector3 localPosition;
        public Vector3 localScale;
        public Quaternion localRotation;
    }

    public class RectTransform : Transform
    {
        public Vector2 anchorMin, anchorMax, anchoredPosition, pivot;
    }

    public class GameObject : Object
    {
        private readonly Dictionary<Type, Component> parts = new();

        public GameObject(string name = "")
        {
            this.name = name;
            transform = new Transform { gameObject = this };
            parts[typeof(Transform)] = transform;
        }

        public string name;
        public Transform transform;
        public bool activeSelf = true;
        public bool activeInHierarchy = true;

        public void SetActive(bool value) => activeSelf = value;

        public T? GetComponent<T>() where T : class
            => parts.TryGetValue(typeof(T), out var part) ? part as T : null;

        internal void Attach(Component part)
        {
            part.gameObject = this;
            if (part.transform == null || part is Transform) part.transform = part as Transform ?? transform;
            parts[part.GetType()] = part;
        }
    }
}

namespace TMPro
{
    public enum TextAlignmentOptions { Top = 0, Center = 1 }
    public enum TextOverflowModes { Overflow = 0, Truncate = 1 }

    public class TextMeshPro : UnityEngine.Component
    {
        public string text = "";
        public UnityEngine.Color color;
        public UnityEngine.Color faceColor;
        public float alpha = 1f;
        public UnityEngine.Vector4 margin;
        public bool enableAutoSizing = true;
        public bool richText = true;
        public bool overrideColorTags = true;
        public bool enableVertexGradient;
        public bool enableWordWrapping = true;
        public TextAlignmentOptions alignment;
        public TextOverflowModes overflowMode;
        public UnityEngine.RectTransform rectTransform = null!;
        public UnityEngine.Bounds textBounds = new(new UnityEngine.Vector3(0f, 0f, 0f), new UnityEngine.Vector3(1f, 1f, 0f));
        public void SetText(string value) => text = value;
    }
}

namespace GameData
{
    public enum eWardenObjectiveEventType
    {
        None = 0, AllLightsOff = 3, AllLightsOn = 4, PlaySound = 5, SetFogSetting = 6, LightsInZone = 13,
        LightsInZoneToggle = 14, AnimationTrigger = 15, SetNavMarker = 17, DialogueOnClosest = 28,
        StartRepeatingFog = 31, StopSustainedEvent = 32
    }

    public enum eLocalZoneIndex { Zone_0 = 0, Zone_1 = 1, Zone_2 = 2, Zone_19 = 19 }

    public sealed class WorldEventConditionPair
    {
        public int ConditionIndex { get; set; }
        public bool IsTrue { get; set; }
    }

    /// <summary>The game's own level event payload, with every member the slice writes. A case reads the one the
    /// handler handed to the event manager, so "which entry, with which arguments" is the whole assertion.</summary>
    public sealed class WardenObjectiveEventData
    {
        public eWardenObjectiveEventType Type { get; set; }
        public WorldEventConditionPair Condition { get; set; } = new();
        public eDimensionIndex DimensionIndex { get; set; }
        public LevelGeneration.LG_LayerType Layer { get; set; }
        public eLocalZoneIndex LocalIndex { get; set; }
        public float Delay { get; set; }
        public float Duration { get; set; }
        public Localization.LocalizedText? WardenIntel { get; set; }
        public uint SoundID { get; set; }
        public Localization.LocalizedText? SoundSubtitle { get; set; }
        public uint DialogueID { get; set; }
        public uint FogSetting { get; set; }
        public float FogTransitionDuration { get; set; }
        public UnityEngine.Vector3 Position { get; set; }
        public int Count { get; set; }
        public bool Enabled { get; set; }
        public int SustainedEventSlotIndex { get; set; }
        public int SustainedEventStateCount { get; set; }
        public float SustainedEventStateDuration { get; set; }
        public float SustainedEventDelay { get; set; }
        public string WorldEventObjectFilter { get; set; } = "";
    }
}

public enum eDimensionIndex { Reality = 0, Dimension_1 = 1, Dimension_2 = 2, Dimension_19 = 19, MAX_COUNT = 21 }

public struct GlobalZoneIndex
{
    public GlobalZoneIndex(int dimension, int layer, int zone)
    { Dimension = (eDimensionIndex)dimension; Layer = (LevelGeneration.LG_LayerType)layer; Zone = (GameData.eLocalZoneIndex)zone; }
    public eDimensionIndex Dimension;
    public LevelGeneration.LG_LayerType Layer;
    public GameData.eLocalZoneIndex Zone;
}

namespace LevelGeneration
{
    public enum LG_LayerType : byte { MainLayer = 0, SecondaryLayer = 1, ThirdLayer = 2 }

    /// <summary>One native light object of a zone. The slice reads nothing off a light itself: the count of the
    /// zone's own list is the fact `v-zone-lights` answers, and no runtime entry switches one light at a time.</summary>
    public sealed class LG_Light
    {
    }

    /// <summary>The zone's own layer. The address's layer coordinate is this object's own m_type.</summary>
    public sealed class LG_Layer
    {
        public LG_LayerType m_type { get; set; }
    }

    /// <summary>One level zone: the dimension it belongs to, the layer it was built on, its index in that layer,
    /// and the light objects the level placed in it.</summary>
    public sealed class LG_Zone
    {
        public LG_Layer? m_layer { get; set; } = new();
        public eDimensionIndex m_dimensionIndex { get; set; }
        public GameData.eLocalZoneIndex LocalIndex { get; set; }
        public List<LG_Light>? m_lightsInZone { get; set; } = new();
    }

    /// <summary>The floor of the level: its own zone list is the one table a zone address is resolved through.</summary>
    public sealed class LG_Floor
    {
        public List<LG_Zone>? allZones { get; set; } = new();
    }

    /// <summary>The level singleton the floor is reached through. Its own `Current` is a settable static, so a
    /// level that was torn down leaves it null, exactly as the game's own scene teardown does.</summary>
    public sealed class LG_LevelBuilder
    {
        public static LG_LevelBuilder? Current { get; set; }
        public LG_Floor? m_currentFloor { get; set; } = new();
    }
}

namespace Localization
{
    /// <summary>The game's own text value: either a localisation id or the untranslated string itself. The slice
    /// uses the string constructor, which is why a computed line can carry a door's name or a live count.</summary>
    public sealed class LocalizedText
    {
        public LocalizedText(string untranslatedText) { UntranslatedText = untranslatedText; }
        public LocalizedText(uint id) { Id = id; }
        public string UntranslatedText { get; set; } = "";
        public uint Id { get; set; }
        public override string ToString() => UntranslatedText;
    }
}

/// <summary>The level event manager: the one entry every hosted and presented row in this slice goes through, and
/// the availability gate each of them reads first.</summary>
public class WorldEventManager
{
    internal static readonly List<(GameData.WardenObjectiveEventData Data, float Duration)> Executed = new();
    internal static Exception? Throw;

    public static WorldEventManager? Current { get; set; } = new();

    public static void ExecuteEvent(GameData.WardenObjectiveEventData eData, float currentDuration)
    {
        if (Throw != null) throw Throw;
        Executed.Add((eData, currentDuration));
    }

    internal static void Reset() { Executed.Clear(); Throw = null; Current = new WorldEventManager(); }
}

/// <summary>The environment manager: the two read-only facts `v-env` answers with.</summary>
public class EnvironmentStateManager
{
    public static EnvironmentStateManager? Current { get; set; } = new();
    public static uint FogId;
    public static bool LightOn;
    internal static readonly List<(int Dimension, int Layer, int Zone)> LightReads = new();
    internal static readonly List<int> FogReads = new();

    public static uint GetCurrentFogID(eDimensionIndex dimensionIndex)
    { FogReads.Add((int)dimensionIndex); return FogId; }

    public static bool GetLightMode(GlobalZoneIndex zone)
    {
        LightReads.Add(((int)zone.Dimension, (int)zone.Layer, (int)zone.Zone));
        return LightOn;
    }

    internal static void Reset()
    {
        Current = new EnvironmentStateManager();
        FogId = 0; LightOn = false; LightReads.Clear(); FogReads.Clear();
    }
}

namespace SNetwork
{
    public sealed class SNet_Player
    {
        public ulong Lookup { get; set; }
        public bool IsBot { get; set; }
        public int Slot { get; set; }
        /// <summary>The native pointer every identity comparison in the slice is made by. The double hands out one
        /// stable pointer per instance, which is what the game's own Il2Cpp object does.</summary>
        public IntPtr Pointer { get; } = (IntPtr)System.Threading.Interlocked.Increment(ref NextPointer);
        private static int NextPointer;
        public int PlayerSlotIndex() => Slot;
        internal static void ResetPointers() => NextPointer = 0;
    }

    public sealed class SNet_SessionHub
    {
        public List<SNet_Player> PlayersInSession { get; } = new();
    }

    public static class SNet
    {
        public static SNet_SessionHub? SessionHub { get; set; } = new();
        /// <summary>The one player sitting at this machine, which is what `audience=self` compares against. A
        /// case sets it through <see cref="SetLocal"/>; null is a machine nobody is sitting at.</summary>
        public static SNet_Player? LocalPlayer { get; set; }
        public static bool HasLocalPlayer => LocalPlayer != null;

        internal static void SetLocal(SNet_Player? player) => LocalPlayer = player;

        internal static void Reset()
        {
            SessionHub = new SNet_SessionHub();
            LocalPlayer = null;
            SNet_Player.ResetPointers();
        }
    }
}

namespace Player
{
    /// <summary>The voice manager: the one entry a player line goes through, named by player slot index.</summary>
    public static class PlayerVoiceManager
    {
        internal static readonly List<(int Slot, uint EventId)> Said = new();
        internal static Exception? Throw;

        public static void WantToSay(int playerID, uint eventID)
        {
            if (Throw != null) throw Throw;
            Said.Add((playerID, eventID));
        }

        internal static void Reset() { Said.Clear(); Throw = null; }
    }

    /// <summary>The player's overhead marker, as the teammate-overhead row reads and writes it. Only the members
    /// that row names are here: the marker's own pointer, the player it belongs to, the native extra-information
    /// flag it shares with the game's hint row, and the name mesh the extra line is cloned from.</summary>
    public sealed class PlaceNavMarkerOnGO : UnityEngine.Component
    {
        public PlaceNavMarkerOnGO(IntPtr pointer) => Pointer = pointer;
        public IntPtr Pointer { get; }
        public PlayerAgent? Player { get; set; }
        public NavMarker? m_marker { get; set; }
        public string m_nameToShow = "";
        public string m_extraInfo = "";
        public bool m_extraInfoVisible;
    }

    /// <summary>The marker's own text meshes, of which the player-name one is the row this slice clones.</summary>
    public sealed class NavMarker
    {
        public TMPro.TextMeshPro? m_playerName;
        public TMPro.TextMeshPro? m_distance;
    }

    /// <summary>One player agent: the owner the identity module resolves a life to, the marker the game put on
    /// that player's head, and whether that player is the one at this keyboard.</summary>
    public class PlayerAgent
    {
        public SNetwork.SNet_Player? Owner { get; set; }
        public PlaceNavMarkerOnGO? NavMarker { get; set; }
        public bool IsLocallyOwned { get; set; }
    }
}

/// <summary>The local player's HUD layer and the two readouts `a-hud` writes.</summary>
public class PUI_LocalPlayerStatus : UnityEngine.Component
{
    public UnityEngine.GameObject? m_shieldUIParent = new("ShieldGroup");
    public TMPro.TextMeshPro? m_shieldText = Attached("ShieldText");
    public TMPro.TextMeshPro? m_healthText = Attached("HealthText");
    public float? Shield;
    public int ShieldWrites;

    public void UpdateShield(float shield) { Shield = shield; ShieldWrites++; }

    internal void Reset()
    {
        m_shieldUIParent = new UnityEngine.GameObject("ShieldGroup");
        m_shieldText = Attached("ShieldText");
        m_healthText = Attached("HealthText");
        Shield = null; ShieldWrites = 0;
    }

    /// <summary>One text object wired the way the game's own prefab wires it: the component knows its own game
    /// object, that object's transform, its rect and the row it sits in.</summary>
    private static TMPro.TextMeshPro Attached(string name)
    {
        var host = new UnityEngine.GameObject(name);
        var rect = new UnityEngine.RectTransform { gameObject = host };
        host.Attach(rect);
        var text = new TMPro.TextMeshPro { gameObject = host, transform = host.transform, rectTransform = rect };
        text.transform.parent = new UnityEngine.Transform();
        return text;
    }
}

public class PlayerGuiLayer : UnityEngine.Component
{
    public PUI_LocalPlayerStatus? m_playerStatus = new();
    internal void Reset() => m_playerStatus = new PUI_LocalPlayerStatus();
}

public class GuiManager : UnityEngine.Component
{
    public static GuiManager? Current { get; set; } = new();
    public PlayerGuiLayer? m_playerLayer = new();
    internal static void Reset() { Current = new GuiManager(); }
}

namespace ForgeMap.Native
{
    /// <summary>The Map provider's player identity namespace, as this slice reaches it. Production reads the
    /// entity kind off the identity and asks it for the agent behind a recorded life; a case registers the lives it
    /// wants through <see cref="Register"/> instead of running the module, because the module needs a
    /// registration, a kernel and native state a unit case cannot build.</summary>
    internal sealed class PlayerIdentityModule
    {
        internal const string EntityKind = "gtfo.player";

        internal sealed record Life(ForgeRuntime.Framework.EntityReference Reference, Player.PlayerAgent Agent);

        /// <summary>The one registered identity half, which is the one-provider-per-process shape the production
        /// module has.</summary>
        internal static PlayerIdentityModule? Current { get; private set; }

        private readonly Dictionary<string, Life> _lives = new(StringComparer.Ordinal);
        private readonly Dictionary<IntPtr, string> _byAgent = new();

        /// <summary>Records one player life: the entity a plan names, the agent it resolves to and the native
        /// pointer the overhead row is keyed by.</summary>
        internal static Life Register(string id, long worldEpoch, long lifeEpoch, Player.PlayerAgent agent)
        {
            Current ??= new PlayerIdentityModule();
            var entry = new Life(new ForgeRuntime.Framework.EntityReference(id, worldEpoch, lifeEpoch), agent);
            Current._lives[id] = entry;
            Current._byAgent[agent.Owner!.Pointer] = id;
            return entry;
        }

        internal static void Reset() => Current = null;

        /// <summary>The life one entity id names, or null when this case registered none.</summary>
        internal Life? Known(string id) => _lives.TryGetValue(id, out var life) ? life : null;

        internal Player.PlayerAgent? CurrentAgent(ForgeRuntime.Framework.EntityReference reference)
            => reference.Id != null && _lives.TryGetValue(reference.Id, out var life) ? life.Agent : null;

        /// <summary>The one entity the agent of a native pointer belongs to, which is what a test asks for after
        /// the fact rather than keeping the reference it registered with.</summary>
        internal ForgeRuntime.Framework.EntityReference? ReferenceOf(IntPtr agentPointer)
            => _byAgent.TryGetValue(agentPointer, out var id) ? _lives[id].Reference : null;
    }
}

namespace ForgeMap
{
    /// <summary>The Map provider's identity, which every row this slice declares names as its owner.</summary>
    public static class ModuleDefinition
    {
        public const string ProviderId = "forge.module.gtfo.map";
    }
}
