using System;
using System.Collections.Generic;
using GameEvent;
using HarmonyLib;
using Player;
using UnityEngine;

namespace ForgeMap.Native;

/// <summary>The two native entries the player's own events are read from.
///
/// The game announces these events through one funnel: `GameEventManager.PostEvent` is handed the event, the
/// player it is about and the two scalar payloads the event may carry. It is patched as a prefix, so every event
/// this half names is published with the values the game posted and nothing is reconstructed. The event names the
/// player but not the position of a ping; a ping's position comes from the pinging agent's own `m_pingPos`, which
/// only a `LocalPlayerAgent` has — a ping event about an agent that is not the local one publishes nothing,
/// because both the row's position port and the row itself are about a ping that was made here.
///
/// Both entries are prefixes because neither waits for the patched body: a posted event is the game's own
/// announcement of something that already happened, and a ping's marker is set with the position the row
/// carries. Reading them first is also what makes the read independent of whatever a listener the body calls
/// does with the event afterwards. The interop bodies of both members only marshal their arguments into the
/// native call and change none of them, so the values read here are the values the game posted.
///
/// The second entry is what makes the host able to see a client's ping at all: `GuiManager.AttemptSetPlayerPingStatus`
/// is called with the pinging agent and the world position, which is exactly the row's two required ports in one
/// call. Both entries are installed together and the observation half de-duplicates them per life, per tick and
/// per position, so a machine that reaches both publishes one fact.
///
/// The mapping from a game event to a row — and the events that are deliberately not mapped — is frozen in
/// `evidence/player-event-facts.json`.</summary>
[HarmonyPatch(typeof(GameEventManager), nameof(GameEventManager.PostEvent),
    new[] { typeof(eGameEvent), typeof(PlayerAgent), typeof(float), typeof(string), typeof(Il2CppSystem.Collections.Generic.Dictionary<string, string>) })]
internal static class PlayerGameEventPosted
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(eGameEvent e, PlayerAgent player)
    {
        if (player == null) return;
        Publish(e, player);
    }

    /// <summary>One event's own transition, run on the session that owns the registration: the game-event
    /// funnel carries all of these rows, so one guard per event is what keeps a failed session from publishing
    /// through a registration that never carried the bindings. An event this half does not name publishes
    /// nothing.</summary>
    private static void Publish(eGameEvent e, PlayerAgent player)
    {
        switch (e)
        {
            case eGameEvent.player_low_health:
                Plugin.Session?.GuardState(state => PlayerEventHooks.LowHealth(state, player));
                break;
            case eGameEvent.player_apply_medikit:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.SupplyUsed(events, player, "medikit"));
                break;
            case eGameEvent.player_apply_ammokit:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.SupplyUsed(events, player, "ammokit"));
                break;
            case eGameEvent.player_apply_disinfection:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.SupplyUsed(events, player, "disinfection"));
                break;
            case eGameEvent.player_apply_toolRefill:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.SupplyUsed(events, player, "tool_refill"));
                break;
            case eGameEvent.player_pickup_medikit:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "medikit"));
                break;
            case eGameEvent.player_pickup_ammokit:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "ammokit"));
                break;
            case eGameEvent.player_pickup_toolRefill:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "tool_refill"));
                break;
            case eGameEvent.player_pickup_artifact:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "artifact"));
                break;
            case eGameEvent.player_pickup_commoditySmall:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "commodity_small"));
                break;
            case eGameEvent.player_pickup_commodityMedium:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "commodity_medium"));
                break;
            case eGameEvent.player_pickup_commodityLarge:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "commodity_large"));
                break;
            case eGameEvent.player_pickup_consumable:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "consumable"));
                break;
            case eGameEvent.player_pickup_keycard:
                Plugin.Session?.GuardEvents(events => PlayerEventHooks.ItemPickedUp(events, player, "keycard"));
                break;
            case eGameEvent.player_ping:
                if (player.TryCast<LocalPlayerAgent>() is { } local)
                    Plugin.Session?.GuardEvents(events => PlayerEventHooks.Ping(events, player, Position(local.m_pingPos), null));
                break;
        }
    }
    /// <summary>One native position in metres, or null when the game did not produce a finite one. A ping whose
    /// position is not finite is not a position this half publishes.</summary>
    internal static double[]? Position(Vector3 value)
        => float.IsFinite(value.x) && float.IsFinite(value.y) && float.IsFinite(value.z)
            ? new double[] { value.x, value.y, value.z } : null;
}

/// <summary>The marker call the game makes for a ping, with the pinging agent and the world position. Only the
/// visible edge is a ping; the same body is called to hide a marker again.</summary>
[HarmonyPatch(typeof(GuiManager), nameof(GuiManager.AttemptSetPlayerPingStatus),
    new[] { typeof(PlayerAgent), typeof(bool), typeof(Vector3), typeof(eNavMarkerStyle) })]
internal static class PlayerPingMarkerSet
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(PlayerAgent sourceAgent, bool visible, Vector3 worldPos)
    {
        if (!visible || sourceAgent == null) return;
        if (PlayerGameEventPosted.Position(worldPos) is not { } position) return;
        Plugin.Session?.GuardEvents(events => PlayerEventHooks.Ping(events, sourceAgent, position, null));
    }
}
