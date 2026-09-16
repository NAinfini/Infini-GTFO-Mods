using System;
using System.Collections.Generic;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// The world one case runs against: the four native singletons the slice reaches (the level event manager, the
/// environment manager, the voice manager and the local player's GUI layer), the session hub the presentation
/// rows route through, the player identities and their overhead markers, and the production handlers wired the way
/// the plugin wires them. Every native object here is a managed double; nothing loads a GTFO assembly and nothing
/// is game-verified.
/// </summary>
internal sealed class EnvironmentWorld : IDisposable
{
    internal const long Epoch = 1;

    /// <summary>Whether this peer is the authority. The environment facts and the player lines both read it the
    /// way the session does, and a case stands on either side of it without a second gate.</summary>
    internal bool CanExecute => _canExecute;
    private readonly bool _canExecute;

    private EnvironmentWorld(bool canExecute)
    {
        _canExecute = canExecute;
        Host = new EnvironmentActions(() => _canExecute, () => WorldEventManager.Current, Reports.Add);
        Presentation = new EnvironmentPresentation(() => WorldEventManager.Current, SlotExists, Reports.Add);
        Hud = new HudActions(Reports.Add);
        // The conversion between a player entity and a session is the identity module's in production; here the
        // registered lives are what it answers from.
        PlayerSessions.UseAgents(reference => PlayerIdentityModule.Current?.CurrentAgent(reference));
    }

    internal EnvironmentActions Host { get; }
    internal EnvironmentPresentation Presentation { get; }
    internal HudActions Hud { get; }
    internal readonly List<string> Reports = new();

    /// <summary>One live world. `canExecute` is the session's own readiness gate, which a case stands on either
    /// side of without a second gate of its own.</summary>
    internal static EnvironmentWorld Start(bool canExecute = true)
    {
        Reset();
        return new EnvironmentWorld(canExecute);
    }

    /// <summary>One level zone in the game's own reference grammar.</summary>
    internal static EntityReference Zone(int dimension, int layer, int zone)
        => RuntimeZones.Reference(Epoch, dimension, layer, zone);

    /// <summary>One player entity in the Map provider's own grammar. A number that was already registered names
    /// the life that is already there, so two calls with one number are one player.</summary>
    internal static EntityReference Player(int number)
        => PlayerIdentityModule.Current?.Known("gtfo.player:" + number)?.Reference
            ?? Register(number, 1000UL + (ulong)number, number - 1, bot: false, locallyOwned: false);

    /// <summary>One player entity with the session, slot, bot flag and ownership a case wants. The agent, its
    /// owner and the overhead marker the game would have built are created together, because the three are one
    /// life in this slice's own model; `marker: false` is the life whose marker the game has not built yet.</summary>
    internal static EntityReference Player(int number, ulong lookup, int slot, bool bot = false, bool locallyOwned = false,
        bool marker = true)
        => PlayerIdentityModule.Current?.Known("gtfo.player:" + number)?.Reference
            ?? Register(number, lookup, slot, bot, locallyOwned, marker);

    /// <summary>The player sitting at this machine: the one session an `audience=self` command is addressed to.
    /// A case sets it once per world and every later command is compared against it.</summary>
    internal static void Local(int number)
        => SNet.SetLocal(PlayerIdentityModule.Current?.Known("gtfo.player:" + number)?.Agent.Owner);

    /// <summary>One entity of some other kind, for the cases that check a request naming the wrong thing.</summary>
    internal static EntityReference Foreign() => new("gtfo.map_object:door.1", Epoch, 1);

    /// <summary>A player entity this world registered no life for: the shape a plan's reference has when the life
    /// it named is gone, which is the case a row that converts a player into something else has to refuse.</summary>
    internal static EntityReference UnknownPlayer(int number) => new("gtfo.player:" + number, Epoch, number);

    /// <summary>The agent of one player, which is what the overhead placement reads for the marker.</summary>
    internal static PlayerAgent? Agent(int number) => PlayerIdentityModule.Current?.Known("gtfo.player:" + number)?.Agent;

    /// <summary>The one player session in the hub, with the slot and lookup the game would report for it.</summary>
    internal static void Session(ulong lookup, int slot, bool bot = false)
        => SNet.SessionHub!.PlayersInSession.Add(new SNet_Player { Lookup = lookup, Slot = slot, IsBot = bot });

    private static EntityReference Register(int number, ulong lookup, int slot, bool bot, bool locallyOwned, bool marker = true)
    {
        Session(lookup, slot, bot);
        var player = SNet.SessionHub!.PlayersInSession[^1];
        var agent = new PlayerAgent
        {
            Owner = player,
            IsLocallyOwned = locallyOwned
        };
        if (marker)
        {
            var nav = new PlaceNavMarkerOnGO(player.Pointer) { Player = agent };
            nav.m_marker = new NavMarker { m_playerName = NameText("PlayerName" + number), m_distance = NameText("Distance" + number) };
            agent.NavMarker = nav;
        }
        return PlayerIdentityModule.Register("gtfo.player:" + number, Epoch, number, agent).Reference;
    }

    /// <summary>One name mesh wired the way the game's own marker wires it: the component knows its own game
    /// object, that object's transform and its rect, and the object is drawn.</summary>
    internal static TMPro.TextMeshPro NameText(string name)
    {
        var host = new UnityEngine.GameObject(name);
        var rect = new UnityEngine.RectTransform { gameObject = host };
        host.Attach(rect);
        var text = new TMPro.TextMeshPro { gameObject = host, transform = host.transform, rectTransform = rect };
        text.transform.parent = new UnityEngine.Transform();
        host.SetActive(true);
        return text;
    }

    private static bool SlotExists(int slot)
    {
        var hub = SNet.SessionHub;
        if (hub == null) return false;
        foreach (var player in hub.PlayersInSession) if (player.PlayerSlotIndex() == slot) return true;
        return false;
    }

    /// <summary>The one event the handler handed the game's own entry, or null when it never reached it.</summary>
    internal static GameData.WardenObjectiveEventData? LastEvent
        => WorldEventManager.Executed.Count == 0 ? null : WorldEventManager.Executed[^1].Data;

    internal static int EventCount => WorldEventManager.Executed.Count;

    /// <summary>One level the zone reads resolve against: the zones the case names, each with a layer, a dimension
    /// and a local index, and the light objects the level placed in it. A level that was never stood up answers no
    /// zone, which is the case a `zone_lights` read has to refuse instead of answering an empty place.</summary>
    internal static void Level(params (int Dimension, int Layer, int Zone, int Lights)[] zones)
    {
        var floor = new LevelGeneration.LG_Floor();
        foreach (var (dimension, layer, zone, lights) in zones)
        {
            var instance = new LevelGeneration.LG_Zone
            {
                m_layer = new LevelGeneration.LG_Layer { m_type = (LevelGeneration.LG_LayerType)layer },
                m_dimensionIndex = (eDimensionIndex)dimension,
                LocalIndex = (GameData.eLocalZoneIndex)zone
            };
            for (var index = 0; index < lights; index++) instance.m_lightsInZone!.Add(new LevelGeneration.LG_Light());
            floor.allZones!.Add(instance);
        }
        LevelGeneration.LG_LevelBuilder.Current = new LevelGeneration.LG_LevelBuilder { m_currentFloor = floor };
    }

    internal static void Reset()
    {
        WorldEventManager.Reset();
        EnvironmentStateManager.Reset();
        global::Player.PlayerVoiceManager.Reset();
        SNet.Reset();
        GuiManager.Reset();
        PlayerIdentityModule.Reset();
        TeammateOverhead.Clear();
        UnityEngine.Object.ForgetAll();
        LevelGeneration.LG_LevelBuilder.Current = null;
    }
    public void Dispose()
    {
        TeammateOverhead.Clear();
        Reset();
    }
}
