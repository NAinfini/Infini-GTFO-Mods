using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The three enemy actions this package's own native write paths carry and whose catalog rows have no
/// other owner: `forge.action.enemy.kill`, `forge.action.enemy.mark` and `forge.action.enemy.target`.
///
/// Each row names one native submission and nothing else:
/// <list type="bullet">
/// <item>`kill` ends one enemy's life through `Dam_EnemyDamageBase.InstantDead(bool force)` — the game's own
/// unconditional end-of-life entry, which is what "clear this room" means, in contrast with
/// `forge.action.combat.execute`, which asks for an execution the target's own immunity rules may refuse.</item>
/// <item>`mark` builds Forge's own NavMarker on the enemy's model object through `GuiManager.NavMarkerLayer`,
/// because the game's own tag accepts neither a colour nor a caller-set duration (ruling 86/89; the evidence is
/// `%TEMP%\nativeinv\ni-markcolor.md`). It replaces the `ToolSyncManager.WantToTagEnemy` adapter this package
/// previously reached for the same catalog row.</item>
/// <item>`target` makes an enemy pursue one player through the game's own target propagation
/// (`EnemyAgent.PropagateTargetFull`, or `PropagateTargetLimited` when a chance is asked for).</item>
/// </list>
///
/// `mark` is `execution: presentation` (ruling 122.3): the host decides the step and when it runs, and every
/// addressed player builds the marker on its own machine, which is the only place a NavMarker is ever seen. The
/// audience is the realm's own player list, answered by `EnemyModule.PresentationAudience` — the one
/// `PresentationSessions` entry this provider registers — so a player who joins late replays the mark by loading
/// the plan and presenting the step it never saw. The row commits nothing: a marker is a local presentation
/// object, and the tier refuses a result that claims a world write.
///
/// The rows are declared here rather than copied out of the website catalog: the shape a plan is resolved against
/// is the shape this provider implements.</summary>
public static class EnemyNodeEffectContract
{
    /// <summary>The provider every row here belongs to; the same string `ModuleDefinition.ProviderId` carries.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>`forge.action.enemy.kill`: end the target enemies' lives through the game's own instant-death
    /// entry.</summary>
    public const string KillCapability = "forge.action.enemy.kill";
    public const string KillBinding = ProviderId + ".binding.kill";
    public const string KillHandler = "gtfo.enemy.kill";

    /// <summary>`forge.action.enemy.mark`: build a Forge-owned navigation marker on each target.</summary>
    public const string MarkCapability = "forge.action.enemy.mark";
    public const string MarkBinding = ProviderId + ".binding.mark";
    public const string MarkHandler = "gtfo.enemy.mark";

    /// <summary>`forge.action.enemy.target`: make each target enemy pursue one player.</summary>
    public const string TargetCapability = "forge.action.enemy.target";
    public const string TargetBinding = ProviderId + ".binding.target";
    public const string TargetHandler = "gtfo.enemy.target";

    /// <summary>Every capability id this contract names, in the same order as <see cref="CapabilityRows"/>.</summary>
    public static readonly string[] CapabilityIds = { KillCapability, MarkCapability, TargetCapability };

    /// <summary>Every binding id this contract declares, in registration order.</summary>
    public static readonly string[] BindingIds = { KillBinding, MarkBinding, TargetBinding };

    /// <summary>Every handler name this contract declares, in the same order as <see cref="BindingIds"/>.</summary>
    public static readonly string[] HandlerNames = { KillHandler, MarkHandler, TargetHandler };

    /// <summary>The permission each binding writes the world through: a life ended, a marker built, an enemy's
    /// own target written.</summary>
    public const string KillPermission = "gtfo.enemy.life.write";
    public const string MarkPermission = "gtfo.enemy.marker.write";
    public const string TargetPermission = "gtfo.enemy.targeting.write";

    /// <summary>The three capability rows, each a complete catalog entry in the website's own row shape, in the
    /// same order as <see cref="CapabilityIds"/>.</summary>
    public static readonly string[] CapabilityRows = { KillRow, MarkRow, TargetRow };

    /// <summary>The three rows as one JSON array body, for a registration that appends them to its own
    /// `capabilities` array.</summary>
    public static string CapabilityRowsJson => string.Join(",\n", CapabilityRows);

    private const string KillRow = """
    {
      "id": "forge.action.enemy.kill",
      "owner": "forge.module.gtfo.enemy",
      "kind": "action",
      "label": "直接杀死敌人",
      "version": "1.0.0",
      "parameters": {
        "description": "直接杀死目标敌人。",
        "summary": "无条件结束这些敌人的生命，走游戏自己的立即死亡入口。",
        "summaryEn": "Ends the target enemies' lives unconditionally through the game's own instant-death entry.",
        "labelEn": "Kill enemy",
        "support": "implementation-only"
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
            "schema": "forge.result.enemy.kill",
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
        "parameters": [],
        "recipients": {
          "input": "targets",
          "target": "entity",
          "cardinality": "many",
          "requires": [
            "life.end"
          ],
          "result": "result"
        }
      }
    }
    """;

    private const string MarkRow = """
    {
      "id": "forge.action.enemy.mark",
      "owner": "forge.module.gtfo.enemy",
      "kind": "action",
      "label": "标记敌人",
      "version": "1.0.0",
      "parameters": {
        "description": "给敌人打上标记，可以指定颜色和时长。",
        "summary": "在这些敌人身上放一个 Forge 自己的导航标记，颜色和时长由你定；生物追踪器自己的红色标记不受影响。",
        "summaryEn": "Puts Forge's own navigation marker on the target enemies, in the colour and for the time you ask for; the BioTracker's own red tag is untouched.",
        "labelEn": "Mark enemy",
        "support": "implementation-only"
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
        "execution": "presentation",
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
            "id": "color",
            "type": "vector3"
          },
          {
            "id": "opacity",
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
            "schema": "forge.result.enemy.mark",
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
          },
          {
            "id": "marker",
            "type": "handle",
            "handleKind": "effect",
            "lifetime": "encounter"
          }
        ],
        "parameters": [
          {
            "id": "visibility_policy",
            "type": "enum",
            "role": "structural",
            "required": true,
            "values": [
              "team"
            ]
          }
        ],
        "recipients": {
          "input": "targets",
          "target": "entity",
          "cardinality": "many",
          "requires": [
            "marker.place"
          ],
          "result": "result",
          "handle": "marker"
        }
      }
    }
    """;

    private const string TargetRow = """
    {
      "id": "forge.action.enemy.target",
      "owner": "forge.module.gtfo.enemy",
      "kind": "action",
      "label": "让敌人去追某个目标",
      "version": "1.0.0",
      "parameters": {
        "description": "让敌人去追一个指定的目标。",
        "summary": "让这些敌人把某个玩家当成目标，走游戏自己的目标传播。",
        "summaryEn": "Points the target enemies at one player through the game's own target propagation.",
        "labelEn": "Target player",
        "support": "implementation-only"
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
            "id": "target",
            "type": "entity"
          },
          {
            "id": "chance",
            "type": "number"
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
            "schema": "forge.result.enemy.target",
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
                "id": "propagated",
                "type": "boolean"
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
            "ai.target"
          ],
          "result": "result"
        }
      }
    }
    """;

    /// <summary>The capability a binding implements, in the pairing <see cref="BindingIds"/> and
    /// <see cref="CapabilityIds"/> declare.</summary>
    public static string CapabilityIdFor(string bindingId) => bindingId switch
    {
        KillBinding => KillCapability,
        MarkBinding => MarkCapability,
        _ => TargetCapability
    };

    /// <summary>The handler a binding resolves through, in the same pairing.</summary>
    public static string HandlerFor(string bindingId) => bindingId switch
    {
        KillBinding => KillHandler,
        MarkBinding => MarkHandler,
        _ => TargetHandler
    };

    /// <summary>The permission a binding's support row declares, in the same pairing.</summary>
    public static string PermissionFor(string bindingId) => bindingId switch
    {
        KillBinding => KillPermission,
        MarkBinding => MarkPermission,
        _ => TargetPermission
    };

    /// <summary>One binding row, in the provider's own binding format.</summary>
    public static string BindingRowJson(string bindingId) => $$"""
    {
      "id": "{{bindingId}}",
      "capabilityId": "{{CapabilityIdFor(bindingId)}}",
      "providerId": "{{ProviderId}}",
      "handler": "{{HandlerFor(bindingId)}}",
      "role": "execute",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The three binding rows, in the same order as <see cref="BindingIds"/>, for a registration that
    /// appends them to its own `bindings` array.</summary>
    public static string BindingRowsJson => string.Join(",\n", BindingIds.Select(BindingRowJson));

    /// <summary>The three support rows a registration appends to its `BindingSupport` table.</summary>
    public static BindingSupport[] Support() => BindingIds
        .Select(binding => new BindingSupport(binding, "implementation-only", new[] { PermissionFor(binding) }))
        .ToArray();

    /// <summary>The kill handler's ports: `targets` is the recipient collection and `source` the only other role.
    /// The row carries no structural parameter, so the shape declares none either.</summary>
    public static HandlerShape KillShape() => new HandlerShape()
        .Inputs("targets", "source").Outputs("result");

    /// <summary>The mark handler's ports: `targets`, then the three things only Forge can carry — the colour, its
    /// opacity and the caller-set duration — with the audience as the row's one structural parameter. The colour is
    /// a `vector3` for the same reason `forge.action.presentation.fog`'s is: the catalog has no colour port type,
    /// and three components are what a `vector3` carries without one being invented.</summary>
    public static HandlerShape MarkShape() => new HandlerShape()
        .Inputs("targets", "color", "opacity", "duration").Outputs("result", "marker").Parameters("visibility_policy");

    /// <summary>The target handler's ports: `enemies` is the recipient collection, `target` the player every
    /// recipient is pointed at, and `chance` the optional propagation chance.</summary>
    public static HandlerShape TargetShape() => new HandlerShape()
        .Inputs("enemies", "target", "chance").Outputs("result");

    public static Dictionary<string, HandlerShape> Shapes() => new(StringComparer.Ordinal)
    {
        [KillHandler] = KillShape(),
        [MarkHandler] = MarkShape(),
        [TargetHandler] = TargetShape()
    };
}
