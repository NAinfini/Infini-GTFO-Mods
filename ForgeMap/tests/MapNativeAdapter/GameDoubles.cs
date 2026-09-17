using System.Runtime.CompilerServices;
using AIGraph;

// Doubles mirror only the interop members the production Map native sources read. Names, namespaces and
// member kinds follow build 20403457 (dump.cs / interop); behaviour is synthetic and NOT game-verified.
//
// This is the one double set the whole native half compiles against. The adapter compiles every source
// `ForgeMap/Native/ForgeMap.Native.csproj` compiles, so a member a native reader names that the real build does
// not have fails here instead of shipping: the type list was assembled from the focused projects that own each
// half (`tests/PlayerActions`, `tests/ObjectiveActions`, `tests/EnvironmentFacts`, `tests/AlarmWaveActions`,
// `tests/PlayerLifeFacts`, `tests/DoorTerminalFacts`, `tests/AgentModifier`), and a member two of them spelled
// differently is declared once, in the shape the production source actually reads.

public static class Pointers
{
    private static long _next = 0x10000;
    public static IntPtr Next() => new(Interlocked.Increment(ref _next));
}

/// <summary>Unity equality: a destroyed object compares equal to null, and the interop base every game wrapper
/// rests on reports whether the native instance was collected.</summary>
public abstract class UnityObjectDouble
{
    public IntPtr Pointer { get; set; } = Pointers.Next();
    public bool Destroyed;
    public bool WasCollected => Destroyed;
    /// <summary>Every interop wrapper carries the cast Unity's own interface slot answers with: the native object
    /// behind the slot, or null when the slot holds something else.</summary>
    public T? TryCast<T>() where T : class => this as T;
    /// <summary>The per-object native identity the level's own addresses are keyed by.</summary>
    public int GetInstanceID() => (int)(Pointer.ToInt64() & 0x7FFFFFFF);
    internal static readonly List<UnityObjectDouble> DestroyedObjects = new();
    private static bool Alive(UnityObjectDouble? value) => value is not null && !value.Destroyed;
    public static bool operator ==(UnityObjectDouble? left, UnityObjectDouble? right)
    {
        bool l = Alive(left), r = Alive(right);
        return !l || !r ? l == r : ReferenceEquals(left, right);
    }
    public static bool operator !=(UnityObjectDouble? left, UnityObjectDouble? right) => !(left == right);
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);
    public override int GetHashCode() => RuntimeHelpers.GetHashCode(this);
}

/// <summary>The interaction base every interactable derives from (dump.cs:563390, the global namespace the game
/// declares it in). Only the member the interaction-prompt patch reads is mirrored: the prompt text the game
/// draws, which is what a rule rewrites.</summary>
public class Interact_Base : UnityEngine.Component
{
    public virtual string InteractionMessage { get; set; } = "";
}

/// <summary>The timed override a door button answers with (dump.cs:564287, `Interact_Base`'s own subclass). The
/// revive hook's two members are mirrored as well: the interacting agent and the interaction's own target.</summary>
public class Interact_Timed : Interact_Base
{
    public Player.PlayerAgent? Agent;
    public Player.PlayerAgent? m_interactTargetAgent { get; set; }
}

namespace ForgeRuntime
{
    public enum RuntimeMode { Off, Play, Authoring }
    /// <summary>The host plugin the production code reads its own mode and kernel from.</summary>
    public static class Plugin
    {
        public static RuntimeMode ConfiguredMode = RuntimeMode.Play;
        public static ForgeRuntime.Framework.RuntimeKernel? Runtime;
        // A suspended host still publishes its kernel; the reason code is what a package reports.
        public static string? Suspension;
        public static bool IsSuspended => Suspension != null;
        public static string? SuspensionCode => Suspension;
    }
}

namespace UnityEngine
{
    /// <summary>The collision object a bullet is resolved against. A door's own components — its blades, its
    /// frame, its button — are what a shot really hits; the door itself is above them.</summary>
    public sealed class Collider : Component { }

    /// <summary>The invisible wall a trigger zone's blocking half builds. Only the three members that half writes
    /// are mirrored.</summary>
    public sealed class BoxCollider : Component
    {
        public bool isTrigger;
        public Vector3 center;
        public Vector3 size;
    }

    public struct Vector2
    {
        public Vector2(float x, float y) { this.x = x; this.y = y; }
        public float x, y;
    }

    /// <summary>Unity's Vector3: the production readers read x/y/z as fields, exactly like the interop struct.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0f, 0f, 0f);
        public static Vector3 one => new(1f, 1f, 1f);
        public static bool operator ==(Vector3 a, Vector3 b) => a.x == b.x && a.y == b.y && a.z == b.z;
        public static bool operator !=(Vector3 a, Vector3 b) => !(a == b);
        public static Vector3 operator +(Vector3 a, Vector3 b) => new(a.x + b.x, a.y + b.y, a.z + b.z);
        public static Vector3 operator -(Vector3 a, Vector3 b) => new(a.x - b.x, a.y - b.y, a.z - b.z);
        public static Vector3 operator *(Vector3 a, float scale) => new(a.x * scale, a.y * scale, a.z * scale);
        public float sqrMagnitude => x * x + y * y + z * z;
        public float magnitude => MathF.Sqrt(sqrMagnitude);
        public Vector3 normalized
        {
            get
            {
                var length = magnitude;
                return length <= 0f ? zero : new Vector3(x / length, y / length, z / length);
            }
        }
        public static float Distance(Vector3 a, Vector3 b) => (a - b).magnitude;
        public override bool Equals(object? other) => other is Vector3 v && this == v;
        public override int GetHashCode() => HashCode.Combine(x, y, z);
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
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
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
        public static Color Lerp(Color from, Color to, float progress) => new(
            from.r + (to.r - from.r) * progress, from.g + (to.g - from.g) * progress,
            from.b + (to.b - from.b) * progress, from.a + (to.a - from.a) * progress);
    }

    /// <summary>The frame's own delta: the one reading the session hands its clock, which the light fade steps by.</summary>
    public static class Time
    {
        public static float deltaTime { get; set; } = 1f / 60f;
        /// <summary>The frame's own absolute time, which the interaction-prompt readback stamps a rule with.</summary>
        public static float time { get; set; }
    }

    /// <summary>The one Unity object the HUD placements touch: object creation and destruction are recorded so a
    /// case can see that a readout or a teammate line was cloned and that it was destroyed with its world.</summary>
    public class Object : UnityObjectDouble
    {
        internal static readonly List<GameObject> Created = new();
        internal static int NextInstanceId = 1000;

        public string name = "";
        public int InstanceId { get; } = ++NextInstanceId;

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

        public static void Destroy(Object target) => UnityObjectDouble.DestroyedObjects.Add(target);

        internal static void ForgetAll() { Created.Clear(); UnityObjectDouble.DestroyedObjects.Clear(); }
    }

    /// <summary>The base every game behaviour, collider and generator is. The hierarchy lookup is Unity's own — a
    /// component finds the first matching component on itself or above it — because that is the climb a bullet's
    /// collider makes to reach the door it hit, and the climb a generator makes to reach its group.</summary>
    public abstract class Component : Object
    {
        public GameObject gameObject = null!;
        public Transform transform = null!;
        public Component? Parent;
        public T? GetComponent<T>() where T : class => gameObject?.GetComponent<T>();
        public T? GetComponentInParent<T>() where T : class => this as T ?? Parent?.GetComponentInParent<T>();
        /// <summary>Unity's own instance identity on a component is its object's.</summary>
        public new int GetInstanceID() => gameObject?.GetInstanceID() ?? base.GetInstanceID();
    }

    /// <summary>Unity's Transform: the production readers take x/y/z off its position and xyzw off its rotation,
    /// exactly like the interop properties whose values are the interop structs.</summary>
    public class Transform : Component
    {
        public Transform? parent;
        public Vector3 position;
        public Quaternion rotation;
        public Vector3 localPosition;
        public Vector3 localScale = Vector3.one;
        public Quaternion localRotation = Quaternion.identity;
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

        public Transform transform;
        public int layer;
        public bool activeSelf = true;
        public bool activeInHierarchy = true;

        public void SetActive(bool value) => activeSelf = value;

        public T? GetComponent<T>() where T : class
            => parts.TryGetValue(typeof(T), out var part) ? part as T : null;

        /// <summary>The parent lookup the world-event reader uses to reach the area its object stands in. The
        /// fixture keeps one parent per object, which is all the reader asks for.</summary>
        public Component? Parent;

        public T? GetComponentInParent<T>() where T : class
            => GetComponent<T>() ?? Parent?.GetComponent<T>();

        public T AddComponent<T>() where T : Component, new()
        {
            var part = new T();
            Attach(part);
            return part;
        }

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

namespace Il2CppInterop.Runtime.InteropTypes.Arrays
{
    /// <summary>An il2cpp array of value-type elements, as far as the objective read uses one.</summary>
    public sealed class Il2CppStructArray<T> where T : struct
    {
        private readonly T[] _items;

        public Il2CppStructArray(int length) => _items = new T[length];
        public Il2CppStructArray(T[] items) => _items = items ?? Array.Empty<T>();

        public int Length => _items.Length;
        public T this[int index] { get => _items[index]; set => _items[index] = value; }
        /// <summary>The array's own contents, which is how a double's write is read back by a case; the interop
        /// type converts to a managed array the same way.</summary>
        public T[] ToArray() => _items;
        public static implicit operator T[](Il2CppStructArray<T> value) => value._items;
    }
}

namespace Il2CppSystem.Collections.Generic
{
    /// <summary>The interop table: the same dictionary plus the interop base's own collected flag, which the
    /// production readers test before they read a table the game handed them.</summary>
    public class Dictionary<TKey, TValue> : System.Collections.Generic.Dictionary<TKey, TValue> where TKey : notnull
    {
        public bool WasCollected => false;
    }
    /// <summary>The interop assembly's own set, which is the type the warp gate reads off the agent.</summary>
    public class HashSet<T> : System.Collections.Generic.HashSet<T> { }
    /// <summary>The interop list the native members take where the game itself builds one.</summary>
    public class List<T> : System.Collections.Generic.List<T> { }
}

namespace Globals
{
    /// <summary>The rundown block the process loaded: the first field of the level identity the matcher reads.</summary>
    public static class Global
    {
        public static uint RundownIdToLoad { get; set; }
    }
}

/// <summary>The game's own tier enum, with the exact values the interop assembly declares.</summary>
public enum eRundownTier { TierA = 1, TierB = 2, TierC = 3, TierD = 4, TierE = 5, Surface = 99 }

/// <summary>The active expedition's identity: a value type in the interop assembly, read as properties by the
/// level reader exactly like the game's own struct.</summary>
public struct pActiveExpedition
{
    public eRundownTier tier { get; set; }
    public int expeditionIndex { get; set; }
}

/// <summary>The manager whose static reads the level identity comes from and whose instance callback reports the
/// end of an expedition.</summary>
public class RundownManager
{
    public static pActiveExpedition Active { get; set; }
    public static string? ActiveExpeditionUniqueKey { get; set; }
    public static bool ThrowOnRead;
    public static pActiveExpedition GetActiveExpeditionData()
        => ThrowOnRead ? throw new InvalidOperationException("fixture native read failure") : Active;
    public void OnExpeditionEnded(ExpeditionEndState endState) { }
    public static void Reset()
    {
        Active = default; ActiveExpeditionUniqueKey = null; ThrowOnRead = false;
        Globals.Global.RundownIdToLoad = 0;
    }
}

/// <summary>The game's own end-of-expedition vocabulary: Success=0, Fail=1, Abort=2 (dump.cs 13128).</summary>
public enum ExpeditionEndState { Success = 0, Fail = 1, Abort = 2 }

/// <summary>The level's own dimension vocabulary.</summary>
public enum eDimensionIndex
{
    Reality = 0, Dimension_1 = 1, Dimension_2 = 2, Dimension_3 = 3, Dimension_4 = 4, Dimension_5 = 5,
    Dimension_6 = 6, Dimension_7 = 7, Dimension_8 = 8, Dimension_9 = 9, Dimension_10 = 10, Dimension_11 = 11,
    Dimension_12 = 12, Dimension_13 = 13, Dimension_14 = 14, Dimension_15 = 15, Dimension_16 = 16,
    Dimension_17 = 17, Dimension_18 = 18, Dimension_19 = 19, Dimension_20 = 20, MAX_COUNT = 21
}

/// <summary>The three coordinates of one zone, as `v-env`'s light row hands them to the native read.</summary>
public struct GlobalZoneIndex
{
    public GlobalZoneIndex(int dimension, int layer, int zone)
    { Dimension = (eDimensionIndex)dimension; Layer = (LevelGeneration.LG_LayerType)layer; Zone = (GameData.eLocalZoneIndex)zone; }
    public eDimensionIndex Dimension;
    public LevelGeneration.LG_LayerType Layer;
    public GameData.eLocalZoneIndex Zone;
}

/// <summary>The native infection value: amount, mode and effect, in the interop struct's own order.</summary>
public struct pInfection
{
    public float amount;
    public pInfectionMode mode;
    public pInfectionEffect effect;
}

public enum pInfectionMode : byte { Set = 0, Add = 1 }
public enum pInfectionEffect : byte { None = 0, DisinfectionPack = 1, DisinfectionStation = 2 }

namespace Localization
{
    /// <summary>The game's localized-text value: the untranslated text and two ids, and the one type the door
    /// lock's no-key setup takes for the prompt it shows at the door.</summary>
    public struct LocalizedText
    {
        public string? UntranslatedText;
        public uint Id;
        public uint OldId;
        public LocalizedText(string untranslatedText) { UntranslatedText = untranslatedText; Id = 0; OldId = 0; }
        public bool HasValue => !string.IsNullOrEmpty(UntranslatedText) || Id != 0 || OldId != 0;
    }
}

namespace SNetwork
{
    public static class SNet
    {
        public static bool IsMaster { get; set; } = true;
        public static SNet_SessionHub? SessionHub { get; set; } = new();
        /// <summary>The one player sitting at this machine, which is what `audience=self` compares against.</summary>
        public static SNet_Player? LocalPlayer { get; set; }
        public static bool HasLocalPlayer => LocalPlayer != null;
        internal static void Reset() { SessionHub = new SNet_SessionHub(); LocalPlayer = null; }
    }

    public sealed class SNet_SessionHub
    {
        public List<SNet_Player> PlayersInSession { get; } = new();
    }

    public sealed class SNet_IPlayerAgent
    {
        public object? Target;
        public T? TryCast<T>() where T : class => Target as T;
    }

    public sealed class SNet_Player : UnityObjectDouble
    {
        public ulong Lookup;
        public bool IsBot;
        public bool IsLocal;
        public bool IsMaster;
        public int SlotIndex;
        public SNet_IPlayerAgent? PlayerAgent;
        // The interop member is a method, not a property.
        public int PlayerSlotIndex() => SlotIndex;
    }

    public interface IReplicator { }
}

namespace SNetStructs
{
    /// <summary>The replicated player slot a container or item state carries. The game's own struct accessor is
    /// the one read the level-object half makes, so the double answers through `TryGetPlayer` exactly like it.</summary>
    public struct pPlayer
    {
        public object? Target;
        public bool TryGetPlayer(out SNetwork.SNet_Player? player)
        {
            player = Target as SNetwork.SNet_Player;
            return player != null;
        }
    }
}

namespace AIGraph
{
    /// <summary>One dimension object of the level graph; a course node's own `m_dimension` is what a portal warp
    /// reads the "from" side out of.</summary>
    public sealed class Dimension : UnityObjectDouble
    {
        public eDimensionIndex DimensionIndex;
    }

    /// <summary>One course node. A terminal's `m_zone` is the terminal's own statement of which zone it stands
    /// in, a player agent's course node is the zone a player stands in, and the static list is the level's own
    /// node table the wave start row takes its origin node from.</summary>
    public class AIG_CourseNode : UnityObjectDouble
    {
        public static List<AIG_CourseNode>? s_allNodes { get; set; } = new();
        private LevelGeneration.LG_Zone? _zone;
        public Dimension? m_dimension;
        /// <summary>When set, reading the node's own zone throws, which is what a node torn down under a live
        /// life looks like from a reader.</summary>
        public bool ZoneThrows;
        public LevelGeneration.LG_Zone? m_zone
        {
            get => ZoneThrows ? throw new InvalidOperationException("fixture course-node read failure") : _zone;
            set => _zone = value;
        }
    }
}

namespace Agents
{
    /// <summary>`PlayerAgent`'s base type in the interop assembly; position and alive live here, and both are
    /// virtual because the interop wrapper routes both reads through the il2cpp virtual method — which is why the
    /// death hook patches the property rather than a backing field.</summary>
    public class Agent : UnityObjectDouble
    {
        public UnityEngine.Vector3 Position { get; set; }
        public virtual bool Alive { get; set; } = true;
        public virtual int PlayerSlotIndex { get; set; }
    }

    /// <summary>The game's own host-authoritative revive action: the one entry the revive row submits through.</summary>
    public static class AgentReplicatedActions
    {
        public static Action<Player.PlayerAgent, Player.PlayerAgent, UnityEngine.Vector3>? Revive { get; set; }

        public static void PlayerReviveAction(Player.PlayerAgent target, Player.PlayerAgent source, UnityEngine.Vector3 where)
            => Revive?.Invoke(target, source, where);
    }
}

/// <summary>The native attribute-modifier enum: every member and its own value, taken from the interop assembly's
/// declaration of build 20403457. The values jump, which is why the production table is explicit.</summary>
public enum AgentModifier
{
    None = 0, RegenerationCap = 1, RegenerationSpeed = 2, HealSupport = 3, ReviveSpeedSupport = 4,
    ReviveStartHealthSupport = 5, MeleeResistance = 6, ProjectileResistance = 7, InfectionResistance = 8,
    DamageOverTime = 9, Nanoswarm_Shield = 10, Nanoswarm_Weakness = 11, ExplosionResistance = 12,
    PistolDamage = 50, SMGDamage = 51, DMRDamage = 52, AssaultRifleDamage = 53, CarbineDamage = 54,
    AutoPistolDamage = 55, HELDamage = 56, ShotgunDamage = 58, RevolverDamage = 59, SniperDamage = 60,
    BurstCannonDamage = 61, MachineGunDamage = 62, MachinePistolDamage = 63, RifleDamage = 64,
    BurstRifleDamage = 65, DoubleTapRifle = 66, BullpupRifleDamage = 67, CombatShotgunDamage = 68,
    ChokeModShotgunDamage = 69, StandardWeaponDamage = 70, SpecialWeaponDamage = 71, GlueStrength = 100,
    GlueEfficiency = 101, SentryGunSpeed = 102, SentryGunDamage = 103, SentryGunLongRangeDamage = 104,
    SentryGunShortRangeDamage = 105, TripMineDamage = 106, ScannerRechargeSpeed = 107, AmmoSupport = 108,
    HackingProficiency = 150, ComputerProcessingSpeed = 151, InitialAmmoStandard = 152, InitialAmmoSpecial = 153,
    InitialAmmoTool = 154, FogRepellerEffect = 155, GlowstickEffect = 156, BioscanSpeed = 157, MeleeDamage = 200,
    MovementSpeed = 250, MovementAcceleration = 251
}

/// <summary>The native attribute write entry. Both members are the ones the action layer reaches, and each call
/// is recorded because the entry returning a value is the whole observable.</summary>
public class AgentModifierManager
{
    public readonly record struct AddCall(Agents.Agent Agent, AgentModifier Modifier, float Value, float DeltaPerSec);

    public static List<AddCall> Adds { get; } = new();
    public static List<uint> Clears { get; } = new();
    public static Func<Agents.Agent, AgentModifier, float, float, uint>? Add { get; set; }
    public static Action<uint>? Clear { get; set; }
    private static uint _nextId = 1;

    public static uint AddSyncedModifierValue(Agents.Agent agent, AgentModifier modifier, float value, float deltaPerSec = 0f)
    {
        Adds.Add(new AddCall(agent, modifier, value, deltaPerSec));
        return Add != null ? Add(agent, modifier, value, deltaPerSec) : _nextId++;
    }

    public static void ClearSyncedModifierChange(uint modificationID)
    {
        Clears.Add(modificationID);
        Clear?.Invoke(modificationID);
    }

    public static void Reset()
    {
        Adds.Clear(); Clears.Clear(); Add = null; Clear = null; _nextId = 1;
    }
}

namespace Player
{
    /// <summary>The locomotion machine's own state list, in the game's own order (interop `PLOC_State`, byte
    /// backed); `Downed` is the member the player readings compare against.</summary>
    public sealed class PlayerLocomotion : UnityObjectDouble
    {
        public enum PLOC_State : byte
        {
            Stand, Crouch, Run, Jump, Fall, Land, Stunned, Downed, ClimbLadder, OnTerminal, Melee, Empty,
            GrabbedByTrap, GrabbedByTank, Testing, InElevator, GrabbedByPouncer, StandStill
        }
        public PLOC_State m_currentStateEnum;
        public PLOC_State m_lastStateEnum;
        /// <summary>The time the machine entered its current state, which the movement rows report as `since`.</summary>
        public float m_changeStateTime;
        /// <summary>The impulse channel the push rows write and read back: the game keeps one accumulated force
        /// per body, and these two entries are its own.</summary>
        public UnityEngine.Vector3 ExternalPushForce;
        public void AddExternalPushForce(UnityEngine.Vector3 force) => ExternalPushForce = ExternalPushForce + force;
        public UnityEngine.Vector3 GetExternalPushForce() => ExternalPushForce;
        /// <summary>The state instance the machine is currently in; the downed state is reached through the same
        /// cast the production reader performs.</summary>
        public object? CurrentStateInstance;
        public T? TryCast<T>() where T : class => CurrentStateInstance as T;
    }

    /// <summary>The downed state of the locomotion machine: its owner, its revive flag and its own callbacks,
    /// each of which the player-life hooks patch.</summary>
    public sealed class PLOC_Downed : UnityObjectDouble
    {
        public PlayerAgent? m_owner;
        public bool m_isRevived;
        public void Enter() { }
        public void SyncEnter() { }
        public void OnPlayerRevived() { }
    }

    /// <summary>The base of every timed interaction, in the global namespace the game declares it in; the two
    /// members the revive hook reads live on it.</summary>
    public sealed class Interact_Revive : Interact_Timed
    {
        public PlayerAgent? m_owner;
        public void OnInteractorStateChanged(PlayerAgent sourceAgent, bool state) { }
        public void TriggerInteractionAction(PlayerAgent source) { }
    }

    /// <summary>The location a warp names: the production reader reads `goodPosition` as a field, exactly the
    /// interop member build 20403457 carries.</summary>
    public struct pPlayerLocationData
    {
        public UnityEngine.Vector3 goodPosition;
        public UnityEngine.Vector3 position;
    }

    /// <summary>The synced health receiver a player's damage base derives from, exactly as the interop declares
    /// it: the health state, the setup flag and the one write entry. The native player receive refuses to store
    /// health for an owner that is not alive, and the double applies that same guard so a write which reached a
    /// dead owner is observable as a call that changed nothing.</summary>
    public class Dam_SyncedDamageBase : UnityObjectDouble
    {
        public bool IsSetup;
        public float Health;
        public float HealthMax;
        public int Sends;
        /// <summary>What the native call does instead of storing the value, for the commit cases that need a
        /// throw or a receiver that changes under the write; null means the game's own behaviour.</summary>
        public Action<float>? Commit;
        /// <summary>The agent the receiver belongs to, or null when this double models no owner.</summary>
        public virtual Agents.Agent? OwnerAgent => null;
        public virtual void SendSetHealth(float health)
        {
            Sends++;
            if (Commit != null) { Commit(health); return; }
            if (OwnerAgent is { Alive: false }) return;
            Health = health;
        }
        public void SendSetDead(bool allowRevive = true)
        {
            Sends++;
            Health = 0;
            if (Owner is { } owner) owner.Alive = false;
        }
        public Agents.Agent? Owner { get; set; }
    }

    /// <summary>One submitted infection change, kept so a case can prove what reached the native entry.</summary>
    public readonly record struct InfectionCall(pInfection Data, bool Sync, bool UpdatePageMap);

    /// <summary>The player damage base: the health state the readers use, the infection value and its one
    /// replicated write entry, and every receive entry the damage-kind hooks patch. The receive entries exist
    /// because the interop members do; a case that proves a kind was read invokes the hook directly.</summary>
    public class Dam_PlayerDamageBase : Dam_SyncedDamageBase
    {
        public PlayerAgent? Owner;
        public override Agents.Agent? OwnerAgent => Owner;
        public float Infection;
        public List<InfectionCall> Calls { get; } = new();
        public Action<pInfection, bool, bool>? InfectionCommit;

        public bool OnIncomingDamage(float damage, Agents.Agent sourceAgent) => damage > 0f;

        public void ModifyInfection(pInfection data, bool sync, bool updatePageMap)
        {
            Calls.Add(new InfectionCall(data, sync, updatePageMap));
            if (InfectionCommit != null) { InfectionCommit(data, sync, updatePageMap); return; }
            Infection = data.mode == pInfectionMode.Add ? Infection + data.amount : data.amount;
        }

        public void ReceiveBulletDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceiveShooterProjectileDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceiveMeleeDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceiveExplosionDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceiveFallDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceiveFireDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceiveStickyDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceiveParasiteDamage(float damage, Agents.Agent sourceAgent) { }
        public void ReceivePushDamage(float damage, Agents.Agent sourceAgent) { }
        /// <summary>The one damage write the player half submits, with the receiver's own body applying it: the
        /// damage that lands is bounded by the health the receiver holds.</summary>
        public void BulletDamage(float dam, Agents.Agent? sourceAgent, UnityEngine.Vector3 position,
            UnityEngine.Vector3 direction, UnityEngine.Vector3 normal, bool allowDirectionalBonus = false,
            float staggerMulti = 1f, float precisionMulti = 1f, uint gearCategoryId = 0u)
            => Health = Math.Max(0f, Health - dam);
    }

    /// <summary>The item a player wields. Its own ammunition readings are the ones the tool and ammo rows make,
    /// and the pool entries take the player whose class pool is asked for.</summary>
    public class ItemEquippable : UnityEngine.Component
    {
        public uint itemID_gearCRC;
        public int Clip;
        public int ClipMaximum = 1;
        public int ClassAmmoInPack;
        public int ClassAmmoMaximum = 1;
        public Gear? m_expeditionGearComponent;

        public int GetCurrentClip() => Clip;
        public int GetMaxClip() => ClipMaximum;
        public int GetClassAmmoInPackAbs(SNetwork.SNet_Player? owner) => ClassAmmoInPack;
        public int GetClassAmmoMaxCap(SNetwork.SNet_Player? owner) => ClassAmmoMaximum;
    }

    /// <summary>The expedition component a backpack slot's item carries when it is the expedition item.</summary>
    public sealed class Gear : UnityEngine.Component { }

    /// <summary>The gear block id one generator state change names.</summary>
    public struct pItemData
    {
        public uint itemID_gearCRC;
    }

    public sealed class PlayerInventory : UnityEngine.Component
    {
        public ItemEquippable? WieldedItem;
    }

    /// <summary>One backpack slot: the item instance it holds is what the expedition-item read walks.</summary>
    public sealed class PlayerBackpackSlot
    {
        public ItemEquippable? Instance;
    }

    /// <summary>One pocket group of the backpack: the stack table the pickup and removal paths maintain.</summary>
    public sealed class PlayerBackpackPocketGroup
    {
        public int Count;
    }

    public sealed class PlayerBackpack : UnityEngine.Component
    {
        public PlayerBackpackSlot[]? Slots = Array.Empty<PlayerBackpackSlot>();
        public List<PlayerBackpackPocketGroup>? PocketItemsGroups = new();
    }

    /// <summary>The one table from a player to the backpack they carry.</summary>
    public static class PlayerBackpackManager
    {
        public static readonly Dictionary<SNetwork.SNet_Player, PlayerBackpack> Backpacks = new();

        public static bool TryGetBackpack(SNetwork.SNet_Player player, out PlayerBackpack? backpack)
            => Backpacks.TryGetValue(player, out backpack);
    }

    public class PlayerAgent : Agents.Agent
    {
        /// <summary>The game's own warp flags, at the enum's own values.</summary>
        [Flags]
        public enum WarpOptions : byte
        {
            None = 0, WithBotsIfAny = 1, ShowScreenEffectForLocal = 2, PlaySounds = 4,
            WithoutBots = ShowScreenEffectForLocal | PlaySounds, All = WithoutBots | WithBotsIfAny
        }

        public SNetwork.SNet_Player? Owner;
        public Dam_PlayerDamageBase? Damage;
        public PlayerLocomotion? Locomotion;
        public Interact_Revive? ReviveInteraction;
        public PlayerInventory? Inventory;
        /// <summary>The stamina component the stamina row writes and the camera the shake row writes, both read
        /// through the agent exactly as the interop exposes them.</summary>
        public PlayerStamina? Stamina;
        public FPSCamera? FPSCamera;
        /// <summary>The eye the presentation rows measure from, and the direction a liquid job is aimed.</summary>
        public UnityEngine.Vector3 EyePosition;
        public UnityEngine.Vector3 Forward = new(0f, 0f, 1f);
        public UnityEngine.Vector3 Position;
        /// <summary>The states this agent accepts a warp in; the game's own set for a player on the ground.</summary>
        public Il2CppSystem.Collections.Generic.HashSet<PlayerLocomotion.PLOC_State>? m_warpableStates = new()
        {
            PlayerLocomotion.PLOC_State.Stand, PlayerLocomotion.PLOC_State.Crouch, PlayerLocomotion.PLOC_State.Run,
            PlayerLocomotion.PLOC_State.Jump, PlayerLocomotion.PLOC_State.Fall, PlayerLocomotion.PLOC_State.Land,
            PlayerLocomotion.PLOC_State.StandStill
        };
        public bool IsLocallyOwned;
        public LevelGeneration.LG_Zone? m_lastEnteredZone;
        /// <summary>The course node the life stands on, which is the zone a player is placed in.</summary>
        public AIGraph.AIG_CourseNode? CourseNode;
        public PlaceNavMarkerOnGO? NavMarker;

        /// <summary>The game's own landing solver. It answers the reference position unless a case replaces
        /// it; a null answer is the game refusing the destination.</summary>
        public static Func<eDimensionIndex, UnityEngine.Vector3, (bool Ok, UnityEngine.Vector3 Landed)>? Sampler;

        public static bool SampleWarpPosition(eDimensionIndex dimensionIndex, UnityEngine.Vector3 referencePosition,
            out UnityEngine.Vector3 result)
        {
            if (Sampler != null) { var (ok, landed) = Sampler(dimensionIndex, referencePosition); result = landed; return ok; }
            result = referencePosition;
            return true;
        }

        /// <summary>The one call that moves a player. The production hook is a postfix on it and the body does
        /// nothing, exactly as the other readback cases invoke their postfix directly.</summary>
        public void WarpTo(UnityEngine.Vector3 position, UnityEngine.Quaternion rotation, pPlayerLocationData locationData) { }

        public void RequestWarpToSync(eDimensionIndex dimensionIndex, UnityEngine.Vector3 position,
            UnityEngine.Vector3 lookDir, WarpOptions options) { }

        public static void ResetStatics() { Sampler = null; }
    }

    /// <summary>The player at this machine: the one agent a ping's world position is read from.</summary>
    public class LocalPlayerAgent : PlayerAgent
    {
        public UnityEngine.Vector3 m_pingPos;
    }

    /// <summary>The replication data one spawn carries: the player it names and the position the game spawns them
    /// at. `snetPlayer` is the field the production reader reads.</summary>
    public sealed class pPlayerSpawnData
    {
        public SNetwork.SNet_Player? snetPlayer;
        public pPlayerLocationData locationData;
        public byte characterIndex;
    }

    /// <summary>The replication path that (re)creates a player. The body does nothing: the fixture invokes the
    /// production postfix directly, exactly as the other readback cases do.</summary>
    public sealed class PlayerReplicationManager
    {
        public void OnSpawn(pPlayerSpawnData spawnData, object? replicator) { }
    }

    /// <summary>The local player's own fixed update the trigger-zone tick hangs off, with the owner the hook reads
    /// to skip every player that is not the machine's own.</summary>
    public sealed class PlayerInteraction : UnityEngine.Component
    {
        public PlayerAgent? m_owner;
        public void FixedUpdate() { }
    }

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

    public sealed class PlayerManager
    {
        private static readonly List<PlayerAgent> Agents = new();
        public static bool ThrowOnRead;
        public static List<PlayerAgent> PlayerAgentsInLevel
            => ThrowOnRead ? throw new InvalidOperationException("fixture native read failure") : Agents;
        public void OnPlayerSpawned() { }
        public void OnPlayerDespawned() { }
        public static void Reset() { Agents.Clear(); ThrowOnRead = false; }
    }
}

/// <summary>The player's overhead marker, as the teammate-overhead row reads and writes it: the marker's own
/// pointer, the player it belongs to, and the native extra-information row it shares with the game's hint
/// row.</summary>
public sealed class PlaceNavMarkerOnGO : UnityEngine.Component
{
    public Player.PlayerAgent? Player { get; set; }
    public NavMarker? m_marker { get; set; }
    public string m_nameToShow = "";
    public string m_extraInfo = "";
    public bool m_extraInfoVisible;

    public void UpdateExtraInfo() { }
    public void SetExtraInfoVisible(bool visible) => m_extraInfoVisible = visible;
    public void OnDestroy() { }
}

/// <summary>The marker's own text meshes, of which the player-name one is the row this provider clones.</summary>
public sealed class NavMarker
{
    public TMPro.TextMeshPro? m_playerName;
    public TMPro.TextMeshPro? m_distance;
}

namespace GameEvent
{
    /// <summary>The player-event vocabulary, at the interop member names and values the funnel's signature takes.
    /// Only the members the event mapping names are declared.</summary>
    public enum eGameEvent
    {
        none = 0, player_low_health = 1, player_apply_medikit = 2, player_apply_ammokit = 3,
        player_apply_disinfection = 4, player_apply_toolRefill = 5, player_pickup_medikit = 6,
        player_pickup_ammokit = 7, player_pickup_toolRefill = 8, player_pickup_artifact = 9,
        player_pickup_commoditySmall = 10, player_pickup_commodityMedium = 11, player_pickup_commodityLarge = 12,
        player_pickup_consumable = 13, player_pickup_keycard = 14, player_ping = 15
    }

    /// <summary>The one game-event funnel every player row is read from.</summary>
    public class GameEventManager
    {
        public void PostEvent(eGameEvent e, Player.PlayerAgent player, float value, string text,
            Il2CppSystem.Collections.Generic.Dictionary<string, string>? data) { }
    }
}

/// <summary>The marker call the game makes for a ping. Only the visible edge is a ping; the same body is called
/// to hide a marker again.</summary>
public enum eNavMarkerStyle { None = 0, Ping = 1, Objective = 2 }

/// <summary>The local player's HUD layer and the two readouts `a-hud` writes.</summary>
public class PUI_LocalPlayerStatus : UnityEngine.Component
{
    public UnityEngine.GameObject? m_shieldUIParent = new("ShieldGroup");
    public TMPro.TextMeshPro? m_shieldText = new();
    public TMPro.TextMeshPro? m_healthText = new();
    public float? Shield;
    public int ShieldWrites;

    public void UpdateShield(float shield) { Shield = shield; ShieldWrites++; }
}

public class PlayerGuiLayer : UnityEngine.Component
{
    public PUI_LocalPlayerStatus? m_playerStatus = new();
}

public class GuiManager : UnityEngine.Component
{
    public static GuiManager? Current { get; set; } = new();
    public PlayerGuiLayer? m_playerLayer = new();

    public void AttemptSetPlayerPingStatus(Player.PlayerAgent sourceAgent, bool visible, UnityEngine.Vector3 worldPos,
        eNavMarkerStyle style) { }
}

/// <summary>The objective interaction vocabulary: the member's byte is the index the native enum declares, which
/// is what the interaction struct's type field carries.</summary>
public enum eWardenObjectiveInteractionType : byte
{
    DiscoverObjective = 0, StartObjective = 1, SolveWardenObjectiveItem = 2, PartiallySolveWardenObjectiveItem = 3,
    SolveWinCondition = 4, UpdateSubObjective = 5, ChangeLocalLayer = 6, EventUpdate = 7,
    CustomSubObjectiveUpdate = 8, SetItemSolved = 9, AddRequiredItem = 10, SetExtraTime = 11,
    SetSolveOnDeath = 12, SetExitWaveTriggered = 13
}

/// <summary>The interaction type the door actions declare: the enum is the native one, the names are this half's
/// own. The member values are the interop indices.</summary>
public enum DoorInteractionType { Unlock = 5, SetLockedNoKey = 3 }

namespace GameData
{
    public enum eLocalZoneIndex { Zone_0 = 0, Zone_1 = 1, Zone_2 = 2, Zone_3 = 3, Zone_4 = 4, Zone_5 = 5, Zone_19 = 19 }

    /// <summary>The level-event vocabulary, at the interop member names and values the event struct's type
    /// member takes. Only the members the environment rows build are declared.</summary>
    public enum eWardenObjectiveEventType
    {
        None = 0, AllLightsOff = 3, AllLightsOn = 4, PlaySound = 5, SetFogSetting = 6, DimensionFlashTeam = 7,
        DimensionWarpTeam = 8, UpdateCustomSubObjective = 11, LightsInZone = 13, LightsInZoneToggle = 14,
        AnimationTrigger = 15, SetNavMarker = 17, StepProgressionObjective = 18, SetWorldEventCondition = 19,
        AddToTimer = 24, ResetTimer = 25, WinOnDeath = 26, ForceInstantWin = 27,
        DialogueOnClosest = 28, ClearDimension = 30, StartRepeatingFog = 31, StopSustainedEvent = 32
    }

    public sealed class WorldEventConditionPair
    {
        public int ConditionIndex { get; set; }
        public bool IsTrue { get; set; }
    }

    /// <summary>The game's own level event payload, with every member the environment rows write.</summary>
    public sealed class WardenObjectiveEventData
    {
        public eWardenObjectiveEventType Type { get; set; }
        public WorldEventConditionPair Condition { get; set; } = new();
        public eDimensionIndex DimensionIndex { get; set; }
        public LevelGeneration.LG_LayerType Layer { get; set; }
        public GameData.eLocalZoneIndex LocalIndex { get; set; }
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
        public bool ClearDimension { get; set; }
        public string WorldEventObjectFilter { get; set; } = "";
        /// <summary>The sub-objective text members (dump.cs: the two `LocalizedText` properties the objective-event
        /// rows write), and the condition slot the world-event row writes.</summary>
        public Localization.LocalizedText? CustomSubObjectiveHeader { get; set; }
        public Localization.LocalizedText? CustomSubObjective { get; set; }
        public int ConditionIndex { get; set; }
        public bool IsTrue { get; set; }
    }

    /// <summary>One specific terminal spawn of a zone's data block. The production reader counts them, because a
    /// zone that declares any does not have a placement order it can trust.</summary>
    public sealed class SpecificTerminalSpawnData : UnityObjectDouble
    {
        public int InstanceIndex;
    }

    /// <summary>The zone's data block entry, with the one member the address reader consults.</summary>
    public sealed class ExpeditionZoneData : UnityObjectDouble
    {
        public List<SpecificTerminalSpawnData>? SpecificTerminalSpawnDatas = new();
    }

    /// <summary>The alarm/scan author block: the alarm name a chained-puzzle resource may be spelled with, and
    /// the flag the game uses to tell an alarm from a scan.</summary>
    public class ChainedPuzzleDataBlock : UnityObjectDouble
    {
        public string? PublicAlarmName { get; set; }
        public bool TriggerAlarmOnActivate { get; set; }
    }

    /// <summary>The data block base the two wave blocks share. The generic keeps one static table per closed
    /// type, exactly like the game's own generic base.</summary>
    public class GameDataBlockBase<T> : UnityObjectDouble where T : GameDataBlockBase<T>
    {
        private static readonly Dictionary<uint, T> Blocks = new();
        public uint persistentID { get; set; }
        public static void Reset() => Blocks.Clear();
        public static void Register(T block) => Blocks[block.persistentID] = block;
        public static T? GetBlock(uint id) => Blocks.TryGetValue(id, out var block) ? block : null;
        public static IReadOnlyList<T> GetAllBlocks() => Blocks.Values.ToList();
    }

    public sealed class SurvivalWaveSettingsDataBlock : GameDataBlockBase<SurvivalWaveSettingsDataBlock> { }
    public sealed class SurvivalWavePopulationDataBlock : GameDataBlockBase<SurvivalWavePopulationDataBlock> { }
}

namespace LevelGeneration
{
    public enum LG_LayerType { MainLayer = 0, SecondaryLayer = 1, ThirdLayer = 2 }

    /// <summary>The objective status vocabulary, at the interop members' own values (they are not an ordinal
    /// run): the two the level-event rows name are the objective's start and its item-solved boundary.</summary>
    public enum eWardenObjectiveStatus : byte
    {
        NotDiscovered = 0, Discovered = 10, Started = 20, WardenObjectivePartiallySolved = 30, WardenObjectiveItemSolved = 40
    }

    /// <summary>The sub-objective vocabulary, with the interop values 0..9.</summary>
    public enum eWardenSubObjectiveStatus : byte
    {
        FindLocationInfo = 0, FindLocationInfoHelp = 1, GoToZone = 2, GoToZoneHelp = 3, InZoneFindItem = 4,
        InZoneFindItemHelp = 5, SolveItem = 6, SolveItemHelp = 7, GoToWinCondition = 8, GoToWinConditionHelp = 9
    }

    /// <summary>The objective type vocabulary, in the enum's own order; `Empty` is the member for a layer that
    /// runs no objective at all.</summary>
    public enum eWardenObjectiveType : byte
    {
        HSU_FindTakeSample = 0, Reactor_Startup = 1, Reactor_Shutdown = 2, GatherSmallItems = 3, ClearAPath = 4,
        SpecialTerminalCommand = 5, RetrieveBigItems = 6, PowerCellDistribution = 7, TerminalUplink = 8,
        CentralGeneratorCluster = 9, ActivateSmallHSU = 10, Survival = 11, GatherTerminal = 12,
        CorruptedTerminalUplink = 13, Empty = 14, TimedTerminalSequence = 15
    }

    public enum eDoorStatus : byte
    {
        None = 0, Closed = 1, Closed_BrokenCantOpen = 2, Closed_LockedWithKeyItem = 3,
        Closed_LockedWithChainedPuzzle_Alarm = 4, Closed_LockedWithChainedPuzzle = 5,
        Closed_LockedWithPowerGenerator = 6, Closed_LockedWithNoKey = 7, ChainedPuzzleActivated = 8,
        Unlocked = 9, Open = 10, Destroyed = 11, GluedMax = 12, TryOpenStuckInGlue = 13,
        TryOpenStuckBroken = 14, Closed_LockedWithBulkheadDC = 15, Opening = 16
    }

    public enum eSecurityDoorType { Security = 0, Apex = 1, Bulkhead = 2 }

    /// <summary>The door damage kinds the native damage entry takes. Enemy and melee weights and explosions are
    /// the whole vocabulary; there is no bullet kind, which is why a catalog damage kind cannot be mapped onto
    /// this enum member for member.</summary>
    public enum eDoorDamageType : byte
    {
        EnemyLight = 0, EnemyHeavy = 1, MeleeWeaponLight = 2, MeleeWeaponHeavy = 3, Explosion = 4,
        MeleeWeaponMinimal = 5
    }

    /// <summary>The door interaction vocabulary, at the native enum's own member indices.</summary>
    public enum eDoorInteractionType : byte
    {
        Open = 0, SetLockedWithChainedPuzzle_Alarm = 1, SetLockedWithChainedPuzzle = 2, SetLockedNoKey = 3,
        ActivateChainedPuzzle = 4, Unlock = 5, Close = 6, DoDamage = 7, SetGluedMaxEnabled = 8,
        SetGluedMaxDisabled = 9, SetGlueLevel = 10, Approach = 11
    }

    /// <summary>The weak lock's own replicated status; `Unlocked` is the member the broken-lock row reads.</summary>
    public enum eWeakLockStatus : byte { LockedMelee = 0, LockedHackable = 1, LockMelterApplied = 2, Unlocked = 3 }

    /// <summary>What a weak lock is broken through: a melee lock is smashed, a hackable one is hacked.</summary>
    public enum eWeakLockType : byte { None = 0, Melee = 1, Hackable = 2 }

    /// <summary>The generator's own replicated status; `Powered` is the member the power rows compare against.</summary>
    public enum ePowerGeneratorStatus : byte { UnPowered = 0, Powered = 1, PoweringUp = 2, PoweringDown = 3 }

    public enum TERM_State
    {
        Sleeping = 0, Awake = 1, PlayerInteracting = 2, DataMining = 3, Hacked = 4, CodePuzzle = 5,
        InputTest = 6, ReactorError = 7, AskToPlayLogAudio = 8, DoPlayAudioFile = 9, AudioLoopError = 10,
        Ping = 11, PasswordProtected = 12, EnterPassword = 13
    }

    public enum TERM_Command : byte
    {
        None = 0, Help = 1, Commands = 2, Cls = 3, Exit = 4, Open = 5, Close = 6, Activate = 7, Deactivate = 8,
        EmptyLine = 9, InvalidCommand = 10, DownloadData = 11, ViewSecurityLog = 12, Override = 13,
        DisableAlarm = 14, Locate = 15, ActivateBeacon = 16, Find = 17, ShowList = 18, Query = 19, Ping = 20,
        ReactorStartup = 21, ReactorVerify = 22, ReactorShutdown = 23, WardenObjectiveSpecialCommand = 24,
        TerminalUplinkConnect = 25, TerminalUplinkVerify = 26, TerminalUplinkConfirm = 27, ListLogs = 28,
        ReadLog = 29, Start = 30, TryUnlockingTerminal = 31, WardenObjectiveGatherCommand = 32,
        TerminalCorruptedUplinkConnect = 33, TerminalCorruptedUplinkVerify = 34, TimedConnectionSend = 35,
        TimedConnectionVerify = 36, UsedCommand = 37, UniqueCommand1 = 38, UniqueCommand2 = 39,
        UniqueCommand3 = 40, UniqueCommand4 = 41, UniqueCommand5 = 42, Info = 43, MAX_COUNT = 44
    }

    /// <summary>The terminal's own line kinds: what a printed line's severity maps onto.</summary>
    public enum TerminalLineType
    {
        Normal = 0, Fail = 1, SpinningWaitDone = 2, SpinningWaitNoDone = 3, ProgressWait = 4, Warning = 5
    }

    public enum ePickupItemStatus : byte { PlacedInLevel = 0, PickedUp = 1, Dropped = 2, Despawned = 3 }

    /// <summary>The replicated state one level item's callback carries: the status and the player the game
    /// itself replicated.</summary>
    public sealed class pPickupItemState
    {
        public ePickupItemStatus status;
        public SNetStructs.pPlayer pPlayer;
    }

    /// <summary>The replicated state one resource container's callback carries.</summary>
    public sealed class pResourceContainerItemState
    {
        public byte status;
        public SNetStructs.pPlayer pPlayer;
    }

    /// <summary>The generator cell state one state change carries: the status, the item that changed it and the
    /// player the change was attributed to.</summary>
    public sealed class pPowerGeneratorState
    {
        public ePowerGeneratorStatus status;
        public Player.pItemData itemDataThatChangedTheState;
        public SNetStructs.pPlayer playerThatChangedTheState;
    }

    public struct pDoorState
    {
        public eDoorStatus status;
        public bool isDropinState;
    }

    public struct pComputerTerminalState
    {
        public int usedCommands;
    }

    public struct pCheckpointInteraction
    {
        public UnityEngine.Vector3 doorLockPosition;
    }

    public struct pWardenObjectiveInteraction
    {
        public eWardenObjectiveInteractionType type;
        public LevelGeneration.eWardenSubObjectiveStatus newSubObj;
        public bool forceUpdate;
        public byte itemIndexPlusOne;
        public byte newOnActivateEventBreakIndex;
        public byte newOnActivateEventIndex;
        public byte ownerChainIndexPlusOne;
        public LevelGeneration.LG_LayerType inLayer;
        public uint newCustomSubObjectiveHeaderID;
        public uint newCustomSubObjectiveTextID;
        public int itemID;
        public float extraTime;
    }

    /// <summary>The interface slot a door holds its lock component in; the production reader is the one that
    /// casts it, exactly like the interop interface.</summary>
    public sealed class iLG_Door_Locks : UnityObjectDouble
    {
        public object? Target;
        public T? TryCast<T>() where T : class => Target as T;
    }

    /// <summary>The interface slot a gate holds its spawned door in. The production reader casts it to a security
    /// door, so a gate whose door is something else is not a zone's `security` entrance.</summary>
    public interface iLG_Door_Core
    {
        bool WasCollected { get; }
        object? Target { get; }
        T? TryCast<T>() where T : class => Target as T;
    }

    /// <summary>The interface slot a door holds its sync component in, with the same cast rule.</summary>
    public sealed class iLG_Door_Sync : UnityObjectDouble
    {
        public object? Target;
        public T? TryCast<T>() where T : class => Target as T;
    }

    /// <summary>The door's own synchronized interaction entry.</summary>
    public sealed class LG_Door_Sync : UnityEngine.Component
    {
        public void AttemptDoorInteraction(eDoorInteractionType type, float val1, float val2,
            UnityEngine.Vector3 position, Player.PlayerAgent? agent) { }
    }

    public sealed class GateKeyItem : UnityObjectDouble
    {
        public uint DataBlockID;
        public string PublicName = "";
    }

    public sealed class LG_SecurityDoor_Locks : UnityObjectDouble
    {
        public LG_SecurityDoor? m_door;
        public bool m_lockedWithNoKey;
        public GateKeyItem? m_gateKeyItemNeeded;
        public bool m_hasAlarm;
        /// <summary>The alarm the door owns. Null means the door's lock never got a chained puzzle.</summary>
        public ChainedPuzzles.ChainedPuzzleInstance? ChainedPuzzleToSolve { get; set; }
        // What the action layer asked this component to do. The real members are game-side writes that leave no
        // return value a case can read, so the record of which member was reached and with what is the fixture.
        public int KeyItemLockCalls;
        public GateKeyItem? LastKeyItem;
        public int NoKeyLockCalls;
        public Localization.LocalizedText LastNoKeyText;
        public int AlarmWaveCalls;
        public bool? LastAlarmWaveEnabled;
        public int SimpleLockCalls;

        public void OnDoorState(pDoorState state, bool isDropinState = false) { }
        public void OnPlayerActivateChainedPuzzle() { }
        public void OnChainedPuzzleSolved() { }

        public eDoorStatus SetupSimpleLock()
        {
            SimpleLockCalls++;
            return eDoorStatus.Closed;
        }

        public eDoorStatus SetupForGateKey(GateKeyItem keyItem)
        {
            KeyItemLockCalls++; LastKeyItem = keyItem; return eDoorStatus.Closed_LockedWithKeyItem;
        }

        public eDoorStatus SetupAsLockedNoKey(Localization.LocalizedText interactionTextId)
        {
            NoKeyLockCalls++; LastNoKeyText = interactionTextId; return eDoorStatus.Closed_LockedWithNoKey;
        }

        public void SetActiveEnemyWaveEnabled(bool enabled)
        {
            AlarmWaveCalls++; LastAlarmWaveEnabled = enabled;
        }

        public void AttemptDoorInteraction(eDoorInteractionType type, float delay, float duration,
            UnityEngine.Vector3 position, Player.PlayerAgent? agent) { }
    }

    public sealed class LG_SecurityDoor : UnityEngine.Component, iLG_Door_Core
    {
        public object? Target => this;
        public eDoorStatus LastStatus { get; set; }
        public eSecurityDoorType m_securityDoorType { get; set; }
        public iLG_Door_Locks? m_locks { get; set; }
        /// <summary>The interface slot the door holds its sync component in, with the same cast rule.</summary>
        public iLG_Door_Sync? m_sync { get; set; }
        /// <summary>The door's own simple-lock setup: the lock component it holds is only the state the setup
        /// writes, so this is a member of the door and not of the component.</summary>
        public eDoorStatus SetupSimpleLock() => eDoorStatus.Closed;
        public bool InteractionAllowed { get; set; } = true;
        public eDoorStatus Status => LastStatus;
        public int OpenCloseCalls;
        public bool? LastOnlyUnlock;
        public int ForceOpenCalls;
        public int DamageCalls;
        public void AttemptOpenCloseInteraction(bool onlyUnlock = false)
        {
            OpenCloseCalls++; LastOnlyUnlock = onlyUnlock;
        }
        public void ForceOpenSecurityDoor() { ForceOpenCalls++; }
        public void AttemptDamage(eDoorDamageType type, UnityEngine.Vector3 sourcePos, Agents.Agent sourceAgent)
            => DamageCalls++;
    }

    /// <summary>A weak door: the level's own breakable door, whose sync notifications the door-terminal hooks
    /// patch. Its gate links the course node whose zone the door stands in.</summary>
    public sealed class LG_WeakDoor : UnityEngine.Component
    {
        public int m_serialNumber;
        public float m_healthMax = 100f;
        public bool m_destroyed;
        public LG_Gate? Gate;
        public eDoorStatus LastStatus { get; set; } = eDoorStatus.Closed;
        public void OnSyncDoorGotDamage(float damageDelta, float totalDamageTaken, bool sourceZPos, bool isDropin,
            SNetwork.SNet_Player? instigatorPlayer) { }
        public void OnSyncDoorGotDestroyed(float damage, bool sourceZPos, bool isDropinState,
            SNetwork.SNet_Player? instigatorPlayer) { }
    }

    /// <summary>The lock a weak door or a locker carries; its own state change is one of the door-terminal
    /// hooks.</summary>
    public sealed class LG_WeakLock : UnityEngine.Component
    {
        public int m_serialNumber;
        public eWeakLockStatus Status { get; set; }
        public eWeakLockType m_lockType { get; set; }
        /// <summary>The component the lock hangs on: the climb from it to the door is how the lock's owner is
        /// resolved.</summary>
        public UnityEngine.Component? m_holder;
        public void OnStateChange() { }
    }

    public sealed class LG_Gate : UnityObjectDouble
    {
        // The gate's own door slot is an interface in the interop assembly; the production reader casts it.
        public iLG_Door_Core? SpawnedDoor { get; set; }
        public List<AIGraph.AIG_CourseNode>? m_nodes { get; set; } = new();
    }

    /// <summary>The zone's own layer. The address's layer coordinate is this object's own m_type.</summary>
    public sealed class LG_Layer : UnityObjectDouble
    {
        public LG_LayerType m_type { get; set; }
    }

    /// <summary>The zone's build settings; the production reader reaches the zone's data block entry through it,
    /// which is where a zone's own terminal spawns live.</summary>
    public sealed class LG_ZoneSettings : UnityObjectDouble
    {
        public GameData.ExpeditionZoneData? m_zoneData { get; set; }
    }

    /// <summary>One generated area of a zone; the geomorph it was built from is what a room search compares its
    /// prefab object against.</summary>
    public sealed class LG_Area : UnityEngine.Component
    {
        public LG_Geomorph? m_geomorph;
        /// <summary>The zone the area was generated in, which the world-event reader reaches through the object's
        /// parent chain.</summary>
        public LG_Zone? m_zone;
    }

    /// <summary>One generated room. Its prefab object is the asset identity a room search matches, and its own
    /// transform is the pose a trigger zone's document pose is composed with.</summary>
    public sealed class LG_Geomorph : UnityEngine.Component
    {
        public UnityEngine.GameObject? m_geoPrefab;
    }

    public sealed class LG_Zone : UnityObjectDouble
    {
        public LG_Layer? m_layer { get; set; } = new();
        public eDimensionIndex m_dimensionIndex { get; set; }
        public GameData.eLocalZoneIndex LocalIndex { get; set; }
        public int IDinLayer;
        public LG_ZoneSettings? m_settings { get; set; } = new();
        public LG_Gate? m_sourceGate { get; set; }
        public List<LG_ComputerTerminal>? TerminalsSpawnedInZone { get; set; } = new();
        public List<LG_Area>? m_areas { get; set; } = new();
        /// <summary>The zone's own light list; the light count `v-env` answers with is its length.</summary>
        public List<LG_Light>? m_lightsInZone { get; set; } = new();
    }

    /// <summary>One native light of a zone: its object identity is the count of the zone's own list, and the
    /// three writes and two reads the light-colour row makes are the game's own `LG_Light` members.</summary>
    public sealed class LG_Light : UnityObjectDouble
    {
        public LightCategory m_category { get; set; }
        public UnityEngine.Color m_color { get; set; } = UnityEngine.Color.white;
        public float Intensity { get; private set; } = 1f;

        public float GetIntensity() => Intensity;
        public void ChangeIntensity(float intensity) => Intensity = intensity;
        public void ChangeColor(UnityEngine.Color color) => m_color = color;

        /// <summary>The game's own seven light categories, in the order the interop enum declares them.</summary>
        public enum LightCategory
        {
            General = 0, Special = 1, Emergency = 2, Independent = 3, Door = 4, Sign = 5, DoorImportant = 6
        }
    }

    /// <summary>The floor of the level: its own zone list is the one table every address is read from.</summary>
    public sealed class LG_Floor : UnityObjectDouble
    {
        public List<LG_Zone>? allZones { get; set; } = new();
    }

    /// <summary>The level singleton the floor is reached through. Its own `Current` is a settable static, so a
    /// level that was torn down leaves it null, exactly as the game's own scene teardown does.</summary>
    public sealed class LG_LevelBuilder : UnityObjectDouble
    {
        public static LG_LevelBuilder? Current { get; set; }
        public LG_Floor? m_currentFloor { get; set; } = new();
    }

    public sealed class LG_ComputerTerminal : UnityEngine.Component
    {
        public uint SyncID { get; set; }
        public TERM_State CurrentStateName { get; set; }
        public AIGraph.AIG_CourseNode? SpawnNode { get; set; }
        public bool m_hasInteractingPlayer;
        public Player.PlayerAgent? m_syncedInteractionSource;
        public LG_ComputerTerminalCommandInterpreter? m_command { get; set; }
        /// <summary>The terminal's own local line buffer, which is the whole of what the output action writes: the
        /// real member appends a line locally and the replicated terminal state carries used and removed commands,
        /// not lines.</summary>
        public List<TerminalLine> Lines { get; } = new();
        /// <summary>The commands this terminal currently hides, which is the state the synchronized setters write
        /// and the reader answers from. A command absent from it reads shown.</summary>
        public HashSet<TERM_Command> HiddenCommands { get; } = new();
        public void AddLine(TerminalLineType terminalLineType, string line, float time = 0)
            => Lines.Add(new TerminalLine(terminalLineType, line, time));
        public void OnStateChange(pComputerTerminalState oldState, pComputerTerminalState newState, bool isRecall) { }
        public bool CommandIsHidden(TERM_Command command) => HiddenCommands.Contains(command);
        public void TrySyncSetCommandHidden(TERM_Command command) => HiddenCommands.Add(command);
        public void TrySyncSetCommandShow(TERM_Command command) => HiddenCommands.Remove(command);

        /// <summary>The terminal's own log files, keyed by file name, which is the table the content action adds
        /// to and removes from and the table the visibility switch reads. `m_logFileDatas` is the member's own
        /// spelling in the interop.</summary>
        public Dictionary<string, GameData.TerminalLogFileData> LogFiles { get; } = new(StringComparer.Ordinal);
        public void AddLocalLog(GameData.TerminalLogFileData data, bool visible)
        {
            data.IsVisible = visible;
            LogFiles[data.FileName] = data;
        }
        public bool RemoveLocalLog(string fileName) => LogFiles.Remove(fileName);
        public bool IsLogVisible(string fileName) => LogFiles.TryGetValue(fileName, out var file) && file.IsVisible;
        public void SetLogVisible(string fileName, bool visible)
        {
            if (LogFiles.TryGetValue(fileName, out var file)) file.IsVisible = visible;
        }
        /// <summary>The table the game itself reads back, as the interop dictionary the production reader calls
        /// `ContainsKey` on.</summary>
        public Il2CppSystem.Collections.Generic.Dictionary<string, GameData.TerminalLogFileData> GetLocalLogs()
        {
            var table = new Il2CppSystem.Collections.Generic.Dictionary<string, GameData.TerminalLogFileData>();
            foreach (var pair in LogFiles) table[pair.Key] = pair.Value;
            return table;
        }
    }

    /// <summary>One line a printed request appended: the terminal's own line kind, the text and the native
    /// member's own time argument, exactly as the real member receives them.</summary>
    public sealed record TerminalLine(TerminalLineType Type, string Text, float Time);

    /// <summary>The terminal's own command interpreter. The parse the real interpreter performs is replaced by a
    /// table a case fills in, because the game's parse is not this fixture's subject — what a case proves is that
    /// the production action asks the terminal's own parser and sends what it answered.</summary>
    public sealed class LG_ComputerTerminalCommandInterpreter : UnityObjectDouble
    {
        public Dictionary<string, TERM_Command> Commands { get; } = new(StringComparer.OrdinalIgnoreCase);
        public int ParseCalls;
        public string? LastInput;
        /// <summary>The commands this terminal now answers to, by the slot the content action wrote them into.
        /// The real member registers one and reports whether the slot was free.</summary>
        public List<string> Added { get; } = new();
        public bool AddCommand(TERM_Command command, string commandString, Localization.LocalizedText helpString,
            TERM_CommandRule rule, Il2CppSystem.Collections.Generic.List<GameData.WardenObjectiveEventData> events)
        {
            Added.Add(commandString);
            return true;
        }
        public bool TryGetCommand(string inputString, out TERM_Command command, out string param1, out string param2)
        {
            ParseCalls++;
            LastInput = inputString;
            command = TERM_Command.None;
            param1 = ""; param2 = "";
            var parts = (inputString ?? "").Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length == 0 || !Commands.TryGetValue(parts[0], out command)) return false;
            param1 = parts.Length > 1 ? parts[1] : "";
            param2 = parts.Length > 2 ? parts[2] : "";
            return true;
        }
    }

    public sealed class LG_ComputerTerminalManager : UnityObjectDouble
    {
        /// <summary>The manager the static command entry belongs to. The action layer refuses a command when no
        /// manager is alive, so a case can stand the manager up or take it away.</summary>
        public static LG_ComputerTerminalManager? Current { get; set; }
        public static readonly List<SentCommand> Sent = new();
        public static void WantToSendTerminalCommand(uint terminalID, TERM_Command command, string inputString,
            string param1, string param2)
            => Sent.Add(new SentCommand(terminalID, command, inputString, param1, param2));
        public static void Reset() { Current = null; Sent.Clear(); }
    }

    /// <summary>One command the manager's own entry was asked to send.</summary>
    public sealed record SentCommand(uint TerminalId, TERM_Command Command, string Input, string Param1, string Param2);

    /// <summary>A generator's own replicated state holder; `State` is the game's own reading of the cell.</summary>
    public sealed class LG_PowerGeneratorStateReplicator : UnityObjectDouble
    {
        public pPowerGeneratorState State = new();
    }

    /// <summary>One generator cell. Its own serial, its group's member array and the replicated state are the
    /// members the generator rows read; a cell is a component so its group is found by the same hierarchy climb a
    /// door's owner is.</summary>
    public sealed class LG_PowerGenerator_Core : UnityEngine.Component
    {
        public int m_serialNumber;
        public LG_PowerGeneratorStateReplicator? m_stateReplicator = new();
        public bool m_powered;
        public void OnStateChange(pPowerGeneratorState newState) { }
    }

    /// <summary>One group of generator cells, with the member array the counts are taken from.</summary>
    public sealed class LG_PowerGeneratorCluster : UnityEngine.Component
    {
        public int m_serialNumber;
        public LG_PowerGenerator_Core[]? m_generators = Array.Empty<LG_PowerGenerator_Core>();
        public void OnStateChange(pPowerGeneratorState newState) { }
    }

    /// <summary>A resource container: a locker or a resource box, whose own state change is one of the
    /// level-object hooks.</summary>
    public sealed class LG_ResourceContainer_Sync : UnityEngine.Component
    {
        public void OnStateChange(pResourceContainerItemState oldState, pResourceContainerItemState newState) { }
    }

    /// <summary>A level item: its own state change is the one point where the game records that an item went into
    /// a player's hands or came back to the floor.</summary>
    public sealed class LG_PickupItem_Sync : UnityEngine.Component
    {
        public void OnStateChange(pPickupItemState oldState, pPickupItemState newState) { }
    }

    /// <summary>The portal's own warp entry: the player, the node they left and the node they arrived at.</summary>
    public sealed class LG_DimensionPortal : UnityEngine.Component
    {
        public int m_serialNumber;
        public eDimensionIndex m_targetDimension;
        public void OnWarp(AIGraph.AIG_CourseNode fromCourseNode) { }
    }
}

namespace ChainedPuzzles
{
    /// <summary>The three interactions the game's own entry takes.</summary>
    public enum eChainedPuzzleInteraction : byte { Activate, Solve, Deactivate }

    public enum eChainedPuzzleStatus : byte { Disabled, Active, Solved }

    /// <summary>One chained puzzle instance: the alarm and the scan are the same native kind, so one double
    /// serves both. `Interactions` records every call to the native entry, which is what proves the action
    /// submitted instead of only reporting that it had.</summary>
    public sealed class ChainedPuzzleInstance : UnityObjectDouble
    {
        public GameData.ChainedPuzzleDataBlock? data;
        public string? m_puzzleUID;
        public bool active;
        public bool solved;
        public int cores = 1;
        public int ActivateCalls;
        public int DeactivateCalls;
        public readonly List<eChainedPuzzleInteraction> Interactions = new();
        public GameData.ChainedPuzzleDataBlock? Data => data;
        public bool IsActive => active;
        public bool IsSolved => solved;
        public int NRofPuzzles() => cores;
        public void MasterActivate() { ActivateCalls++; active = true; }
        public void MasterDeactivate() { DeactivateCalls++; active = false; }
        public void AttemptInteract(eChainedPuzzleInteraction interaction)
        {
            Interactions.Add(interaction);
            if (interaction == eChainedPuzzleInteraction.Activate) active = true;
            if (interaction == eChainedPuzzleInteraction.Deactivate) active = false;
        }

        public void OnStateChange() { }
        public void Master_OnPlayerScanChanged(float scanProgress, List<Player.PlayerAgent> playersInScan,
            int inScanMax, bool[] reqObjsInScan) { }
    }

    /// <summary>The level's own puzzle list. The game adds a puzzle to it while building the level, so an empty
    /// list is the state a level with no alarm and no scan is in.</summary>
    public sealed class ChainedPuzzleManager : UnityObjectDouble
    {
        public static ChainedPuzzleManager? Current { get; set; }
        public List<ChainedPuzzleInstance>? m_instances { get; set; } = new();
    }
}

/// <summary>The wave system's own manager and event base, with the identity field the start entry fills in. The
/// manager derives from the interop object base because the game's own does (`Mastermind : GlobalManager`).</summary>
public class Mastermind : UnityObjectDouble
{
    public class MastermindEvent : UnityObjectDouble
    {
        public ushort EventID { get; set; }
        public int Stops;
        public virtual void StopEvent() => Stops++;
    }

    /// <summary>One survival wave: the game's own event type for it, with the stop entry the handle path
    /// calls.</summary>
    public sealed class SurvivalWave : MastermindEvent { }

    public static Mastermind? Current { get; set; }
    public bool Accepts = true;
    public readonly List<(uint Settings, uint Population, string Node)> Triggers = new();
    private readonly Dictionary<ushort, MastermindEvent> _events = new();

    public IReadOnlyDictionary<ushort, MastermindEvent> Events => _events;

    public bool TriggerSurvivalWave(AIGraph.AIG_CourseNode? refNode, uint settingsID, uint populationDataID,
        out ushort eventID)
    {
        eventID = 0;
        if (!Accepts) return false;
        eventID = (ushort)(Triggers.Count + 1);
        Triggers.Add((settingsID, populationDataID, refNode?.Pointer.ToString() ?? ""));
        _events[eventID] = new SurvivalWave { EventID = eventID };
        return true;
    }

    public bool TryGetEvent(ushort eventId, out MastermindEvent? masterMindEvent)
        => _events.TryGetValue(eventId, out masterMindEvent);
}

/// <summary>The level event manager: the one entry every hosted and presented environment row goes through, and
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
}

/// <summary>`eWardenObjectiveType`'s own interface, and the objective instance a layer's chain resolves to.</summary>
public interface IWardenObjective
{
    LevelGeneration.eWardenObjectiveType ObjectiveType { get; }
}

/// <summary>One objective behaviour of the level. The two callbacks are the ones the level-event hooks patch,
/// and the layer is the objective's own layer key.</summary>
public class WardenObjective : IWardenObjective
{
    public LevelGeneration.LG_LayerType Layer { get; set; }
    public LevelGeneration.eWardenObjectiveType ObjectiveType { get; set; }
    public void OnStatusChange(bool isRecall, pWardenObjectiveState state,
        LevelGeneration.eWardenObjectiveStatus oldStatus, LevelGeneration.eWardenObjectiveStatus newStatus,
        LevelGeneration.eWardenSubObjectiveStatus oldSubStatus, LevelGeneration.eWardenSubObjectiveStatus newSubStatus) { }
    public void OnStartInChain() { }
}

/// <summary>The HSU objective component, whose own solved-item callback is the "the sample is in"
/// transition.</summary>
public sealed class WO_HSUFindTakeSample : WardenObjective
{
    public HSU? m_hsu;
    public void OnLocalPlayerSolvedObjectiveItem() { }
}

/// <summary>One HSU unit: the item container's placement identity.</summary>
public sealed class HSU : UnityObjectDouble
{
    public int m_serialNumber;
    public string? m_itemKey;
}

/// <summary>The objective machine's one replicated state, with the accessors every objective read goes
/// through.</summary>
public class pWardenObjectiveState
{
    public LevelGeneration.eWardenObjectiveStatus main_status;
    public LevelGeneration.eWardenSubObjectiveStatus main_subObj;
    public byte main_chainIndex;
    public float main_startTime;
    public float extraTime;
    public bool forceWinOnDeath;
    public bool exitWaveTriggered;
    public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>? ObjectiveItemStates;
    public Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>? RequiredObjectiveItems;

    public LevelGeneration.eWardenObjectiveStatus GetLayerStatus(LevelGeneration.LG_LayerType layer) => main_status;
    public LevelGeneration.eWardenSubObjectiveStatus GetLayerSubStatus(LevelGeneration.LG_LayerType layer, bool onlyGetHelpStatus = false) => main_subObj;
    public int GetChainIndexForLayer(LevelGeneration.LG_LayerType layer) => main_chainIndex;
    public float GetStartTimeFromLayer(LevelGeneration.LG_LayerType layer) => main_startTime;
}

/// <summary>The objective machine: the one replicated state, the chain table the action layer's addresses are
/// checked against, and the callback rows the level-event hooks patch. The state is the machine's own, so a layer
/// with no data still answers a state; `HasWardenObjectiveDataForLayer` is what tells the two apart.</summary>
public class WardenObjectiveManager : UnityObjectDouble
{
    public static WardenObjectiveManager? Current { get; set; }
    /// <summary>The machine's one replicated state. The state itself is the machine's own, so a level with no
    /// objective at all still answers one; `HasWardenObjectiveDataForLayer` is what tells the two apart.</summary>
    public static pWardenObjectiveState? CurrentState { get; set; } = new();

    /// <summary>The chains the current level built, by layer. The action layer only ever asks the lookups below,
    /// so the fixture's table is the whole of the level's objective data.</summary>
    public static readonly Dictionary<LevelGeneration.LG_LayerType, List<int>> Chains = new();
    public static readonly List<LevelGeneration.pWardenObjectiveInteraction> Interactions = new();
    public static readonly List<(LevelGeneration.LG_LayerType Layer, float Time)> ExtraTimes = new();
    public static readonly List<LevelGeneration.LG_LayerType> ForceCompletions = new();
    public static Exception? ThrowOnAttemptInteract;

    public void AttemptInteract(LevelGeneration.pWardenObjectiveInteraction interaction)
    {
        Interactions.Add(interaction);
        if (ThrowOnAttemptInteract != null) throw ThrowOnAttemptInteract;
    }

    public static void SetExtraTime(float time) => ExtraTimes.Add((default, time));
    public static void ForceCompleteObjectiveAll(LevelGeneration.LG_LayerType layer) => ForceCompletions.Add(layer);
    public static bool HasWardenObjectiveDataForLayer(LevelGeneration.LG_LayerType layer) => Chains.ContainsKey(layer);

    public static bool TryGetWardenObjective(LevelGeneration.LG_LayerType layer, int chainIndex,
        out IWardenObjective objective)
    {
        objective = null!;
        if (!Chains.TryGetValue(layer, out var chains) || !chains.Contains(chainIndex)) return false;
        objective = new WardenObjective { Layer = layer };
        return true;
    }

    public void OnLocalPlayerStartExpedition() { }
    public void OnLocalPlayerEnterZone(Player.PlayerAgent player, LevelGeneration.LG_Zone zone) { }

    public static void Reset()
    {
        Current = null; CurrentState = null; Chains.Clear(); Interactions.Clear(); ExtraTimes.Clear();
        ForceCompletions.Clear(); ThrowOnAttemptInteract = null;
    }
}

/// <summary>The checkpoint manager: a process-wide singleton whose own recall callback is the level-event row's
/// "read a checkpoint back" transition.</summary>
public class CheckpointManager : UnityObjectDouble
{
    public static CheckpointManager? Current { get; set; }
    public static int CheckpointUsage { get; set; }
    public static bool IsReloadingCheckpoint { get; set; }
    public void AttemptInteract(LevelGeneration.pCheckpointInteraction interaction) { }
    public void OnRecallComplete() { }
    public static void Reset()
    {
        Current = null; CheckpointUsage = 0; IsReloadingCheckpoint = false;
    }
}

/// <summary>The level's own elevator landing, which owns the exit win-condition item. Both members are the
/// interop's public virtual pair.</summary>
public class ElevatorShaftLanding : UnityObjectDouble
{
    public static ElevatorShaftLanding? Current { get; set; }
    public int ActivateCalls;
    public int DeactivateCalls;
    public virtual void ActivateWinCondition() => ActivateCalls++;
    public virtual void DeactivateWinCondition() => DeactivateCalls++;
    public static void Reset() => Current = null;
}

namespace AssetShards
{
    /// <summary>The game's one asset loader entry: an authored room reference names the prefab object its
    /// `Assets/` path loads, and the identity is the loaded object itself.</summary>
    public static class AssetShardManager
    {
        public static readonly Dictionary<string, UnityEngine.GameObject> Loaded = new(StringComparer.Ordinal);

        public static T? GetLoadedAsset<T>(string path, bool allowNull = false) where T : class
            => Loaded.TryGetValue(path ?? "", out var asset) ? asset as T : null;
    }
}
