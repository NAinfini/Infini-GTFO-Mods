using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two combat actions this provider implements for `gtfo.player`: a sourced modifier applied to a
/// player's own attribute, and the removal of a modifier this provider handed out. Both are canonical
/// `forge.action.combat.*` capabilities owned by, and declared by, the combat contract module; this package
/// declares its own binding rows and nothing else, and reads the two rows back from that module rather than
/// restating their text.
///
/// The ruled shape differs from the website catalog's current row in three places, all of them because the
/// native entry cannot express what the catalog used to declare: `attribute` is an `agent_modifier` member
/// instead of a free string, `operation` keeps only `set`/`add`/`subtract` (the native entry takes a value and a
/// decay rate and has no multiplication, minimum or maximum form), and `priority` is gone (the native
/// modification table has no ordering). `agent_modifier.none` is a no-op member rather than an applicable
/// attribute; the handler refuses it by name instead of writing a modification that contributes nothing.
///
/// Only players are recipients. The native modification table is keyed by `Agent`, and this package is the owner
/// of `gtfo.player`; an enemy's attribute is its own profile's business, so a target of any other kind is refused
/// with `modifier-target-kind` rather than resolved through some other provider's table.</summary>
public static class AgentModifierContract
{
    /// <summary>The provider that owns the two capability ids: the combat contract module, which is also where
    /// `forge.action.combat.heal` and `forge.action.combat.damage` are declared.</summary>
    public const string OwnerProviderId = "forge.contract.combat";
    public const string ApplyCapabilityId = CombatContracts.AttributeApplyCapabilityId;
    public const string RemoveCapabilityId = CombatContracts.AttributeRemoveCapabilityId;
    public const string ApplyHandlerName = "gtfo.player.attribute_apply";
    public const string RemoveHandlerName = "gtfo.player.attribute_remove";
    /// <summary>The permission both rows' catalog recipient contracts name.</summary>
    public const string Permission = "attribute.modify";
    /// <summary>The enum set the `attribute` port indexes, in both rows: the catalog's `agent_modifier` set, which
    /// the native `AgentModifier` enum is aligned with member for member.</summary>
    public const string AttributeSet = "agent_modifier";

    /// <summary>The catalog's own domain list for these rows, in its own order.</summary>
    public static readonly string[] Domains = { "enemy", "weapon", "tool", "consumable", "player" };

    /// <summary>Both capability rows as the combat contract declares them, for this provider's own shape checks:
    /// the declaration lives in `CombatContracts` because a row may only be declared by the provider that owns
    /// it. The rows' own code tables are that file's too — this provider keeps no second copy beside them.</summary>
    public static readonly JsonElement ApplyCapability = CombatContracts.AttributeApplyCapability;

    /// <inheritdoc cref="ApplyCapability"/>
    public static readonly JsonElement RemoveCapability = CombatContracts.AttributeRemoveCapability;

    /// <summary>The apply handler's own ports, resolved once at registration against the capability: the recipient
    /// collection, the source reference, the attribute member and the amount, with the one structural operation.
    /// The native half reads this shape rather than describing a second one. The row carries no `duration` port:
    /// how long the modification lasts is the plan's own `effect` block on the step (plan §3.4 动作卡), which the
    /// kernel times through the effect handle this row's capability publishes as `modifier`.</summary>
    public static readonly HandlerShape ApplyShape = new HandlerShape()
        .Inputs("targets", "source", "attribute", "amount").Outputs("result", "modifier").Parameters("operation");

    /// <summary>The remove handler's ports: the effect handles the apply row handed out, and the optional
    /// attribute the request narrows them to.</summary>
    public static readonly HandlerShape RemoveShape = new HandlerShape()
        .Inputs("modifiers", "attribute").Outputs("result");

    /// <summary>This provider's binding id for a row: the capability's own suffix under the Map provider, so the
    /// counterpart of a binding is readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.action.combat.".Length..];

    /// <summary>The two binding rows this provider answers through, in the order the shapes are declared.</summary>
    public static IReadOnlyList<object> Bindings() => Array.AsReadOnly(new[] { ApplyBinding(), RemoveBinding() });

    /// <summary>Both registration support rows, paired with <see cref="Bindings"/> by position.</summary>
    public static IReadOnlyList<BindingSupport> Support() => Array.AsReadOnly(new[] { ApplySupport(), RemoveSupport() });

    /// <summary>The handler shapes this provider's native half answers, keyed by handler name.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [ApplyHandlerName] = ApplyShape,
        [RemoveHandlerName] = RemoveShape
    };

    /// <summary>One execute binding row: this provider's own id, the canonical capability it implements, and the
    /// handler the native half supplies. Neither row depends on another binding, so the closure of a plan that
    /// pins one is the row itself.</summary>
    public static object ApplyBinding() => BindingRow(ApplyCapabilityId, ApplyHandlerName);

    /// <inheritdoc cref="ApplyBinding"/>
    public static object RemoveBinding() => BindingRow(RemoveCapabilityId, RemoveHandlerName);

    /// <summary>Both rows' registration support: each carries the permission its own catalog recipient contract
    /// names, and no other row has to be pinned for it.</summary>
    public static BindingSupport ApplySupport() => new(Binding(ApplyCapabilityId), "implementation-only", new[] { Permission });

    /// <inheritdoc cref="ApplySupport"/>
    public static BindingSupport RemoveSupport() => new(Binding(RemoveCapabilityId), "implementation-only", new[] { Permission });

    private static object BindingRow(string capabilityId, string handler) => new
    {
        id = Binding(capabilityId),
        capabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
