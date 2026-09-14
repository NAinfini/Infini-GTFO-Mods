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
          "id": "forge.trigger.combat.damage_applied",
          "owner": "forge.contract.combat",
          "kind": "trigger",
          "label": "实际伤害提交完成",
          "version": "1.0.0",
          "parameters": {
            "description": "伤害真的打上去了，数字是实际值。"
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
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "source",
                "type": "entity",
                "nullable": true
              },
              {
                "id": "target",
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
                "schema": "damage_kind",
                "nullable": true
              },
              {
                "id": "limb",
                "type": "integer",
                "nullable": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.action.combat.heal",
          "owner": "forge.contract.combat",
          "kind": "action",
          "label": "恢复生命并限制溢出",
          "version": "2.0.0",
          "parameters": {
            "description": "给你选中的目标回血。溢出规则：截断只回到上限；丢弃是会溢出就整次不治疗；溢出允许超过上限，做不到的目标会拒绝。",
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
                "schema": "forge.result.combat.heal"
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
          "id": "forge.trigger.combat.health_changed",
          "owner": "forge.contract.combat",
          "kind": "trigger",
          "label": "生命值变化",
          "version": "1.0.0",
          "parameters": {
            "description": "生命值变了。"
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
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "target",
                "type": "entity"
              },
              {
                "id": "value",
                "type": "number",
                "unit": "hp"
              },
              {
                "id": "delta",
                "type": "number",
                "unit": "hp"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.enemy.death_started",
          "owner": "forge.contract.combat",
          "kind": "trigger",
          "label": "死亡流程开始",
          "version": "1.0.0",
          "parameters": {
            "description": "敌人的死亡流程开始。"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "enemy",
              "logic"
            ],
            "execution": "host",
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "enemy",
                "type": "entity"
              },
              {
                "id": "source",
                "type": "entity",
                "nullable": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.combat.limb_broken",
          "owner": "forge.contract.combat",
          "kind": "trigger",
          "label": "可破坏部位破坏完成",
          "version": "1.0.0",
          "parameters": {
            "description": "某个可破坏部位被打断了。"
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
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "target",
                "type": "entity"
              },
              {
                "id": "limb",
                "type": "integer",
                "nullable": true
              }
            ],
            "parameters": []
          }
        }
      ],
      "bindings": []
    }
    """;
}
