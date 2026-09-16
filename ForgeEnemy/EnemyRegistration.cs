using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The one ForgeEnemy provider registration: the game-independent half of it anyway — the base family's
/// eleven binding rows, the permission each of them needs, and the registry text every family's contracts are
/// composed into. The native module that answers the rows and the release export that only declares them both
/// read this, so a row a contract adds reaches the game and the manifest together.
///
/// Only the base family is spelled here rather than in a contract of its own: every one of its rows binds a
/// capability the runtime's own contract providers declare, so no capability row travels with them, and the
/// native module is what publishes the facts they carry.</summary>
internal static class EnemyRegistration
{
    internal const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>The base family's binding ids, named rather than spelled at each call site. They are the ids the
    /// rows below declare, the ones the native module publishes its facts under, and the ones the harnesses pin
    /// their plans against.</summary>
    internal const string DamageBinding = ProviderId + ".binding.damage_applied";
    internal const string DamageActionBinding = ProviderId + ".binding.damage";
    internal const string HealBinding = ProviderId + ".binding.heal";
    internal const string HealthChangedBinding = ProviderId + ".binding.health_changed";
    internal const string DeathStartedBinding = ProviderId + ".binding.death_started";
    internal const string LimbBrokenBinding = ProviderId + ".binding.limb_broken";
    internal const string AwakenedBinding = ProviderId + ".binding.awakened";
    internal const string TargetAcquiredBinding = ProviderId + ".binding.target_acquired";
    internal const string TargetLostBinding = ProviderId + ".binding.target_lost";
    internal const string ScoutDetectionBinding = ProviderId + ".binding.scout_detection";
    internal const string ScoutScreamBinding = ProviderId + ".binding.scout_scream";

    /// <summary>The provider head and the opening of the capability list: the block every registration of this
    /// provider starts with.</summary>
    private const string RegistryHead = """
    {
      "providers": [
        {
          "id": "forge.module.gtfo.enemy",
          "kind": "native",
          "version": "1.0.0",
          "dependencies": []
        }
      ],
      "capabilities": [
    """;

    /// <summary>The base family's binding rows. Every one of them binds a capability the runtime's own contract
    /// providers declare, so no capability row travels with them.</summary>
    private const string BaseBindingRows = """
        {
          "id": "forge.module.gtfo.enemy.binding.damage_applied",
          "capabilityId": "forge.trigger.combat.damage_applied",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.damage_applied",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.damage",
          "capabilityId": "forge.action.combat.damage",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.damage",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.heal",
          "capabilityId": "forge.action.combat.heal",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.heal",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.health_changed",
          "capabilityId": "forge.trigger.combat.health_changed",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.health_changed",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.death_started",
          "capabilityId": "forge.trigger.enemy.death_started",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.death_started",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.limb_broken",
          "capabilityId": "forge.trigger.combat.limb_broken",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.limb_broken",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.awakened",
          "capabilityId": "forge.trigger.enemy.awakened",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.awakened",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.target_acquired",
          "capabilityId": "forge.trigger.enemy.target_acquired",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.target_acquired",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.target_lost",
          "capabilityId": "forge.trigger.enemy.target_lost",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.target_lost",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.scout_detection",
          "capabilityId": "forge.trigger.enemy.scout_detection",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.scout_detection",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.scout_scream",
          "capabilityId": "forge.trigger.enemy.scout_scream",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.scout_scream",
          "role": "observe",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
    """;

    /// <summary>One support row per implemented binding, with the native read or write each of them performs.
    /// The kernel refuses a registration whose implemented binding has no support row, so these travel with the
    /// rows above and not with the half that answers them.</summary>
    private static readonly (string Binding, string[] Permissions)[] BaseSupport =
    {
        (DamageBinding, new[] { "gtfo.enemy.health.read" }),
        (HealBinding, new[] { "gtfo.enemy.health.write" }),
        (HealthChangedBinding, new[] { "gtfo.enemy.health.read" }),
        (DeathStartedBinding, new[] { "gtfo.enemy.lifecycle.read" }),
        (LimbBrokenBinding, new[] { "gtfo.enemy.limbs.read" }),
        (AwakenedBinding, new[] { "gtfo.enemy.behavior.read" }),
        // The target pair reads `AgentAI.Target`/`IsTargetValid`, which is the targeting read every other
        // fact of that pair declares; without these two rows the registration has implemented bindings no
        // support row answers, which the kernel refuses outright.
        (TargetAcquiredBinding, new[] { "gtfo.enemy.targeting.read" }),
        (TargetLostBinding, new[] { "gtfo.enemy.targeting.read" }),
        (ScoutDetectionBinding, new[] { "gtfo.enemy.detection.read" }),
        (ScoutScreamBinding, new[] { "gtfo.enemy.behavior.read" }),
        // The two combat actions: the heal writes health and the damage action both reads and writes it.
        (DamageActionBinding, new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" })
    };

    /// <summary>The whole registry text: the provider head and the base rows above, then every family's
    /// capability and binding rows in the order the native registration appends them — the selector, the
    /// node-list family, the four action families, the wave trigger rows and the selector's own binding.</summary>
    internal static string RegistryJson()
    {
        string nodeCapabilities = string.Join(",\n", EnemyNodeValueContract.ValueRows().Select(RuntimeJson.From))
            + ",\n" + EnemyNodeEffectContract.CapabilityRowsJson;
        string nodeBindings = string.Join(",\n", EnemyNodeValueContract.ValueBindings().Select(RuntimeJson.From))
            + ",\n" + EnemyNodeEffectContract.BindingRowsJson
            + ",\n" + EnemyNodeTriggerContract.SpawnedBindingRowJson
            + ",\n" + EnemyNodeTriggerContract.BindingRowsJson;
        string familyCapabilities = string.Join(",\n", EnemyControlContract.CapabilityRows)
            + ",\n" + EnemyCombatContract.CapabilityRows
            + ",\n" + GlueContract.CapabilityRows
            + ",\n" + EnemyBehaviorContract.CapabilityRows;
        string familyBindings = string.Join(",\n", EnemyControlContract.BindingRows)
            + ",\n" + EnemyCombatContract.BindingRows
            + ",\n" + GlueContract.BindingRows
            + ",\n" + EnemyBehaviorContract.BindingRows;
        return RegistryHead
            + EnemySelectorContract.CapabilityRowJson
            + ",\n" + nodeCapabilities
            + ",\n" + familyCapabilities
            + "\n  ],\n  \"bindings\": [\n" + BaseBindingRows + ",\n"
            + AttackInstanceContract.BindingRowsJson + ",\n"
            + nodeBindings + ",\n"
            + familyBindings + ",\n"
            + EnemyWaveContract.BindingRowsJson + ",\n"
            + EnemySelectorContract.BindingRowJson + "\n  ]\n}";
    }

    /// <summary>Every support row the registration carries, in the order the native module appends them.</summary>
    internal static IReadOnlyList<BindingSupport> Support()
    {
        var rows = new List<BindingSupport>();
        foreach (var (binding, permissions) in BaseSupport) rows.Add(new(binding, "implementation-only", permissions));
        // The selector needs no permission beyond the `gtfo.enemy` kind this provider already owns.
        rows.Add(new(EnemySelectorContract.BindingId, "implementation-only", Array.Empty<string>()));
        // The two damage-transaction rows of this family: the kill a committed hit settled and the part it went
        // into. Their bindings and the permission each needs are declared with them in
        // `AttackInstanceContract`, so the registry and the publication gate cannot name different rows.
        rows.AddRange(AttackInstanceContract.Support());
        // The node-list family's own bindings: the generic spawn row, the six value rows and the tag and glue
        // rows, each with the permission its own contract declares, plus the three actions' write permissions.
        rows.Add(new(EnemyNodeTriggerContract.SpawnedBinding, "implementation-only", Array.Empty<string>()));
        rows.AddRange(EnemyNodeValueContract.ValueSupport());
        rows.AddRange(EnemyNodeTriggerContract.Support());
        rows.Add(new(EnemyNodeEffectContract.KillBinding, "implementation-only", new[] { EnemyNodeEffectContract.KillPermission }));
        rows.Add(new(EnemyNodeEffectContract.MarkBinding, "implementation-only", new[] { EnemyNodeEffectContract.MarkPermission }));
        rows.Add(new(EnemyNodeEffectContract.TargetBinding, "implementation-only", new[] { EnemyNodeEffectContract.TargetPermission }));
        // The four action families' bindings and the wave trigger rows, each with the permission its own
        // contract declares.
        rows.AddRange(EnemyControlContract.Support());
        rows.AddRange(EnemyCombatContract.Support);
        rows.AddRange(GlueContract.Support);
        rows.AddRange(EnemyBehaviorContract.Support());
        rows.AddRange(EnemyWaveContract.Support());
        return rows.ToArray();
    }
}
