using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>
/// The one conversion between a player and the address that player is on, and the one player-session
/// table this provider's presentation rows are routed by. A session id is what a presentation command is
/// addressed to (`NetworkCommandRouting.PresentationRequest` writes the address into the request's endpoint slot
/// and `IsPresentationRequest` compares it with the address of the machine that receives it), and the game's own
/// answer to "which player is this" is its player slot, `SNet_Player.PlayerSlotIndex` — the number the game itself
/// addresses a player by. `SNet_Player.Lookup` is the account id and stays what it is elsewhere in this package:
/// a private identity key that is compared and never spelled.
///
/// Three things need the conversion. `a-p-say` names its speaker by player entity while the game's own voice
/// entry takes the speaker's slot index, so a command that arrives with an entity has to be turned into the
/// number the entry takes. `a-hud`'s `audience=self` shows one player's value on that player's own machine, so
/// the client that receives the command has to be able to ask whether the player the value belongs to is the
/// one sitting at this keyboard. And the environment rows are routed to a session list, which is the hub's own
/// player list when a step names no recipient in particular.
///
/// The entity half is <see cref="PlayerIdentityModule"/>'s: the agent the reference resolves to, then the
/// `SNet_Player` that agent's own `Owner` points at. This is the one table the runtime's `PlayerSessions`
/// registration is filled from — the environment rows and the player rows are routed by the same answer, so
/// there is no second list of sessions anywhere in this package.
/// </summary>
internal static class PlayerSessions
{
    /// <summary>The production wiring: the identity module's own current-agent lookup, read late so a question
    /// asked before that module is attached names nobody rather than answering from a stale table. A focused test
    /// installs its own lookup through <see cref="UseAgents"/>, because the module needs a registration, a kernel
    /// and native state that a unit case has no way to build.</summary>
    private static Func<EntityReference, PlayerAgent?> _agents
        = reference => PlayerIdentityModule.Current?.CurrentAgent(reference);

    /// <summary>The lookup a focused case installs in place of the identity module's.</summary>
    internal static void UseAgents(Func<EntityReference, PlayerAgent?>? agents)
        => _agents = agents ?? (reference => PlayerIdentityModule.Current?.CurrentAgent(reference));

    /// <summary>The session a player entity is on right now, or null when this process cannot name it: no identity
    /// half is wired, the reference is not a current life, the agent has no linked player, or the game has no slot
    /// for the player. Null is the refusal — a presentation addressed to a session nobody can name is not a
    /// broadcast, and the callers refuse it by their own name rather than defaulting to everyone. The conversion is
    /// <see cref="SlotOf"/>'s, so a step's recipients and `audience=self` name sessions the same way.</summary>
    internal static string? SessionOf(EntityReference reference)
        => SlotOf(reference) is var slot && slot >= 0 ? slot.ToString(CultureInfo.InvariantCulture) : null;

    /// <summary>The game's own slot index of a player entity, or -1 when this process cannot name one. The index
    /// is read from the game rather than derived from the session: the voice entry, the presentation address and
    /// the routing are three spellings of the same fact, and this is the one that reads it.</summary>
    internal static int SlotOf(EntityReference reference)
    {
        var player = PlayerOf(reference);
        if (player == null) return -1;
        try { return player.PlayerSlotIndex(); }
        catch (Exception) { return -1; }
    }

    /// <summary>Whether this machine is the one the named player is sitting at. It is the local-session
    /// comparison the network layer performs for an inbound presentation request, asked here about the player the
    /// step carries: the local player has to exist and it has to be the same `SNet_Player` the reference resolves
    /// to.</summary>
    internal static bool IsLocal(EntityReference reference)
    {
        if (!SNet.HasLocalPlayer) return false;
        var local = SNet.LocalPlayer;
        var player = PlayerOf(reference);
        return local != null && player != null && local.Pointer == player.Pointer;
    }

    /// <summary>The `SNet_Player` a recorded life belongs to, or null when this process cannot reach it. The
    /// identity module answers with the agent; the agent's own `Owner` is the player the life was recorded for,
    /// and the comparisons above are made against that object, never against a copy of its key.</summary>
    private static SNet_Player? PlayerOf(EntityReference reference)
    {
        var agent = _agents(reference);
        if (agent == null) return null;
        var owner = agent.Owner;
        return owner != null && owner.Pointer != IntPtr.Zero ? owner : null;
    }

    /// <summary>The sessions one presentation step is addressed to, from the entity references the step's own
    /// `recipients` port carries. It is the one answer the runtime's presentation routing asks this provider for,
    /// so an environment row and a player row are routed by the same conversion: the entity a plan named, the
    /// player it resolves to, and that player's own session id. A named recipient that cannot be converted refuses
    /// the whole answer — addressing the rest of the list would present a step to players the plan did not ask for
    /// — and null is what the routing refuses by name instead of widening the step to everyone. The conversion is
    /// the one above, so a step's recipients and `audience=self` name sessions the same way.</summary>
    internal static IReadOnlyList<string>? SessionsOf(IReadOnlyList<EntityReference>? recipients)
    {
        if (recipients == null || recipients.Count == 0) return null;
        // A map object is not a player, and the one presentation row of this provider that names one — the
        // interaction-prompt row — draws on every machine that might look at that object: the prompt belongs to
        // whichever player is aiming at it, and this layer cannot know which one that will be. So a step whose
        // recipients are all map objects is addressed to every player in the level, which is the only conversion
        // that puts the rule on the machine that will draw the prompt. A step that mixes a map object with a
        // player is still converted one reference at a time below, and refuses as before.
        if (AllMapObjects(recipients)) return EverySession();
        var sessions = new List<string>(recipients.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var reference in recipients)
        {
            if (SessionOf(reference) is not { } session) return null;
            if (seen.Add(session)) sessions.Add(session);
        }
        sessions.Sort(StringComparer.Ordinal);
        return sessions;
    }

    /// <summary>Whether every reference names a map object of this provider. The kind is the one prefix every
    /// map-object reference carries, read here rather than through a category parser: routing asks which entity
    /// namespace a step addresses, not which category inside it.</summary>
    private static bool AllMapObjects(IReadOnlyList<EntityReference> recipients)
    {
        foreach (var reference in recipients)
            if (reference?.Id == null
                || !reference.Id.StartsWith(MapObjectModule.EntityKind + ":", StringComparison.Ordinal)) return false;
        return true;
    }

    /// <summary>Every session this machine can name in the level, or null when it can name none. This is the
    /// address list a presentation step about a map object is routed with: the game's own player list, read the
    /// way the identity half reads it, so a player who is in the level is addressed and one who is not is not.</summary>
    internal static IReadOnlyList<string>? EverySession()
    {
        var agents = PlayerManager.PlayerAgentsInLevel;
        int count = agents == null ? 0 : agents.Count;
        var sessions = new SortedSet<string>(StringComparer.Ordinal);
        for (int index = 0; index < count; index++)
        {
            var agent = agents![index];
            if (agent == null) continue;
            var player = agent.Owner;
            if (player == null || player.Pointer == IntPtr.Zero) continue;
            int slot;
            try { slot = player.PlayerSlotIndex(); }
            catch (Exception) { continue; }
            if (slot >= 0) sessions.Add(slot.ToString(CultureInfo.InvariantCulture));
        }
        return sessions.Count == 0 ? null : new List<string>(sessions);
    }
}
