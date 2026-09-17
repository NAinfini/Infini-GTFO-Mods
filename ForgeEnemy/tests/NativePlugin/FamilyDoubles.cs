// Stand-ins for the game types the rest of the package's native surface names. This suite compiles the whole
// provider — its statement is the plugin entry point, the session lifetime and the install table, and the install
// table spans every hook family — so it needs the union of the shapes the family suites declare one at a time,
// plus the two no family suite ever doubled: the course-node lookup and the noise write, whose declarations are
// recorded in `ForgeEnemy/evidence/enemy-behavior-ledger.json`.
//
// Member kinds follow build 20403457 (interop metadata and the dump); behaviour is synthetic and NOT
// game-verified. Only the members the compiled production sources read are declared, and the suites that own a
// family keep the fuller double for it: what is here is what compiling the whole package needs, no more.
namespace UnityEngine
{
    /// <summary>`Color`: the value `NavMarker.SetColor` takes. The mark row builds it from the plan's own
    /// red/green/blue components plus the opacity, so the four-argument constructor is the whole surface.</summary>
    public struct Color
    {
        public float r, g, b, a;
        public Color(float r, float g, float b, float a) { this.r = r; this.g = g; this.b = b; this.a = a; }
    }

    /// <summary>`Vector3`'s three-argument constructor: the move row builds a goal from the plan's own position
    /// port rather than passing one through.</summary>
    public partial struct Vector3
    {
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}

namespace UnityEngine.AI
{
    /// <summary>The navigation wrapper's native identity, compared after a move write to prove the receiver did
    /// not change.</summary>
    public partial class INavigation { public IntPtr Pointer = new(30); }
}

namespace AIGraph
{
    /// <summary>The noise-maker handle `NM_NoiseData` carries. The one this package submits is null — the row
    /// names no maker, because a plan's noise is emitted by the world rather than by an object — so the interface
    /// has no members here.</summary>
    public interface INM_NoiseMaker { }

    /// <summary>`NM_NoiseType` (evidence `enemy-behavior-ledger.json`, `NM_NoiseType.cs`): the stimulus kinds the
    /// noise manager understands, in the build's own order. `Detectable` is the only one this package submits.</summary>
    public enum NM_NoiseType : byte { InstaDetect, Detectable, Investigate, PulseOnly }

    /// <summary>The level's reality index. Every entity a plan can name is an agent in `Reality`; the other
    /// members are the level's alternate realities and are never named by this package.</summary>
    public enum eDimensionIndex { Reality = 0 }

    /// <summary>`NM_NoiseData`, with the build's own constructor argument list (evidence
    /// `enemy-behavior-ledger.json`, `NM_NoiseData..ctor`). The fields are kept so a case can read back exactly
    /// what the row submitted.</summary>
    public struct NM_NoiseData
    {
        public INM_NoiseMaker? NoiseMaker;
        public UnityEngine.Vector3 Position;
        public float RadiusMin, RadiusMax, YScale;
        public Enemies.AIG_CourseNode? Node;
        public NM_NoiseType Type;
        public bool IncludeToNeightbourAreas, RaycastFirstNode;

        public NM_NoiseData(INM_NoiseMaker? noiseMaker, UnityEngine.Vector3 position, float radiusMin,
            float radiusMax, float yScale, Enemies.AIG_CourseNode? node, NM_NoiseType type,
            bool includeToNeightbourAreas, bool raycastFirstNode)
        {
            NoiseMaker = noiseMaker;
            Position = position;
            RadiusMin = radiusMin;
            RadiusMax = radiusMax;
            YScale = yScale;
            Node = node;
            Type = type;
            IncludeToNeightbourAreas = includeToNeightbourAreas;
            RaycastFirstNode = raycastFirstNode;
        }
    }

    /// <summary>The game's only noise write point. The double records the submission; whether the game's own
    /// listening, occlusion and propagation then run is not modelled here.</summary>
    public static class NoiseManager
    {
        public static int Calls;
        public static NM_NoiseData Last;
        public static void MakeNoise(NM_NoiseData noiseData) { Calls++; Last = noiseData; }
    }
}

namespace Agents
{
    /// <summary>The agent mode the behaviour and control rows read as an integer (`EnemyAI.m_mode`). The members
    /// are the build's own; no compiled source names one by name, every read is a cast to `int`.</summary>
    public enum AgentMode : byte { Off, Agressive, Patrolling, Scout, Hibernate }

    /// <summary>`pEnemyAgent` (evidence: `EnemyNodeFacts` reads the tag packet's handle): the native handle the
    /// tag packet carries in place of an agent, asked for the instance it names. A handle whose agent was torn
    /// down between the action and the postfix answers false.</summary>
    public sealed class pEnemyAgent
    {
        public Enemies.EnemyAgent? Value;
        public bool TryGet(out Enemies.EnemyAgent? comp) { comp = Value; return Value != null; }
        public void Set(Enemies.EnemyAgent comp) => Value = comp;
    }
}

namespace Enemies
{
    /// <summary>`EnemyAbility`: one entry of `EnemyAbilities.AllComps`. `Pointer` is the interop wrapper's native
    /// identity (the row compares it to decide a submitted ability is still running), `AbilityIsDone` is the
    /// component's own completion test, and `m_abilityType` is the kind the index scan matches.</summary>
    public class EnemyAbility
    {
        public IntPtr Pointer = IntPtr.Zero;
        public Agents.AgentAbility m_abilityType = Agents.AgentAbility.None;
        public bool Done;
        public virtual bool AbilityIsDone() => Done;
    }

    /// <summary>One spawned group: `Pointer` is the native identity the wave bookkeeping compares, `Members` is
    /// the native member list a batch harvests.</summary>
    public sealed class EnemyGroup
    {
        public IntPtr Pointer { get; set; }
        public List<EnemyAgent>? Members { get; set; } = new();
    }

    /// <summary>`ES_HitreactBase` (evidence: the stagger and interrupt rows): the enemy's own reaction machine.
    /// The gate and the write are its two members; `CurrentReactionType` is the readback that decides whether a
    /// submission is reported as committed or as unseen.</summary>
    public class ES_HitreactBase : ES_Base
    {
        public new IntPtr Pointer = new(16);
        public ES_HitreactType CurrentReactionType { get; protected set; } = ES_HitreactType.None;
        public bool HitreactForbidden { get; set; }
        public virtual bool CanHitreact(ES_HitreactType suggestedType, bool overrideTimer = false) => true;
        public virtual void ActivateState(ES_HitreactType hitreactType, ImpactDirection impactDirection,
            bool attackerIsPlayer, Agents.Agent? attacker, UnityEngine.Vector3 damagePos,
            DamageNoiseLevel noiseLevel = DamageNoiseLevel.Normal)
            => CurrentReactionType = hitreactType;
    }

    /// <summary>`ES_HibernateWakeUp`: the wake-up the awaken row submits through the enemy's locomotion. Its
    /// signature is the awaken member's own declaration — direction, distance, delay, propagated flag.</summary>
    public class ES_HibernateWakeUp : ES_Base
    {
        public new IntPtr Pointer = new(17);
        public int Activations;
        public virtual void ActivateState(UnityEngine.Vector3 direction, float distance, float delay,
            bool isPropagatedWakeup) => Activations++;
    }

    /// <summary>The enemy half of the doubles the rest of the package's native surface reads: the model object a
    /// mark tracks, the two target propagations, the course-node lookup, the two attack-state questions and the
    /// ability table. They are declared here rather than in the suite's base `GameDoubles.cs` because that file is
    /// shared with `LifecycleFacts`, whose own statement is the lifecycle family and which compiles neither the
    /// combat, the control, the foam nor the behaviour family.</summary>
    public sealed partial class EnemyAgent
    {
        public UnityEngine.GameObject? Model;
        public UnityEngine.GameObject? MainModelGO => Model;
        public int FullPropagations;
        public int LimitedPropagations;
        public Agents.Agent? LastTarget;
        public float LastChance = -1f;
        public bool LimitedResult = true;
        public void PropagateTargetFull(Agents.Agent agent) { FullPropagations++; LastTarget = agent; }
        public bool PropagateTargetLimited(Agents.Agent agent, float chance)
        { LimitedPropagations++; LastTarget = agent; LastChance = chance; return LimitedResult; }
    }

    public sealed partial class AIG_CourseNode
    {
        public IntPtr Pointer = new(20);
        /// <summary>The node a case says the game would resolve for a position; a null one is the position no node
        /// covers, which is what the noise row refuses on. The real lookup walks the level's own graph.
        /// (Evidence `enemy-behavior-ledger.json`, `AIGraph.AIG_CourseNode.TryGetCourseNode`.)</summary>
        public static AIG_CourseNode? Resolvable;
        public static bool TryGetCourseNode(AIGraph.eDimensionIndex dimensionIndex, UnityEngine.Vector3 worldPos,
            float sampleDistance, out AIG_CourseNode? node)
        { node = Resolvable; return Resolvable != null; }
    }

    public sealed partial class AIG_Zone
    {
        /// <summary>The interop's own liveness flag: a level object the game destroyed mid-read is collected,
        /// which is the one way a zone read fails after it has already produced an object.</summary>
        public bool WasCollected { get; set; }
    }

    public sealed partial class AIG_Layer { public bool WasCollected { get; set; } }

    public sealed partial class EnemyAI
    {
        /// <summary>The mode the control rows read as an integer (`ai.m_mode`), and the navigation goal every
        /// native move behaviour writes: writing it is how the game itself tells an enemy where to walk.</summary>
        public Agents.AgentMode m_mode = Agents.AgentMode.Patrolling;
        public UnityEngine.Vector3 NavmeshAgentGoal;
    }

    public partial class EnemyBehaviour
    {
        /// <summary>The state-machine write the sleep row submits; it writes the same field the readback reads.
        /// (Evidence: `EnemyBehaviour.ChangeState`, RVA 0x1580A6A.)</summary>
        public void ChangeState(EB_States state) => m_currentStateName = state;
    }

    public sealed partial class EnemyLocomotion
    {
        /// <summary>The wrapper's native identity, compared after a commit to prove the receiver did not change,
        /// and the state machines the combat and control rows submit through: the one reaction machine an enemy
        /// has, the wake-up receiver, and the four attack states the build puts on an enemy.</summary>
        public IntPtr Pointer = new(18);
        public ES_HitreactBase Hitreact = null!;
        public ES_HibernateWakeUp HibernateWakeup = null!;
        public ES_EnemyAttackBase? StrikerAttack;
        public ES_EnemyAttackBase? TankAttack;
        public ES_EnemyAttackBase? TankMultiTargetAttack;
        public ES_EnemyAttackBase? ShooterAttack;
    }

    public sealed partial class EnemyAbilities
    {
        /// <summary>`AllComps` is the flat component table the ability row scans for the index the native trigger
        /// takes, and `UseAbility(AgentAbility, index)` is the trigger itself.</summary>
        public EnemyAbility[] AllComps = System.Array.Empty<EnemyAbility>();
        public int UseAbilityCalls;
        public bool UseAbilityAnswer = true;
        public bool UseAbility(Agents.AgentAbility ability, int index) { UseAbilityCalls++; return UseAbilityAnswer; }
    }

    public partial class ES_EnemyAttackBase
    {
        /// <summary>The two questions an attack state answers, which is how the stagger row decides whether the
        /// enemy is already mid-attack. A case sets the answers through the fields the getters read.</summary>
        public bool Performing;
        public bool Charging;
        public Action? OnAttackStateRead;
        public virtual bool IsPerformingAttack() { OnAttackStateRead?.Invoke(); return Performing; }
        public virtual bool IsChargingAttack() { OnAttackStateRead?.Invoke(); return Charging; }
    }
}

public sealed partial class Dam_EnemyDamageBase
{
    /// <summary>The foam family's write entries and the game's own unconditional end-of-life entry
    /// (`InstantDead(bool force = false)`, evidence `e10-damage-window.json` and `enemy-node-facts.json`).
    /// `Kills` is whether this double's call really ends the life, which is what a case turns off to model an
    /// entry point whose effect cannot be observed.</summary>
    public int Removals;
    public float RemovedVolume;
    public int Clears;
    public int InstantDeaths;
    public bool Kills = true;
    public Action<Dam_EnemyDamageBase>? OnInstantDead;
    public void RemoveGlueVolume(float deltaToRemove, float duration = 0f)
    {
        Removals++;
        RemovedVolume += deltaToRemove;
        // The field, not the property: a case that makes the read misbehave is modelling the readback, not the
        // receiver's own arithmetic.
        _attachedGlueVolume = Math.Max(0f, _attachedGlueVolume - deltaToRemove);
    }
    public void ClearGlue() { Clears++; _attachedGlueVolume = 0f; }
    public void InstantDead(bool force = false)
    {
        InstantDeaths++;
        OnInstantDead?.Invoke(this);
        if (Kills && Owner != null) Owner.Alive = false;
    }
}

namespace SNetwork
{
    public static partial class SNet { public static SessionHub? SessionHub; }
}

/// <summary>The native component base both state machines derive from; its interop wrapper carries the pointer.</summary>
public class ES_Base { public IntPtr Pointer = IntPtr.Zero; }

/// <summary>`ES_HitreactType` is a top-level type in `Modules-ASM.dll`; the members and their order are the
/// build's own. `ToDeath` and `InstantRagdollDeath` are kills and are deliberately not offered as reactions.</summary>
public enum ES_HitreactType : byte { Unspecified, None, Micro, Light, Heavy, ToDeath, InstantRagdollDeath }

/// <summary>The direction an impact came from. The stagger shape has no direction port, so the rows pass
/// `Unspecified`; the member exists so a case can prove exactly that.</summary>
public enum ImpactDirection : byte { Unspecified, Front, Back, Right, Left }

public enum DamageNoiseLevel : byte { Normal, Low }

/// <summary>The marker a Forge mark builds. The double records the colour it was set to, which is the one reading
/// the mark row has: the build exposes no colour getter on `NavMarker`.</summary>
public sealed class NavMarker
{
    public UnityEngine.GameObject? Tracking;
    public UnityEngine.Color? Color;
    public int ColorCalls;
    public void SetColor(UnityEngine.Color col) { Color = col; ColorCalls++; }
    public void SetTrackingObject(UnityEngine.GameObject obj) => Tracking = obj;
}

[Flags]
public enum NavMarkerOption : ulong
{
    None = 0uL, Waypoint = 2uL, Distance = 8uL, Title = 0x10uL, Enemy = 0x80uL,
    EnemyDistance = Distance | Enemy, EnemyTitleDistance = EnemyDistance | Title
}

/// <summary>The game's own marker layer. Every placement records what it was asked for, so a case can prove the
/// option, the tracking object and the placement count a mark produced.</summary>
public sealed class NavMarkerLayer
{
    public int Placements;
    public int Removals;
    public NavMarkerOption? LastOption;
    public UnityEngine.GameObject? LastTracking;
    public float LastDestroyDelay = -1f;
    public bool ReturnNull;
    public readonly List<NavMarker> Placed = new();

    public NavMarker? PlaceCustomMarker(NavMarkerOption type, UnityEngine.GameObject? trackingObj, string? name,
        float destroyDelay = 0f, bool debug = false)
    {
        Placements++;
        LastOption = type;
        LastTracking = trackingObj;
        LastDestroyDelay = destroyDelay;
        if (ReturnNull) return null;
        var marker = new NavMarker { Tracking = trackingObj };
        Placed.Add(marker);
        return marker;
    }

    public void RemoveMarker(NavMarker? marker) { Removals++; }
}

public static class GuiManager
{
    public static NavMarkerLayer? NavMarkerLayer;
}

/// <summary>The tag transaction the observation hooks. `pTagEnemy` is a struct holding the handle the build
/// declares, and nothing else.</summary>
public static class ToolSyncManager
{
    public struct pTagEnemy
    {
        public Agents.pEnemyAgent enemy;
    }

    public static int DoTagEnemyCalls;
    public static pTagEnemy LastTagged;
    public static void DoTagEnemy(pTagEnemy data) { DoTagEnemyCalls++; LastTagged = data; }
}

/// <summary>The Mastermind singleton and the event base every wave is. Both are in the global namespace in the
/// game's own assembly, and `MastermindEvent` is nested inside `Mastermind`. The group list after the
/// Mastermind's own maintenance is the native answer to whether a wave still has anything alive.</summary>
public class Mastermind
{
    public class MastermindEvent
    {
        public ushort EventID { get; set; }
    }

    public static Mastermind? Current { get; set; }
    public List<Enemies.EnemyGroup>? m_activeGroups { get; set; } = new();
    public int Maintenances;
    public void MaintainGroups() => Maintenances++;
}

/// <summary>A survival wave: the event base plus the native pointer the bookkeeping keys its own table with. The
/// five members the hooks patch are the ones the wave hooks declare: the spawn callback, the private per-batch
/// group step, the group delivery, the wave's own completion test and the teardown callback.</summary>
public sealed class SurvivalWave : Mastermind.MastermindEvent
{
    public IntPtr Pointer { get; set; }
    public int SpawnGroups;
    public int RegisteredGroups;
    public bool EndAnswer;
    public void OnSpawn() { }
    public void SpawnGroup() => SpawnGroups++;
    public void RegisterGroup(Enemies.EnemyGroup group) => RegisteredGroups++;
    public bool TryEndEvent() => EndAnswer;
    public void OnDespawn() { }
}

namespace SNetwork
{
    /// <summary>The realm's own player list, the audience a `presentation` step is addressed with. Only the two
    /// members the audience read touches are modelled — the list and each player's `Lookup` key; the zero key is
    /// the game's own "no account" spelling and is skipped by the caller.</summary>
    public sealed class SessionHub
    {
        public List<SessionPlayer>? PlayersInSession = new();
    }

    public sealed class SessionPlayer
    {
        public ulong Lookup;
    }
}

/// <summary>The game's glue entry points (evidence `ForgeEnemy/evidence/enemy-glue-hooks.json`): the manager's own
/// sync-id sequence and the spawn that replicates the foam to the named enemy's receiver. The volume a spawn
/// leaves behind lands on that receiver, because the real entry point replicates to the enemy and not to a shared
/// counter.</summary>
public static class ProjectileManager
{
    public static uint NextSyncID = 1;
    public static int Spawns;
    public static float ExpandAttached;
    public static float MultiplierSeen;
    public static int LimbSeen = int.MinValue;
    public static Action? OnSpawnGlueOnEnemyAgent;

    public static uint GetNextSyncID() => NextSyncID++;

    public static void WantToSpawnGlueOnEnemyAgent(uint syncID, Enemies.EnemyAgent enemy, int limbID,
        UnityEngine.Vector3 localPos, GlueVolumeDesc volumeDesc, float effectMultiplier)
    {
        Spawns++;
        LimbSeen = limbID;
        MultiplierSeen = effectMultiplier;
        ExpandAttached = volumeDesc.expandVolume;
        if (enemy?.Damage != null) enemy.Damage.AttachedGlueVolume += volumeDesc.volume;
        OnSpawnGlueOnEnemyAgent?.Invoke();
    }
}
