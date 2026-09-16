using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The two damage-transaction rows this package observes: the kill a committed damage transaction
/// settles (`forge.trigger.combat.killed`) and the specific body part a received hit went into
/// (`forge.trigger.combat.limb_damaged`). Both are `forge.trigger.*` rows, so their shapes belong to the framework
/// contract module and are declared by `ForgeRuntime/Framework/TriggerContracts.cs`; this file keeps the ids and
/// the binding half, and carries no row text of its own.
///
/// The catalog ports are the ones `TriggerContracts` declares: `killed` carries
/// `next / target / source? / damage_kind`, `limb_damaged` carries `next / target / limb? / amount`. `killed` is
/// not the enemy's death: a death flow that never settles as a kill is `forge.trigger.enemy.death_started`, and the
/// two rows are published from different native moments precisely so a plan can tell them apart.</summary>
public static class AttackInstanceContract
{
    /// <summary>The kill row: published from the damage window that dropped its target, never from the death
    /// callback alone.</summary>
    public const string CapabilityId = "forge.trigger.combat.killed";
    /// <summary>The limb row: published for every received hit that named a part and really took health off
    /// the target.</summary>
    public const string LimbDamagedCapabilityId = "forge.trigger.combat.limb_damaged";

    /// <summary>The two provider bindings. A binding id lives in the provider's own namespace and may never
    /// repeat its capability id, which the kernel reads as a duplicate row.</summary>
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.killed";
    public const string LimbDamagedBindingId = ModuleDefinition.ProviderId + ".binding.limb_damaged";

    /// <summary>The native handler names the bindings resolve through. An `observe` trigger binding needs no
    /// handler shape: an event payload arrives as a whole frame.</summary>
    public const string HandlerName = "gtfo.enemy.killed";
    public const string LimbDamagedHandlerName = "gtfo.enemy.limb_damaged";

    /// <summary>Both bindings only read the receiver the damage already landed on, so the permission is the
    /// health read every other observed combat fact in this package declares.</summary>
    public const string ReadPermission = "gtfo.enemy.health.read";

    /// <summary>Every binding id this contract declares, in registration order.</summary>
    public static readonly string[] BindingIds = { BindingId, LimbDamagedBindingId };

    /// <summary>The capability a binding implements, in the pairing <see cref="BindingIds"/> and the two capability
    /// ids above declare.</summary>
    public static string CapabilityIdFor(string bindingId) => bindingId == BindingId ? CapabilityId : LimbDamagedCapabilityId;

    /// <summary>The handler a binding resolves through, in the same pairing.</summary>
    public static string HandlerFor(string bindingId) => bindingId == BindingId ? HandlerName : LimbDamagedHandlerName;

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
        .Select(binding => new BindingSupport(binding, "implementation-only", new[] { ReadPermission }))
        .ToArray();
}
