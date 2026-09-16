using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

// The game types this slice's production sources are compiled against. Every member is one the production code
// actually reads or calls, declared with the interop assembly's own shape so a renamed or re-typed member fails
// here; the bodies record what the production code did, which is what the action cases assert on.

namespace UnityEngine
{
    /// <summary>One native position. Only the three coordinates the production reads use.</summary>
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static Vector3 zero => new(0, 0, 0);
    }
}

namespace Player
{
    /// <summary>One native agent: the one member the damage entry takes.</summary>
    public class Agent
    {
        public IntPtr Pointer { get; set; }
    }

    /// <summary>One player agent: the members the player half reads, and the damage receiver behind them.</summary>
    public class PlayerAgent : Agent
    {
        public bool Alive { get; set; } = true;
        public UnityEngine.Vector3 Position { get; set; }
        public Dam_PlayerDamageBase? Damage { get; set; }
    }

    /// <summary>The player damage receiver: the health state the reads use and the three native entries the
    /// actions submit through. Each entry records the call and applies the state the game's own body would.</summary>
    public class Dam_PlayerDamageBase
    {
        public IntPtr Pointer { get; set; }
        public PlayerAgent? Owner { get; set; }
        public bool IsSetup { get; set; } = true;
        public float Health { get; set; } = 100f;
        public float HealthMax { get; set; } = 100f;
        public float Infection { get; set; }
        public List<string> Calls { get; } = new();

        /// <summary>The packet-sending half of the damage path. The amount that lands is the amount the caller
        /// passed, bounded by the health the receiver holds — the shape the game's own receive path has.</summary>
        public void BulletDamage(float dam, Agent? sourceAgent, UnityEngine.Vector3 position, UnityEngine.Vector3 direction,
            UnityEngine.Vector3 normal, bool allowDirectionalBonus = false, float staggerMulti = 1f, float precisionMulti = 1f,
            uint gearCategoryId = 0u)
        {
            Calls.Add("bullet:" + dam.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Health = Math.Max(0, Health - dam);
        }

        public void SendSetDead(bool allowRevive = true)
        {
            Calls.Add("dead:" + allowRevive);
            Health = 0;
            if (Owner != null) Owner.Alive = false;
        }

        public void SendSetHealth(float health)
        {
            Calls.Add("health:" + health.ToString(System.Globalization.CultureInfo.InvariantCulture));
            Health = health;
        }
    }
}

namespace Agents
{
    /// <summary>The game's own host-authoritative revive action: the one entry the revive row submits through.</summary>
    public static class AgentReplicatedActions
    {
        public static Action<Player.PlayerAgent, Player.PlayerAgent, UnityEngine.Vector3>? Revive { get; set; }

        public static void PlayerReviveAction(Player.PlayerAgent target, Player.PlayerAgent source, UnityEngine.Vector3 where)
            => Revive?.Invoke(target, source, where);
    }
}

namespace ForgeMap.Native
{
    using Player;

    /// <summary>One read of one player life, as the life world reports it.</summary>
    internal readonly record struct RuntimePlayerLife(bool Alive, bool Downed, bool Revived);

    /// <summary>The Map provider's player identity, as the action layer reaches it: the reference one native agent
    /// is recorded under, the life state of a reference, and the authority gate a commit asks. The production
    /// member set — including which members are internal — is repeated here, so a case that drives the real
    /// handlers exercises the same surface the game does.</summary>
    internal sealed class PlayerIdentityModule
    {
        internal const string EntityKind = "gtfo.player";
        internal static PlayerIdentityModule? Current { get; set; }

        private readonly Dictionary<string, PlayerAgent> agents = new(StringComparer.Ordinal);
        private readonly Dictionary<string, RuntimePlayerLife> lives = new(StringComparer.Ordinal);

        internal bool IsRegistered { get; set; } = true;
        public bool Authoritative { get; set; } = true;
        internal bool CanCommit => IsRegistered && Authoritative;

        internal long World { get; set; } = 1;
        internal long Life { get; set; } = 1;

        /// <summary>Records one life under one reference, as the spawn readback would.</summary>
        internal void Record(EntityReference reference, PlayerAgent agent)
        {
            agents[reference.Id] = agent;
            lives[reference.Id] = new RuntimePlayerLife(agent.Alive, false, false);
        }

        internal void SetLife(EntityReference reference, bool alive, bool downed)
            => lives[reference.Id] = new RuntimePlayerLife(alive, downed, false);

        public void Reconcile() { }

        /// <summary>The life as the identity reads it right now: the alive flag is the agent's own, exactly as
        /// the native identity reads it, and the downed flag is what the fixture recorded.</summary>
        public RuntimePlayerLife? LifeOf(EntityReference reference)
            => agents.TryGetValue(reference.Id, out var agent)
                ? new RuntimePlayerLife(agent.Alive, lives[reference.Id].Downed, lives[reference.Id].Revived)
                : null;

        internal PlayerAgent? CurrentAgent(EntityReference reference)
            => agents.TryGetValue(reference.Id, out var agent) ? agent : null;

        internal bool IsCurrent(EntityReference reference) => agents.ContainsKey(reference.Id);
    }
}
