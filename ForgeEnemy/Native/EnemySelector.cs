using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>Complete current enemy roster; relation selection is an explicit downstream operation.</summary>
internal static class EnemySelector
{
    internal const string CapabilityId = ForgeEnemy.EnemySelectorContract.CapabilityId;
    internal const string BindingId = ForgeEnemy.EnemySelectorContract.BindingId;
    internal const string HandlerName = ForgeEnemy.EnemySelectorContract.HandlerName;
    internal const string EntityKind = ForgeEnemy.EnemySelectorContract.EntityKind;
    internal static JsonElement Evaluate(EvaluationContext context)
    {
        if (!context.Query.TryCandidates(EntityKind, out var candidates, out var code))
            throw new RuntimeContractException(code, "The enemy candidate source refused the read: " + code);
        var targets = new List<EntityFrame>(candidates.Count);
        foreach (var reference in candidates)
            targets.Add(new EntityFrame(reference.Id, reference.WorldEpoch, reference.LifeEpoch));
        return RuntimeJson.From(new { targets });
    }
    private sealed record EntityFrame(string Id, long WorldEpoch, long LifeEpoch);
}
