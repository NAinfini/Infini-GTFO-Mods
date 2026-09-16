using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;
using ForgeTrigger.Targeting;

namespace ForgeTrigger;

/// <summary>The production Trigger provider. Its node vocabulary is the three declaration tables this class composes
/// — <see cref="PureModule"/> for the value-only rows, <see cref="ObservedQueryModule"/> for the observation rows
/// and <see cref="ObservedSpaceNodes"/> for the rows that select from explicit candidates — and every row publishes
/// the authoring catalog's own capability shape and binds it to a handler in this assembly. Nothing here declares a
/// node, and no `execute` binding exists yet: this provider evaluates, it does not run commands.</summary>
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.trigger";
    public const string Version = "0.1.0";

    public static RuntimeModule Create() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = ProviderId, kind = "native", version = Version, dependencies = Array.Empty<string>() } },
            capabilities = PureModule.Capabilities.Concat(ObservedQueryModule.Capabilities)
                .Concat(ObservedSpaceNodes.Capabilities).ToArray(),
            bindings = PureModule.Bindings.Concat(ObservedQueryModule.Bindings)
                .Concat(ObservedSpaceNodes.Bindings).ToArray()
        }).GetRawText(),
        new Dictionary<string, CommandHandler>(),
        PureModule.Support.Concat(ObservedQueryModule.Support).Concat(ObservedSpaceNodes.Support).ToArray())
    {
        Evaluators = Merge(Merge(PureModule.Evaluators, ObservedQueryModule.Evaluators), ObservedSpaceNodes.Evaluators),
        Shapes = Merge(Merge(PureModule.Shapes, ObservedQueryModule.Shapes), ObservedSpaceNodes.Shapes)
    };

    /// <summary>One handler table for the whole provider: the registry resolves a shape per binding and refuses a
    /// name two rows disagree about, so the tables are joined rather than registered as separate modules. A handler
    /// name is used by exactly one row — the registry requires the tables to be the exact set the bindings name —
    /// so the merge cannot silently let one row's handler answer for another's binding.</summary>
    private static IReadOnlyDictionary<string, T> Merge<T>(IReadOnlyDictionary<string, T> first,
        IReadOnlyDictionary<string, T> second)
    {
        var merged = new Dictionary<string, T>(first, StringComparer.Ordinal);
        foreach (var pair in second) merged.Add(pair.Key, pair.Value);
        return merged;
    }
}
