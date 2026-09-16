using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The enemy control rows this provider implements, spelled as the authoring catalog rows
/// `forge.action.enemy.awaken`, `forge.action.enemy.sleep` and `forge.action.enemy.move_to`: the same ports, the
/// same structural enums in the same order, and the same result row fields. The rows live beside the handlers
/// because this package's registry is assembled from the native module's own strings, so a row and the member it
/// is submitted through cannot drift apart without failing the registration.
///
/// Three rows of the same family stay undeclared here, because declaring a capability this provider cannot
/// answer would advertise a node that can never run: `forge.action.enemy.target_set` declares a `lease` handle
/// output, `forge.action.enemy.target_clear` and `forge.action.enemy.state_request` consume those handles as
/// their recipient input, and the runtime gives a provider no way to mint or read one
/// (`RuntimeKernel.CreateHandle` is private; `RuntimeModuleHandle` exposes Publish, CancelScope and Dispose).
/// They are reported in `ForgeEnemy/evidence/enemy-control-actions.json` rather than half-implemented.</summary>
internal static class EnemyControlContract
{
    /// <summary>The rows this provider answers, in catalog order. `CapabilityRows` is the declaration half and
    /// `BindingRows` the registration half; both are the same three actions in the same order, so a reader can
    /// match them by index.</summary>
    internal static readonly string[] CapabilityIds =
    {
        EnemyModule.AwakenCapability, EnemyModule.SleepCapability, EnemyModule.MoveToCapability
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
        EnemyModule.AwakenHandler, EnemyModule.SleepHandler, EnemyModule.MoveToHandler
    };

    /// <summary>The capability rows above, in index order — the shape a registration site needs for the two
    /// insertion points that take one row at a time. A registration site appends all three, in this order, after
    /// the rows the provider already declares.</summary>
    internal static IReadOnlyList<string> CapabilityRowText() => CapabilityRows;

    /// <summary>The binding rows above, in index order, and in the same order as <see cref="CapabilityRowText"/>:
    /// binding `i` names capability `i`.</summary>
    internal static IReadOnlyList<string> BindingRowText() => BindingRows;

    /// <summary>The three command handlers this contract's binding rows name, handed to the registration as one
    /// table so the binding, the handler and its shape are declared in one place.</summary>
    internal static Dictionary<string, CommandHandler> Handlers(EnemyModule module) => new(StringComparer.Ordinal)
    {
        [EnemyModule.AwakenHandler] = module.Awaken,
        [EnemyModule.SleepHandler] = module.Sleep,
        [EnemyModule.MoveToHandler] = module.MoveTo
    };

    /// <summary>The three handlers' own port sets, resolved once at registration against the rows above.</summary>
    internal static Dictionary<string, HandlerShape> Shapes() => new(StringComparer.Ordinal)
    {
        [EnemyModule.AwakenHandler] = EnemyModule.AwakenPorts,
        [EnemyModule.SleepHandler] = EnemyModule.SleepPorts,
        [EnemyModule.MoveToHandler] = EnemyModule.MoveToPorts
    };

    /// <summary>One binding's registration support. Writing an enemy's own state machine reads and writes only
    /// state this module already tracks, so each binding carries the one permission its write path needs.</summary>
    internal static BindingSupport[] Support() => new[]
    {
        new BindingSupport(EnemyModule.AwakenBinding, "implementation-only", new[] { "gtfo.enemy.behavior.write" }),
        new BindingSupport(EnemyModule.SleepBinding, "implementation-only", new[] { "gtfo.enemy.behavior.write" }),
        new BindingSupport(EnemyModule.MoveToBinding, "implementation-only", new[] { "gtfo.enemy.navigation.write" })
    };
}