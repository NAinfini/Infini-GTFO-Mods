using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The ability-interrupted row this package observes: `forge.trigger.enemy.ability_interrupted`,
/// published from the ability ledger when an ability this provider triggered through
/// `forge.action.enemy.ability` ends without the game's own `EnemyAbility.AbilityIsDone()` answer.
///
/// That answer is the only end-of-ability reading the game offers, and it does not say how an ability ended:
/// the hitreact state machine that interrupts one leaves the same answer behind as a completed one. What this
/// package can prove is its own record of the submission: a row it still holds while the component reports
/// itself unfinished was ended by something other than the ability's own lifetime, which is the interrupted case
/// the row carries. Two end paths can prove it — a later trigger that replaced the row (`superseded`) and the
/// enemy leaving while the ability still ran (`despawn`, read back by the despawn hook before the entry goes) —
/// and each publishes its own `reason`. An ability the component reports as finished publishes nothing, a
/// component that can no longer be read publishes nothing, and the world going away publishes nothing: an
/// unknown is never reported as an interruption.
///
/// Unlike <see cref="AttackInstanceContract"/> this row is not declared by the framework contract module: no
/// `TriggerContracts` row carries it, so the provider declares the capability here and registers the binding
/// below it, the same way every in-module trigger row reaches the registry. The catalog ports are
/// `next / enemy / ability / reason`, all required: `enemy` is the enemy whose ability ended, `ability` is the
/// `forge.resource.ability` reference the ability action itself resolves against, and `reason` is the end path's
/// own member of the vocabulary this file declares.</summary>
public static class EnemyAbilityInterruptedContract
{
    /// <summary>The interruption row: an ability the game's own end answer still reports as unfinished.</summary>
    public const string CapabilityId = "forge.trigger.enemy.ability_interrupted";

    /// <summary>The one provider binding of this row. A binding id lives in the provider's own namespace and
    /// may never repeat its capability id, which the kernel reads as a duplicate row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.ability_interrupted";

    /// <summary>The native handler name the binding resolves through. An `observe` trigger binding needs no
    /// handler shape: an event payload arrives as a whole frame.</summary>
    public const string HandlerName = "gtfo.enemy.ability_interrupted";

    /// <summary>The read the published fact needs: the ability table of an enemy this provider already tracks
    /// and the ledger row it wrote for the submission.</summary>
    public const string ReadPermission = "gtfo.enemy.behavior.read";

    /// <summary>The `reason` values this provider publishes, one per end path that is provably not a normal end.
    /// `superseded`: a later ability trigger replaced the row while the component still reported it running.
    /// `despawn`: the enemy left and its component still reported the ability running. These two strings are the
    /// whole vocabulary; a plan that wants only one of the two filters on them.</summary>
    public const string ReasonSuperseded = "superseded";
    public const string ReasonDespawn = "despawn";

    /// <summary>The capability row, in the catalog's own field order and wording, so the runtime declaration
    /// and the vocabulary an author reads name the same row.</summary>
    public const string CapabilityRow = """
    {
      "id": "forge.trigger.enemy.ability_interrupted",
      "owner": "forge.module.gtfo.enemy",
      "kind": "trigger",
      "label": "敌人的技能被打断",
      "version": "1.0.0",
      "parameters": {
        "description": "敌人的技能被打断。",
        "summary": "敌人的技能被打断。",
        "summaryEn": "Fires when an enemy's ability is interrupted.",
        "labelEn": "Ability interrupted",
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
            "id": "reason",
            "type": "string"
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
