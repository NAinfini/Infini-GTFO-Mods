using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using LevelGeneration;

// The objective-machine types the production read names, declared for the focused build only. They sit in the
// global namespace exactly as the interop assemblies declare them, so the production source names them the same
// way in both builds. The fixture (`ObjectiveWorld.cs`) drives them; the mirror project compiles the same source
// against the real interop.

/// <summary>`eWardenObjectiveType`'s members and values, in the enum's own order.</summary>
public enum eWardenObjectiveType : byte
{
    HSU_FindTakeSample = 0, Reactor_Startup = 1, Reactor_Shutdown = 2, GatherSmallItems = 3, ClearAPath = 4,
    SpecialTerminalCommand = 5, RetrieveBigItems = 6, PowerCellDistribution = 7, TerminalUplink = 8,
    CentralGeneratorCluster = 9, ActivateSmallHSU = 10, Survival = 11, GatherTerminal = 12,
    CorruptedTerminalUplink = 13, Empty = 14, TimedTerminalSequence = 15
}

/// <summary>`eWardenObjectiveStatus`'s members and their own values (they are not an ordinal run).</summary>
public enum eWardenObjectiveStatus : byte
{
    NotDiscovered = 0, Discovered = 10, Started = 20, WardenObjectivePartiallySolved = 30, WardenObjectiveItemSolved = 40
}

/// <summary>`eWardenSubObjectiveStatus`'s members and values, in the enum's own order.</summary>
public enum eWardenSubObjectiveStatus : byte
{
    FindLocationInfo = 0, FindLocationInfoHelp = 1, GoToZone = 2, GoToZoneHelp = 3, InZoneFindItem = 4,
    InZoneFindItemHelp = 5, SolveItem = 6, SolveItemHelp = 7, GoToWinCondition = 8, GoToWinConditionHelp = 9
}

/// <summary>One objective's own definition handle, with the type the layer is running.</summary>
public interface IWardenObjective
{
    eWardenObjectiveType ObjectiveType { get; }
}

/// <summary>The objective behaviour the machine resolves a chain to.</summary>
public sealed class WardenObjective : IWardenObjective
{
    public eWardenObjectiveType ObjectiveType { get; set; }
}

/// <summary>The objective machine's one replicated state, with the members the read uses.</summary>
public sealed class pWardenObjectiveState
{
    public eWardenObjectiveStatus main_status;
    public eWardenSubObjectiveStatus main_subObj;
    public byte main_chainIndex;
    public float main_startTime;
    public float extraTime;
    public bool forceWinOnDeath;
    public bool exitWaveTriggered;
    public Il2CppStructArray<byte>? ObjectiveItemStates;
    public Il2CppStructArray<byte>? RequiredObjectiveItems;

    public eWardenObjectiveStatus GetLayerStatus(LG_LayerType layer) => main_status;
    public eWardenSubObjectiveStatus GetLayerSubStatus(LG_LayerType layer, bool onlyGetHelpStatus = false) => main_subObj;
    public byte GetChainIndexForLayer(LG_LayerType layer) => main_chainIndex;
    public float GetStartTimeFromLayer(LG_LayerType layer) => main_startTime;
}

/// <summary>The objective machine, with the three static entries the read goes through. The fixture fills the
/// tables below; nothing else in this double answers.</summary>
public static class WardenObjectiveManager
{
    public static pWardenObjectiveState? CurrentState;
    public static readonly HashSet<LG_LayerType> LayersWithData = new();
    public static readonly Dictionary<(LG_LayerType Layer, int Chain), IWardenObjective> Objectives = new();

    public static bool HasWardenObjectiveDataForLayer(LG_LayerType layer) => LayersWithData.Contains(layer);

    public static bool TryGetWardenObjective(LG_LayerType layer, int objectiveChainIndex, out IWardenObjective objective)
    {
        if (Objectives.TryGetValue((layer, objectiveChainIndex), out var found)) { objective = found; return true; }
        objective = null!;
        return false;
    }
}
