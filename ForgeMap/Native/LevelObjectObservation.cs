using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ChainedPuzzles;
using ForgeRuntime.Framework;
using LevelGeneration;
using Player;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>
/// The game-bound half of the level-object facts: it reads one native subject, builds the key that subject is
/// addressed by, and hands the values to the game-independent publication half
/// (<c>ForgeMap.LevelObjectModule</c>). Nothing here knows a binding id, an event id or a transition, and
/// nothing in the publication half knows a game type — which is the seam that lets every publishing branch be
/// tested without a game.
///
/// The keys this half builds are `<category>/<dimension>/<layer>/<zone>/<serial>`: the zone the object stands
/// in, read through the same <see cref="ZoneIndex"/> the map-object addresses use so both halves agree on what
/// a zone's coordinates are, and the serial the level assigned the object. A subject whose zone or serial does
/// not read produces no key, and the fact is then not published at all rather than published under a guess.
///
/// The scan value row needs the native subject again when the plan asks, and a key is all the module holds: this
/// half therefore keeps the table the scan facts were read from and drops it with the world the keys belong to.
/// A key that is not in the table is a subject this provider never read a fact from, and the reader refuses it
/// rather than reading a same-key object of a world it no longer holds. The generators are not here: they are a
/// map-object category with their own reader and their own table (`GeneratorSource.cs`).
/// </summary>
internal static class LevelObjectObservation
{
    internal const string ContainerCategory = "container";
    internal const string ItemCategory = "item";

    private static readonly Dictionary<string, ChainedPuzzleInstance> Scans = new(StringComparer.Ordinal);
    private static long _world = long.MinValue;

    /// <summary>One world's tables. A new world epoch drops the native instances of the level that no longer
    /// exists, so a key of the old level cannot answer with an instance of the new one.</summary>
    private static void Observe(long world)
    {
        if (world == _world) return;
        _world = world;
        Scans.Clear();
    }

    /// <summary>Whether the tables still belong to the world the session is in. A session that is gone reads no
    /// table at all.</summary>
    private static bool Current() => Plugin.Session is not { } session || session.WorldEpoch == _world;

    // ---- scan ---------------------------------------------------------------------------------------

    /// <summary>One scan progress reading. The uid is the instance's own, and the participants are the players
    /// the native callback itself named, resolved through the player half so this provider never invents a
    /// player identity.</summary>
    internal static void ScanProgress(ChainedPuzzleInstance instance, float progress, IReadOnlyList<PlayerAgent> players,
        LevelObjectModule module)
    {
        if (ScanUid(instance) is not { } uid) return;
        Observe(module.WorldEpoch);
        Scans[uid] = instance;
        var participants = new List<ForgeRuntime.Framework.EntityReference>(players.Count);
        for (int i = 0; i != players.Count; i++)
            if (Actor(players[i]) is { } actor) participants.Add(actor);
        module.ScanProgress(uid, progress, participants);
    }

    /// <summary>One scan state change, read after the instance's own body returned so the reading is the state
    /// the instance is really in.</summary>
    internal static void ScanStateChanged(ChainedPuzzleInstance instance, LevelObjectModule module)
    {
        if (ScanUid(instance) is not { } uid) return;
        Observe(module.WorldEpoch);
        Scans[uid] = instance;
        module.ScanStateChanged(uid, instance.IsActive, instance.IsSolved);
    }

    internal static string? ScanUid(ChainedPuzzleInstance instance)
    {
        string uid = instance.m_puzzleUID ?? "";
        return uid.Length == 0 ? null : uid;
    }

    // ---- value row ----------------------------------------------------------------------------------

    /// <summary>One scan's current state and progress, for `forge.condition.predicate.scan`. The state is the
    /// instance's own two public readings — `IsSolved` first, because a solved instance stays active, and
    /// `disabled` when it is neither — and the fraction is the master's own last report, which the publication
    /// half hands in because the instance exposes no fraction of its own. A scan this provider never read a
    /// fact from is refused by the module rather than answered from a same-key instance of another world.</summary>
    internal static JsonElement ReadScanState(string scanUid, double progress)
    {
        if (!Current() || !Scans.TryGetValue(scanUid, out var instance) || instance.WasCollected)
            throw new RuntimeContractException("scan-unavailable", scanUid);
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["state"] = RuntimeJson.From(ScanStateName(instance)),
            ["progress"] = RuntimeJson.From(progress)
        });
    }

    /// <summary>One instance's own status, as the state names the value row declares. The three names are the
    /// game's own `eChainedPuzzleStatus` members: an instance that is neither active nor solved is `Disabled`,
    /// which is the status it holds before it is activated.</summary>
    internal static string ScanStateName(ChainedPuzzleInstance instance)
        => instance.IsSolved ? "solved" : instance.IsActive ? "active" : "disabled";

    // ---- container and item -------------------------------------------------------------------------

    /// <summary>One container state change. The status is the game's own enum ordinal; the reading is resolved
    /// to the row's declared name here, where the enum lives, and the publication half refuses a name its row
    /// does not declare.</summary>
    internal static void ContainerStateChanged(LG_ResourceContainer_Sync container,
        pResourceContainerItemState newState, LevelObjectModule module)
        => module.ContainerStateChanged(KeyOf(container, ContainerCategory, SerialOf(container.gameObject)) ?? "",
            ContainerStateName((int)newState.status));

    internal static string ContainerStateName(int status) => status switch
    {
        0 => "not-setup",
        1 => "locked",
        2 => "closed",
        3 => "open",
        4 => "player-close",
        5 => "player-far",
        _ => ""
    };

    /// <summary>One level item changing hands. The direction is the game's own `ePickupItemStatus`, and the
    /// actor is the player the replicated state named.</summary>
    internal static void ItemStateChanged(LG_PickupItem_Sync item, pPickupItemState newState,
        LevelObjectModule module)
    {
        if (KeyOf(item, ItemCategory, SerialOf(item.gameObject)) is not { } key) return;
        if (newState.status is not (ePickupItemStatus.PlacedInLevel or ePickupItemStatus.PickedUp)) return;
        module.ItemStateChanged(key, newState.status == ePickupItemStatus.PickedUp, ActorOf(newState.pPlayer));
    }

    // ---- shared reads -------------------------------------------------------------------------------

    /// <summary>One level object's key. The zone is read from the object's own hierarchy through the one zone
    /// table every reader asks, and the serial is the level's own per-object identity. A negative serial is not
    /// an identity and yields no key.</summary>
    internal static string? KeyOf(UnityEngine.Component component, string category, int serial)
    {
        if (serial < 0) return null;
        var zone = component.GetComponentInParent<LG_Zone>();
        if (zone == null || zone.WasCollected) return null;
        if (ZoneIndex.Coordinates(zone) is not { } coordinates) return null;
        return category + "/" + coordinates.Dimension.ToString(CultureInfo.InvariantCulture) + "/"
            + coordinates.Layer.ToString(CultureInfo.InvariantCulture) + "/"
            + coordinates.Zone.ToString(CultureInfo.InvariantCulture) + "/"
            + serial.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The per-object identity value of a container or a level item, which carry no serial member of
    /// their own: the object's own instance id, which the game assigns once per object inside one level. A
    /// missing or destroyed object has none.</summary>
    internal static int SerialOf(UnityEngine.GameObject? gameObject)
        => gameObject == null || gameObject.WasCollected ? -1 : gameObject.GetInstanceID();

    /// <summary>The player one replicated state names, read through the game's own struct accessor: the struct
    /// carries the replicated player, and the player domain's identity is what turns it into the entity a port
    /// carries. A state that names none — a level-build change or a drop-in replay — yields nothing rather than
    /// the last player that touched the object.</summary>
    internal static ForgeRuntime.Framework.EntityReference? ActorOf(SNetStructs.pPlayer player)
    {
        try
        {
            return player.TryGetPlayer(out var replicated) && replicated != null && replicated.Pointer != IntPtr.Zero
                ? PlayerIdentityModule.Current?.ReferenceOf(replicated) : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The entity reference of one player, resolved through the player domain's own instance lookup so
    /// this provider never invents a player identity. A player the domain does not track is absent.</summary>
    internal static ForgeRuntime.Framework.EntityReference? Actor(object? instance)
        => instance == null ? null : PlayerIdentityModule.Current?.ReferenceOf(instance);
}
