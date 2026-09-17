using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The game-independent half of `forge.action.combat.foaming`: the capability row, the binding row, the
/// handler's port shape and the support row the provider's execute handler is registered with. The integration
/// step picks them up from here, so the registry spelling lives in one place instead of being repeated inside
/// `EnemyModule`.
///
/// The row is declared here, in the contract module's own vocabulary, rather than copied out of the website's
/// catalog text: the shape a plan is resolved against is the shape this provider implements.</summary>
internal static class GlueContract
{
    /// <summary>The provider every row here belongs to; the same string `EnemyModule.ProviderId` carries.</summary>
    internal const string ProviderId = "forge.module.gtfo.enemy";

    /// <summary>`forge.action.combat.foaming`: put the game's own foam on an enemy.</summary>
    internal const string FoamingCapability = "forge.action.combat.foaming";
    internal const string FoamingBinding = ProviderId + ".binding.foaming";
    internal const string FoamingHandler = "gtfo.enemy.foaming";

    /// <summary>The one execute handler this contract declares.</summary>
    internal static readonly string[] Handlers = { FoamingHandler };

    /// <summary>The one binding this contract declares. Every id here is a binding id; a capability id is a
    /// separate namespace in the kernel registry and may never be reused as one.</summary>
    internal static readonly string[] Bindings = { FoamingBinding };

    /// <summary>One support row per binding. The permission is the native write the handler performs: it reaches
    /// the game's own foam entry point, which is the enemy's glue write.</summary>
    internal static readonly BindingSupport[] Support =
    {
        new(FoamingBinding, "implementation-only", new[] { "gtfo.enemy.glue.write" })
    };

    /// <summary>The handler's own port set, resolved once at registration against the capability row below.
    /// `targets` is the recipient collection, whose whole declared width separates `source` from the rest.</summary>
    internal static HandlerShape FoamingShape() => new HandlerShape()
        .Inputs("targets", "source", "volume", "strength", "duration").Outputs("result", "foam").Parameters();

    internal static Dictionary<string, HandlerShape> Shapes() => new(StringComparer.Ordinal)
    {
        [FoamingHandler] = FoamingShape()
    };

    /// <summary>The capability row, in the website catalog's own row shape: identity, owner, kind, the label the
    /// website shows, and the graph the handler is resolved against.
    ///
    /// The row is the ruled shape of `forge.action.combat.foaming`, not the pre-ruling catalog row: the
    /// `blob:resource/effect` port has no producer in this round and is gone, and the two things the native
    /// volume descriptor and entry point really take are exposed as `volume` and `strength`. `duration` is the
    /// Forge-side lifetime of the applied foam, because the native glue carries no timer of its own. `foam` is the
    /// effect handle the applied foam can be cleared through, with the lifetime the native glue has.</summary>
    internal const string CapabilityRows = """
        {
          "id": "forge.action.combat.foaming",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "附加泡沫并保持敌人、门、地面不同规则",
          "version": "1.0.0",
          "parameters": {
            "description": "给敌人、门或地面上泡沫，各自按不同规则。",
            "summary": "给敌人上原版泡沫，按原版胶量单位写。",
            "summaryEn": "Foams an enemy through the game's own glue, in the game's own volume unit.",
            "labelEn": "Apply foam",
            "support": "implementation-only"
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
                "id": "volume",
                "type": "number"
              },
              {
                "id": "strength",
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
                "schema": "forge.result.combat.foaming",
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
                "id": "foam",
                "type": "handle",
                "handleKind": "effect",
                "lifetime": "encounter"
              }
            ],
            "parameters": [],
            "recipients": {
              "input": "targets",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "foam.apply"
              ],
              "result": "result",
              "handle": "foam"
            }
          }
        }
        """;

    /// <summary>The binding row the capability above is reached through. `targets` is the recipient collection,
    /// whose whole declared width is what places `source`, `volume`, `strength` and `duration` after it.</summary>
    internal const string BindingRows = """
        {
          "id": "forge.module.gtfo.enemy.binding.foaming",
          "capabilityId": "forge.action.combat.foaming",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.foaming",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """;
}
