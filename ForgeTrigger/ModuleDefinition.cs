using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

namespace ForgeTrigger;

/// <summary>D-017 R4-a: the production Trigger module advertises one `evaluate` binding for
/// `forge.condition.predicate.compare` (catalog category "condition"), delegating to the existing pure
/// <see cref="PureConditions.Compare"/>. Still no `execute`/`observe` bindings and no handler functions.</summary>
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.trigger";
    public const string Version = "0.1.0";
    private const string CompareBindingId = ProviderId + ".binding.compare";
    private const string CompareHandlerName = "trigger.condition.compare";
    /// <summary>The `compare_operator` enum set's member order (Forge Standard, `eq ne lt lte gt gte`); a wired
    /// `operator` input arrives here already resolved from its wire index to this name (Q3, RuntimeJson.EnumPortToHandlerValue).
    /// <see cref="ScalarComparison"/> is declared in the same order, so the member's position in this array is
    /// also its <see cref="ScalarComparison"/> value.</summary>
    private static readonly string[] CompareOperators = { "eq", "ne", "lt", "lte", "gt", "gte" };

    public static RuntimeModule Create() => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = ProviderId, kind = "native", version = Version, dependencies = Array.Empty<string>() } },
            capabilities = new object[] {
                new {
                    id = "forge.condition.predicate.compare",
                    owner = ProviderId,
                    kind = "condition",
                    label = "有单位的数值比较",
                    version = "1.0.0",
                    parameters = new { description = "比较两个数，可以带容差。" },
                    graph = new {
                        domains = new[] { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic" },
                        execution = "pure",
                        inputs = new object[] {
                            new { id = "left", type = "number" },
                            new { id = "right", type = "number" },
                            new { id = "operator", type = "enum", schema = "compare_operator" },
                            new { id = "tolerance", type = "number" }
                        },
                        outputs = new object[] { new { id = "value", type = "boolean" } },
                        parameters = Array.Empty<object>()
                    }
                }
            },
            bindings = new object[] {
                new {
                    id = CompareBindingId,
                    capabilityId = "forge.condition.predicate.compare",
                    providerId = ProviderId,
                    handler = CompareHandlerName,
                    role = "evaluate",
                    status = "implemented",
                    dependencies = Array.Empty<string>(),
                    requires = Array.Empty<string>()
                }
            }
        }).GetRawText(),
        new Dictionary<string, CommandHandler>(),
        new[] { new BindingSupport(CompareBindingId, "implementation-only", Array.Empty<string>()) })
    {
        Evaluators = new Dictionary<string, EvaluatorHandler> { [CompareHandlerName] = Compare }
    };

    private static JsonElement Compare(EvaluationContext context)
    {
        var left = context.Inputs.GetProperty("left").GetDouble();
        var right = context.Inputs.GetProperty("right").GetDouble();
        var operatorName = context.Inputs.GetProperty("operator").GetString();
        var index = Array.IndexOf(CompareOperators, operatorName);
        if (index < 0) throw new RuntimeContractException("pure-operation", "Unknown comparison operator.");
        var tolerance = context.Inputs.GetProperty("tolerance").GetDouble();
        var value = PureConditions.Compare(left, right, (ScalarComparison)index, tolerance);
        return RuntimeJson.From(new { value });
    }
}
