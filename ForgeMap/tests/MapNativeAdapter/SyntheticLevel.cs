using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;
using GameData;
using LevelGeneration;

namespace ForgeMap.Tests.MapNativeAdapter;

/// <summary>A played level made of doubles: zones on a floor of the level builder, each entrance gate spawned by
/// its own zone, and terminals placed in the zone's own list. Every address below is read through the production
/// readers, so a case proves the reader, not the double.</summary>
internal sealed class SyntheticLevel
{
    private readonly RuntimeKernel _kernel;

    internal SyntheticLevel(RuntimeKernel kernel)
    {
        _kernel = kernel;
        LG_LevelBuilder.Current = new LG_LevelBuilder { m_currentFloor = new LG_Floor { allZones = new List<LG_Zone>() } };
    }

    internal LG_Zone Zone(int dimension, LG_LayerType layer, eLocalZoneIndex localIndex)
    {
        var zone = new LG_Zone { m_layer = new LG_Layer { m_type = layer }, m_dimensionIndex = (eDimensionIndex)dimension,
            LocalIndex = localIndex, m_settings = new LG_ZoneSettings { m_zoneData = new GameData.ExpeditionZoneData() } };
        LG_LevelBuilder.Current!.m_currentFloor!.allZones!.Add(zone);
        return zone;
    }

    /// <summary>The security door a zone is entered through, spawned by the zone's own source gate.</summary>
    internal LG_SecurityDoor Entrance(LG_Zone zone, eDoorStatus status = eDoorStatus.Closed,
        eSecurityDoorType type = eSecurityDoorType.Security)
    {
        var door = new LG_SecurityDoor { LastStatus = status, m_securityDoorType = type };
        door.m_locks = new iLG_Door_Locks { Target = new LG_SecurityDoor_Locks { m_door = door } };
        zone.m_sourceGate = new LG_Gate { SpawnedDoor = door };
        return door;
    }

    /// <summary>A door no zone's gate spawned: a weak, node or decorative door of the level.</summary>
    internal LG_SecurityDoor LooseDoor(eDoorStatus status)
        => new() { LastStatus = status, m_locks = new iLG_Door_Locks { Target = new LG_SecurityDoor_Locks() } };

    internal LG_ComputerTerminal Terminal(LG_Zone zone, TERM_State state)
    {
        var terminal = new LG_ComputerTerminal { CurrentStateName = state,
            SpawnNode = new AIGraph.AIG_CourseNode { m_zone = zone } };
        (zone.TerminalsSpawnedInZone ??= new List<LG_ComputerTerminal>()).Add(terminal);
        return terminal;
    }

    /// <summary>Takes a zone out of the level's list, as a level that never built it would.</summary>
    internal void Forget(LG_Zone zone) => LG_LevelBuilder.Current!.m_currentFloor!.allZones!.Remove(zone);

    internal string? AddressOf(LG_SecurityDoor door) => DoorObservation.Decide(door).Address?.ToString();
    internal string? AddressOf(LG_ComputerTerminal terminal) => TerminalObservation.Decide(terminal).Address?.ToString();

    /// <summary>The address the kernel itself resolves the instance to, which is the reader the module and the
    /// entity observer both go through.</summary>
    internal EntityReference? Reference(object instance)
        => _kernel.ResolveEntityInstance(MapObjectModule.EntityKind, instance);
}
