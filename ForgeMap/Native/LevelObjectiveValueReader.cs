using System;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>The game-bound half of the `v-obj` row: the one read of one objective layer's live state. Every member
/// below is taken from the build's own metadata (20403457, `%TEMP%\covtrigb\ilspy` and the interop assemblies):
///
/// - `WardenObjectiveManager.CurrentState` is the machine's one replicated `pWardenObjectiveState`.
/// - `HasWardenObjectiveDataForLayer(layer)` is the machine's own answer to "is there an objective here", so a
///   layer that has none is refused from the game's table and not from a Forge-side list.
/// - `pWardenObjectiveState.GetLayerStatus(layer)` / `GetLayerSubStatus(layer)` / `GetChainIndexForLayer(layer)` /
///   `GetStartTimeFromLayer(layer)` are the machine's per-layer accessors: they take the layer, which is why this
///   row selects a layer instead of asking for "the current one".
/// - `pWardenObjectiveState.extraTime` is the countdown value member. It is **not** read: ruling 124.2 withdrew
///   the row's countdown port until `probes/points.tsv` settles what the field holds in game, and a member no
///   row names is not read for a reader that does not exist. The member record and the open question stay in
///   `evidence/level-objective-value.json`.
/// - `forceWinOnDeath` / `exitWaveTriggered` are the state's own flags; `ObjectiveItemStates` is the
///   byte-per-item table (`OBJECTIVE_ITEM_STATE_COUNT` = 96) and `RequiredObjectiveItems` the byte-per-required-item
///   table (`REQUIRED_OBJECTIVE_ITEM_MAX_COUNT` = 8), so both counters are counts of the tables themselves and
///   neither is derived.
/// - The objective's type comes from the layer's objective instance: `TryGetWardenObjective(layer, chainIndex,
///   out IWardenObjective)` gives the instance and `WardenObjective.ObjectiveType` names the type the machine
///   built for that layer. The chain index is the state's own, so the instance resolved is the one the layer is
///   actually running.
///
/// The read writes nothing: it takes no lock, publishes no event and calls no setter, which is what the row's
/// `query` tier means. A layer the machine has no state for, no data for, or no objective instance for answers
/// null, and the contract turns that into a refusal by name rather than into zeros.</summary>
internal static class LevelObjectiveValueReader
{
    /// <summary>Reads one layer's objective state, or null when this process cannot read it. The data check comes
    /// first because `pWardenObjectiveState` is a value type: the machine's `CurrentState` answers a state for a
    /// level that has no objective at all, so "there is no objective here" has to be asked of
    /// `HasWardenObjectiveDataForLayer` rather than inferred from a state that happens to read as untouched.</summary>
    internal static LevelObjectiveValueContract.LayerSample? Read(string layer)
    {
        var type = LayerType(layer);
        if (type == null) return null;
        if (!WardenObjectiveManager.HasWardenObjectiveDataForLayer(type.Value)) return null;
        var state = WardenObjectiveManager.CurrentState;
        if (state == null) return null;
        int chainIndex = state.GetChainIndexForLayer(type.Value);
        return new LevelObjectiveValueContract.LayerSample(
            KindOf(type.Value, chainIndex),
            (int)state.GetLayerStatus(type.Value),
            (int)state.GetLayerSubStatus(type.Value),
            chainIndex,
            state.GetStartTimeFromLayer(type.Value),
            state.forceWinOnDeath,
            state.exitWaveTriggered,
            CountSet(state.ObjectiveItemStates),
            state.RequiredObjectiveItems?.Length ?? 0);
    }

    /// <summary>The layer member one of the contract's three names spells, or null for a name the contract would
    /// have refused already. `LG_LayerType` declares exactly these three, so the name table is the enum's own and
    /// the contract's own list is asserted against it by a case. It is read by the session's `objective` resource
    /// provider as well, so the resource table and the read answer about the same three slots.</summary>
    internal static LevelGeneration.LG_LayerType? LayerType(string layer) => layer switch
    {
        "main" => LevelGeneration.LG_LayerType.MainLayer,
        "secondary" => LevelGeneration.LG_LayerType.SecondaryLayer,
        "third" => LevelGeneration.LG_LayerType.ThirdLayer,
        _ => null
    };

    /// <summary>The objective type the layer is running. The instance is resolved through the machine's own chain
    /// lookup with the state's own chain index, so the type belongs to the objective the layer is on. A lookup that
    /// cannot answer leaves the kind unknown rather than guessing one: `Empty` (14) is the game's own member for
    /// "no objective of any type", which is the honest answer for a layer whose instance could not be resolved.</summary>
    private static int KindOf(LevelGeneration.LG_LayerType layer, int chainIndex)
        => WardenObjectiveManager.TryGetWardenObjective(layer, chainIndex, out var objective) && objective != null
            ? (int)objective.ObjectiveType
            : EmptyKind;

    /// <summary>`eWardenObjectiveType.Empty`, the member the game itself uses for a layer that runs nothing.</summary>
    private const int EmptyKind = 14;

    /// <summary>How many of the objective's item slots are set. The table is one byte per slot and a non-zero byte
    /// is the game's own "solved", so the count is taken the same way.</summary>
    private static int CountSet(Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>? states)
    {
        if (states == null) return 0;
        int count = 0;
        for (int index = 0; index < states.Length; index++) if (states[index] != 0) count++;
        return count;
    }
}
