using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>Shared combat semantics. Receiver implementations register separate explicit bindings.</summary>
public static class CombatContracts
{
    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RegistryJson,
        new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());

    /// <summary>The two sourced-attribute rows this provider owns besides heal, damage and revive. A native half
    /// binds them; the row itself is declared here, because a row may only be declared by the provider that owns
    /// it.</summary>
    public const string AttributeApplyCapabilityId = "forge.action.combat.attribute_apply";

    /// <inheritdoc cref="AttributeApplyCapabilityId"/>
    public const string AttributeRemoveCapabilityId = "forge.action.combat.attribute_remove";

    /// <summary>Both rows as the registry parses them, read back from the one declaration below by id, so a
    /// provider that binds them reads the shape this file publishes instead of a copy of its text.</summary>
    public static readonly JsonElement AttributeApplyCapability = Capability(AttributeApplyCapabilityId);

    /// <inheritdoc cref="AttributeApplyCapability"/>
    public static readonly JsonElement AttributeRemoveCapability = Capability(AttributeRemoveCapabilityId);

    private static JsonElement Capability(string capabilityId)
    {
        foreach (var row in RuntimeJson.Rows(RuntimeJson.Parse(RegistryJson), "capabilities"))
            if (RuntimeJson.Text(row, "id") == capabilityId) return row;
        throw new RuntimeContractException("combat-contract-row", "No combat contract declares " + capabilityId + ".");
    }

    private const string RegistryJson = """
    {
      "providers": [
        {
          "id": "forge.contract.combat",
          "kind": "native",
          "version": "1.0.0",
          "dependencies": []
        }
      ],
      "capabilities": [
    """ + PrimitiveContracts.HealCapabilityJson + """
        ,
        {
          "id": "forge.action.combat.damage",
          "owner": "forge.contract.combat",
          "kind": "action",
          "label": "提交有来源与命中上下文的伤害",
          "version": "1.0.0",
          "parameters": {
            "description": "对你选中的目标扣血，实际扣多少由对方的护甲和规则决定。",
            "summary": "对你选中的目标扣血，实际扣多少由对方的护甲和规则决定。",
            "summaryEn": "Takes health off the targets you picked; armour and rules decide how much actually lands.",
            "labelEn": "Deal damage",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
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
                "type": "entity",
                "optional": true
              },
              {
                "entityKinds": ["gtfo.player"],
                "id": "instigator",
                "type": "entity"
              },
              {
                "id": "amount",
                "type": "number",
                "unit": "hp"
              },
              {
                "id": "damage_kind",
                "type": "enum",
                "schema": "damage_kind"
              },
              {
                "id": "limb",
                "type": "integer",
                "optional": true
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
                "schema": "forge.result.combat.damage",
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
                    "type": "number",
                    "unit": "hp"
                  },
                  {
                    "id": "target_count",
                    "type": "integer"
                  }
                ]
              }
            ],
            "parameters": [
              {
                "id": "mitigation_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "receiver_rules",
                  "ignore_armor",
                  "explicit_profile"
                ]
              }
            ],
            "recipients": {
              "input": "targets",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "health.damage"
              ],
              "result": "result"
            }
          }
        },
        {
          "id": "forge.action.combat.revive",
          "owner": "forge.contract.combat",
          "kind": "action",
          "label": "救起倒地玩家",
          "version": "1.0.0",
          "parameters": {
            "description": "按明确规则把倒地的人救起来。",
            "summary": "按明确规则把倒地的人救起来。",
            "summaryEn": "Picks a downed target back up under rules you set.",
            "labelEn": "Revive",
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
                "entityKinds": ["gtfo.player"],
                "id": "source",
                "type": "entity"
              },
              {
                "id": "duration",
                "type": "integer",
                "unit": "tick"
              },
              {
                "id": "restored_health",
                "type": "number",
                "unit": "hp"
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
                "schema": "forge.result.combat.revive",
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
                    "id": "duration",
                    "type": "integer",
                    "unit": "tick"
                  },
                  {
                    "id": "target_count",
                    "type": "integer"
                  }
                ]
              }
            ],
            "parameters": [
              {
                "id": "interrupt_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "cancel",
                  "continue"
                ]
              },
              {
                "id": "cost_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "none",
                  "charge",
                  "consume"
                ]
              }
            ],
            "recipients": {
              "input": "targets",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "health.revive"
              ],
              "result": "result"
            }
          }
        },
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
        },
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
      ],
      "bindings": []
    }
    """;
}
