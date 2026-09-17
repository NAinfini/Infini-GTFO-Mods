using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The staggered row this package observes: `forge.trigger.combat.staggered`, published from the
/// closing half of the one damage window every hit passes through, and only when the receiver's own answer says
/// the hit landed *and* the enemy's own hitreact state machine still stands on the stagger-class reaction the
/// hit asked for. A receiver that applied the damage but left no such reaction behind — an immune or throttled
/// reaction — is not a stagger, and neither is a hit the receiver refused.
///
/// Unlike <see cref="AttackInstanceContract"/> this row is not declared by the framework contract module: no
/// `TriggerContracts` row carries it, so the provider declares the capability here and registers the binding
/// below it, the same way every in-module trigger row reaches the registry. The catalog ports are
/// `next / target / source?`: `target` is the enemy whose own reaction state shows the stagger, and `source` is
/// the inflictor when the receiver still names one this package can resolve — an explosion packet carries no
/// source, and that is the optional member's whole reason.
///
/// The reaction classes are the three `forge.action.combat.stagger` may submit (`Micro`, `Light`, `Heavy`).
/// `None` and `Unspecified` are no reaction at all, and `ToDeath` and `InstantRagdollDeath` are kills, which
/// belong to `forge.trigger.combat.killed` instead.</summary>
public static class EnemyStaggerContract
{
    /// <summary>The stagger row: "the target really is staggered", never "a hit asked for a stagger".</summary>
    public const string CapabilityId = "forge.trigger.combat.staggered";

    /// <summary>The one provider binding of this row. A binding id lives in the provider's own namespace and
    /// may never repeat its capability id, which the kernel reads as a duplicate row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.staggered";

    /// <summary>The native handler name the binding resolves through. An `observe` trigger binding needs no
    /// handler shape: an event payload arrives as a whole frame.</summary>
    public const string HandlerName = "gtfo.enemy.staggered";

    /// <summary>The read the published fact needs: the receiver the damage landed on and the reaction state of
    /// the enemy that owns it, both read off an enemy this provider already tracks.</summary>
    public const string ReadPermission = "gtfo.enemy.health.read";

    /// <summary>The capability row, in the catalog's own field order and wording, so the runtime declaration
    /// and the vocabulary an author reads name the same row.</summary>
    public const string CapabilityRow = """
    {
      "id": "forge.trigger.combat.staggered",
      "owner": "forge.module.gtfo.enemy",
      "kind": "trigger",
      "label": "敌人被打硬直或被击倒",
      "version": "1.0.0",
      "parameters": {
        "description": "目标真的被打出了硬直。",
        "summary": "敌人被打硬直或被击倒。",
        "summaryEn": "Fires when a target is actually staggered.",
        "labelEn": "Staggered",
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
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "entityKinds": [
              "gtfo.enemy"
            ],
            "id": "target",
            "type": "entity"
          },
          {
            "entityKinds": [
              "gtfo.player",
              "gtfo.enemy"
            ],
            "id": "source",
            "type": "entity",
            "optional": true
          }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>One binding row, in the provider's own binding format.</summary>
    public static string BindingRowJson => $$"""
    {
      "id": "{{BindingId}}",
      "capabilityId": "{{CapabilityId}}",
      "providerId": "{{ModuleDefinition.ProviderId}}",
      "handler": "{{HandlerName}}",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The one support row a registration appends to its `BindingSupport` table.</summary>
    public static BindingSupport[] Support() =>
        new[] { new BindingSupport(BindingId, "implementation-only", new[] { ReadPermission }) };
}
