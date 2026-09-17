using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The enemy behaviour rows this provider implements, spelled as the authoring catalog rows
/// `forge.action.enemy.ability` and `forge.action.enemy.noise_emit`: the same ports, the same structural
/// parameters, and the same result row fields. The rows live beside the handlers because this package's registry
/// is assembled from the native module's own strings, so a row and the member it is submitted through cannot
/// drift apart without failing the registration.
///
/// The two rows carry the ports the implemented actions really answer. `forge.action.enemy.ability` keeps the
/// catalog's `enemies`/`ability` inputs, its `target_policy` (whose `nearest` member is refused by the handler
/// rather than served as `current`) and its `cooldown_scope`; `forge.action.enemy.noise_emit` keeps
/// `source`/`position`/`radius` after ruling 87 deleted `propagation_mask` and `heard_by`.
///
/// Three rows of the same family are deleted by ruling 88 and are not declared: `forge.action.enemy.patrol`
/// (no waypoint-path entry point exists), `flee` (the game's behaviour machine has no flee state) and
/// `threat_change` (the game keeps no threat table). `forge.action.enemy.investigate` is reshaped by ruling 88
/// into a state request, which is `forge.action.enemy.state_request`'s row in the enemy control family and is not
/// declared here under a second id; see `ForgeEnemy/evidence/enemy-behavior-ledger.json` for the evidence behind
/// each of those decisions.
///
/// These declarations reach the registry through the slice's integration fragment: the shared registration point
/// (`EnemyModule.Declare`) and the shared registry string are not this slice's files to edit.</summary>
internal static class EnemyBehaviorContract
{
    /// <summary>The provider id every binding row below names, spelled once. It is the same id the enemy module
    /// registers under, and the contract repeats it rather than borrowing the module's constant so that the
    /// declaration half can be compiled — and checked against the rows — without the native half.</summary>
    internal const string ProviderId = "forge.module.gtfo.enemy";
    internal const string AbilityCapability = "forge.action.enemy.ability";
    internal const string NoiseEmitCapability = "forge.action.enemy.noise_emit";
    internal const string AbilityBinding = ProviderId + ".binding.ability";
    internal const string NoiseEmitBinding = ProviderId + ".binding.noise_emit";
    internal const string AbilityHandler = "gtfo.enemy.ability";
    internal const string NoiseEmitHandler = "gtfo.enemy.noise_emit";

    /// <summary>The handlers' own port sets, declared next to the rows they are resolved against so neither half
    /// can describe a different layout: registration refuses a row whose ports and a shape that disagrees.</summary>
    internal static readonly HandlerShape AbilityPorts = new HandlerShape()
        .Inputs("enemies", "ability").Outputs("result").Parameters("target_policy", "cooldown_scope");
    internal static readonly HandlerShape NoiseEmitPorts = new HandlerShape()
        .Inputs("source", "position", "radius").Outputs("result");

    internal static readonly string[] CapabilityIds = { AbilityCapability, NoiseEmitCapability };

    /// <summary>The two rows, verbatim in catalog shape and order. `forge.action.enemy.ability`'s result schema is
    /// `forge.result.enemy.ability` and `noise_emit`'s is `forge.result.enemy.noise_emit`, so the row's own fields
    /// are the ones the contract resolver checks the handler's shape against.</summary>
    internal const string CapabilityRows = """
        {
          "id": "forge.action.enemy.ability",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "调用已注册敌人能力",
          "version": "1.0.0",
          "parameters": {
            "description": "调用敌人已注册的能力，目标策略只能取当前目标或最近的敌人。",
            "summary": "调用敌人已注册的能力。",
            "summaryEn": "Calls one of an enemy's registered abilities.",
            "labelEn": "Use AI ability",
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
                "entityKinds": ["gtfo.enemy"],
                "id": "enemies",
                "type": "entity",
                "cardinality": "many"
              },
              {
                "id": "ability",
                "type": "resource",
                "resourceKind": "ability",
                "schema": "forge.resource.ability"
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
                "schema": "forge.result.enemy.ability",
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
            "parameters": [
              {
                "id": "target_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "current",
                  "nearest"
                ]
              },
              {
                "id": "cooldown_scope",
                "type": "enum",
                "role": "structural",
                "required": true,
                "set": "lifetime_scope"
              }
            ],
            "recipients": {
              "input": "enemies",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "ai.ability"
              ],
              "result": "result"
            }
          }
        },
        {
          "id": "forge.action.enemy.noise_emit",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "发出具有传播定义的噪声",
          "version": "1.0.0",
          "parameters": {
            "description": "在世界里制造一个会传播的声音。",
            "summary": "在世界里制造一个会传播的声音。",
            "summaryEn": "Makes a noise in the world that spreads.",
            "labelEn": "Emit noise",
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
                "id": "source",
                "type": "entity"
              },
              {
                "id": "position",
                "type": "vector3",
                "unit": "m"
              },
              {
                "id": "radius",
                "type": "number",
                "unit": "m"
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
                "schema": "forge.result.enemy.noise_emit",
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
                    "id": "radius",
                    "type": "number",
                    "unit": "m"
                  }
                ]
              }
            ],
            "parameters": [],
            "recipients": {
              "input": "source",
              "target": "entity",
              "cardinality": "one",
              "requires": [
                "ai.noise"
              ],
              "result": "result"
            }
          }
        }
        """;

    /// <summary>The two bindings, one per row above: this provider's own name for the registration, the canonical
    /// capability it implements, and the handler the native module supplies. `requires` stays empty because the
    /// enforcement the catalog names (`ai.ability`, `ai.noise`) is this module's own per-target check, not another
    /// binding.</summary>
    internal const string BindingRows = """
        {
          "id": "forge.module.gtfo.enemy.binding.ability",
          "capabilityId": "forge.action.enemy.ability",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.ability",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.noise_emit",
          "capabilityId": "forge.action.enemy.noise_emit",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.noise_emit",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """;

    /// <summary>The handler names the two binding rows above spell, so the registration site and the binding rows
    /// cannot disagree.</summary>
    internal static readonly string[] HandlerNames = { AbilityHandler, NoiseEmitHandler };

    /// <summary>The two handlers' own port sets, resolved once at registration against the rows above.</summary>
    internal static Dictionary<string, HandlerShape> Shapes() => new(StringComparer.Ordinal)
    {
        [AbilityHandler] = AbilityPorts,
        [NoiseEmitHandler] = NoiseEmitPorts
    };

    /// <summary>One binding's registration support. Triggering an enemy's own ability component writes state the
    /// enemy behaviour permission already covers; emitting a noise writes the world's noise channel, which the
    /// enemy domain owns because the noise vocabulary is the enemy AI's stimulus.</summary>
    internal static BindingSupport[] Support() => new[]
    {
        new BindingSupport(AbilityBinding, "implementation-only", new[] { "gtfo.enemy.behavior.write" }),
        new BindingSupport(NoiseEmitBinding, "implementation-only", new[] { "gtfo.enemy.noise.write" })
    };
}
