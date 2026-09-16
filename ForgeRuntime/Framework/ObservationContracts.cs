using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>The kernel-owned read-only rows that are about any entity a wire already carries, declared by the
/// provider that can answer them for every kind.
///
/// <c>forge.query.entity.position</c> (rule 146.4d) is the position of an entity a plan holds, not only of the two
/// actor kinds that already have a row of their own: an author holding a door, a terminal or a deployed item can
/// place something at where it stands. It answers from the kernel's own entity observation — the same snapshot
/// every other read goes through — so the provider that answered the reference is the provider that publishes the
/// position, and a kind no provider observes is refused with the kernel's own code instead of being answered as
/// an origin.
///
/// The row is the kernel's rather than a domain package's because the read is: a provider that registered an
/// observer for its own kind is exactly what this row asks, and no package owns "any entity". It declares a world
/// port, so its tier is the evaluated read tier (`query`) and its binding is an `observe` binding with one
/// evaluator, the same shape the value rows of the domain packages use.</summary>
public static class ObservationContracts
{
    internal const string ProviderId = "forge.contract.observation";

    public const string EntityPositionCapability = "forge.query.entity.position";
    public const string EntityPositionBinding = "forge.contract.observation.binding.entity_position";
    public const string EntityPositionHandler = "runtime.query.entity_position";

    /// <summary>The one shape of the position handler: the entity the read is about and the metres it stands at.</summary>
    public static readonly HandlerShape EntityPositionShape = new HandlerShape().Inputs("entity").Outputs("position");

    /// <summary>The refusal a snapshot without a three-number position gets. It is the provider's own answer, so it
    /// reads like the same refusal the player and enemy position rows raise rather than like a missing port.</summary>
    public const string PositionUnavailableCode = "position-unavailable";

    /// <summary>The one registration support row: a read-only row owns no object and writes nothing, so it
    /// declares no permission.</summary>
    private static BindingSupport Support { get; } = new(EntityPositionBinding, "implementation-only", Array.Empty<string>());

    private static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators { get; } =
        new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [EntityPositionHandler] = EntityPosition
        };

    private static IReadOnlyDictionary<string, HandlerShape> Shapes { get; } =
        new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [EntityPositionHandler] = EntityPositionShape
        };

    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RegistryJson,
        new Dictionary<string, CommandHandler>(), new[] { Support })
    {
        Evaluators = Evaluators,
        Shapes = Shapes
    };

    /// <summary>One entity's current position, read through the step's own budgeted session: the read is charged
    /// to the query budget, a reference the kernel cannot observe is that read's refusal, and a snapshot whose
    /// provider publishes no position is refused by name rather than answered as the origin.</summary>
    public static JsonElement EntityPosition(EvaluationContext context)
    {
        var reference = RuntimeJson.Entity(RequiredInput(context));
        if (!context.Query.TrySnapshot(reference, out var snapshot, out var code))
            throw new RuntimeContractException(code, "Entity position read failed: " + code);
        var position = snapshot!.Position;
        if (position.Count != 3)
            throw new RuntimeContractException(PositionUnavailableCode, "This provider publishes no position for the entity.");
        return RuntimeJson.From(new { position = new[] { position[0], position[1], position[2] } });
    }

    /// <summary>The row's one required input, refused by name when the plan left it out: an absent entity answered
    /// as the origin would read exactly like an entity standing there.</summary>
    private static JsonElement RequiredInput(EvaluationContext context)
        => context.Inputs.TryGetProperty("entity", out var value) && value.ValueKind != JsonValueKind.Null
            ? value
            : throw new RuntimeContractException("missing-field", "entity");

    private const string RegistryJson = """
    {
      "providers": [
        {
          "id": "forge.contract.observation",
          "kind": "native",
          "version": "1.0.0",
          "dependencies": []
        }
      ],
      "capabilities": [
        {
          "id": "forge.query.entity.position",
          "owner": "forge.contract.observation",
          "kind": "state",
          "label": "任意实体位置",
          "version": "1.0.0",
          "parameters": {
            "description": "读任意一个实体的当前位置。"
          },
          "graph": {
            "domains": [ "map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic" ],
            "execution": "query",
            "inputs": [
              { "id": "entity", "type": "entity" }
            ],
            "outputs": [
              { "id": "position", "type": "vector3", "unit": "m" }
            ],
            "parameters": []
          }
        }
      ],
      "bindings": [
        {
          "id": "forge.contract.observation.binding.entity_position",
          "capabilityId": "forge.query.entity.position",
          "providerId": "forge.contract.observation",
          "handler": "runtime.query.entity_position",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
      ]
    }
    """;
}
