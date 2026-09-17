using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The enemy control rows this provider implements, spelled as the authoring catalog rows
/// `forge.action.enemy.awaken`, `forge.action.enemy.sleep` and `forge.action.enemy.move_to`: the same ports, the
/// same structural enums in the same order, and the same result row fields. The declaration half lives in the
/// game-independent package assembly, so the registration the game-side module builds and the one the release
/// export builds read the same rows instead of two copies of them; the native handlers that answer the rows are
/// the game-bound assembly's own.
///
/// Three rows of the same family stay undeclared here, because declaring a capability this provider cannot
/// answer would advertise a node that can never run: `forge.action.enemy.target_set` declares a `lease` handle
/// output, `forge.action.enemy.target_clear` and `forge.action.enemy.state_request` consume those handles as
/// their recipient input, and the runtime gives a provider no way to mint or read one
/// (`RuntimeKernel.CreateHandle` is private; `RuntimeModuleHandle` exposes Publish, CancelScope and Dispose).
/// They are reported in `ForgeEnemy/evidence/enemy-control-actions.json` rather than half-implemented.</summary>
internal static class EnemyControlContract
{
    /// <summary>The three binding ids, handler names and capability ids, named rather than spelled at each call
    /// site: the rows below declare them, `Support` carries them and the native handlers answer them.</summary>
    internal const string AwakenBinding = ModuleDefinition.ProviderId + ".binding.awaken";
    internal const string SleepBinding = ModuleDefinition.ProviderId + ".binding.sleep";
    internal const string MoveToBinding = ModuleDefinition.ProviderId + ".binding.move_to";
    internal const string AwakenHandler = "gtfo.enemy.awaken";
    internal const string SleepHandler = "gtfo.enemy.sleep";
    internal const string MoveToHandler = "gtfo.enemy.move_to";
    internal const string AwakenCapability = "forge.action.enemy.awaken";
    internal const string SleepCapability = "forge.action.enemy.sleep";
    internal const string MoveToCapability = "forge.action.enemy.move_to";

    /// <summary>The rows this provider answers, in catalog order. `CapabilityRows` is the declaration half and
    /// `BindingRows` the registration half; both are the same three actions in the same order, so a reader can
    /// match them by index.</summary>
    internal static readonly string[] CapabilityIds =
    {
        AwakenCapability, SleepCapability, MoveToCapability
    };

    /// <summary>The three capability rows, one JSON object each, in the order `CapabilityIds` names. They are
    /// three strings rather than one block so the registration site can append them after the row the provider
    /// already declares — the capabilities array is a list, and its last element carries no trailing comma.</summary>
    internal static readonly string[] CapabilityRows =
    {
        """
        {
          "id": "forge.action.enemy.awaken",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "唤醒敌人 / 让敌人休眠",
          "version": "1.0.0",
          "parameters": {
            "description": "把睡着的敌人叫醒。",
            "summary": "把睡着的敌人叫醒。",
            "summaryEn": "Wakes a sleeping enemy.",
            "labelEn": "Awaken enemy",
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
                "id": "source",
                "type": "entity"
              },
              {
                "id": "alert_amount",
                "type": "number"
              },
              {
                "id": "reason",
                "type": "string"
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
                "schema": "forge.result.enemy.awaken",
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
                    "id": "alert_amount",
                    "type": "number"
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
                "id": "wake_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "immediate",
                  "gradual"
                ]
              }
            ],
            "recipients": {
              "input": "enemies",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "ai.wake"
              ],
              "result": "result"
            }
          }
        }
        """,
        """
        {
          "id": "forge.action.enemy.sleep",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "让敌人休眠",
          "version": "1.0.0",
          "parameters": {
            "description": "在能力支持时让敌人休眠。",
            "summary": "在能力支持时让敌人休眠。",
            "summaryEn": "Puts an enemy back to sleep where that is supported.",
            "labelEn": "Put enemy to sleep",
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
                "schema": "forge.result.enemy.sleep",
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
                "id": "sleep_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "immediate",
                  "when_idle"
                ]
              },
              {
                "id": "interrupt_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "any",
                  "damage_only"
                ]
              }
            ],
            "recipients": {
              "input": "enemies",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "ai.sleep"
              ],
              "result": "result"
            }
          }
        }
        """,
        """
        {
          "id": "forge.action.enemy.move_to",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "让敌人移动到某处",
          "version": "1.0.0",
          "parameters": {
            "description": "让敌人导航到一个合法位置。",
            "summary": "让敌人导航到一个合法位置。",
            "summaryEn": "Sends an enemy walking to a legal spot.",
            "labelEn": "Move to",
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
                "id": "destination",
                "type": "vector3",
                "unit": "m"
              },
              {
                "id": "area",
                "type": "resource",
                "resourceKind": "area_field",
                "schema": "forge.resource.area_field"
              },
              {
                "id": "speed",
                "type": "number"
              },
              {
                "id": "arrival_tolerance",
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
                "schema": "forge.result.enemy.move_to",
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
                    "id": "speed",
                    "type": "number"
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
              "input": "enemies",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "ai.navigate"
              ],
              "result": "result"
            }
          }
        }
        """
    };

    /// <summary>The three bindings, one per row above: this provider's own name for the registration, the
    /// canonical capability it implements, and the handler the native module supplies. `execute` is the role
    /// every action binding in this repository uses; `requires` stays empty because the enforcement the catalog
    /// names (`ai.wake`, `ai.sleep`, `ai.navigate`) is this module's own per-target check, not another binding.
    /// Binding `i` implements capability `i`, so the two arrays are read together.</summary>
    internal static readonly string[] BindingRows =
    {
        """
        {
          "id": "forge.module.gtfo.enemy.binding.awaken",
          "capabilityId": "forge.action.enemy.awaken",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.awaken",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """,
        """
        {
          "id": "forge.module.gtfo.enemy.binding.sleep",
          "capabilityId": "forge.action.enemy.sleep",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.sleep",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """,
        """
        {
          "id": "forge.module.gtfo.enemy.binding.move_to",
          "capabilityId": "forge.action.enemy.move_to",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.move_to",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """
    };

    /// <summary>The handler names the three binding rows above spell, so the registration site and the binding
    /// rows cannot disagree: a handler the registry is handed under another name is a `missing-shape` at
    /// registration, and a handler name that drifts from its row is a binding nobody can answer.</summary>
    internal static readonly string[] HandlerNames =
    {
        AwakenHandler, SleepHandler, MoveToHandler
    };

    /// <summary>Each handler's own ports, declared once for both halves that register the rows and resolved at
    /// registration against the capability row the binding implements, so neither half can describe a different
    /// layout.</summary>
    internal static readonly HandlerShape AwakenPorts = new HandlerShape()
        .Inputs("enemies", "source", "alert_amount", "reason").Outputs("result").Parameters("wake_policy");
    internal static readonly HandlerShape SleepPorts = new HandlerShape()
        .Inputs("enemies", "duration").Outputs("result").Parameters("sleep_policy", "interrupt_policy");
    internal static readonly HandlerShape MoveToPorts = new HandlerShape()
        .Inputs("enemies", "destination", "area", "speed", "arrival_tolerance").Outputs("result");

    /// <summary>The capability rows above, in index order — the shape a registration site needs for the two
    /// insertion points that take one row at a time. A registration site appends all three, in this order, after
    /// the rows the provider already declares.</summary>
    internal static IReadOnlyList<string> CapabilityRowText() => CapabilityRows;

    /// <summary>The binding rows above, in index order, and in the same order as <see cref="CapabilityRowText"/>:
    /// binding `i` names capability `i`.</summary>
    internal static IReadOnlyList<string> BindingRowText() => BindingRows;

    /// <summary>The three handlers' own port sets, keyed by the handler names the rows above spell.</summary>
    internal static Dictionary<string, HandlerShape> Shapes() => new(StringComparer.Ordinal)
    {
        [AwakenHandler] = AwakenPorts,
        [SleepHandler] = SleepPorts,
        [MoveToHandler] = MoveToPorts
    };

    /// <summary>One binding's registration support. Writing an enemy's own state machine reads and writes only
    /// state this module already tracks, so each binding carries the one permission its write path needs.</summary>
    internal static BindingSupport[] Support() => new[]
    {
        new BindingSupport(AwakenBinding, "implementation-only", new[] { "gtfo.enemy.behavior.write" }),
        new BindingSupport(SleepBinding, "implementation-only", new[] { "gtfo.enemy.behavior.write" }),
        new BindingSupport(MoveToBinding, "implementation-only", new[] { "gtfo.enemy.navigation.write" })
    };
}
