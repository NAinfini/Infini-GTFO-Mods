using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// Kernel-owned read-only Data operators for entity values already published through RuntimeEntitySnapshot.
/// Domain packages own the observation that fills the snapshot; this provider owns the reusable reads.
/// </summary>
public static class ObservationContracts
{
    internal const string ProviderId = "forge.contract.observation";

    public const string EntityPositionCapability = "forge.query.entity.position";
    public const string EntityPositionBinding = ProviderId + ".binding.entity_position";
    public const string EntityPositionHandler = "runtime.query.entity_position";

    public const string EntityHealthCapability = "forge.query.entity.health";
    public const string EntityHealthBinding = ProviderId + ".binding.entity_health";
    public const string EntityHealthHandler = "runtime.query.entity_health";

    public const string PositionUnavailableCode = "position-unavailable";
    public const string HealthUnavailableCode = "health-unavailable";
    public const string HealthKindUnsupportedCode = "health-kind-unsupported";

    public static readonly HandlerShape EntityPositionShape =
        new HandlerShape().Inputs("entity").Outputs("position");
    public static readonly HandlerShape EntityHealthShape =
        new HandlerShape().Inputs("entity").Outputs("value", "maximum", "fraction");

    private static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators { get; } =
        new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [EntityPositionHandler] = EntityPosition,
            [EntityHealthHandler] = EntityHealth
        };

    private static IReadOnlyDictionary<string, HandlerShape> Shapes { get; } =
        new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [EntityPositionHandler] = EntityPositionShape,
            [EntityHealthHandler] = EntityHealthShape
        };

    private static readonly BindingSupport[] Support =
    {
        new(EntityPositionBinding, "implementation-only", Array.Empty<string>()),
        new(EntityHealthBinding, "implementation-only", Array.Empty<string>())
    };

    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RegistryJson(),
        new Dictionary<string, CommandHandler>(), Support)
    {
        Evaluators = Evaluators,
        Shapes = Shapes
    };

    private static string RegistryJson() => RuntimeJson.From(new
    {
        providers = new[]
        {
            new { id = ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() }
        },
        capabilities = new[]
        {
            Capability(EntityPositionCapability, "任意实体位置", "读任意一个可观察实体的位置。"),
            Capability(EntityHealthCapability, "生命值", "读一个玩家或敌人的当前生命、最大生命与生命比例。")
        },
        bindings = new[]
        {
            Binding(EntityPositionBinding, EntityPositionCapability, EntityPositionHandler),
            Binding(EntityHealthBinding, EntityHealthCapability, EntityHealthHandler)
        }
    }).GetRawText();

    private static JsonElement Capability(string id, string label, string description) => RuntimeJson.From(new
    {
        id,
        owner = ProviderId,
        kind = "state",
        label,
        version = "1.0.0",
        parameters = new { description },
        graph = BehaviorOperatorGraphSource.Get(id)
    });

    private static JsonElement Binding(string id, string capabilityId, string handler) => RuntimeJson.From(new
    {
        id,
        capabilityId,
        providerId = ProviderId,
        handler,
        role = "observe",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    });

    public static JsonElement EntityPosition(EvaluationContext context)
    {
        var snapshot = Snapshot(context, out _);
        if (snapshot.Position.Count != 3)
            throw new RuntimeContractException(PositionUnavailableCode,
                "This provider publishes no position for the entity.");
        return RuntimeJson.From(new
        {
            position = new[] { snapshot.Position[0], snapshot.Position[1], snapshot.Position[2] }
        });
    }

    public static JsonElement EntityHealth(EvaluationContext context)
    {
        var snapshot = Snapshot(context, out _);
        if (snapshot.Kind != "gtfo.player" && snapshot.Kind != "gtfo.enemy")
            throw new RuntimeContractException(HealthKindUnsupportedCode,
                "Health is currently defined only for player and enemy entities.");
        if (snapshot.Health is not { } current || snapshot.HealthMaximum is not { } maximum)
            throw new RuntimeContractException(HealthUnavailableCode,
                "This provider publishes no health for the entity.");
        return RuntimeJson.From(new
        {
            value = current,
            maximum,
            fraction = maximum > 0d ? current / maximum : 0d
        });
    }

    private static RuntimeEntitySnapshot Snapshot(EvaluationContext context, out EntityReference reference)
    {
        reference = RuntimeJson.Entity(RequiredInput(context));
        if (!context.Query.TrySnapshot(reference, out var snapshot, out var code))
            throw new RuntimeContractException(code, "Entity data read failed: " + code);
        return snapshot!;
    }

    private static JsonElement RequiredInput(EvaluationContext context)
        => context.Inputs.TryGetProperty("entity", out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", "entity");
}
