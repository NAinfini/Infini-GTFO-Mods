using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The four enemy actions this package's own native write paths carry and whose catalog rows have no
/// other owner: `forge.action.enemy.kill`, `forge.action.enemy.remove`, `forge.action.enemy.mark` and
/// `forge.action.enemy.target`.
///
/// Each row names one native submission and nothing else:
/// <list type="bullet">
/// <item>`kill` ends one enemy's life through `Dam_EnemyDamageBase.InstantDead(bool force)` — the game's own
/// unconditional end-of-life entry, which is what "clear this room" means, in contrast with
/// `forge.action.combat.execute`, which asks for an execution the target's own immunity rules may refuse.</item>
/// <item>`remove` takes one enemy out of the world through the game's own replication despawn
/// (`EnemyAgent.Sync.Replicator.Despawn()`), which is the other half of the same choice: no life ends, no death
/// settlement runs and no corpse is left — the enemy is gone the way a despawned enemy always goes. `kill` and
/// `remove` share one port shape because they are one author choice with two outcomes, and the catalog keeps
/// them as two rows so a plan says which outcome it wants rather than passing a mode.</item>
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
/// is the shape this provider implements.
///
/// Both rows' `recipients.requires` names the same permission string the binding's own support row registers
/// (`gtfo.enemy.life.write` / `gtfo.enemy.removal.write`). Those two spellings used to differ — the row asked for
/// `life.end` / `life.remove` while the support row demanded a `gtfo.`-prefixed permission — and the kernel reads
/// exactly one of them: `RuntimePlan`'s permission lock compares the plan's own `permissions` with the union of
/// every binding's `BindingSupport.RequiredPermissions`, and `recipients.requires` is only checked for being a
/// list of identifiers (`RuntimeGraphContracts`). One string, one place it is read from.</summary>
public static class EnemyNodeEffectContract
{
    /// <summary>The provider every row here belongs to; the same string `ModuleDefinition.ProviderId` carries.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>`forge.action.enemy.kill`: end the target enemies' lives through the game's own instant-death
    /// entry.</summary>
    public const string KillCapability = "forge.action.enemy.kill";
    public const string KillBinding = ProviderId + ".binding.kill";
    public const string KillHandler = "gtfo.enemy.kill";

    /// <summary>`forge.action.enemy.remove`: take the target enemies out of the world through the game's own
    /// replication despawn, with no death settlement and no corpse.</summary>
    public const string RemoveCapability = "forge.action.enemy.remove";
    public const string RemoveBinding = ProviderId + ".binding.remove";
    public const string RemoveHandler = "gtfo.enemy.remove";

    /// <summary>`forge.action.enemy.mark`: build a Forge-owned navigation marker on each target.</summary>
    public const string MarkCapability = "forge.action.enemy.mark";
    public const string MarkBinding = ProviderId + ".binding.mark";
    public const string MarkHandler = "gtfo.enemy.mark";

    /// <summary>`forge.action.enemy.target`: make each target enemy pursue one player.</summary>
    public const string TargetCapability = "forge.action.enemy.target";
    public const string TargetBinding = ProviderId + ".binding.target";
    public const string TargetHandler = "gtfo.enemy.target";

    /// <summary>Every capability id this contract names, in the same order as <see cref="CapabilityRows"/>.</summary>
    public static readonly string[] CapabilityIds = { KillCapability, RemoveCapability, MarkCapability, TargetCapability };

    /// <summary>Every binding id this contract declares, in registration order.</summary>
    public static readonly string[] BindingIds = { KillBinding, RemoveBinding, MarkBinding, TargetBinding };

    /// <summary>Every handler name this contract declares, in the same order as <see cref="BindingIds"/>.</summary>
    public static readonly string[] HandlerNames = { KillHandler, RemoveHandler, MarkHandler, TargetHandler };

    /// <summary>The permission each binding writes the world through: a life ended, an enemy removed from the
    /// world, a marker built, an enemy's own target written.</summary>
    public const string KillPermission = "gtfo.enemy.life.write";
    public const string RemovePermission = "gtfo.enemy.removal.write";
    public const string MarkPermission = "gtfo.enemy.marker.write";
    public const string TargetPermission = "gtfo.enemy.targeting.write";

    /// <summary>The four capability rows, each a complete catalog entry in the website's own row shape, in the
    /// same order as <see cref="CapabilityIds"/>.</summary>
    public static readonly string[] CapabilityRows = { KillRow, RemoveRow, MarkRow, TargetRow };

    /// <summary>The four rows as one JSON array body, for a registration that appends them to its own
    /// `capabilities` array.</summary>
    public static string CapabilityRowsJson => string.Join(",\n", CapabilityRows);

    private static string KillRow => RuntimeJson.From(new
    {
        id = KillCapability,
        owner = ProviderId,
        kind = "action",
        label = "直接杀死敌人",
        version = "1.0.0",
        parameters = new
        {
            description = "直接杀死目标敌人。",
            summary = "无条件结束这些敌人的生命，走游戏自己的立即死亡入口。",
            summaryEn = "Ends the target enemies' lives unconditionally through the game's own instant-death entry.",
            labelEn = "Kill enemy",
            support = "implementation-only"
        },
        graph = PrimitiveGraphSource.Get(KillCapability)
    }).GetRawText();

    private const string RemoveRow = """
    {
      "id": "forge.action.enemy.remove",
      "owner": "forge.module.gtfo.enemy",
      "kind": "action",
      "label": "移除敌人",
      "version": "1.0.0",
      "parameters": {
        "description": "把目标敌人从世界里移除，不留尸体。",
        "summary": "让这些敌人直接消失，走游戏自己的回收（despawn）通道：不算死亡、不掉尸体、不触发死亡结算。",
        "summaryEn": "Takes the target enemies out of the world through the game's own despawn path: no death, no corpse, no death settlement.",
        "labelEn": "Remove enemy",
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
            "entityKinds": ["gtfo.enemy"],
            "id": "targets",
            "type": "entity",
            "cardinality": "many"
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
            "schema": "forge.result.enemy.remove",
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
            "gtfo.enemy.removal.write"
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
            "entityKinds": ["gtfo.enemy"],
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
        RemoveBinding => RemoveCapability,
        MarkBinding => MarkCapability,
        _ => TargetCapability
    };

    /// <summary>The handler a binding resolves through, in the same pairing.</summary>
    public static string HandlerFor(string bindingId) => bindingId switch
    {
        KillBinding => KillHandler,
        RemoveBinding => RemoveHandler,
        MarkBinding => MarkHandler,
        _ => TargetHandler
    };

    /// <summary>The permission a binding's support row declares, in the same pairing.</summary>
    public static string PermissionFor(string bindingId) => bindingId switch
    {
        KillBinding => KillPermission,
        RemoveBinding => RemovePermission,
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

    /// <summary>The four support rows a registration appends to its `BindingSupport` table.</summary>
    public static BindingSupport[] Support() => BindingIds
        .Select(binding => new BindingSupport(binding, "implementation-only", new[] { PermissionFor(binding) }))
        .ToArray();

    /// <summary>The kill handler's ports: `targets` is the recipient collection. The row carries no source role:
    /// neither the native instant-death entry nor this handler reads one, and a port nothing reads is an option
    /// the author would see and the game would ignore.</summary>
    public static HandlerShape KillShape() => LifeOutcomeShape();

    /// <summary>The remove handler's ports. It is the same shape as `kill`'s because the two rows are the same
    /// author choice with two outcomes: one recipient collection, one result row per target.</summary>
    public static HandlerShape RemoveShape() => LifeOutcomeShape();

    /// <summary>The one port shape `kill` and `remove` share.</summary>
    private static HandlerShape LifeOutcomeShape() => new HandlerShape()
        .Inputs("targets").Outputs("result");

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
        [RemoveHandler] = RemoveShape(),
        [MarkHandler] = MarkShape(),
        [TargetHandler] = TargetShape()
    };
}
