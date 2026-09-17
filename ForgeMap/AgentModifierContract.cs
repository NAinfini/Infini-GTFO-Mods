using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The two combat actions this provider implements for `gtfo.player`: a sourced modifier applied to a
/// player's own attribute, and the removal of a modifier this provider handed out. Both are canonical
/// `forge.action.combat.*` capabilities owned by the combat contract module; this package declares its own
/// binding rows and nothing else, so the capability text below is the segment the integration moves into
/// `forge.contract.combat` — a module can only declare its own capabilities, and these two ids belong to that
/// provider.
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
    public const string ApplyCapabilityId = "forge.action.combat.attribute_apply";
    public const string RemoveCapabilityId = "forge.action.combat.attribute_remove";
    public const string ApplyHandlerName = "gtfo.player.attribute_apply";
    public const string RemoveHandlerName = "gtfo.player.attribute_remove";
    /// <summary>The permission both rows' catalog recipient contracts name.</summary>
    public const string Permission = "attribute.modify";
    /// <summary>The enum set the `attribute` port indexes, in both rows: the catalog's `agent_modifier` set, which
    /// the native `AgentModifier` enum is aligned with member for member.</summary>
    public const string AttributeSet = "agent_modifier";

    /// <summary>The catalog's own domain list for these rows, in its own order.</summary>
    public static readonly string[] Domains = { "enemy", "weapon", "tool", "consumable", "player" };

    /// <summary>Every code the apply handler can answer with besides the committed row. Declared on the
    /// capability so a refusal is part of the contract rather than a string invented at run time.</summary>
    public static readonly string[] ApplyCodes =
    {
        "authority-or-phase", "modifier-target-kind", "stale-or-unsupported-recipient", "attribute-unknown",
        "attribute-no-op", "operation-unsupported", "amount-out-of-range", "duration-out-of-range",
        "too-many-targets", "modifier-budget", "handle-budget", "modifier-id-exhausted", "native-commit-exception",
        "not-attempted-after-unknown-commit", "attribute-all-rejected", "attribute-all-unknown"
    };

    /// <summary>Every code the remove handler can answer with besides the committed row.</summary>
    public static readonly string[] RemoveCodes =
    {
        "authority-or-phase", "modifier-handle-missing", "stale-handle", "attribute-unknown",
        "modifier-attribute-mismatch", "too-many-targets", "native-clear-exception",
        "not-attempted-after-unknown-commit", "attribute-all-rejected", "attribute-all-unknown"
    };

    /// <summary>The `forge.action.combat.attribute_apply` row under the ruled shape, as the exact text the combat
    /// contract module's capability list takes. It is a literal so the integration splices the same bytes the
    /// registry parses, and so the row reads beside the two rows already declared there.</summary>
    public const string ApplyCapabilityJson = """
    {
      "id": "forge.action.combat.attribute_apply",
      "owner": "forge.contract.combat",
      "kind": "action",
      "label": "应用有来源的属性修正器",
      "version": "1.0.0",
      "parameters": {
        "description": "给目标加一条带来源的属性修正。",
        "summary": "给目标加一条带来源的属性修正。",
        "summaryEn": "Adds a sourced modifier to one of a target's attributes.",
        "labelEn": "Apply attribute modifier",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "enemy",
          "weapon",
          "tool",
          "consumable",
          "player"
        ],
        "execution": "host",
        "inputs": [
          {
            "id": "in",
            "type": "execution"
          },
          {
            "id": "targets",
            "type": "entity",
            "cardinality": "many"
          },
          {
            "entityKinds": ["gtfo.player", "gtfo.enemy", "gtfo.equipment"],
            "id": "source",
            "type": "entity"
          },
          {
            "id": "attribute",
            "type": "enum",
            "schema": "agent_modifier"
          },
          {
            "id": "amount",
            "type": "number"
          },
          {
            "id": "duration",
            "type": "integer",
            "unit": "tick"
          }
        ],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "result",
            "type": "result",
            "schema": "forge.result.combat.attribute_apply",
            "codes": [
              "authority-or-phase",
              "modifier-target-kind",
              "stale-or-unsupported-recipient",
              "attribute-unknown",
              "attribute-no-op",
              "operation-unsupported",
              "amount-out-of-range",
              "duration-out-of-range",
              "too-many-targets",
              "modifier-budget",
              "handle-budget",
              "modifier-id-exhausted",
              "native-commit-exception",
              "not-attempted-after-unknown-commit",
              "attribute-all-rejected",
              "attribute-all-unknown"
            ],
            "fields": [
              {
                "id": "target",
                "type": "entity"
              },
              {
                "id": "status",
                "type": "enum",
                "schema": "execution_outcome"
              },
              {
                "id": "committed",
                "type": "enum",
                "schema": "commit_state"
              },
              {
                "id": "code",
                "type": "string"
              },
              {
                "id": "amount",
                "type": "number"
              },
              {
                "id": "target_count",
                "type": "integer"
              }
            ]
          },
          {
            "id": "modifier",
            "type": "handle",
            "handleKind": "effect",
            "lifetime": "entity_life"
          }
        ],
        "parameters": [
          {
            "id": "operation",
            "type": "enum",
            "role": "structural",
            "required": true,
            "values": [
              "set",
              "add",
              "subtract"
            ],
            "set": "value_operation"
          }
        ],
        "recipients": {
          "input": "targets",
          "target": "entity",
          "cardinality": "many",
          "requires": [
            "attribute.modify"
          ],
          "result": "result",
          "handle": "modifier"
        }
      }
    }
    """;

    /// <summary>The `forge.action.combat.attribute_remove` row under the ruled shape: the handle collection the
    /// apply row handed out, narrowed by the same attribute member. There is no second way to name a removal —
    /// the handle is what the provider minted for the modification it wrote, so it is also what identifies it
    /// here — which is why the row declares no entity input of its own.</summary>
    public const string RemoveCapabilityJson = """
    {
      "id": "forge.action.combat.attribute_remove",
      "owner": "forge.contract.combat",
      "kind": "action",
      "label": "移除指定来源属性修正器",
      "version": "1.0.0",
      "parameters": {
        "description": "把指定来源的属性修正撤掉。",
        "summary": "把指定来源的属性修正撤掉。",
        "summaryEn": "Removes an attribute modifier that came from a given source.",
        "labelEn": "Remove attribute modifier",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "enemy",
          "weapon",
          "tool",
          "consumable",
          "player"
        ],
        "execution": "host",
        "inputs": [
          {
            "id": "in",
            "type": "execution"
          },
          {
            "id": "modifiers",
            "type": "handle",
            "cardinality": "many",
            "handleKind": "effect",
            "lifetime": "entity_life"
          },
          {
            "id": "attribute",
            "type": "enum",
            "schema": "agent_modifier"
          }
        ],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "result",
            "type": "result",
            "schema": "forge.result.combat.attribute_remove",
            "codes": [
              "authority-or-phase",
              "modifier-handle-missing",
              "stale-handle",
              "attribute-unknown",
              "modifier-attribute-mismatch",
              "too-many-targets",
              "native-clear-exception",
              "not-attempted-after-unknown-commit",
              "attribute-all-rejected",
              "attribute-all-unknown"
            ],
            "fields": [
              {
                "id": "target",
                "type": "entity"
              },
              {
                "id": "status",
                "type": "enum",
                "schema": "execution_outcome"
              },
              {
                "id": "committed",
                "type": "enum",
                "schema": "commit_state"
              },
              {
                "id": "code",
                "type": "string"
              },
              {
                "id": "target_count",
                "type": "integer"
              }
            ]
          }
        ],
        "parameters": [],
        "recipients": {
          "input": "modifiers",
          "target": "handle",
          "cardinality": "many",
          "requires": [
            "attribute.modify"
          ],
          "result": "result"
        }
      }
    }
    """;

    /// <summary>Both capability rows as the registry parses them, for a registration that declares the contract
    /// segment (the integration's own contract module) and for the focused suite's shape checks.</summary>
    public static readonly JsonElement ApplyCapability = RuntimeJson.Parse(ApplyCapabilityJson);

    /// <inheritdoc cref="ApplyCapability"/>
    public static readonly JsonElement RemoveCapability = RuntimeJson.Parse(RemoveCapabilityJson);

    /// <summary>The apply handler's own ports, resolved once at registration against the capability: the recipient
    /// collection, the source reference, the attribute member, the amount and the lifetime, with the one
    /// structural operation. The native half reads this shape rather than describing a second one.</summary>
    public static readonly HandlerShape ApplyShape = new HandlerShape()
        .Inputs("targets", "source", "attribute", "amount", "duration").Outputs("result", "modifier").Parameters("operation");

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
