using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>One player snapshot from explicit native reads on build 20403457. Every read is paired with a
/// re-read of the instance identity, so a life that changed mid-read is refused instead of reported.</summary>
internal static class PlayerObservation
{
    private const string Kind = PlayerIdentityModule.EntityKind;
    private const string Faction = "player";
    private const string LocalPlayerTag = "player.local";
    private const string HostPlayerTag = "player.host";
    private const string BotTag = "player.bot";
    private const string SlotTagPrefix = "player.slot-";
    private const string HealthTag = "hp";
    private const string HealthMaximumTag = "hp.max";

    /// <summary>The one recipient capability this provider serves for a player. It is advertised only where the
    /// reader can also serve it — a verified receiver on a living agent — so a dead player or one without a
    /// readable damage base never claims a heal it could not accept.</summary>
    private static readonly string[] HealReceiver = { "health.heal" };

    private sealed record Health(bool Setup, float Current, float Maximum);

    internal static RuntimeEntitySnapshot? Read(PlayerAgent agent, SNet_Player player, EntityReference reference)
    {
        RuntimeEntityReferences.Validate(reference);
        // The reference is the identity the module already resolved to this exact agent and player; the read
        // below only has to disagree with itself to be refused. A player's slot number is a fact this observer
        // publishes as a tag, not the entity number the module assigned that life.
        var first = Capture(agent, player);
        if (first == null || first != Capture(agent, player)) return null;
        return new RuntimeEntitySnapshot(reference, Kind, Faction, first.LifeState, Tags(player, first.SlotIndex, first.Health),
            first.Alive && first.Health.Setup ? HealReceiver : Array.Empty<string>(),
            new double[] { first.X, first.Y, first.Z });
    }

    private sealed record PlayerSample(IntPtr PlayerPointer, IntPtr AgentPointer, string LifeState, bool Alive, int SlotIndex,
        float X, float Y, float Z, Health Health);

    private static PlayerSample? Capture(PlayerAgent agent, SNet_Player player)
    {
        if (agent == null || agent.Pointer == IntPtr.Zero || player == null || player.Pointer == IntPtr.Zero
            || player.PlayerAgent == null) return null;
        var playerPointer = player.Pointer;
        var agentPointer = agent.Pointer;
        int slotIndex = player.PlayerSlotIndex(), agentSlot = agent.PlayerSlotIndex;
        bool alive = agent.Alive;
        var position = agent.Position;
        if (slotIndex < 0 || slotIndex != agentSlot
            || !float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        var health = ReadHealth(agent);
        // A downed player is still alive in the native health model: PlayerAgent.Alive stays true while
        // PlayerLocomotion reports the Downed state, and being downed ends with either a revive or the
        // dead transition that clears Alive. Reading Alive first keeps "dead" the moment that flag drops.
        string lifeState = !alive ? "dead" : Downed(agent) ? "downed" : "alive";
        // Native reads can trigger teardown or replacement; a changed instance never yields a snapshot.
        if (agent.Pointer != agentPointer || player.Pointer != playerPointer || player.PlayerAgent == null) return null;
        return new PlayerSample(playerPointer, agentPointer, lifeState, alive, slotIndex, position.x, position.y, position.z, health);
    }

    private static Health ReadHealth(PlayerAgent agent)
    {
        var damage = agent.Damage;
        if (damage == null || damage.Pointer == IntPtr.Zero) return new Health(false, 0, 0);
        float current = damage.Health, maximum = damage.HealthMax;
        // The receiver's own setup flag is part of the evidence: a receiver that has not been set up, belongs
        // to another agent, or holds an impossible range is never published as readable health.
        bool setup = damage.IsSetup && damage.Owner != null && damage.Owner.Pointer == agent.Pointer
            && float.IsFinite(current) && float.IsFinite(maximum) && maximum > 0 && current >= 0 && current <= maximum;
        return new Health(setup, current, maximum);
    }

    /// <summary>Downed is the locomotion machine's own state; it is only consulted for a living agent, so a dead
    /// player is never reported as downed even if the state machine stopped on its last state.</summary>
    private static bool Downed(PlayerAgent agent)
    {
        var locomotion = agent.Locomotion;
        return locomotion != null && locomotion.Pointer != IntPtr.Zero
            && locomotion.m_currentStateEnum == PlayerLocomotion.PLOC_State.Downed;
    }

    /// <summary>Position is the agent's own value. Health is reported as two tags because the shared snapshot
    /// contract carries no health field: a health reader without a verified receiver publishes neither tag.</summary>
    private static IReadOnlyList<string> Tags(SNet_Player player, int slotIndex, Health health)
    {
        var tags = new List<string>(6);
        if (player.IsLocal) tags.Add(LocalPlayerTag);
        if (player.IsMaster) tags.Add(HostPlayerTag);
        if (player.IsBot) tags.Add(BotTag);
        tags.Add(SlotTagPrefix + slotIndex.ToString(CultureInfo.InvariantCulture));
        if (health.Setup)
        {
            tags.Add(HealthTag + "." + Amount(health.Current));
            tags.Add(HealthMaximumTag + "." + Amount(health.Maximum));
        }
        return tags;
    }

    private static string Amount(float value) => value.ToString("0.00", CultureInfo.InvariantCulture);
}
