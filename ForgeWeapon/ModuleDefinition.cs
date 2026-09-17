using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>Weapon provider. Wield facts come only from Weapon's own equipment observation; the actor is a
/// player reference owned by the player domain and is never created here. Most bindings are observe-only, and
/// most capabilities they name are declared by the runtime's own `TriggerContracts` provider, so this module
/// carries no second copy of a shape that contract owns. The exceptions are owned here, row for row: the
/// three deployed-device fact rows, the melee-hit row and the three attack-instance rows, whose canonical ids
/// are this provider's own; and the actions it executes itself — the
/// ammunition pair, the three instance-override rows and the inventory give/consume pair — each
/// declared with the native body this machine supplies. The reload rows are this provider's bindings but not its
/// capabilities — their owner is `forge.contract.trigger`, which declares every one of their shapes. The rows are
/// registered together with the support lines their bindings require, because the runtime refuses an implemented
/// binding it has no support line for.</summary>
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.weapon";
    public const string Version = "1.0.0";
    /// <summary>The one mount target kind this provider answers for: an official offline gear block id, which
    /// every instance of that block hangs on, as opposed to one address or one native object.</summary>
    public const string GearBlockAttachmentKind = "gear-block";
    public const string EquippedCapability = "forge.trigger.input.equipped";
    public const string UnequippedCapability = "forge.trigger.input.unequipped";
    public const string ShotCommittedCapability = "forge.trigger.combat.shot_committed";
    public const string HitCandidateCapability = "forge.trigger.combat.hit_candidate";
    public const string DespawnedCapability = "forge.trigger.entity.despawned";
    public const string DeployCompletedCapability = "forge.trigger.equipment.deploy_completed";
    public const string RecallCompletedCapability = "forge.trigger.equipment.recall_completed";
    public const string EquippedBinding = ProviderId + ".binding.equipped";
    public const string UnequippedBinding = ProviderId + ".binding.unequipped";
    public const string ShotCommittedBinding = ProviderId + ".binding.shot_committed";
    public const string HitCandidateBinding = ProviderId + ".binding.hit_candidate";
    public const string DespawnedBinding = ProviderId + ".binding.despawned";
    public const string DeployCompletedBinding = ProviderId + ".binding.deploy_completed";
    public const string RecallCompletedBinding = ProviderId + ".binding.recall_completed";
    public const string WieldReadPermission = "gtfo.equipment.wield.read";
    public const string CombatReadPermission = "gtfo.weapon.combat.read";
    public const string DeployableReadPermission = "gtfo.equipment.deployable.read";

    /// <summary>The one Weapon provider declaration. Every handler here is the native half's body for one row
    /// this provider owns; a registration only ever declares what it answers, and the runtime refuses an
    /// implemented binding whose handler the registration does not supply, so each row, its capability row and
    /// its shape travel together with that handler or not at all.
    ///
    /// <paramref name="overrides"/> is the instance-override family's own handler table
    /// (<see cref="WeaponOverrideContract.Handlers"/>): its three rows are declared by that contract as one set,
    /// so the table that carries their bodies is what is supplied or not. The inventory pair is the `a-p-item`
    /// half — give and consume. `drop` declares nothing and has no body: the node list has no drop node, so no
    /// binding row is added for it, no capability row is declared (ruling 110.5) and the implementation is gone
    /// with them (ruling 133.3). The reload and inventory family travels with its own contract:
    /// <see cref="ReloadInventoryContract"/> hands back the nine rows the runtime's trigger contract declares and
    /// the one carried-item row whose id this provider owns, and <see cref="InventoryQueryContract"/> hands back
    /// the two read rows it evaluates on demand, each with the evaluator body and the shape that body answers
    /// through.</summary>
    public static RuntimeModule Create(
        CommandHandler? ammoAdd = null, CommandHandler? ammoConsume = null,
        IReadOnlyDictionary<string, CommandHandler>? overrides = null,
        CommandHandler? inventoryGive = null, CommandHandler? inventoryConsume = null)
    {
        // Every capability this provider binds is declared by one contract, never twice: the runtime's own
        // `TriggerContracts` carries the equipment, input and combat rows including the placement pair, and a
        // second declaration of one id is refused at registration.
        var capabilities = new List<object>();
        // The rows whose canonical ids this provider owns travel with their own contract: a device fact, the melee
        // hit and an attack instance are all observed by the native half that declares them, and each contract
        // hands back the rows, the bindings and the support lines in one shape.
        foreach (var row in WeaponDeployableFactsContract.Capabilities()) capabilities.Add(row);
        capabilities.AddRange(WeaponMeleeHitContract.Capabilities());
        capabilities.AddRange(AttackInstanceContract.Capabilities());
        // The three action families this provider executes itself: the ammunition pair, the instance-override
        // trio and the inventory pair. Each is declared by its own contract, only for the bodies really supplied.
        capabilities.AddRange(WeaponSupplyContract.Rows(ammoAdd, ammoConsume));
        if (overrides != null) capabilities.AddRange(WeaponOverrideContract.Capabilities());
        capabilities.AddRange(InventoryActionContract.Capabilities(inventoryGive, inventoryConsume));
        capabilities.AddRange(ReloadInventoryContract.Capabilities());
        capabilities.AddRange(InventoryQueryContract.Capabilities());
        var bindings = new List<object>
        {
            Binding(EquippedBinding, EquippedCapability, "gtfo.equipment.equipped"),
            Binding(UnequippedBinding, UnequippedCapability, "gtfo.equipment.unequipped"),
            Binding(ShotCommittedBinding, ShotCommittedCapability, "gtfo.weapon.shot_committed"),
            Binding(HitCandidateBinding, HitCandidateCapability, "gtfo.weapon.hit_candidate"),
            Binding(DespawnedBinding, DespawnedCapability, "gtfo.equipment.despawned"),
            Binding(DeployCompletedBinding, DeployCompletedCapability, "gtfo.equipment.deploy_completed"),
            Binding(RecallCompletedBinding, RecallCompletedCapability, "gtfo.equipment.recall_completed")
        };
        bindings.AddRange(WeaponDeployableFactsContract.Bindings());
        bindings.AddRange(WeaponMeleeHitContract.Bindings());
        bindings.AddRange(AttackInstanceContract.Bindings());
        bindings.AddRange(WeaponSupplyContract.Bindings(ammoAdd, ammoConsume));
        if (overrides != null) bindings.AddRange(WeaponOverrideContract.Bindings());
        bindings.AddRange(InventoryActionContract.Rows(inventoryGive, inventoryConsume));
        bindings.AddRange(ReloadInventoryContract.Bindings());
        bindings.AddRange(InventoryQueryContract.Bindings());
        var support = new List<BindingSupport>
        {
            new(EquippedBinding, "implementation-only", new[] { WieldReadPermission }),
            new(UnequippedBinding, "implementation-only", new[] { WieldReadPermission }),
            new(ShotCommittedBinding, "implementation-only", new[] { CombatReadPermission }),
            new(HitCandidateBinding, "implementation-only", new[] { CombatReadPermission }),
            new(DespawnedBinding, "implementation-only", new[] { DeployableReadPermission }),
            new(DeployCompletedBinding, "implementation-only", new[] { DeployableReadPermission }),
            new(RecallCompletedBinding, "implementation-only", new[] { DeployableReadPermission })
        };
        support.AddRange(WeaponDeployableFactsContract.Support());
        support.AddRange(WeaponMeleeHitContract.Support());
        support.AddRange(AttackInstanceContract.Support());
        support.AddRange(WeaponSupplyContract.Support(ammoAdd, ammoConsume));
        if (overrides != null) support.AddRange(WeaponOverrideContract.Support());
        support.AddRange(InventoryActionContract.Support(inventoryGive, inventoryConsume));
        support.AddRange(ReloadInventoryContract.Support());
        support.AddRange(InventoryQueryContract.Support());
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        // One body and one shape per declared row. A handler the registration carries but no binding names is
        // refused as unused, which is why every table above answers per supplied body and not per family.
        foreach (var entry in WeaponSupplyContract.Handlers(ammoAdd, ammoConsume)) handlers[entry.Key] = entry.Value;
        foreach (var entry in WeaponSupplyContract.Shapes(ammoAdd, ammoConsume)) shapes[entry.Key] = entry.Value;
        foreach (var entry in InventoryActionContract.Handlers(inventoryGive, inventoryConsume)) handlers[entry.Key] = entry.Value;
        foreach (var entry in InventoryActionContract.Shapes(inventoryGive, inventoryConsume)) shapes[entry.Key] = entry.Value;
        if (overrides != null)
        {
            foreach (var entry in overrides) handlers[entry.Key] = entry.Value;
            foreach (var entry in WeaponOverrideContract.Shapes()) shapes[entry.Key] = entry.Value;
        }
        // The two read rows are evaluated on demand rather than dispatched, so their bodies go in the evaluator
        // table the registration carries beside the handlers. Each body reads the world through the source the
        // native half attaches to it, and refuses by name while no source is attached — which is the state every
        // process that only declares this provider is in.
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal);
        foreach (var entry in InventoryQueryContract.Evaluators()) evaluators[entry.Key] = entry.Value;
        foreach (var entry in InventoryQueryContract.Shapes()) shapes[entry.Key] = entry.Value;
        return new RuntimeModule(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new
            {
                providers = new[] { new { id = ProviderId, kind = "native", version = Version, dependencies = Array.Empty<string>() } },
                capabilities = capabilities.ToArray(),
                bindings = bindings.ToArray()
            }).GetRawText(),
            handlers, support.ToArray())
        { Shapes = shapes, Evaluators = evaluators };
    }

    private static object Binding(string id, string capabilityId, string handler, string role = "observe") => new
    {
        id, capabilityId, providerId = ProviderId, handler, role, status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };
}
