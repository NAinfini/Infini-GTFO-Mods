using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>The two ammunition rows the node list's `a-p-ammo` lands on: `forge.action.weapon.ammo_add` and
/// `forge.action.weapon.ammo_consume`. Both are declared here because `forge.action.weapon.*` is this package's
/// own namespace and no other provider implements them; the ports, the labels and the two structural parameters
/// are the website catalog's row, field for field.
///
/// <b>What the native side really has.</b> The game's own ammunition gift is
/// `PlayerBackpackManager.GiveAmmoToPlayer(SNet_Player, float standardRel, float specialRel, float classRel)`,
/// which is the body the resource pack reaches (`ResourcePackFirstPerson.ApplyPack` → `PlayerAgent.GiveAmmoRel` →
/// this call) and which submits a `pAmmoGive` through the manager's own `SNet_AuthorativeAction&lt;pAmmoGive&gt;`
/// (field +0x20) — the host-authoritative object whose receive half is `ReceiveAmmoGive`, applying the three
/// relative amounts through `PlayerAmmoStorage.PickupAmmo`. Its amounts are relative to a pool's capacity, so an
/// absolute round count is converted against the pool's own cap read before the call and the landing is read back
/// after it. Evidence: `evidence/weapon-supply.json`.
///
/// <b>What the consume row cannot do from the host.</b> The gift body clamps only at the pool's cap
/// (`InventorySlotAmmo.AddAmmo` is `Min(AmmoInPack + amount, AmmoMaxCap)`), so a negative amount could drive a
/// pool below zero; the body that does clamp both ends is `PlayerAmmoStorage.UpdateBulletsInPack(AmmoType, int)`,
/// which is what the resource pack itself calls on a backpack this machine owns. A recipient whose pool belongs
/// to another machine is refused by name instead of being written through a mirrored copy its owner would
/// overwrite. Evidence: `evidence/weapon-supply.json`.</summary>
public static class WeaponSupplyContract
{
    public const string AmmoAddCapability = "forge.action.weapon.ammo_add";
    public const string AmmoConsumeCapability = "forge.action.weapon.ammo_consume";

    public const string AmmoAddBinding = ModuleDefinition.ProviderId + ".binding.action.weapon.ammo_add";
    public const string AmmoConsumeBinding = ModuleDefinition.ProviderId + ".binding.action.weapon.ammo_consume";

    /// <summary>The handler the native half supplies for each row. A row is declared with its handler or not at
    /// all, so a registration that carries no body never promises the row.</summary>
    public const string AmmoAddHandler = "gtfo.weapon.ammo_add";
    public const string AmmoConsumeHandler = "gtfo.weapon.ammo_consume";

    /// <summary>The permission both rows write under: they change a player's own ammunition pool, which is the one
    /// write this provider performs on a player's state that is not equipment.</summary>
    public const string AmmoWritePermission = "gtfo.weapon.ammo.write";

    /// <summary>The native `Player.AmmoType` members in their declared order. The order is what lets an
    /// `ammo_type` name travel as the native value without a second mapping table. `none` stays in this table
    /// because it is the native spelling of "no pool"; no row offers it, since adding to or reading back from no
    /// pool is refused by name.</summary>
    public static readonly IReadOnlyList<string> AmmoTypes = Array.AsReadOnly(new[]
    {
        "standard", "special", "class", "resource_pack_rel", "none", "current_consumable"
    });

    /// <summary>The two overflow policies the native path can actually honour: clamp through the game's own
    /// pool cap, or reject before the write when the requested amount would overflow.</summary>
    public static readonly IReadOnlyList<string> OverflowPolicies = Array.AsReadOnly(new[]
    {
        "clamp", "reject"
    });

    public static readonly IReadOnlyList<string> FailurePolicies = Array.AsReadOnly(new[]
    {
        "reject", "partial"
    });

    /// <summary>The two handler shapes, resolved at registration against each row's own ports: the single
    /// recipient the row names, the amount it asks for, and the result it answers with.</summary>
    public static readonly HandlerShape AddShape = new HandlerShape()
        .Inputs("player", "amount").Outputs("result").Parameters("ammo_type", "overflow_policy");
    public static readonly HandlerShape ConsumeShape = new HandlerShape()
        .Inputs("player", "amount").Outputs("result").Parameters("ammo_type", "failure_policy");

    /// <summary>The two catalog rows expose only native-backed ports. The consume row declares no transaction
    /// port: the native removal is one packet-sending call with no transaction to join, so the handle kind has no
    /// member and the port has nothing to carry.</summary>
    public const string AmmoAddRowDocument = """
    {
      "id": "forge.action.weapon.ammo_add",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "补充明确弹药池",
      "version": "1.0.0",
      "parameters": { "description": "往指定弹药池里加子弹。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.player"], "id": "player", "type": "entity" },
          { "id": "amount", "type": "integer" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.weapon.ammo_add",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "amount", "type": "integer" }
            ] }
        ],
        "parameters": [
          { "id": "ammo_type", "type": "enum", "role": "structural", "required": true,
            "values": ["standard", "special", "class", "resource_pack_rel", "current_consumable"] },
          { "id": "overflow_policy", "type": "enum", "role": "structural", "required": true,
            "values": ["clamp", "reject"] }
        ],
        "recipients": { "input": "player", "target": "entity", "cardinality": "one",
          "requires": ["ammo.pool"], "result": "result" }
      }
    }
    """;

    public const string AmmoConsumeRowDocument = """
    {
      "id": "forge.action.weapon.ammo_consume",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "补充 / 扣除备用弹药",
      "version": "1.0.0",
      "parameters": { "description": "从指定弹药池里扣子弹。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.player"], "id": "player", "type": "entity" },
          { "id": "amount", "type": "integer" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.weapon.ammo_consume",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "amount", "type": "integer" }
            ] }
        ],
        "parameters": [
          { "id": "ammo_type", "type": "enum", "role": "structural", "required": true,
            "values": ["standard", "special", "class", "resource_pack_rel", "current_consumable"] },
          { "id": "failure_policy", "type": "enum", "role": "structural", "required": true,
            "values": ["reject", "partial"] }
        ],
        "recipients": { "input": "player", "target": "entity", "cardinality": "one",
          "requires": ["ammo.pool"], "result": "result" }
      }
    }
    """;

    /// <summary>One row as the catalogue document the registration carries.</summary>
    public static object AddRow() => RuntimeJson.Parse(AmmoAddRowDocument);
    public static object ConsumeRow() => RuntimeJson.Parse(AmmoConsumeRowDocument);

    /// <summary>The two capability rows in registration order, each declared only when its handler is supplied:
    /// a row nothing answers is a promise this package does not keep.</summary>
    public static IReadOnlyList<object> Rows(CommandHandler? add, CommandHandler? consume)
    {
        var rows = new List<object>(2);
        if (add != null) rows.Add(AddRow());
        if (consume != null) rows.Add(ConsumeRow());
        return rows;
    }

    /// <summary>One execute binding row, spelled the way this package's other action bindings spell theirs.</summary>
    public static object Binding(string bindingId, string capabilityId, string handler) => new
    {
        id = bindingId, capabilityId, providerId = ModuleDefinition.ProviderId, handler, role = "execute",
        status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    /// <summary>The two execute rows, each declared only when its own handler is really supplied.</summary>
    public static IReadOnlyList<object> Bindings(CommandHandler? add, CommandHandler? consume)
    {
        var rows = new List<object>(2);
        if (add != null) rows.Add(Binding(AmmoAddBinding, AmmoAddCapability, AmmoAddHandler));
        if (consume != null) rows.Add(Binding(AmmoConsumeBinding, AmmoConsumeCapability, AmmoConsumeHandler));
        return rows;
    }

    /// <summary>The support lines of the two rows: each writes a player's own pool, and neither depends on another
    /// binding to close a plan.</summary>
    public static IReadOnlyList<BindingSupport> Support(CommandHandler? add, CommandHandler? consume)
    {
        var rows = new List<BindingSupport>(2);
        if (add != null) rows.Add(new BindingSupport(AmmoAddBinding, "implementation-only", new[] { AmmoWritePermission }));
        if (consume != null) rows.Add(new BindingSupport(AmmoConsumeBinding, "implementation-only", new[] { AmmoWritePermission }));
        return rows;
    }

    /// <summary>The handler shapes of the rows that are really declared, keyed by the handler name the row names.
    /// A shape whose row is not declared is refused by the runtime as an unused shape, so this table answers per
    /// supplied handler exactly like <see cref="Rows"/>.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes(CommandHandler? add, CommandHandler? consume)
    {
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        if (add != null) shapes[AmmoAddHandler] = AddShape;
        if (consume != null) shapes[AmmoConsumeHandler] = ConsumeShape;
        return shapes;
    }

    /// <summary>The bodies that were supplied, keyed by the handler names the rows name.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers(CommandHandler? add, CommandHandler? consume)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (add != null) handlers[AmmoAddHandler] = add;
        if (consume != null) handlers[AmmoConsumeHandler] = consume;
        return handlers;
    }
}
