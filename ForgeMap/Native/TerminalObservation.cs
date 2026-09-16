using System;
using System.Globalization;
using ForgeMap;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// One terminal read through its own native members on build 20403457. The address is the coordinates of the
/// zone the terminal was spawned in — read through its own spawn node — plus its position in that zone's
/// terminal list, so several terminals in one zone are told apart by the order the level places them in. Every
/// state value is the terminal's own `CurrentStateName`. The terminal's own sync id is not part of the
/// address: the terminal manager hands it out while the level is being built, and it is only ever used to find
/// the terminal a native callback was carried for. `Decide` is the single reading behind both the address and
/// the reason a source reports.
/// </summary>
internal static class TerminalObservation
{
    /// <summary>The one decision about whether a terminal can be addressed, and the reason when it cannot. The
    /// address and the reason come from one reading, so a source reports the cause that actually refused the
    /// terminal. A zone that declares specific terminal spawns places terminals outside its own terminal list,
    /// so the position in that list is not the zone's whole placement order: no terminal of such a zone is
    /// addressed, because a partial order would silently rename the terminals it does not cover.</summary>
    internal static MapObjectAddressDecision Decide(LG_ComputerTerminal terminal)
    {
        if (terminal == null || terminal.WasCollected) return MapObjectAddressDecision.Refused(MapObjectRefusal.Unreadable);
        var index = ZoneIndex.Current(Plugin.Session?.WorldEpoch);
        var zone = index.ZoneOfTerminal(terminal);
        if (zone == null) return MapObjectAddressDecision.Refused(MapObjectRefusal.NoOwningZone);
        if (index.PlacementOf(terminal) is not { } placement)
            return MapObjectAddressDecision.Refused(MapObjectRefusal.NotListedByZone);
        if (ZoneIndex.SpecificTerminalPlacements(zone) is > 0)
            return MapObjectAddressDecision.Refused(MapObjectRefusal.SpecificTerminalSpawns);
        if (ZoneIndex.Coordinates(zone) is not { } coordinates)
            return MapObjectAddressDecision.Refused(MapObjectRefusal.NoCoordinates);
        var address = MapObjectTerminalAddress.Create(coordinates.Dimension, coordinates.Layer, coordinates.Zone,
            placement);
        return address == null ? MapObjectAddressDecision.Refused(MapObjectRefusal.NoCoordinates)
            : MapObjectAddressDecision.Addressed(address);
    }

    internal static MapObjectReference? Address(LG_ComputerTerminal terminal) => Decide(terminal).Address;

    /// <summary>Whether the terminal still reads as the address it was resolved through. A terminal that was
    /// spawned into another zone, or moved to another position in its zone's list, is no longer that
    /// terminal.</summary>
    internal static bool IsCurrentAddress(LG_ComputerTerminal terminal, MapObjectReference address)
    {
        var now = Address(terminal);
        return now != null && now == address;
    }

    /// <summary>The terminal's own world position, or null when it does not read. A snapshot carries three
    /// finite coordinates, so this provider publishes the terminal's real position or no observation at all.</summary>
    internal static double[]? Position(LG_ComputerTerminal terminal)
    {
        if (terminal == null || terminal.WasCollected) return null;
        var transform = terminal.transform;
        if (transform == null) return null;
        var position = transform.position;
        return float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)
            ? new[] { (double)position.x, (double)position.y, (double)position.z } : null;
    }

    internal static MapObjectObservation? Read(LG_ComputerTerminal terminal)
    {
        if (terminal == null || terminal.WasCollected) return null;
        var before = Address(terminal);
        if (before == null) return null;
        int state = (int)terminal.CurrentStateName;
        string name = MapObjectTerminalState.Name(state);
        bool active = MapObjectTerminalState.SessionActive(state);
        int? outcome = MapObjectTerminalState.Outcome(state);
        // A native read can trigger teardown or replacement; a changed instance never yields an observation.
        if (terminal.WasCollected || Address(terminal) != before) return null;
        return new MapObjectObservation(true, null, new MapObjectTerminalSnapshot(name, state, active, outcome));
    }

    /// <summary>The terminal an address names: the zone its coordinates identify and the terminal at that
    /// placement index inside it. Reads the address's own segments, so a wrong category or a segment that is
    /// not a decimal number is refused before anything native is touched.</summary>
    internal static LG_ComputerTerminal? ByAddress(MapObjectReference address)
    {
        var index = ZoneIndex.Current(Plugin.Session?.WorldEpoch);
        if (!int.TryParse(address.Dimension, NumberStyles.None, CultureInfo.InvariantCulture, out int dimension)
            || !int.TryParse(address.Layer, NumberStyles.None, CultureInfo.InvariantCulture, out int layer)
            || !int.TryParse(address.Zone, NumberStyles.None, CultureInfo.InvariantCulture, out int zone)
            || !int.TryParse(address.Key, NumberStyles.None, CultureInfo.InvariantCulture, out int placement))
            return null;
        return index.TerminalAt(new ZoneCoordinates(dimension, layer, zone), placement);
    }

    /// <summary>The terminal the game's own sync id names. The id is what a native entry point carries — the
    /// command entry is handed one and nothing else — so the level's terminal lists are searched once; the id
    /// itself is never part of an address.</summary>
    internal static LG_ComputerTerminal? BySyncId(uint syncId)
        => ZoneIndex.Current(Plugin.Session?.WorldEpoch).TerminalBySyncId(syncId);
}
