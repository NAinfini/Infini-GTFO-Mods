using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The player-domain actions this provider executes: damage, revive and down. Three rows, one native
/// write entry each, all on the host, because a player's life is the host's to change and every one of these
/// entries is a replicated one:
/// <list type="bullet">
/// <item>`forge.action.combat.damage` for `gtfo.player` submits the receiver's own damage entry, which is the
/// packet-sending half of the game's damage path, so the amount that lands is the receiver's answer and not what
/// was asked for. The capability and its result row belong to the combat contract; this file declares only the
/// binding, the shape and the permission.</item>
/// <item>`forge.action.combat.revive` submits `AgentReplicatedActions.PlayerReviveAction`, the game's own
/// host-authoritative revive action, which validates and applies on every machine. The catalog's `duration` and
/// `restored_health` ports are refused by name rather than ignored: the game owns the revive interaction's length
/// and the health a revive restores, and a plan that asks for either would otherwise be promised something this
/// build cannot keep.</item>
/// <item>`forge.action.player.down` is this provider's own row: it submits the receiver's `SendSetDead`, whose one
/// argument is whether the downed life may still be revived. That argument is the row's one structural parameter,
/// so the two behaviours are two authored choices rather than one implicit one.</item>
/// </list>
///
/// Every handler here refuses before it writes: authority, recipient kind, staleness, amount bounds and the
/// structural parameters are all checked once, and a request whose native entry cannot express what it asked for is
/// refused by name instead of being approximated. Each accepted target produces one result row; the aggregate is
/// `succeeded`, `partial` or rejected, with an unknown commit reported as unknown rather than as success.</summary>
public static class PlayerCommandContract
{
    public const string ProviderId = "forge.module.gtfo.map";

    /// <summary>The canonical damage row, bound here for a player subject.</summary>
    public const string DamageCapability = "forge.action.combat.damage";
    /// <summary>The canonical revive row, declared by the combat contract and bound here.</summary>
    public const string ReviveCapability = "forge.action.combat.revive";
    /// <summary>This provider's own row: put a player into the downed state.</summary>
    public const string DownCapability = "forge.action.player.down";

    public const string DamageHandlerName = "gtfo.player.damage";
    public const string ReviveHandlerName = "gtfo.player.revive";
    public const string DownHandlerName = "gtfo.player.down";

    /// <summary>The permission a write to a player's life carries, mirroring the enemy provider's own namespaced
    /// health permission. It is the binding's own requirement; the catalog's recipient contract names the
    /// authoring-side permission separately and neither is derived from the other.</summary>
    public const string HealthWritePermission = "gtfo.player.health.write";

    /// <summary>The two behaviours `SendSetDead`'s one argument selects between.</summary>
    public static readonly IReadOnlyList<string> RevivePolicies = Array.AsReadOnly(new[] { "allowed", "denied" });

    /// <summary>This provider's binding id for a capability: the capability's own suffix under the Map provider.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>One execute binding row per action, in the order <see cref="Rows"/> declares them.</summary>
    public static IReadOnlyList<object> Rows() => Array.AsReadOnly(new[]
    {
        Row(DamageCapability, DamageHandlerName),
        Row(ReviveCapability, ReviveHandlerName),
        Row(DownCapability, DownHandlerName)
    });

    /// <inheritdoc cref="Rows"/>
    public static IReadOnlyList<BindingSupport> Support() => Array.AsReadOnly(new[]
    {
        new BindingSupport(Binding(DamageCapability), "implementation-only", new[] { HealthWritePermission }),
        new BindingSupport(Binding(ReviveCapability), "implementation-only", new[] { HealthWritePermission }),
        new BindingSupport(Binding(DownCapability), "implementation-only", new[] { HealthWritePermission })
    });

    /// <summary>The one shape of each handler, resolved at registration against the capability it implements: the
    /// damage and revive rows are the combat contract's own port lists, and the down row's list is this
    /// declaration's. The native half reads these shapes rather than describing a second layout.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [DamageHandlerName] = new HandlerShape()
            .Inputs("targets", "source", "instigator", "amount", "damage_kind", "limb").Outputs("result").Parameters("mitigation_policy"),
        [ReviveHandlerName] = new HandlerShape()
            .Inputs("targets", "source", "duration", "restored_health").Outputs("result").Parameters("interrupt_policy", "cost_policy"),
        [DownHandlerName] = new HandlerShape()
            .Inputs("targets").Outputs("result").Parameters("revive_policy")
    };

    /// <summary>The `forge.action.player.down` row: this provider's own capability, with the result schema the
    /// native half writes one row per target into.</summary>
    public static object DownRow() => new
    {
        id = DownCapability,
        owner = ProviderId,
        kind = "action",
        label = "让玩家倒地",
        version = "1.0.0",
        parameters = new { description = "把玩家打进倒地状态。", descriptionEn = "Puts a player into the downed state." },
        graph = new
        {
            domains = new[] { "player", "map", "logic" },
            execution = "host",
            inputs = new object[]
            {
                new { id = "in", type = "execution" },
                new { id = "targets", type = "entity", cardinality = "many" }
            },
            outputs = new object[]
            {
                new { id = "next", type = "execution" },
                new
                {
                    id = "result", type = "result", schema = "forge.result.player.down",
                    fields = new object[]
                    {
                        new { id = "target", type = "entity" },
                        new { id = "status", type = "enum", schema = "execution_outcome" },
                        new { id = "committed", type = "enum", schema = "commit_state" },
                        new { id = "code", type = "string" },
                        new { id = "target_count", type = "integer" }
                    }
                }
            },
            parameters = new object[]
            {
                new
                {
                    id = "revive_policy", type = "enum", role = "structural", required = true,
                    values = RevivePolicies
                }
            },
            recipients = new
            {
                input = "targets", target = "entity", cardinality = "many",
                requires = new[] { HealthWritePermission }, result = "result"
            }
        }
    };

    /// <summary>The `forge.action.combat.revive` row as the combat contract has to declare it: the catalog's own
    /// row, field for field. This provider cannot declare it — the id belongs to `forge.contract.combat` — so the
    /// integration batch inserts this text into `CombatContracts` next to the damage row, and this package's
    /// focused tests register it so the binding is validated against the shipped shape.</summary>
    public const string ReviveRowJson = """
    {
      "id": "forge.action.combat.revive",
      "owner": "forge.contract.combat",
      "kind": "action",
      "label": "救起倒地玩家",
      "version": "2.0.0",
      "parameters": {
        "description": "按明确规则把倒地的人救起来。",
        "summary": "按明确规则把倒地的人救起来。",
        "summaryEn": "Picks a downed target back up under rules you set.",
        "labelEn": "Revive",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": ["enemy", "weapon", "tool", "consumable", "player"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "id": "targets", "type": "entity", "cardinality": "many" },
          { "id": "source", "type": "entity" },
          { "id": "duration", "type": "integer", "unit": "tick" },
          { "id": "restored_health", "type": "number", "unit": "hp" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          {
            "id": "result", "type": "result", "schema": "forge.result.combat.revive",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "duration", "type": "integer", "unit": "tick" },
              { "id": "target_count", "type": "integer" }
            ]
          }
        ],
        "parameters": [
          {
            "id": "interrupt_policy", "type": "enum", "role": "structural", "required": true,
            "values": ["cancel", "continue"]
          },
          {
            "id": "cost_policy", "type": "enum", "role": "structural", "required": true,
            "values": ["none", "charge", "consume"]
          }
        ],
        "recipients": {
          "input": "targets", "target": "entity", "cardinality": "many",
          "requires": ["health.revive"], "result": "result"
        }
      }
    }
    """;

    private static object Row(string capabilityId, string handler) => new
    {
        id = Binding(capabilityId),
        capabilityId,
        providerId = ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
