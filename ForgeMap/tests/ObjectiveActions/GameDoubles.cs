using System;
using System.Collections.Generic;
using AIGraph;
using LevelGeneration;

// Doubles for the native members the objective action layer reads and calls, following build 20403457's interop
// member kinds. Names, namespaces, member kinds and constructor shapes are the interop assembly's; behaviour is
// synthetic and NOT game-verified. Only the members ObjectiveActions and ObjectiveActionHandler actually touch
// are mirrored.

/// <summary>Unity's interop base: a game object wrapper reports whether it has been collected, and the action
/// layer refuses an instance that has rather than reading through it.</summary>
public abstract class UnityObjectDouble
{
    public bool Destroyed;
    public bool WasCollected => Destroyed;
    public static bool operator ==(UnityObjectDouble? left, UnityObjectDouble? right)
    {
        bool l = left is { Destroyed: false }, r = right is { Destroyed: false };
        return !l || !r ? l == r : ReferenceEquals(left, right);
    }
    public static bool operator !=(UnityObjectDouble? left, UnityObjectDouble? right) => !(left == right);
    public override bool Equals(object? obj) => ReferenceEquals(this, obj);
    public override int GetHashCode() => System.Runtime.CompilerServices.RuntimeHelpers.GetHashCode(this);
}

/// <summary>Unity's Vector3 as the checkpoint interaction payload carries it: three readable fields, and the
/// default constructor the interop struct's own call sites use. The namespace is the interop assembly's, which
/// is why the production layer reaches it by its full name.</summary>
namespace ForgeMap
{
    /// <summary>The one member of the provider declaration this slice's contract names: the provider id every
    /// binding id is built from. The declaration itself is the game-independent module's own file, which belongs
    /// to the assembly that registers the provider rather than to this focused test.</summary>
    public static class ModuleDefinition
    {
        public const string ProviderId = "forge.module.gtfo.map";
        public const string Version = "1.0.0";
    }
}

/// <summary>Unity's Vector3 as the checkpoint interaction payload carries it: three readable fields, and the
/// default constructor the interop struct's own call sites use. The namespace is the interop assembly's, which
/// is why the production layer reaches it by its full name.</summary>
namespace UnityEngine
{
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
    }
}

namespace AIGraph
{
    /// <summary>The course node the exit scan is anchored to; the landing's own member, and the only reason the
    /// type is here is that the interop signature mentions it.</summary>
    public sealed class AIG_CourseNode : UnityObjectDouble { }
}

namespace LevelGeneration
{
    /// <summary>The objective layer vocabulary, with the interop values: MainLayer=0, SecondaryLayer=1,
    /// ThirdLayer=2.</summary>
    public enum LG_LayerType { MainLayer = 0, SecondaryLayer = 1, ThirdLayer = 2 }

    /// <summary>The sub-objective vocabulary, with the interop values 0..9.</summary>
    public enum eWardenSubObjectiveStatus
    {
        FindLocationInfo = 0, FindLocationInfoHelp = 1, GoToZone = 2, GoToZoneHelp = 3, InZoneFindItem = 4,
        InZoneFindItemHelp = 5, SolveItem = 6, SolveItemHelp = 7, GoToWinCondition = 8, GoToWinConditionHelp = 9
    }
}

/// <summary>The objective interaction type vocabulary, with the interop values: the members the action layer
/// reaches are the ones it names, and the numeric values are the wire contract with the native struct.</summary>
public enum eWardenObjectiveInteractionType : byte
{
    DiscoverObjective = 0,
    StartObjective = 1,
    SolveWardenObjectiveItem = 2,
    PartiallySolveWardenObjectiveItem = 3,
    SolveWinCondition = 4,
    UpdateSubObjective = 5,
    ChangeLocalLayer = 6,
    EventUpdate = 7,
    CustomSubObjectiveUpdate = 8,
    SetItemSolved = 9,
    AddRequiredItem = 10,
    SetExtraTime = 11,
    SetSolveOnDeath = 12,
    SetExitWaveTriggered = 13
}

/// <summary>The interaction payload: a byte-per-field explicit struct, mirrored field for field with the
/// interop offsets. Every field is public and settable, which is what lets a case read back exactly what the
/// action layer asked the native entry for.</summary>
public struct pWardenObjectiveInteraction
{
    public eWardenObjectiveInteractionType type;
    public eWardenSubObjectiveStatus newSubObj;
    public bool forceUpdate;
    public byte itemIndexPlusOne;
    public byte newOnActivateEventBreakIndex;
    public byte newOnActivateEventIndex;
    public byte ownerChainIndexPlusOne;
    public LevelGeneration.LG_LayerType inLayer;
    public uint newCustomSubObjectiveHeaderID;
    public uint newCustomSubObjectiveTextID;
    public int itemID;
    public float extraTime;
}

/// <summary>The objective state the manager's own lookups read. Only the members the production layer touches
/// are mirrored: the chain index a state carries, and the payload's own identity.</summary>
public sealed class pWardenObjectiveState : UnityObjectDouble { }

/// <summary>The objective manager: a state-replicator provider whose interaction entry is the one action channel
/// for objective state, whose two force entries own the whole-layer completion, and whose lookups are the
/// authority on whether an addressed chain exists at all.</summary>
public class WardenObjectiveManager : UnityObjectDouble
{
    public static WardenObjectiveManager? Current { get; set; }
    /// <summary>The chains the current level built, by layer. The production layer only ever asks the three
    /// lookups below, so the fixture's table is the whole of the level's objective data.</summary>
    public static readonly Dictionary<LevelGeneration.LG_LayerType, List<int>> Chains = new();
    public static readonly List<pWardenObjectiveInteraction> Interactions = new();
    public static readonly List<(LevelGeneration.LG_LayerType Layer, float Time)> ExtraTimes = new();
    public static readonly List<LevelGeneration.LG_LayerType> ForceCompletions = new();
    public static Exception? ThrowOnAttemptInteract;

    public void AttemptInteract(pWardenObjectiveInteraction interaction)
    {
        Interactions.Add(interaction);
        if (ThrowOnAttemptInteract != null) throw ThrowOnAttemptInteract;
    }

    public static void SetExtraTime(float time) => ExtraTimes.Add((default, time));
    public static void ForceCompleteObjectiveAll(LevelGeneration.LG_LayerType layer) => ForceCompletions.Add(layer);
    public static bool HasWardenObjectiveDataForLayer(LevelGeneration.LG_LayerType layer) => Chains.ContainsKey(layer);
    public static bool TryGetWardenObjective(LevelGeneration.LG_LayerType layer, int chainIndex, out IWardenObjective objective)
    {
        objective = null!;
        if (!Chains.TryGetValue(layer, out var chains) || !chains.Contains(chainIndex)) return false;
        objective = new IWardenObjective();
        return true;
    }

    public static void Reset()
    {
        Current = null; Chains.Clear(); Interactions.Clear(); ExtraTimes.Clear();
        ForceCompletions.Clear(); ThrowOnAttemptInteract = null;
    }
}

/// <summary>The objective behaviour the lookup hands back; its own members are not this layer's subject.</summary>
public sealed class IWardenObjective : UnityObjectDouble { }

/// <summary>The checkpoint manager: a process-wide singleton whose interaction channel carries the store and the
/// reload member, and whose own state replicator is what makes the interaction replicate.</summary>
public class CheckpointManager : UnityObjectDouble
{
    public static CheckpointManager? Current { get; set; }
    public static int CheckpointUsage { get; set; }
    public static bool IsReloadingCheckpoint { get; set; }
    public static readonly List<pCheckpointInteraction> Interactions = new();
    public static Exception? ThrowOnAttemptInteract;

    public void AttemptInteract(pCheckpointInteraction interaction)
    {
        Interactions.Add(interaction);
        if (ThrowOnAttemptInteract != null) throw ThrowOnAttemptInteract;
    }

    public static void Reset()
    {
        Current = null; CheckpointUsage = 0; IsReloadingCheckpoint = false;
        Interactions.Clear(); ThrowOnAttemptInteract = null;
    }
}

/// <summary>The checkpoint interaction types, with the interop values: StoreCheckpoint=1, ReloadCheckpoint=2.
/// The zero member is deliberately absent, exactly as the interop enum has no zero.</summary>
public enum eCheckpointInteractionType : byte { StoreCheckpoint = 1, ReloadCheckpoint = 2 }

/// <summary>The checkpoint interaction payload: the type plus the door lock position, mirrored field for field
/// with the interop offsets.</summary>
public struct pCheckpointInteraction
{
    public eCheckpointInteractionType type;
    public UnityEngine.Vector3 doorLockPosition;
    public pCheckpointInteraction(eCheckpointInteractionType type, UnityEngine.Vector3 doorLockPosition)
    { this.type = type; this.doorLockPosition = doorLockPosition; }
}

/// <summary>The level's own elevator landing, which owns the exit win-condition item. Both members are the
/// interop's public virtual pair; which one a case saw is the fixture's record.</summary>
public class ElevatorShaftLanding : UnityObjectDouble
{
    public static ElevatorShaftLanding? Current { get; set; }
    public int ActivateCalls;
    public int DeactivateCalls;
    public virtual void ActivateWinCondition() => ActivateCalls++;
    public virtual void DeactivateWinCondition() => DeactivateCalls++;
    public static void Reset() => Current = null;
}
