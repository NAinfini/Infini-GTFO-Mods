using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;
using GameData;
using LevelGeneration;

namespace ForgeMap.Native;

/// <summary>
/// The two read-only environment rows: `v-env` (`forge.query.environment.state`) and `v-zone-lights`
/// (`forge.query.map.zone_lights`). Each answers the facts the checklist names and nothing else — the fog block the
/// dimension currently shows and whether a zone's lights are on, and how many native light objects a zone holds
/// beside that same on/off answer — and each reads them from the game's own state, which is the state the game
/// replicates to every client.
///
/// Both rows are `query` capabilities, so they run on the host through the kernel's budgeted evaluation path: they
/// write nothing, publish no fact and produce no command. Their reads are declared on the capability
/// (`reads: ["world"]`) rather than hidden in the handler, which is what lets a plan's authority be judged from the
/// graph before the step ever runs.
/// </summary>
internal static class EnvironmentQuery
{
    internal const string DimensionCode = "environment-dimension-required";
    internal const string LayerCode = "environment-layer-required";
    internal const string ZoneCode = "environment-zone-required";
    internal const string UnavailableCode = "environment-unavailable";
    /// <summary>A `zone` input that is not a `gtfo.zone` address, or an address whose three coordinates are
    /// outside the enums the game indexes them with. It is refused rather than narrowed to a nearby zone.</summary>
    internal const string ZoneInputCode = "environment-zone-input";
    /// <summary>A zone reference of another world. The reference names a place of a level that is gone, so the read
    /// is refused instead of answered from the level standing now.</summary>
    internal const string StaleZoneCode = "environment-zone-stale";
    /// <summary>The zone address is well formed but this level has no zone at it. An empty answer here would read
    /// exactly like a zone whose lights are off and which holds none.</summary>
    internal const string ZoneMissingCode = "environment-zone-missing";

    /// <summary>Runs as the `query` step's evaluator. Both parameters are structural, so they arrive as the
    /// members their row inlines; a name outside the row's own list is refused rather than mapped to a nearby
    /// layer, the same rule the objective rows follow.</summary>
    internal static JsonElement Evaluate(EvaluationContext context)
    {
        int dimension = Integer(context.Parameters, "dimension", -1);
        if (dimension < 0 || dimension >= (int)eDimensionIndex.MAX_COUNT)
            throw new RuntimeContractException(DimensionCode, "A dimension outside the game's own enum has no fog state.");
        string? layer = Text(context.Parameters, "layer");
        int layerIndex = layer == null ? -1 : Array.IndexOf(EnvironmentContract.Layers, layer);
        if (layerIndex < 0) throw new RuntimeContractException(LayerCode, "The layer is not one of the game's three.");
        int zone = Integer(context.Parameters, "zone", -1);
        if (zone < 0 || zone > (int)eLocalZoneIndex.Zone_19)
            throw new RuntimeContractException(ZoneCode, "A zone index outside the game's own enum has no light state.");

        // A manager that is not there is the step's own refusal: an absent level would otherwise read as
        // "the fog is block 0 and the lights are off", which is a state the level never reported.
        if (EnvironmentStateManager.Current == null)
            throw new RuntimeContractException(UnavailableCode, "No environment state manager is loaded.");

        uint fog = EnvironmentStateManager.GetCurrentFogID((eDimensionIndex)dimension);
        bool light = EnvironmentStateManager.GetLightMode(new GlobalZoneIndex(dimension, layerIndex, zone));
        return RuntimeJson.From(new { fog = (long)fog, light });
    }

    /// <summary>Runs as the `zone_lights` row's evaluator: the number of native lights the named zone holds and
    /// whether that zone's lights are on. The zone is resolved through the level's own zone list — the same list
    /// every map-object address is built from — so a place this level does not have is refused instead of answered
    /// as an empty one, and the count is the zone's own light list rather than a second light ledger this provider
    /// would have to keep in step.</summary>
    internal static JsonElement ZoneLights(EvaluationContext context)
    {
        var reference = Input(context, "zone");
        var coordinates = EnvironmentZone.Parse(reference)
            ?? throw new RuntimeContractException(ZoneInputCode, "The input is not a zone address: " + reference.Id);
        if (reference.WorldEpoch != context.Query.WorldEpoch)
            throw new RuntimeContractException(StaleZoneCode, "The zone belongs to another world: " + reference.Id);
        if (EnvironmentStateManager.Current == null)
            throw new RuntimeContractException(UnavailableCode, "No environment state manager is loaded.");
        var zone = EnvironmentZoneTable.At(coordinates)
            ?? throw new RuntimeContractException(ZoneMissingCode, "This level has no zone at " + reference.Id);
        bool on = EnvironmentStateManager.GetLightMode(coordinates.Global);
        return RuntimeJson.From(new { on, count = (long)(zone.m_lightsInZone?.Count ?? 0) });
    }

    /// <summary>The row's one required input, refused by name when the plan left it out: an absent zone answered as
    /// a dark zone would read exactly like a zone that really is dark.</summary>
    private static EntityReference Input(EvaluationContext context, string port)
        => context.Inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
            ? RuntimeJson.Entity(value)
            : throw new RuntimeContractException("missing-field", port);

    private static string? Text(JsonElement bag, string name)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static int Integer(JsonElement bag, string name, int fallback)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out int number) ? number : fallback;
}
