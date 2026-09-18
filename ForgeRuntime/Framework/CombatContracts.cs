using System;
using System.Collections.Generic;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>Shared combat semantics. Receiver implementations register separate explicit bindings.</summary>
public static class CombatContracts
{
    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RegistryJson,
        new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>());

    /// <summary>The sourced attribute apply row and the generic effect-cancel row owned by the shared combat contract.
    /// Native providers bind these rows without restating their semantic graphs.</summary>
    public const string AttributeApplyCapabilityId = "forge.action.combat.attribute_apply";

    /// <summary>The canonical handle cancellation outcome replacing the legacy attribute-remove row.</summary>
    public const string EffectCancelCapabilityId = "forge.action.effect.cancel";

    /// <summary>Both rows as the registry parses them, read back from the one declaration below by id, so a
    /// provider that binds them reads the shape this file publishes instead of a copy of its text.</summary>
    public static readonly JsonElement AttributeApplyCapability = Capability(AttributeApplyCapabilityId);

    /// <summary>The reviewed effect-cancel row read back from the one declaration below.</summary>
    public static readonly JsonElement EffectCancelCapability = Capability(EffectCancelCapabilityId);

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
    """ + PrimitiveContracts.DamageCapabilityJson + """
        ,
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
    """ + PrimitiveContracts.AttributeApplyCapabilityJson + """
        ,
    """ + PrimitiveContracts.EffectCancelCapabilityJson + """
      ],
      "bindings": []
    }
    """;
}
