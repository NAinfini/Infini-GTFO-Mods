using System;
using System.Collections.Generic;

namespace ForgeRuntime.Framework;

/// <summary>Shared combat semantics. Receiver implementations register separate explicit bindings.</summary>
public static class CombatContracts
{
    public static RuntimeModule Module() => new("1.0.0", RegistryJson,
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
          "label": "实际伤害已提交",
          "version": "1.0.0",
          "parameters": {
            "description": "已经提交的实际伤害事实，目标身份明确；来源无法核实时保留未知。",
            "amountUnit": "hp"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "enemy",
              "weapon",
              "tool",
              "consumable",
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
                "id": "target",
                "type": "entity"
              },
              {
                "id": "actual_damage",
                "type": "number",
                "unit": "hp"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.action.combat.heal",
          "owner": "forge.contract.combat",
          "kind": "action",
          "label": "恢复生命",
          "version": "1.0.0",
          "parameters": {
            "description": "通过显式接收目标恢复绝对HP，阵营关系独立；当前值封顶最大生命，不隐式复活。实际支持由所选Binding明确。",
            "amountUnit": "hp"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "enemy",
              "weapon",
              "tool",
              "consumable",
              "logic"
            ],
            "execution": "host",
            "inputs": [
              {
                "id": "in",
                "type": "execution"
              },
              {
                "id": "target",
                "type": "entity"
              }
            ],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              }
            ],
            "parameters": [
              {
                "id": "amount",
                "type": "number",
                "required": true,
                "minimum": 1e-06,
                "maximum": 1000000
              }
            ],
            "recipients": {
              "input": "target",
              "requires": [
                "health.heal"
              ]
            }
          }
        },
        {
          "id": "forge.trigger.combat.health_changed",
          "owner": "forge.contract.combat",
          "kind": "trigger",
          "label": "实际生命变化",
          "version": "1.0.0",
          "parameters": {
            "description": "已经提交的生命变化，包含变化前后绝对HP与有符号差值；每个Binding声明自己的观察来源。",
            "amountUnit": "hp"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "enemy",
              "weapon",
              "tool",
              "consumable",
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
                "id": "target",
                "type": "entity"
              },
              {
                "id": "health_before",
                "type": "number",
                "unit": "hp"
              },
              {
                "id": "health_after",
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
            "description": "OnDead正常返回且同生命状态为死亡的流程事实；不是击杀归因、奖励或尸体清理证明。"
          },
          "graph": {
            "domains": [
              "enemy",
              "map",
              "room",
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
                "id": "target",
                "type": "entity"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.combat.limb_broken",
          "owner": "forge.contract.combat",
          "kind": "trigger",
          "label": "部位破坏完成",
          "version": "1.0.0",
          "parameters": {
            "description": "同一完整实体生命、健康接收器和已索引部位在原生窗口中由未破坏变为已破坏；不推断攻击者或击杀。"
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
                "id": "limb_id",
                "type": "integer"
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
