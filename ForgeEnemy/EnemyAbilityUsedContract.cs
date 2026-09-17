using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The ability-used row this package observes: `forge.trigger.enemy.ability_used`, published from the
/// game's own attack-start datum (`Enemies.pES_EnemyAttackData`) the moment an enemy begins an attack ability.
///
/// That stanza is the only native body in this build that carries "which ability, at whom, for how long" as one
/// value: `AbilityType` and `AbilityIndex` name the ability the enemy's own component table holds, `TargetAgent`
/// is the handle of the agent it was aimed at, and `Duration` is the length the attack state was told to run for.
/// It is the body `ES_EnemyAttackBase` sends to every peer, so the fact is read at the one point where the game
/// itself has decided the attack is happening, and the watching half (`EnemyNodeHooks`) reads it back on the
/// machine whose module owns the enemy rather than at the send site.
///
/// Only the ability kinds this provider already owns as resources are published: `ability` is the same
/// `forge.resource.ability` reference `forge.action.enemy.ability` resolves against, minted from the native enum
/// by `EnemyAbilityResources`, so a plan that triggers an ability and a plan that watches for one name the same
/// id. A kind outside that table is not a resource this provider owns and publishes nothing.
///
/// The row carries no parameters. Filtering by ability kind is the graph's own job: the payload names the
/// ability, and a plan restricts itself with a condition on that port, which is how every other observe row in
/// this package narrows what it reacts to. A row-level filter parameter would be inert here — an `observe`
/// binding has no handler shape and the publisher's only subscription query is "does any plan want this binding".
///
/// The catalog ports are `next / enemy / ability / target / duration`: `enemy` is the enemy whose ability
/// started, `ability` the `forge.resource.ability` reference of the kind that started, `target` the agent it was
/// aimed at — optional, because an attack aimed at a position names no agent — and `duration` the attack's own
/// length in plan ticks. The datum carries seconds and the port carries the clock's own unit, so the length is
/// converted once, at the one place it is read; a datum whose own length is not a finite number publishes no
/// `duration` at all, which is why that port is optional too: a zero would be a length no plan could tell from
/// a real one.</summary>
public static class EnemyAbilityUsedContract
{
    /// <summary>The attack-start row: an enemy began one of its attack abilities.</summary>
    public const string CapabilityId = "forge.trigger.enemy.ability_used";

    /// <summary>The one provider binding of this row, in the provider's own namespace.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.ability_used";

    /// <summary>The native handler name the binding resolves through. An `observe` trigger binding needs no
    /// handler shape: an event payload arrives as a whole frame.</summary>
    public const string HandlerName = "gtfo.enemy.ability_used";

    /// <summary>The read the published fact needs: the enemy instance this provider already tracks, the ability
    /// component table it carries, and the attack state's own datum.</summary>
    public const string ReadPermission = "gtfo.enemy.ability.read";

    /// <summary>The capability row, in the catalog's own field order and wording, so the runtime declaration
    /// and the vocabulary an author reads name the same row.</summary>
    public const string CapabilityRow = """
    {
      "id": "forge.trigger.enemy.ability_used",
      "owner": "forge.module.gtfo.enemy",
      "kind": "trigger",
      "label": "敌人使用技能",
      "version": "1.0.0",
      "parameters": {
        "description": "敌人开始使用一次攻击技能。",
        "summary": "敌人的攻击动作开始时触发，带出技能类型、目标与时长。",
        "summaryEn": "Fires when an enemy begins an attack ability, with its kind, target and duration.",
        "labelEn": "Enemy used ability",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "enemy",
          "logic"
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
            "id": "enemy",
            "type": "entity"
          },
          {
            "id": "ability",
            "type": "resource",
            "resourceKind": "ability",
            "schema": "forge.resource.ability"
          },
          {
            "id": "target",
            "type": "entity",
            "optional": true
          },
          {
            "id": "duration",
            "type": "integer",
            "unit": "tick",
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
