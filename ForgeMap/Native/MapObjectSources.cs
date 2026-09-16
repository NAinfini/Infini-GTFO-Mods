using System;
using System.Collections.Generic;
using ForgeMap;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>The door reader. It answers only for `LG_SecurityDoor` instances, so a terminal reported through
/// this source resolves to nothing instead of being read as a door. A door has no table of its own: the zone
/// it is the entrance gate of is what its address is built from, and the one zone table answers that.
///
/// A door that is not a zone's entrance is not addressed and its cause is reported once per cause: a door the
/// level did not create as an entrance carries none of the address's keys, and inventing coordinates for it
/// would make an address that names something else.</summary>
internal sealed class DoorSource : IMapObjectSource
{
    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    internal DoorSource(Action<string> report) => _report = report;

    public string Category => MapObjectCategories.Door;

    public MapObjectReference? Parse(string text) => MapObjectDoorAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance)
    {
        if (instance is not LG_SecurityDoor door) return null;
        var decision = DoorObservation.Decide(door);
        if (decision.Address is { } address) return address;
        Report("map-object door is not addressable: " + Cause(decision.Refusal));
        return null;
    }

    private static string Cause(MapObjectRefusal? refusal) => refusal switch
    {
        MapObjectRefusal.BulkheadTransition => "it is a bulkhead layer transition, not a zone entrance.",
        MapObjectRefusal.NoCoordinates => "the zone it guards has no readable dimension, layer or local index.",
        MapObjectRefusal.Unreadable => "it does not read.",
        _ => "it is not the entrance gate of a zone this level knows."
    };
    public MapObjectObservation? Read(object instance)
        => instance is LG_SecurityDoor door ? DoorObservation.Read(door) : null;

    public double[]? Position(object instance)
        => instance is LG_SecurityDoor door ? DoorObservation.Position(door) : null;

    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => instance is LG_SecurityDoor door && DoorObservation.IsCurrentAddress(door, address);

    public void Report(string message)
    {
        if (_reported.Add(message)) _report(message);
    }
}

/// <summary>The terminal reader. It answers only for `LG_ComputerTerminal` instances. A terminal is addressed
/// by the zone it was spawned in and its position in that zone's terminal list, so an address resolves back
/// through the same table rather than through the terminal manager's runtime registry.
///
/// A terminal the level does not place through its zone's own terminal list — a reactor or mission terminal —
/// gets no address, and its cause is reported once per cause.</summary>
internal sealed class TerminalSource : IMapObjectSource
{
    private readonly Action<string> _report;
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    internal TerminalSource(Action<string> report) => _report = report;

    public string Category => MapObjectCategories.Terminal;

    public MapObjectReference? Parse(string text) => MapObjectTerminalAddress.TryParse(text);

    public MapObjectReference? TryAddress(object instance)
    {
        if (instance is not LG_ComputerTerminal terminal) return null;
        var decision = TerminalObservation.Decide(terminal);
        if (decision.Address is { } address) return address;
        Report("map-object terminal is not addressable: " + Cause(decision.Refusal));
        return null;
    }

    /// <summary>The cause as one fixed sentence per class, so a level with several unaddressable terminals of
    /// one class reports that class once instead of once per terminal.</summary>
    private static string Cause(MapObjectRefusal? refusal) => refusal switch
    {
        MapObjectRefusal.NotListedByZone => "its zone does not list it among the terminals it spawned.",
        MapObjectRefusal.SpecificTerminalSpawns => "the zone it stands in declares specific terminal spawns, "
            + "so its own terminal list is not the zone's whole placement order.",
        MapObjectRefusal.NoCoordinates => "the zone it was spawned in has no readable dimension, layer or local index.",
        MapObjectRefusal.Unreadable => "it does not read.",
        _ => "no zone of this level reports it as one of its own terminal placements."
    };

    public MapObjectObservation? Read(object instance)
        => instance is LG_ComputerTerminal terminal ? TerminalObservation.Read(terminal) : null;

    public double[]? Position(object instance)
        => instance is LG_ComputerTerminal terminal ? TerminalObservation.Position(terminal) : null;

    public bool IsCurrentAddress(object instance, MapObjectReference address)
        => instance is LG_ComputerTerminal terminal && TerminalObservation.IsCurrentAddress(terminal, address);

    public void Report(string message)
    {
        if (_reported.Add(message)) _report(message);
    }
}
