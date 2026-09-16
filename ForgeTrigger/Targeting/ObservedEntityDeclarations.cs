using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeTrigger.Targeting;

/// <summary>The event-role selectors: the rows that answer with a role of the event being dispatched instead of
/// reading a world. Every one of them reads <see cref="EvaluationContext.Actors"/>, the one delivery table, and
/// never rebuilds a role-to-port mapping here: the four roles the catalog marks nullable answer null when the
/// event does not carry them, and `self` — the one role no event may leave out — is refused by name through
/// <see cref="RuntimeActorContext.Require"/>, the same `actor-missing` the kernel raises for a role a contract
/// says it answers with.</summary>
public static class ObservedEntityDeclarations
{
    /// <summary>The declared rows in registration order, in the one table this family owns.</summary>
    public static ObservedFamily Family { get; } = ObservedFamily.Declare(
        ObservedDeclaration.Node("forge.selector.target.self", "query", "当前资源实例", "选中这块逻辑挂在的东西本身。",
            ObservedDeclaration.NoPorts, ObservedDeclaration.Outputs(ObservedDeclaration.Entity("target")), ObservedDeclaration.NoParameters,
            new HandlerShape().Outputs("target"), context => Role(context, "self", required: true)),
        ObservedDeclaration.Node("forge.selector.target.owner", "query", "所属玩家或拥有者", "选中拥有者，比如拿着这把枪的玩家。",
            ObservedDeclaration.NoPorts, ObservedDeclaration.Outputs(ObservedDeclaration.NullableEntity("target")), ObservedDeclaration.NoParameters,
            new HandlerShape().Outputs("target"), context => Role(context, "owner", required: false)),
        ObservedDeclaration.Node("forge.selector.target.source", "query", "直接效果来源实体", "选中直接造成这件事的东西。",
            ObservedDeclaration.NoPorts, ObservedDeclaration.Outputs(ObservedDeclaration.NullableEntity("target")), ObservedDeclaration.NoParameters,
            new HandlerShape().Outputs("target"), context => Role(context, "source", required: false)),
        ObservedDeclaration.Node("forge.selector.target.instigator", "query", "让敌人去追某个玩家", "选中最初挑起这件事的人。",
            ObservedDeclaration.NoPorts, ObservedDeclaration.Outputs(ObservedDeclaration.NullableEntity("target")), ObservedDeclaration.NoParameters,
            new HandlerShape().Outputs("target"), context => Role(context, "instigator", required: false)),
        ObservedDeclaration.Node("forge.selector.target.event_target", "query", "事件中的目标实体", "选中事件里被作用的那个对象。",
            ObservedDeclaration.NoPorts, ObservedDeclaration.Outputs(ObservedDeclaration.NullableEntity("target")), ObservedDeclaration.NoParameters,
            new HandlerShape().Outputs("target"), context => Role(context, "event-target", required: false)));

    /// <summary>The role's reference as the selector's only output: the four nullable roles answer null when the
    /// event does not carry them, and `self` refuses the step by name instead of answering a placeholder.</summary>
    private static JsonElement Role(EvaluationContext context, string role, bool required)
        => ObservedEvaluation.Value(required ? context.Actors.Require(role) : context.Actors.Get(role));
}
