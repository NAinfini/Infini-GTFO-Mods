using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The placement half of the deployed-device facts: the two rows a sentry or a mine is placed and recalled
/// under, and the one port that tells the two devices apart.
///
/// The two rows are the catalog's own and are reused rather than re-created. `forge.trigger.equipment.deploy_completed`
/// is the node list's "哨戒炮放下" and "绊雷放下": the world instance's own spawn is the moment both families
/// really become deployed — `SentryGunInstance.OnSpawn` is one of the three bodies in this build that write the
/// owning backpack's deployed marker — and it is also the one deploy signal the mine family shares, because the
/// mine never writes that marker at all. `forge.trigger.equipment.recall_completed` is "收回": it is cleared by
/// the instance's own `SyncedPickup`, which the recall interaction reaches on every machine.
///
/// `equipment_kind` is the port that separates the two placements. It is the same set the device facts carry
/// (`sentry_gun`, `mine`, and the glue gun's `glue_gun`), so a plan that reads either family switches on one
/// vocabulary instead of on two row ids. The set is a closed enum and a fact carries a member's index, which is
/// what this file's own table answers for both the publication and the shape.
///
/// `zone` is deliberately not a port here. The level's zone is a resource of the map domain, read for any entity
/// through `forge.selector.target.zone`; putting a copy of it on the placement fact would be a second spelling of
/// a reference that query already owns, and the position this row does carry is what the query is anchored on.
/// </summary>
public static class WeaponPlacementContract
{
    /// <summary>The placement fact: `e-sentry-place` and the placement half of `e-mine`.</summary>
    public const string DeployCapability = ModuleDefinition.DeployCompletedCapability;
    /// <summary>The recall fact: the other half of `e-sentry-place`.</summary>
    public const string RecallCapability = ModuleDefinition.RecallCompletedCapability;

    public const string DeployBinding = ModuleDefinition.DeployCompletedBinding;
    public const string RecallBinding = ModuleDefinition.RecallCompletedBinding;

    /// <summary>The permission both rows are read under. They observe a placement this provider already owns an
    /// identity for, so they need the same deployable read the life-cycle rows carry and nothing more.</summary>
    public const string DeployableReadPermission = ModuleDefinition.DeployableReadPermission;

    /// <summary>The enum set the `equipment_kind` port reads its members from.</summary>
    public const string EquipmentKindSchema = "equipment_kind";

    /// <summary>The three shells this package can name, in the compiled order of the set they belong to. That order
    /// is the wire contract: a fact carries a member's index, so this list and the framework's own
    /// `equipment_kind` set have to spell the same members in the same order. The integration fragment records the
    /// framework half; the focused cases pin this half, and the index a publication writes is read out of this one
    /// list rather than from a second copy somewhere else. `sentry_gun` and `mine` are the two placed devices;
    /// `glue_gun` is the hand-held launcher whose shot is the same kind of fact and travels with the same
    /// vocabulary.</summary>
    public static readonly IReadOnlyList<string> EquipmentKinds = new[] { "sentry_gun", "mine", "glue_gun" };

    /// <summary>Canonical member names, so a publication never spells one by hand.</summary>
    public const string SentryGunKind = "sentry_gun";
    public const string MineKind = "mine";
    public const string GlueGunKind = "glue_gun";

    /// <summary>The two declared rows, in the order they are published: a placement, then its recall.</summary>
    public static readonly IReadOnlyList<(string Capability, string Binding)> Rows = new[]
    {
        (DeployCapability, DeployBinding), (RecallCapability, RecallBinding)
    };

    /// <summary>The two rows as the trigger contract writes them: the placement row carrying the node list's own
    /// ports plus the kind, and the recall row. `deploy_completed` already exists in
    /// `ForgeRuntime/Framework/TriggerContracts.cs` and its declared row is the one a publication is checked
    /// against, so the integration batch replaces that row's port list with this one; `recall_completed` has no row
    /// there yet — this package declared its shape itself — and the same batch adds this one. The shapes are the
    /// catalog's field for field, because the runtime refuses a published port its own row does not declare.</summary>
    public static IReadOnlyList<System.Text.Json.JsonElement> RowsJson()
    {
        var rows = new List<System.Text.Json.JsonElement>(2)
        {
            RuntimeJson.Parse(DeployRow), RuntimeJson.Parse(RecallRow)
        };
        return rows;
    }

    private const string DeployRow = """
    {
      "id": "forge.trigger.equipment.deploy_completed",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "部署成功",
      "version": "1.0.0",
      "parameters": {
        "description": "部署物成功放好了。",
        "summary": "部署物成功放好了。",
        "summaryEn": "Fires when a deployable has been placed successfully.",
        "labelEn": "Deploy completed",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.player"], "id": "actor", "type": "entity" },
          { "entityKinds": ["gtfo.equipment"], "id": "deployed", "type": "entity" },
          { "id": "equipment_kind", "type": "enum", "schema": "equipment_kind", "nullable": true },
          { "id": "position", "type": "vector3", "unit": "m", "optional": true }
        ],
        "parameters": []
      }
    }
    """;

    private const string RecallRow = """
    {
      "id": "forge.trigger.equipment.recall_completed",
      "owner": "forge.contract.trigger",
      "kind": "trigger",
      "label": "哨戒炮放下 / 收回",
      "version": "1.0.0",
      "parameters": {
        "description": "把放下的装置收回背包了。",
        "summary": "把放下的装置收回背包了。",
        "summaryEn": "Fires when a deployed device has been recalled.",
        "labelEn": "Recall completed",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.player"], "id": "actor", "type": "entity" },
          { "entityKinds": ["gtfo.equipment"], "id": "deployed", "type": "entity" },
          { "id": "equipment_kind", "type": "enum", "schema": "equipment_kind", "nullable": true }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>The compiled value of one kind name: its position in the set, which is what a fact carries on an
    /// enum port. A name that is not in this file's own table is refused rather than published as an index into a
    /// set it does not belong to, so a misspelled member fails at the publication instead of at the reader.</summary>
    public static int? KindIndex(string? kind)
    {
        for (var position = 0; position < EquipmentKinds.Count; position++)
            if (string.Equals(EquipmentKinds[position], kind, StringComparison.Ordinal)) return position;
        return null;
    }

    /// <summary>The outputs of one placement, which is the whole port set of the placement row: the actor, the
    /// device, the kind when the machine could name it, and the place when the instance reported one. It is built
    /// here rather than in the native half so the row and the fact are one artifact — a port added to the row
    /// without a writer, or written without a port, is the drift this pairing exists to prevent. A port whose value
    /// this machine could not read is left out rather than carried as null: the row declares it optional for
    /// exactly that case, and a reader that cannot tell "absent" from "null" would treat an unread place as one.
    /// </summary>
    public static System.Text.Json.JsonElement DeployOutputs(EntityReference? actor, EntityReference deployed,
        int? kind, double[]? position)
    {
        ArgumentNullException.ThrowIfNull(deployed);
        var outputs = new Dictionary<string, object>(StringComparer.Ordinal);
        if (actor != null) outputs["actor"] = actor;
        outputs["deployed"] = deployed;
        if (kind is { } member) outputs["equipment_kind"] = member;
        if (position is { Length: 3 }) outputs["position"] = position;
        return RuntimeJson.From(outputs);
    }

    /// <summary>The outputs of one recall: the same three ports as a placement without the place, because a device
    /// being picked up has no position its own placement did not already report.</summary>
    public static System.Text.Json.JsonElement RecallOutputs(EntityReference? actor, EntityReference deployed, int? kind)
    {
        ArgumentNullException.ThrowIfNull(deployed);
        var outputs = new Dictionary<string, object>(StringComparer.Ordinal);
        if (actor != null) outputs["actor"] = actor;
        outputs["deployed"] = deployed;
        if (kind is { } member) outputs["equipment_kind"] = member;
        return RuntimeJson.From(outputs);
    }
}
