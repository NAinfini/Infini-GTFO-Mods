using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>Every Trigger capability shape the runtime publishes, declared once by the contract provider
/// `forge.contract.trigger`. A domain package binds one of these ids to its own observation and registers no
/// shape of its own, so an id has exactly one owner and the catalog row, this declaration and the registered
/// manifest cannot drift apart. The rows below are the website catalog rows field for field: label,
/// description, domains, execution tier, output ports and authoring parameters. This provider implements no
/// binding, so it registers capabilities only.
///
/// Three ports are declared `optional` on top of the catalog row: `combat.killed.source`, `combat.limb_damaged.limb`
/// and `objective.wave_*.wave`. Each one names a fact the observing provider only sometimes holds — an unattributed
/// kill, a hit that named no part, a wave instance this runtime mints no handle for — and a required port the
/// publisher cannot fill would fail the kernel's own event shape check on every publish, so the port is declared
/// absent-able instead of always-null.
///
/// The rows a domain package moved here (`combat.killed`, `combat.limb_damaged`, `enemy.tagged`, `enemy.glued`, the
/// four `objective.wave_*` rows) keep the domain lists and labels that provider registered: a plan's `domain` must be
/// one of the capability's `domains` (`node-domain-authority`), so the wave rows carry `enemy` because that is the
/// domain their own plans load in, and the catalog's narrower or newer text for those six rows is a difference for
/// the website batch to reconcile rather than one this file can honour without refusing those plans.</summary>
public static class TriggerContracts
{
    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RegistryJson,
        new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());

    /// <summary>
    /// The shipped rows a domain package binds, by id, in the order asked for. A package's own tests register
    /// these rows under a fixture provider instead of restating the shape, so a port this file changes fails the
    /// package's cases rather than drifting in a private copy. An id this contract does not declare is a fault in
    /// the caller, not a row to invent, so it is refused by name.
    /// </summary>
    public static IReadOnlyList<JsonElement> Rows(IReadOnlyList<string> capabilityIds)
    {
        ArgumentNullException.ThrowIfNull(capabilityIds);
        var declared = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(RuntimeJson.Parse(RegistryJson), "capabilities"))
            declared[RuntimeJson.Text(row, "id")] = row;
        var rows = new JsonElement[capabilityIds.Count];
        for (var index = 0; index < capabilityIds.Count; index++)
        {
            if (!declared.TryGetValue(capabilityIds[index], out var row))
                throw new RuntimeContractException("trigger-contract-row",
                    "No trigger contract declares " + capabilityIds[index] + ".");
            rows[index] = row;
        }
        return Array.AsReadOnly(rows);
    }

    private const string RegistryJson = """
    {
      "providers": [
        {
          "id": "forge.contract.trigger",
          "kind": "native",
          "version": "1.0.0",
          "dependencies": []
        }
      ],
      "capabilities": [
        {
          "id": "forge.trigger.combat.damage_applied",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "实际伤害提交完成",
          "version": "2.0.0",
          "parameters": {
            "description": "伤害真的打上去了，数字是实际值。",
            "summary": "伤害真的打上去了，数字是实际值。",
            "summaryEn": "Fires when damage is really applied; the number is what actually landed.",
            "labelEn": "Damage applied",
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
                "nullable": true,
                "optional": true
              },
              {
                "id": "limb",
                "type": "integer",
                "nullable": true,
                "optional": true
              },
              {
                "id": "friendly_fire",
                "type": "boolean",
                "nullable": true,
                "optional": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.combat.health_changed",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "生命值变化",
          "version": "2.0.0",
          "parameters": {
            "description": "生命值变了。",
            "summary": "生命值变了。",
            "summaryEn": "Fires when health changes.",
            "labelEn": "Health changed",
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
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "敌人开始死亡",
          "version": "2.0.0",
          "parameters": {
            "description": "敌人的死亡流程开始。",
            "summary": "敌人的死亡流程开始。",
            "summaryEn": "Fires when an enemy starts dying. The settled kill, with the source it was credited to, is Killed.",
            "labelEn": "Death started",
            "support": "authoring-contract-only"
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
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "敌人部位被打爆",
          "version": "2.0.0",
          "parameters": {
            "description": "某个可破坏部位被打断了。",
            "summary": "某个可破坏部位被打断了。",
            "summaryEn": "Fires when a breakable body part is destroyed.",
            "labelEn": "Limb broken",
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
        },
        {
          "id": "forge.trigger.combat.shot_committed",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "开火（每一发）",
          "version": "2.0.0",
          "parameters": {
            "description": "确实打出了一发。",
            "summary": "确实打出了一发。整次攻击从起手到收招用「攻击开始」到「攻击完成」。",
            "summaryEn": "Fires when one shot has really been fired; the attack as a whole is Attack started through Attack completed.",
            "labelEn": "Shot committed",
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
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "source",
                "type": "entity"
              },
              {
                "id": "equipment",
                "type": "entity"
              },
              {
                "id": "index",
                "type": "integer"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.combat.hit_candidate",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "产生待解析命中候选",
          "version": "2.0.0",
          "parameters": {
            "description": "产生了一个待确认的命中，还没结算伤害。",
            "summary": "产生了一个待确认的命中，还没结算伤害。",
            "summaryEn": "Fires when a possible hit is found, before any damage is worked out.",
            "labelEn": "Hit candidate",
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
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "source",
                "type": "entity"
              },
              {
                "id": "equipment",
                "type": "entity"
              },
              {
                "id": "target",
                "type": "entity",
                "optional": true
              },
              {
                "id": "limb",
                "type": "integer",
                "nullable": true
              },
              {
                "id": "position",
                "type": "vector3",
                "unit": "m"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.entity.despawned",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "实体被移除",
          "version": "2.0.0",
          "parameters": {
            "description": "某个东西从世界里移除了。",
            "summary": "某个东西从世界里移除了。",
            "summaryEn": "Fires when something is removed from the world.",
            "labelEn": "Despawned",
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
                "id": "entity",
                "type": "entity"
              },
              {
                "id": "reason",
                "type": "string"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.entity.spawned",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "敌人生成",
          "version": "2.0.0",
          "parameters": {
            "description": "一个东西真的出现在世界里了。",
            "summary": "一个东西真的出现在世界里了。",
            "summaryEn": "Fires when something has actually appeared in the world.",
            "labelEn": "Spawned",
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
                "id": "entity",
                "type": "entity"
              },
              {
                "id": "position",
                "type": "vector3",
                "unit": "m"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.equipment.deploy_completed",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "部署成功",
          "version": "2.1.0",
          "parameters": {
            "description": "部署物成功放好了。",
            "summary": "部署物成功放好了。",
            "summaryEn": "Fires when a deployable has been placed successfully.",
            "labelEn": "Deploy completed",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "weapon",
              "tool",
              "consumable"
            ],
            "execution": "host",
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "actor",
                "type": "entity"
              },
              {
                "id": "deployed",
                "type": "entity"
              },
              {
                "id": "equipment_kind",
                "type": "enum",
                "schema": "equipment_kind",
                "nullable": true
              },
              {
                "id": "position",
                "type": "vector3",
                "unit": "m",
                "optional": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.equipment.recall_completed",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "哨戒炮放下 / 收回",
          "version": "2.1.0",
          "parameters": {
            "description": "回收完成，资源也还回来了。",
            "summary": "回收完成。",
            "summaryEn": "Fires when a deployed device has been recalled.",
            "labelEn": "Recall completed",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "weapon",
              "tool",
              "consumable"
            ],
            "execution": "host",
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "actor",
                "type": "entity"
              },
              {
                "id": "deployed",
                "type": "entity"
              },
              {
                "id": "equipment_kind",
                "type": "enum",
                "schema": "equipment_kind",
                "nullable": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.input.equipped",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "装备切入",
          "version": "2.0.0",
          "parameters": {
            "description": "玩家切到了某件装备。",
            "summary": "玩家切到了某件装备。",
            "summaryEn": "Fires when a player switches to a piece of equipment.",
            "labelEn": "Equipped",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
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
                "id": "actor",
                "type": "entity"
              },
              {
                "id": "equipment",
                "type": "entity"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.input.unequipped",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "装备切出",
          "version": "2.0.0",
          "parameters": {
            "description": "玩家把某件装备收起来了。",
            "summary": "玩家把某件装备收起来了。",
            "summaryEn": "Fires when a player puts a piece of equipment away.",
            "labelEn": "Unequipped",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
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
                "id": "actor",
                "type": "entity"
              },
              {
                "id": "equipment",
                "type": "entity"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.interaction.lock_state",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "锁或钥匙条件状态变化",
          "version": "2.0.0",
          "parameters": {
            "description": "锁上了、解锁了，或钥匙条件变了。",
            "summary": "锁上了、解锁了，或钥匙条件变了。",
            "summaryEn": "Fires when a lock is engaged, released, or its key condition changes.",
            "labelEn": "Lock state changed",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "tool",
              "consumable"
            ],
            "execution": "host",
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "door",
                "type": "entity"
              },
              {
                "id": "locked",
                "type": "boolean"
              },
              {
                "id": "key",
                "type": "string"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.interaction.terminal_result",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "终端命令执行结果",
          "version": "2.0.0",
          "parameters": {
            "description": "终端命令跑完，带结果。",
            "summary": "终端命令跑完，带结果。",
            "summaryEn": "Fires when a terminal command finishes, with its result.",
            "labelEn": "Terminal command result",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "tool",
              "consumable"
            ],
            "execution": "host",
            "inputs": [],
            "outputs": [
              {
                "id": "next",
                "type": "execution"
              },
              {
                "id": "terminal",
                "type": "entity"
              },
              {
                "id": "command",
                "type": "string"
              },
              {
                "id": "outcome",
                "type": "enum",
                "schema": "execution_outcome"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.session.expedition_ended",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "远征成功、失败或退出",
          "version": "2.0.0",
          "parameters": {
            "description": "远征结束，无论是通关、团灭还是退出。",
            "summary": "远征结束，无论是通关、团灭还是退出。",
            "summaryEn": "Fires when the expedition ends, whether cleared, wiped or abandoned.",
            "labelEn": "Expedition ended",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "session",
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
                "id": "outcome",
                "type": "enum",
                "schema": "execution_outcome"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.player.low_health",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "玩家进入低血量",
          "version": "2.0.0",
          "parameters": {
            "description": "一个玩家掉进低血量的那一刻。",
            "summary": "玩家进入低血量。",
            "summaryEn": "Fires when a player enters the low-health state.",
            "labelEn": "Player low health",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "player",
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
                "id": "player",
                "type": "entity"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.player.infection_changed",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "感染值变化",
          "version": "2.0.0",
          "parameters": {
            "description": "一个玩家的感染值发生变化。",
            "summary": "感染值变了。",
            "summaryEn": "Fires after a player's infection value is written.",
            "labelEn": "Player infection changed",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "player",
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
                "id": "player",
                "type": "entity"
              },
              {
                "id": "value",
                "type": "number"
              },
              {
                "id": "delta",
                "type": "number"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.player.supply_used",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "使用补给包",
          "version": "2.0.0",
          "parameters": {
            "description": "一个玩家用掉一份补给。",
            "summary": "玩家使用补给。",
            "summaryEn": "Fires when a player applies a medical, ammunition, disinfection or tool supply.",
            "labelEn": "Player used a supply",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "consumable",
              "player",
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
                "id": "player",
                "type": "entity"
              },
              {
                "id": "supply_kind",
                "type": "enum",
                "schema": "supply_kind"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.player.item_picked_up",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "捡起消耗品或资源",
          "version": "2.0.0",
          "parameters": {
            "description": "一个玩家捡起消耗品或资源。",
            "summary": "玩家捡起物品。",
            "summaryEn": "Fires when a player picks up a consumable or a resource.",
            "labelEn": "Player picked an item up",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "consumable",
              "player",
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
                "id": "player",
                "type": "entity"
              },
              {
                "id": "pickup_kind",
                "type": "enum",
                "schema": "pickup_kind"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.input.ping",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "玩家发出标记（Ping）",
          "version": "2.0.0",
          "parameters": {
            "description": "一个玩家按下标记键，带上他标出的位置。",
            "summary": "玩家发出标记。",
            "summaryEn": "Fires when a player pings.",
            "labelEn": "Player pinged",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "player",
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
                "id": "player",
                "type": "entity"
              },
              {
                "id": "position",
                "type": "vector3",
                "unit": "m"
              },
              {
                "id": "target",
                "type": "entity",
                "nullable": true,
                "optional": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.enemy.awakened",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "敌人由休眠转为清醒",
          "version": "2.0.0",
          "parameters": {
            "description": "敌人从睡眠中醒来。",
            "summary": "敌人从睡眠中醒来。",
            "summaryEn": "Fires when a sleeping enemy wakes up.",
            "labelEn": "Enemy awakened",
            "support": "authoring-contract-only"
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
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.enemy.target_acquired",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "获得有效攻击目标",
          "version": "2.0.0",
          "parameters": {
            "description": "敌人锁定了一个攻击目标。",
            "summary": "敌人锁定了一个攻击目标。",
            "summaryEn": "Fires when an enemy locks onto a target.",
            "labelEn": "Target acquired",
            "support": "authoring-contract-only"
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
                "id": "target",
                "type": "entity",
                "nullable": true,
                "optional": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.enemy.target_lost",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "失去攻击目标",
          "version": "2.0.0",
          "parameters": {
            "description": "敌人跟丢了目标。",
            "summary": "敌人跟丢了目标。",
            "summaryEn": "Fires when an enemy loses its target.",
            "labelEn": "Target lost",
            "support": "authoring-contract-only"
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
                "id": "target",
                "type": "entity",
                "nullable": true,
                "optional": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.enemy.scout_detection",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "侦察兵发现玩家 / 尖叫",
          "version": "2.0.0",
          "parameters": {
            "description": "侦察兵的触须扫到了东西。",
            "summary": "侦察兵的触须扫到了东西。",
            "summaryEn": "Fires when a scout's tendrils detect something.",
            "labelEn": "Scout detection",
            "support": "authoring-contract-only"
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
                "id": "target",
                "type": "entity",
                "nullable": true,
                "optional": true
              },
              {
                "id": "position",
                "type": "vector3",
                "unit": "m"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.enemy.scout_scream",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "侦察兵尖叫",
          "version": "2.0.0",
          "parameters": {
            "description": "侦察兵的呼叫进入新阶段。",
            "summary": "侦察兵的呼叫进入新阶段。",
            "summaryEn": "Fires as a scout's scream moves through its stages.",
            "labelEn": "Scout scream",
            "support": "authoring-contract-only"
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
                "id": "phase",
                "type": "integer"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.interaction.door_scan",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "门的扫描开始或完成",
          "version": "1.0.0",
          "parameters": {
            "description": "门的扫描开始或者完成。",
            "summary": "门上的扫描开始或完成。",
            "summaryEn": "Fires when a door's scan starts or completes.",
            "labelEn": "Door scan",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
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
                "id": "door",
                "type": "entity"
              },
              {
                "id": "phase",
                "type": "enum",
                "schema": "interaction_phase"
              },
              {
                "id": "status",
                "type": "string"
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.interaction.lock_broken",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "门锁被砸开或破解",
          "version": "1.0.0",
          "parameters": {
            "description": "门锁被砸开或被破解。",
            "summary": "门锁被砸开或被破解。",
            "summaryEn": "Fires when a door's lock is smashed or hacked.",
            "labelEn": "Door lock broken",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
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
                "id": "door",
                "type": "entity"
              },
              {
                "id": "cause",
                "type": "enum",
                "schema": "door_lock_cause"
              },
              {
                "id": "lock_kind",
                "type": "string",
                "optional": true
              }
            ],
            "parameters": []
          }
        },
        {
          "id": "forge.trigger.interaction.door_broken",
          "owner": "forge.contract.trigger",
          "kind": "trigger",
          "label": "门被攻击或被打破",
          "version": "1.0.0",
          "parameters": {
            "description": "门被攻击，或者被打坏。",
            "summary": "门被敌人攻击或打破。",
            "summaryEn": "Fires when a door enemies can break is attacked or broken.",
            "labelEn": "Door attacked or broken",
            "support": "authoring-contract-only"
          },
          "graph": {
            "domains": [
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
                "id": "door",
                "type": "entity"
              },
              {
                "id": "phase",
                "type": "enum",
                "schema": "door_phase"
              },
              {
                "id": "zone",
                "type": "resource",
                "resourceKind": "zone",
                "schema": "forge.resource.zone",
                "optional": true
              },
              {
                "id": "position",
                "type": "vector3",
                "unit": "m"
              },
              {
                "id": "attacker",
                "type": "entity",
                "optional": true
              }
            ],
            "parameters": []
          }
        },
    {
      "id": "forge.trigger.player.downed",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "玩家倒地",
      "version": "2.0.0",
      "parameters": {
        "description": "玩家倒地了。",
        "summary": "玩家倒地了。",
        "summaryEn": "Fires when a player goes down.",
        "labelEn": "Player downed",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "player",
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
            "id": "player",
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
      "id": "forge.trigger.player.revive_started",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "开始救人 / 被救起",
      "version": "2.0.0",
      "parameters": {
        "description": "有人开始救人。",
        "summary": "有人开始救人。",
        "summaryEn": "Fires when someone starts reviving a downed player.",
        "labelEn": "Revive started",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "player",
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
            "id": "player",
            "type": "entity"
          },
          {
            "id": "rescuer",
            "type": "entity"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.player.revive_cancelled",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "救援被打断",
      "version": "2.0.0",
      "parameters": {
        "description": "救援被打断。",
        "summary": "救援被打断。",
        "summaryEn": "Fires when a revive is interrupted.",
        "labelEn": "Revive cancelled",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "player",
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
            "id": "player",
            "type": "entity"
          },
          {
            "id": "rescuer",
            "type": "entity",
            "nullable": true
          },
          {
            "id": "reason",
            "type": "string"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.player.revived",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "玩家被救起",
      "version": "2.0.0",
      "parameters": {
        "description": "玩家被救起来了。",
        "summary": "玩家被救起来了。",
        "summaryEn": "Fires when a player is revived.",
        "labelEn": "Player revived",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "player",
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
            "id": "player",
            "type": "entity"
          },
          {
            "id": "rescuer",
            "type": "entity",
            "nullable": true
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.player.died",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "规则确认玩家死亡",
      "version": "2.0.0",
      "parameters": {
        "description": "规则确认玩家死亡。",
        "summary": "规则确认玩家死亡。",
        "summaryEn": "Fires when the rules confirm a player has died.",
        "labelEn": "Player died",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "player",
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
            "id": "player",
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
      "id": "forge.trigger.player.teleported",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "玩家传送完成",
      "version": "2.0.0",
      "parameters": {
        "description": "玩家被传送了。",
        "summary": "玩家被传送了。",
        "summaryEn": "Fires when a player has been teleported.",
        "labelEn": "Player teleported",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "player",
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
            "id": "player",
            "type": "entity"
          },
          {
            "id": "from",
            "type": "vector3",
            "unit": "m"
          },
          {
            "id": "to",
            "type": "vector3",
            "unit": "m"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.player.respawned",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "玩家重生完成",
      "version": "2.0.0",
      "parameters": {
        "description": "玩家重生完成。",
        "summary": "玩家重生完成。",
        "summaryEn": "Fires when a player has respawned.",
        "labelEn": "Player respawned",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "player",
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
            "id": "player",
            "type": "entity"
          },
          {
            "id": "position",
            "type": "vector3",
            "unit": "m"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.combat.reload_started",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "换弹开始 / 完成",
      "version": "2.0.0",
      "parameters": {
        "description": "开始装填。",
        "summary": "开始装填。",
        "summaryEn": "Fires when reloading starts.",
        "labelEn": "Reload started",
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
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.combat.reload_transferred",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "弹药进入弹匣",
      "version": "2.0.0",
      "parameters": {
        "description": "子弹真的进弹匣了。",
        "summary": "子弹真的进弹匣了。",
        "summaryEn": "Fires when ammo actually moves into the magazine.",
        "labelEn": "Ammo transferred",
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
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity"
          },
          {
            "id": "amount",
            "type": "integer"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.combat.reload_completed",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "换弹完成",
      "version": "2.0.0",
      "parameters": {
        "description": "装填结束。",
        "summary": "装填结束。",
        "summaryEn": "Fires when reloading ends.",
        "labelEn": "Reload completed",
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
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.equipment.refilled",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "装备补给完成",
      "version": "2.0.0",
      "parameters": {
        "description": "补给完成。",
        "summary": "补给完成。",
        "summaryEn": "Fires when a refill completes.",
        "labelEn": "Refilled",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "weapon",
          "tool",
          "consumable"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity"
          },
          {
            "id": "amount",
            "type": "integer"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.equipment.stack_changed",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "堆叠数量变化",
      "version": "2.0.0",
      "parameters": {
        "description": "消耗品的数量变了。",
        "summary": "消耗品的数量变了。",
        "summaryEn": "Fires when a consumable's stack count changes.",
        "labelEn": "Stack changed",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "weapon",
          "tool",
          "consumable"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "item",
            "type": "entity"
          },
          {
            "id": "count",
            "type": "integer"
          },
          {
            "id": "delta",
            "type": "integer"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.equipment.picked_up",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "拾取完成",
      "version": "2.0.0",
      "parameters": {
        "description": "玩家捡起了东西。",
        "summary": "玩家捡起了东西。",
        "summaryEn": "Fires when a player picks something up.",
        "labelEn": "Picked up",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "weapon",
          "tool",
          "consumable"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "item",
            "type": "entity"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.equipment.dropped",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "物品被捡起 / 放下",
      "version": "2.0.0",
      "parameters": {
        "description": "玩家丢下了东西。",
        "summary": "玩家丢下了东西。",
        "summaryEn": "Fires when a player drops something.",
        "labelEn": "Dropped",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "weapon",
          "tool",
          "consumable"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "item",
            "type": "entity"
          },
          {
            "id": "position",
            "type": "vector3",
            "unit": "m",
            "optional": true
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.equipment.use_started",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "使用前摇开始",
      "version": "2.0.0",
      "parameters": {
        "description": "使用动作开始。",
        "summary": "使用动作开始。",
        "summaryEn": "Fires when the use animation starts.",
        "labelEn": "Use started",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "weapon",
          "tool",
          "consumable"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.equipment.use_failed",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "使用失败并给出原因",
      "version": "2.0.0",
      "parameters": {
        "description": "使用失败，并告诉你原因。弹匣空了打不出去走「弹匣打空 / 全部没弹」，不是这一条。",
        "summary": "使用失败，并告诉你原因。弹匣空了打不出去走「弹匣打空 / 全部没弹」，不是这一条。",
        "summaryEn": "Fires when a use fails, and reports why. A shot refused for an empty magazine is Dry fire.",
        "labelEn": "Use failed",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "weapon",
          "tool",
          "consumable"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity"
          },
          {
            "id": "outcome",
            "type": "enum",
            "schema": "execution_outcome"
          },
          {
            "id": "reason",
            "type": "string"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.session.expedition_started",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "关卡开始（电梯落地）",
      "version": "1.0.0",
      "parameters": {
        "description": "远征正式开始，倒计时和敌人开始跑。"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "map", "type": "resource", "resourceKind": "map", "schema": "forge.resource.map" }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.objective.reactor_wave",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "反应堆每一波",
      "version": "1.0.0",
      "parameters": {
        "description": "反应堆目标推进到下一格。"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "wave", "type": "integer" },
          { "id": "previous", "type": "integer" }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.objective.hsu_sampled",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "HSU 取样完成",
      "version": "1.0.0",
      "parameters": {
        "description": "HSU 的目标物品已经被取出来了。"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "objective", "type": "resource", "resourceKind": "objective", "schema": "forge.resource.objective" },
          { "id": "container", "type": "resource", "resourceKind": "item", "schema": "forge.resource.item", "optional": true }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.session.checkpoint_restored",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "读档回到检查点",
      "version": "1.0.0",
      "parameters": {
        "description": "检查点回档完成。"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.map.zone_entered",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "玩家进入区域",
      "version": "1.0.0",
      "parameters": {
        "description": "有玩家走进了新的区域。"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "player", "type": "entity" },
          { "id": "zone", "type": "string" }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.map.portal_warped",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "穿过维度门",
      "version": "1.0.0",
      "parameters": {
        "description": "维度门把玩家送了过去。"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "portal", "type": "entity" },
          { "id": "dimension", "type": "integer" },
          { "id": "previous", "type": "integer" }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.combat.killed",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "敌人死亡",
      "version": "2.0.0",
      "parameters": {
        "description": "这次伤害把目标打死了。",
        "summary": "这次伤害把目标打死了。敌人刚进入死亡流程用「死亡流程开始」，那一步不一定真的结算成击杀。",
        "summaryEn": "Fires when this damage kills the target. An enemy entering its death is Death started, which may never settle as a kill.",
        "labelEn": "Killed",
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
            "id": "source",
            "type": "entity",
            "nullable": true,
            "optional": true
          },
          {
            "id": "damage_kind",
            "type": "enum",
            "schema": "damage_kind"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.combat.limb_damaged",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "敌人部位受伤",
      "version": "2.0.0",
      "parameters": {
        "description": "某个部位挨打了。",
        "summary": "某个部位挨打了。",
        "summaryEn": "Fires when a specific body part takes damage.",
        "labelEn": "Limb damaged",
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
            "nullable": true,
            "optional": true
          },
          {
            "id": "amount",
            "type": "number",
            "unit": "hp"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.enemy.tagged",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "生物追踪器标记到敌人",
      "version": "1.0.0",
      "parameters": {
        "description": "一个敌人被生物追踪器标记，或者标记消失。",
        "summary": "生物追踪器在敌人身上放了一个标记，或一个标记结束了。",
        "summaryEn": "Fires when the BioTracker places a tag on an enemy, and again when a live tag ends.",
        "labelEn": "Enemy tagged",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "enemy",
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
            "id": "enemy",
            "type": "entity"
          },
          {
            "id": "tagged",
            "type": "boolean"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.enemy.glued",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "敌人被胶住",
      "version": "1.0.0",
      "parameters": {
        "description": "胶打到敌人身上，带上这一次和累计的胶量。",
        "summary": "胶真的挂到敌人身上了，数字是这次增加的胶量和它身上的总量。",
        "summaryEn": "Fires when glue really lands on an enemy; the numbers are what this hit added and what the enemy now carries.",
        "labelEn": "Enemy glued",
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
            "id": "volume",
            "type": "number"
          },
          {
            "id": "total",
            "type": "number"
          }
        ],
        "parameters": []
      }
    },
    {
      "id": "forge.trigger.objective.wave_started",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "命名波次开始",
      "version": "1.0.0",
      "parameters": {
        "description": "一波敌人开始刷。",
        "summary": "一波敌人开始刷。",
        "summaryEn": "Fires when a named wave starts spawning.",
        "labelEn": "Wave started",
        "support": "authoring-contract-only"
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
            "id": "wave",
            "type": "handle",
            "handleKind": "effect",
            "lifetime": "encounter",
            "optional": true
          }
        ],
        "parameters": [
          {
            "id": "resource",
            "type": "resource",
            "role": "structural",
            "required": true,
            "resourceKind": "wave"
          }
        ]
      }
    },
    {
      "id": "forge.trigger.objective.wave_spawned",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "一波敌人刷出",
      "version": "1.0.0",
      "parameters": {
        "description": "这一波的一批敌人已经刷出来了。",
        "summary": "这一波的一批敌人已经刷出来了。",
        "summaryEn": "Fires when a batch of that wave has finished spawning.",
        "labelEn": "Wave batch spawned",
        "support": "authoring-contract-only"
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
            "id": "wave",
            "type": "handle",
            "handleKind": "effect",
            "lifetime": "encounter",
            "optional": true
          },
          {
            "id": "spawned",
            "type": "entity",
            "cardinality": "many"
          },
          {
            "id": "count",
            "type": "integer"
          }
        ],
        "parameters": [
          {
            "id": "resource",
            "type": "resource",
            "role": "structural",
            "required": true,
            "resourceKind": "wave"
          }
        ]
      }
    },
    {
      "id": "forge.trigger.objective.wave_exhausted",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "波次生成预算耗尽",
      "version": "1.0.0",
      "parameters": {
        "description": "这一波的刷怪额度用完了。",
        "summary": "这一波的刷怪额度用完了。",
        "summaryEn": "Fires when a wave has spent its whole spawn budget.",
        "labelEn": "Wave budget exhausted",
        "support": "authoring-contract-only"
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
            "id": "wave",
            "type": "handle",
            "handleKind": "effect",
            "lifetime": "encounter",
            "optional": true
          },
          {
            "id": "count",
            "type": "integer"
          }
        ],
        "parameters": [
          {
            "id": "resource",
            "type": "resource",
            "role": "structural",
            "required": true,
            "resourceKind": "wave"
          }
        ]
      }
    },
    {
      "id": "forge.trigger.objective.wave_cleared",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "某个刷怪波次是否还在刷",
      "version": "1.0.0",
      "parameters": {
        "description": "这一波的敌人全被清光。",
        "summary": "这一波的敌人全被清光。",
        "summaryEn": "Fires when every enemy of that wave is dead.",
        "labelEn": "Wave cleared",
        "support": "authoring-contract-only"
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
            "id": "wave",
            "type": "handle",
            "handleKind": "effect",
            "lifetime": "encounter",
            "optional": true
          }
        ],
        "parameters": [
          {
            "id": "resource",
            "type": "resource",
            "role": "structural",
            "required": true,
            "resourceKind": "wave"
          }
        ]
      }
    }
      ],
      "bindings": []
    }
    """;
}
