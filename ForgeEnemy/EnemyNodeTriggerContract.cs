using System;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The enemy-domain observation rows this provider publishes: `forge.trigger.enemy.tagged` (the
/// BioTracker tag a native tag transaction placed) and `forge.trigger.enemy.glued` (foam volume the game's own
/// glue receiver gained).
///
/// Both are `forge.trigger.*` rows, so their shapes belong to the framework's own contract module and are declared
/// by `ForgeRuntime/Framework/TriggerContracts.cs`; this file keeps the ids and the binding half, and carries no
/// row text of its own. The third row of this family, `forge.trigger.entity.spawned`, is declared by the trigger
/// contract for the same reason: this provider binds it and carries no copy of its text.
///
/// The catalog ports are the ones `TriggerContracts` declares: `tagged` carries `enemy / tagged`, `glued` carries
/// `enemy / volume / total`. Neither row has a port the native observation cannot fill: the tag transaction's own
/// packet holds the tagged enemy and nothing else, and the glue receiver publishes one attached volume per enemy,
/// which is both the gain and the total.</summary>
public static class EnemyNodeTriggerContract
{
    /// <summary>The tag row: published when the game's own tag transaction lands a tag on an enemy, and again when
    /// a live tag ends.</summary>
    public const string TaggedCapability = "forge.trigger.enemy.tagged";
    public const string TaggedBinding = ModuleDefinition.ProviderId + ".binding.tagged";
    public const string TaggedHandler = "gtfo.enemy.tagged";

    /// <summary>The glue row: published for the foam volume one hit really added to an enemy's own receiver.</summary>
    public const string GluedCapability = "forge.trigger.enemy.glued";
    public const string GluedBinding = ModuleDefinition.ProviderId + ".binding.glued";
    public const string GluedHandler = "gtfo.enemy.glued";

    /// <summary>The canonical row this provider only binds: the generic entity-spawn fact, published for an enemy
    /// by the enemy provider's own spawn observation and for every other kind by the package that owns it. The row
    /// itself is declared by the trigger contract (`forge.contract.trigger`, `TriggerContracts.Module()`), which
    /// registers before any domain package, so this binding is legal as soon as the module registers and this
    /// package never carries the row's text.</summary>
    public const string SpawnedCapability = "forge.trigger.entity.spawned";

    /// <summary>Every binding id this contract declares, in registration order.</summary>
    public static readonly string[] BindingIds = { TaggedBinding, GluedBinding };

    /// <summary>The observation permission each binding needs: both only read the enemy instance the provider
    /// already tracks — its tag flag, or the glue volume its own damage receiver publishes.</summary>
    public const string TagReadPermission = "gtfo.enemy.detection.read";
    public const string GlueReadPermission = "gtfo.enemy.glue.read";

    /// <summary>The binding id this package declares for the generic spawn row. The capability belongs to the
    /// trigger contract's provider and that provider registers before any domain package, so this binding is
    /// legal as soon as the row exists — the fact is the watching provider's, and no other package declares a
    /// second binding for it.</summary>
    public const string SpawnedBinding = ModuleDefinition.ProviderId + ".binding.spawned";

    /// <summary>The handler name that travels with <see cref="SpawnedBinding"/>.</summary>
    public const string SpawnedHandler = "gtfo.enemy.spawned";

    /// <summary>The spawn row's one binding, in this provider's own binding format.</summary>
    public static string SpawnedBindingRowJson => $$"""
    {
      "id": "{{SpawnedBinding}}",
      "capabilityId": "{{SpawnedCapability}}",
      "providerId": "{{ModuleDefinition.ProviderId}}",
      "handler": "{{SpawnedHandler}}",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The capability a binding implements, in the pairing <see cref="BindingIds"/> declares.</summary>
    public static string CapabilityIdFor(string bindingId)
        => bindingId == TaggedBinding ? TaggedCapability : GluedCapability;

    /// <summary>The observation handler a binding resolves through, in the same pairing. An `observe` trigger
    /// binding needs no handler shape: an event payload arrives as a whole frame.</summary>
    public static string HandlerFor(string bindingId)
        => bindingId == TaggedBinding ? TaggedHandler : GluedHandler;

    /// <summary>The permission a binding's support row declares, in the same pairing.</summary>
    public static string PermissionFor(string bindingId)
        => bindingId == TaggedBinding ? TagReadPermission : GlueReadPermission;

    /// <summary>One binding row, in the provider's own binding format.</summary>
    public static string BindingRowJson(string bindingId) => $$"""
    {
      "id": "{{bindingId}}",
      "capabilityId": "{{CapabilityIdFor(bindingId)}}",
      "providerId": "{{ModuleDefinition.ProviderId}}",
      "handler": "{{HandlerFor(bindingId)}}",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The two binding rows, in the same order as <see cref="BindingIds"/>, for a registration that
    /// appends them to its own `bindings` array.</summary>
    public static string BindingRowsJson => string.Join(",\n", BindingIds.Select(BindingRowJson));

    /// <summary>The two support rows a registration appends to its `BindingSupport` table, one per binding
    /// above.</summary>
    public static BindingSupport[] Support() => BindingIds
        .Select(binding => new BindingSupport(binding, "implementation-only", new[] { PermissionFor(binding) }))
        .ToArray();
}
