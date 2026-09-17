using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The deployed-device facts the node list names beyond the life-cycle pair this package already declares:
/// a device that really fired, a device that ran out of ammunition, and a device that detonated.
///
/// The three rows are new canonical ids, and each one is here because the existing catalog rows answer a
/// different question. `forge.trigger.equipment.expired` is a life ending, which a mine's detonation is not: a
/// glue or explosive mine detonates and the instance is destroyed as a consequence, and an author who reads
/// `expired` learns "it is gone", not "it went off here". `forge.trigger.equipment.fuse_elapsed` is a timer
/// running out, which is the explosive mine's own trigger but not the glue mine's, and not the sentry's depletion
/// either.
///
/// The glue-gun half of the same question is `fired` as well: the node list's `e-glue` is "the glue gun fired",
/// and the enemy that ends up glued is the enemy domain's fact, not this one. The `equipment_kind` port is what
/// tells the two apart, and its member set is the shells this package actually hooks, not a wish list: a member
/// nothing publishes is not declared.
/// </summary>
public static class WeaponDeployableFactsContract
{
    /// <summary>A deployed device or tool really produced one shot.</summary>
    public const string FiredCapability = "forge.trigger.equipment.fired";
    /// <summary>A deployed device's ammunition reached zero.</summary>
    public const string AmmoDepletedCapability = "forge.trigger.equipment.ammo_depleted";
    /// <summary>A deployed device detonated.</summary>
    public const string DetonatedCapability = "forge.trigger.equipment.detonated";

    public const string FiredBinding = ModuleDefinition.ProviderId + ".binding.fired";
    public const string AmmoDepletedBinding = ModuleDefinition.ProviderId + ".binding.ammo_depleted";
    public const string DetonatedBinding = ModuleDefinition.ProviderId + ".binding.detonated";

    /// <summary>The permission all three facts are read under. They observe a placed device this provider already
    /// owns an identity for, so they need the same deployable read the life-cycle rows carry and nothing more.</summary>
    public const string DeployableReadPermission = ModuleDefinition.DeployableReadPermission;

    /// <summary>The enum set `equipment_kind` reads its members from: which shell fired. `sentry_gun` is a placed
    /// sentry's own firing component; `glue_gun` is the hand-held glue gun's launch, which is the half of `e-glue`
    /// that belongs to this package. It is a new set rather than an existing one because no declared set names
    /// equipment classes, and folding a shell name into `interaction_phase` or `equipment_action` would spell a
    /// different question with the same word.</summary>
    public const string EquipmentKindSchema = "equipment_kind";
    /// <summary>The members of that set, in compiled index order.</summary>
    public static readonly IReadOnlyList<string> EquipmentKinds = new[] { "sentry_gun", "glue_gun" };

    /// <summary>One declared row: the capability, the binding that carries it and the provider-scoped domains the
    /// catalog row names.</summary>
    public sealed record Row(string Capability, string Binding, string[] Domains);

    private static readonly string[] DeployDomains = { "weapon", "tool", "consumable" };

    /// <summary>The three rows, in node-list order.</summary>
    public static readonly IReadOnlyList<Row> Rows = new[]
    {
        new Row(FiredCapability, FiredBinding, DeployDomains),
        new Row(AmmoDepletedCapability, AmmoDepletedBinding, DeployDomains),
        new Row(DetonatedCapability, DetonatedBinding, DeployDomains)
    };

    /// <summary>The capability documents, in the same order as <see cref="Rows"/>.</summary>
    public static IReadOnlyList<JsonElement> Capabilities()
    {
        var rows = new List<JsonElement>(3)
        {
            RuntimeJson.Parse(FiredRow), RuntimeJson.Parse(AmmoDepletedRow), RuntimeJson.Parse(DetonatedRow)
        };
        return rows;
    }

    /// <summary>The binding rows. Every one of the three is observe-only: nothing in this package commands a
    /// sentry, a mine or the glue gun, and a fact row carries no handler.</summary>
    public static IReadOnlyList<object> Bindings()
    {
        var rows = new List<object>(3);
        foreach (var row in Rows) rows.Add(Binding(row));
        return rows;
    }

    /// <summary>The registration support rows, one per binding.</summary>
    public static IReadOnlyList<BindingSupport> Support()
    {
        var rows = new List<BindingSupport>(3);
        foreach (var row in Rows) rows.Add(new BindingSupport(row.Binding, "implementation-only", new[] { DeployableReadPermission }));
        return rows;
    }

    private static object Binding(Row row) => new
    {
        id = row.Binding, capabilityId = row.Capability, providerId = ModuleDefinition.ProviderId,
        handler = row.Binding, role = "observe", status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    // The three rows, written as the catalog writes its own: the provider owns the capability, the graph carries
    // the domains and the `host` execution every fact row has, and every port is one the native half really
    // reads. `actor` and `ammo` are nullable because both are genuinely absent in cases the game produces: a
    // device whose owner is gone, and a firing mode that spends no ammunition. Both enum ports carry their schema
    // because a port without one cannot be resolved to a member name at the handler boundary.
    private const string FiredRow = """
    {
      "id": "forge.trigger.equipment.fired",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "部署物开火",
      "version": "1.0.0",
      "parameters": { "description": "哨戒炮或者胶枪打出一发。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "device", "type": "entity" },
          { "entityKinds": ["gtfo.player"], "id": "actor", "type": "entity", "nullable": true },
          { "id": "equipment_kind", "type": "enum", "schema": "equipment_kind" },
          { "id": "equipment_action", "type": "enum", "schema": "equipment_action", "nullable": true },
          { "id": "ammo", "type": "integer", "nullable": true }
        ],
        "parameters": []
      }
    }
    """;

    private const string AmmoDepletedRow = """
    {
      "id": "forge.trigger.equipment.ammo_depleted",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "部署物弹药耗尽",
      "version": "1.0.0",
      "parameters": { "description": "哨戒炮的弹药打完了。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "device", "type": "entity" },
          { "entityKinds": ["gtfo.player"], "id": "actor", "type": "entity", "nullable": true }
        ],
        "parameters": []
      }
    }
    """;

    private const string DetonatedRow = """
    {
      "id": "forge.trigger.equipment.detonated",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "绊雷爆炸",
      "version": "1.0.0",
      "parameters": { "description": "绊雷爆炸了。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "device", "type": "entity" },
          { "id": "position", "type": "vector3", "unit": "m" }
        ],
        "parameters": []
      }
    }
    """;
}
