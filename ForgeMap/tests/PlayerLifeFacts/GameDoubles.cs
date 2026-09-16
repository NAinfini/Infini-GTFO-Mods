using System.Runtime.CompilerServices;
using ForgeRuntime.Framework;
using UnityEngine;

// Doubles mirror only the interop members the player-life production sources read. Names, namespaces and member
// kinds follow build 20403457 (Modules-ASM / SNet_ASM interop); behaviour is synthetic and NOT game-verified.
// The life registry the fixture drives is the one thing these doubles share: a player's alive flag, its
// locomotion state and the position of its agent are the three reads the production readers make, and each of
// them is answered here exactly as the interop member is read.

public static class Pointers
{
    private static long _next = 0x10000;
    public static IntPtr Next() => new(Interlocked.Increment(ref _next));
}

/// <summary>Unity equality: a destroyed object compares equal to null.</summary>
public abstract class UnityObjectDouble
{
    public IntPtr Pointer { get; set; } = Pointers.Next();
    public bool Destroyed;
    public bool WasCollected => Destroyed;
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

// The two Unity value types sit in Unity's own namespace because the production sources are compiled against
// the real UnityEngine and reach them through `using UnityEngine;`; declared at global scope they would leave
// that using unresolved. Every other Facts project supplies its Unity doubles the same way.
namespace UnityEngine
{
    /// <summary>Unity's Vector3: the production readers read x/y/z as fields, exactly like the interop struct.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }

    public struct Quaternion
    {
        public float x, y, z, w;
        public Quaternion(float x, float y, float z, float w) { this.x = x; this.y = y; this.z = z; this.w = w; }
    }
}

namespace SNetwork
{
    public static class SNet { public static bool IsMaster { get; set; } = true; }
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
}

namespace Agents
{
    /// <summary>`Agent`'s base type in the interop assembly; position and alive live here, and both are virtual
    /// because `PlayerAgent` overrides them. The interop wrapper routes both reads through the il2cpp virtual
    /// method, which is why the alive hook below patches the property rather than the backing field.</summary>
    public class Agent : UnityObjectDouble
    {
        public Vector3 Position { get; set; }
        public virtual bool Alive { get; set; } = true;
        public virtual int PlayerSlotIndex { get; set; }
    }
}

namespace Player
{
    /// <summary>The locomotion machine's own state list, in the game's own order (interop `PLOC_State`, byte
    /// backed); `Downed` is the one member the player-life reading compares against.</summary>
    public sealed class PlayerLocomotion : UnityObjectDouble
    {
        public enum PLOC_State : byte
        {
            Stand, Crouch, Run, Jump, Fall, Land, Stunned, Downed, ClimbLadder, OnTerminal, Melee, Empty,
            GrabbedByTrap, GrabbedByTank, Testing, InElevator, GrabbedByPouncer, StandStill
        }
        public PLOC_State m_currentStateEnum;
        public PLOC_State m_lastStateEnum;
        /// <summary>The state instance the machine is currently in; the downed state is reached through the same
        /// cast the production reader performs.</summary>
        public object? CurrentStateInstance;
        public T? TryCast<T>() where T : class => CurrentStateInstance as T;
    }

    /// <summary>The downed state of the locomotion machine: its owner, its revive flag and its own callbacks.</summary>
    public sealed class PLOC_Downed : UnityObjectDouble
    {
        public PlayerAgent? m_owner;
        public bool m_isRevived;
        public void Enter() { }
        public void SyncEnter() { }
        public void OnPlayerRevived() { }
    }

    /// <summary>The base of every timed interaction. Only the members the revive hooks read are mirrored: the
    /// interacting agent, the interaction's own owner and the player the interaction belongs to — the last one
    /// read through the interop's own getter, as build 20403457 publishes it.</summary>
    public class Interact_Timed : UnityObjectDouble
    {
        public PlayerAgent? Agent;
        public PlayerAgent? m_interactTargetAgent { get; set; }
    }

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
        public Vector3 goodPosition;
    }

    /// <summary>The player's synced health receiver; the fixture keeps it for the identity readbacks the life
    /// half performs, and the production heal path is not part of this slice.</summary>
    public class Dam_SyncedDamageBase : UnityObjectDouble
    {
        public bool IsSetup;
        public float Health;
        public float HealthMax;
    }

    public sealed class Dam_PlayerDamageBase : Dam_SyncedDamageBase
    {
        public PlayerAgent? Owner;
    }

    public sealed class PlayerAgent : Agents.Agent
    {
        public SNetwork.SNet_Player? Owner;
        public Dam_PlayerDamageBase? Damage;
        public PlayerLocomotion? Locomotion;
        public Interact_Revive? ReviveInteraction;
        /// <summary>The one call that moves a player; the production hook is a postfix on it and the body does
        /// nothing, exactly as the other readback cases invoke their postfix directly.</summary>
        public void WarpTo(Vector3 position, Quaternion rotation, pPlayerLocationData locationData) { }
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

    /// <summary>The replication data one spawn carries: the player it names and the position the game spawns
    /// them at. `snetPlayer` is the field the production reader reads.</summary>
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
}

/// <summary>The Map provider's player identity, as the life half reads it. Production reads the module the Map
/// registration recorded; this double is that module's read surface over a synthetic life table, so the half
/// performs the same reads against the same shape and no read has a second implementation.
///
/// The transition side of the world contract belongs to the life half, which the fixture wires to its own entry.
/// Reaching it through the identity would be a second publisher, so it is refused by name.</summary>
namespace ForgeMap.Native
{
    /// <summary>The Map session's own surface as the life hooks reach it. The production `GuardLifeFacts` body is
    /// exactly this: read the half late, run the transition, and publish nothing while no half is attached. Only
    /// that one member is mirrored — every case drives the half directly through the fixture.</summary>
    internal sealed class LifeHost
    {
        internal PlayerLifeFacts? Facts;

        internal void GuardLifeFacts(Action<PlayerLifeFacts> callback)
        {
            if (Facts is { } facts) callback(facts);
        }
    }

    /// <summary>The plugin type the life patch bodies reach: `Session` is the static the game-bound plugin
    /// publishes, and it answers null until a session is attached.</summary>
    internal static class Plugin
    {
        internal static LifeHost? Session { get; set; }
    }

    internal sealed class PlayerIdentityModule : IPlayerLifeWorld
    {
        internal const string EntityKind = "gtfo.player";
        internal static PlayerIdentityModule? Current { get; set; }

        private readonly Dictionary<IntPtr, Entry> _byAgent = new();
        private readonly Dictionary<IntPtr, Entry> _byPlayer = new();
        private readonly List<Entry> _lives = new();
        internal long World;
        internal bool Live = true;

        private sealed record Entry(EntityReference Reference, Agents.Agent Agent, SNetwork.SNet_Player Player);

        public bool Authoritative => Live;

        public IReadOnlyList<string> Journal => Array.Empty<string>();

        internal void Attach(long world)
        {
            World = world;
            _byAgent.Clear(); _byPlayer.Clear(); _lives.Clear();
        }

        internal int LifeCount => _lives.Count;

        internal void Record(EntityReference reference, Agents.Agent agent, SNetwork.SNet_Player player)
        {
            var entry = new Entry(reference, agent, player);
            _byAgent[agent.Pointer] = entry;
            _byPlayer[player.Pointer] = entry;
            _lives.Add(entry);
        }

        /// <summary>Drops one life the way the production readback drops a despawned or replaced one: the
        /// reference stops resolving and no read through the module answers for it any more.</summary>
        internal void Forget(EntityReference reference)
        {
            for (int i = 0; i < _lives.Count; i++)
            {
                if (_lives[i].Reference != reference) continue;
                _byAgent.Remove(_lives[i].Agent.Pointer);
                _byPlayer.Remove(_lives[i].Player.Pointer);
                _lives.RemoveAt(i);
                return;
            }
        }

        public void Reconcile() { }

        /// <summary>Whether this identity still holds one reference. It is the answer the kernel's own entity
        /// check reads: production's Map provider registers the same predicate against `gtfo.player`, so a fact
        /// about a life the identity no longer holds is refused by the kernel and not by the publishing half.</summary>
        public bool IsCurrent(EntityReference reference) => Live && Find(reference) != null;

        /// <summary>The agent one reference names right now, for the fixture's own cases.</summary>
        internal bool TryFind(EntityReference reference, out global::Player.PlayerAgent agent)
        {
            agent = null!;
            if (Find(reference)?.Agent is not global::Player.PlayerAgent found) return false;
            agent = found;
            return true;
        }

        public double[]? Position(EntityReference reference)
            => Live && Find(reference)?.Agent is global::Player.PlayerAgent agent ? Finite(agent.Position) : null;

        /// <summary>The position a teleport argument names. It is the one read that does not go through a recorded
        /// life: the argument is the game's own location struct, read exactly as it stands.</summary>
        public double[]? Position(object? locationData)
            => Live && locationData is global::Player.pPlayerLocationData location ? Finite(location.goodPosition) : null;

        /// <summary>One native agent to the life it is, by the pointer the identity recorded: the same comparison
        /// the production lookup performs.</summary>
        public EntityReference? ReferenceOf(object? instance)
            => instance is Agents.Agent agent && _byAgent.TryGetValue(agent.Pointer, out var entry) ? entry.Reference : null;

        public EntityReference? ReferenceOf(SNetwork.SNet_Player? player)
            => player != null && _byPlayer.TryGetValue(player.Pointer, out var entry) ? entry.Reference : null;

        /// <summary>One read of a life right now: alive from the agent's flag, downed from the locomotion
        /// machine's current state, and the revive that state has already run.</summary>
        public RuntimePlayerLife? LifeOf(EntityReference reference)
        {
            if (!Live || Find(reference)?.Agent is not global::Player.PlayerAgent agent) return null;
            var locomotion = agent.Locomotion;
            bool downed = locomotion != null && locomotion.Pointer != IntPtr.Zero
                && locomotion.m_currentStateEnum == global::Player.PlayerLocomotion.PLOC_State.Downed;
            var state = downed ? locomotion!.CurrentStateInstance as global::Player.PLOC_Downed : null;
            return new RuntimePlayerLife(agent.Alive, downed, state is { m_isRevived: true });
        }

        /// <summary>The actor the downed state's revive interaction carries, or null when there is none.</summary>
        public EntityReference? ReviverOf(EntityReference reference)
        {
            if (!Live || Find(reference)?.Agent is not global::Player.PlayerAgent agent) return null;
            var locomotion = agent.Locomotion;
            if (locomotion == null || locomotion.m_currentStateEnum != global::Player.PlayerLocomotion.PLOC_State.Downed) return null;
            var interaction = (locomotion.CurrentStateInstance as global::Player.PLOC_Downed)?.m_owner?.ReviveInteraction;
            return interaction?.Agent == null ? null : ReferenceOf(interaction.Agent);
        }

        public void BeginWorld() => Attach(World);

        public bool Downed(object? downedState) => throw new NotSupportedException("The identity does not publish transitions.");
        public bool Revived(object? downedState) => throw new NotSupportedException("The identity does not publish transitions.");
        public bool ReviveStarted(object? rescuer, object? target) => throw new NotSupportedException("The identity does not publish transitions.");
        public bool ReviveCancelled(string reason, object? rescuer, object? target) => throw new NotSupportedException("The identity does not publish transitions.");
        public bool Died(bool alive, object? agent) => throw new NotSupportedException("The identity does not publish transitions.");
        public bool Teleported(object? agent, object? destination) => throw new NotSupportedException("The identity does not publish transitions.");
        public bool Respawned(object? spawnData) => throw new NotSupportedException("The identity does not publish transitions.");

        private static double[]? Finite(Vector3 position)
            => float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
                ? new double[] { position.x, position.y, position.z } : null;

        private Entry? Find(EntityReference reference)
        {
            foreach (var entry in _lives) if (entry.Reference == reference) return entry;
            return null;
        }
    }
}
