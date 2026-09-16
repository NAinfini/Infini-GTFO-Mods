using System;
using System.Collections.Generic;

namespace ForgeRuntime.Framework;

/// <summary>Shared combat semantics. Receiver implementations register separate explicit bindings.</summary>
public static class CombatContracts
{
    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RegistryJson,
        new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());
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
        {
          "id": "forge.action.combat.heal",
          "owner": "forge.contract.combat",
          "kind": "action",
          "label": "恢复生命并限制溢出",
          "version": "2.0.0",
          "parameters": {
            "description": "给你选中的目标回血。溢出规则：截断只回到上限；丢弃是会溢出就整次不治疗；溢出允许超过上限，做不到的目标会拒绝。",
            "summary": "给你选中的目标回血。溢出规则：截断只回到上限；丢弃是会溢出就整次不治疗；溢出允许超过上限，做不到的目标会拒绝。",
            "summaryEn": "Puts health back on the targets you picked. Clamp stops at maximum health; discard skips the whole heal if it would overflow; overheal may exceed the maximum, and targets that cannot are rejected.",
            "labelEn": "Heal",
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
                "id": "source",
                "type": "entity"
              },
              {
                "id": "amount",
                "type": "number",
                "unit": "hp"
              },
              {
                "id": "cap",
                "type": "number",
                "unit": "hp",
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
                "schema": "forge.result.combat.heal",
                "codes": [
                  "overheal-unsupported",
                  "amount-out-of-range",
                  "invalid-cap",
                  "too-many-targets",
                  "authority-or-phase",
                  "not-attempted-after-unknown-commit",
                  "stale-or-unsupported-recipient",
                  "missing-health-receiver",
                  "health-receiver-owner-mismatch",
                  "not-alive",
                  "invalid-health-state",
                  "would-overheal",
                  "quantization-failed",
                  "state-changed-before-commit",
                  "preflight-exception",
                  "receiver-changed-during-commit",
                  "unexpected-health-readback",
                  "native-commit-exception",
                  "readback-exception",
                  "heal-all-rejected",
                  "heal-all-unknown"
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
                "id": "overheal_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "clamp",
                  "discard",
                  "overheal"
                ]
              }
            ],
            "recipients": {
              "input": "targets",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "health.heal"
              ],
              "result": "result"
            }
          }
        },
        {
          "id": "forge.action.combat.damage",
          "owner": "forge.contract.combat",
          "kind": "action",
          "label": "提交有来源与命中上下文的伤害",
          "version": "2.0.0",
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
                "id": "source",
                "type": "entity",
                "optional": true
              },
              {
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
          "label": "执行有明确规则的救援",
          "version": "2.0.0",
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
        }
      ],
      "bindings": []
    }
    """;
}
