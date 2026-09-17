using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The game-independent half of `forge.enemy.profile`: the capability rows, binding rows, handler shapes
/// and support rows this provider's execute handlers are registered with. `EnemyRegistration` composes them into
/// the one registry text both the native module and the release export read, so the registry spelling lives in one
/// place instead of being repeated inside `EnemyModule`. The file sits in the module root beside the other family
/// contracts — the root sources are what the release export compiles — and keeps the `ForgeEnemy.Native`
/// namespace the way `EnemyBehaviorContract` does, because the assembly boundary is the csproj, not the folder.
///
/// Five rows were surveyed against build 20403457. Only `phase_set` has a native write entry whose effect the
/// game itself replicates, so only that row has a capability row and a binding here; the other four are absent
/// from <see cref="CapabilityRows"/> on purpose and their reasons are enumerated in
/// <see cref="Unimplemented"/> rather than being filled in with a shape the runtime cannot honour. Two of the
/// four are nevertheless served by this package without a graph row: `limb_profile` and `perception_profile` are
/// spawn-time static profiles, written on every peer from one document, which is why their reasons no longer say
/// the values cannot be delivered at all.</summary>
internal static class EnemyProfileContract
{
    /// <summary>The provider every row here belongs to; the same string `EnemyModule.ProviderId` carries.</summary>
    internal const string ProviderId = "forge.module.gtfo.enemy";

    /// <summary>`forge.action.enemy.phase_set`: switch a SquidBoss to another phase.</summary>
    internal const string PhaseSetCapability = "forge.action.enemy.phase_set";
    internal const string PhaseSetBinding = ProviderId + ".binding.phase_set";
    internal const string PhaseSetHandler = "gtfo.enemy.phase_set";

    /// <summary>The one execute handler this contract declares.</summary>
    internal static readonly string[] Handlers = { PhaseSetHandler };

    /// <summary>The one binding this contract declares. Every id here is a binding id; a capability id is a
    /// separate namespace in the kernel registry and may never be reused as one.</summary>
    internal static readonly string[] Bindings = { PhaseSetBinding };

    /// <summary>The capability rows, in the website catalog's own row shape: identity, owner, kind, the label the
    /// website shows, and the graph the handler is resolved against. The mapping is one to one with the catalog
    /// row `forge.action.enemy.phase_set` — `enemies` (many entity), `phase` and `expected_phase` (integer), the
    /// `reset_policy` structural parameter, and the result fields in their declared order.</summary>
    internal const string CapabilityRows = """
        {
          "id": "forge.action.enemy.phase_set",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "切换首领行为配置",
          "version": "1.0.0",
          "parameters": {
            "description": "切换首领的阶段配置。",
            "summary": "切换首领的阶段配置。",
            "summaryEn": "Switches a boss to another phase.",
            "labelEn": "Set boss phase",
            "support": "implementation-only"
          },
          "graph": {
            "domains": [
              "map",
              "room",
              "enemy",
              "tool",
              "consumable"
            ],
            "execution": "host",
            "inputs": [
              {
                "id": "in",
                "type": "execution"
              },
              {
                "id": "enemies",
                "type": "entity",
                "cardinality": "many"
              },
              {
                "id": "phase",
                "type": "integer"
              },
              {
                "id": "expected_phase",
                "type": "integer"
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
                "schema": "forge.result.enemy.phase_set",
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
                    "id": "phase",
                    "type": "integer"
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
                "id": "reset_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "keep",
                  "reset"
                ]
              }
            ],
            "recipients": {
              "input": "enemies",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "ai.phase"
              ],
              "result": "result"
            }
          }
        }
        """;

    /// <summary>The binding row the capability above is reached through. `enemies` is the recipient collection,
    /// whose whole declared width is what places `phase` and `expected_phase` after it.</summary>
    internal const string BindingRows = """
        {
          "id": "forge.module.gtfo.enemy.binding.phase_set",
          "capabilityId": "forge.action.enemy.phase_set",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.phase_set",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """;

    /// <summary>One support row per binding. The permission is the native write the handler performs: it writes a
    /// replicated boss phase through the game's own setter, never a read the provider already holds.</summary>
    internal static readonly BindingSupport[] Support =
    {
        new(PhaseSetBinding, "implementation-only", new[] { "gtfo.enemy.behavior.write" })
    };

    /// <summary>The handler's own port set, resolved once at registration against the capability row above.
    /// `enemies` is the recipient collection; `phase` and `expected_phase` follow it in the declared order.</summary>
    internal static HandlerShape PhaseSetShape() => new HandlerShape()
        .Inputs("enemies", "phase", "expected_phase").Outputs("result").Parameters("reset_policy");

    internal static Dictionary<string, HandlerShape> Shapes() => new(StringComparer.Ordinal)
    {
        [PhaseSetHandler] = PhaseSetShape()
    };

    /// <summary>The rows of this slice that have no capability row here, with the reason each one is absent. The
    /// shape detail is in `ForgeEnemy/evidence/enemy-profile-actions.json`; this list is what a reader of the
    /// code needs to know before looking for a handler that does not exist.</summary>
    internal static readonly (string Capability, string Reason)[] Unimplemented =
    {
        ("forge.action.enemy.attack",
            "未实现：缺 ability 资源 provider 与目录端口。native 唯一入口 ES_EnemyAttackBase.ActivateState 需要 "
            + "(Agent 目标, AgentAbility, abilityIndex)，目录 `ability` 是 resource(ability)，没有任何 provider 登记 "
            + "forge.resource.ability，目录也没有 ability_index 端口，ai_target 传入的 entity 引用无法反解成别的 provider 的原生 Agent。"),
        ("forge.action.enemy.behavior_interrupt",
            "未实现：缺句柄池与实例台账。native ES_Base.Stop() 在本版本没有任何 override（基类槽位是共享 thunk），"
            + "目录输入是 handle(lease)，运行时没有可用来表达该行的句柄与行为实例台账；本行需要行为账本，超出本片范围。"),
        ("forge.action.enemy.limb_profile",
            "未实现：图上不注册这一行——本版本改由生成期静态档案服务，不再按「缺同步入口」处理。"
            + "Dam_EnemyDamageLimb 的 m_health/m_healthMax/m_weakspotDamageMulti/m_armorDamageMulti 与 SetLimbDamageType "
            + "都是可写实例字段且没有一个进入 SNet 复制，但档案在每个原生生命的生成点由各端用同一份文档写同样数值 "
            + "（Profile/EnemyProfiles.cs 的 limbs 段 + EnemyModuleProfiles.ApplyLimbs），因此不需要复制通道。"),
        ("forge.action.enemy.perception_profile",
            "未实现：图上不注册这一行——本版本改由生成期静态档案服务。EnemyDetection 的 m_movementDetectionDistance/"
            + "m_noiseDetectionRange/m_detectionCooldownSpeed/m_detectionBuildupSpeed 没有原生复制通道，"
            + "档案在生成期应用（Profile/EnemyProfiles.cs 的 detection 段 + EnemyModuleProfiles.ApplyDetection）。")
    };
}
