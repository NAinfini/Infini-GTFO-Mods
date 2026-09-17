using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>Enumerate the player kind. Faction and effect polarity are not roster filters.</summary>
internal static class PlayerSelector
{
    internal const string CapabilityId = ForgeMap.PlayerSelectorContract.CapabilityId;
    internal const string BindingId = ForgeMap.PlayerSelectorContract.BindingId;
    internal const string HandlerName = ForgeMap.PlayerSelectorContract.HandlerName;
    internal static JsonElement Evaluate(EvaluationContext context)
    {
        // One budgeted source; failures are never silently converted to an empty roster.
        if (!context.Query.TryCandidates(PlayerIdentityModule.EntityKind, out var candidates, out var code))
            throw new RuntimeContractException(code, "The player candidate source refused the read: " + code);
        var targets = new List<EntityFrame>(candidates.Count);
        foreach (var reference in candidates)
            targets.Add(new EntityFrame(reference.Id, reference.WorldEpoch, reference.LifeEpoch));
        return RuntimeJson.From(new { targets });
    }
    private sealed record EntityFrame(string Id, long WorldEpoch, long LifeEpoch);
}
