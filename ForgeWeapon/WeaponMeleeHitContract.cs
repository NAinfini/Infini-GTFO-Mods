using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The one row the node list's `e-w-melee` names and this build did not have: a melee swing that <b>landed on a
/// target</b>. The catalog's melee vocabulary is the action `forge.action.combat.melee_swing` (a swing is asked
/// for) plus the attack-instance facts this package already declares (`attack_requested` / `attack_completed` /
/// `attack_missed`), and none of them says "this swing hit this thing": the attack scope closes the same way
/// whether the swing connected or not, and `hit_candidate` is published before any damage is worked out, so it
/// carries no limb and no damage. The node list's example — a hammer that heals its wielder when it connects —
/// needs the connected hit itself, once per target, with the damage the hit really dealt.
///
/// <b>Where the row is read.</b> Every port here is a read of the swing's own hit entry on the machine that
/// performs the swing, which is the only place all five exist:
/// <list type="bullet">
/// <item>`MeleeWeaponFirstPerson.DoAttackDamage(MeleeWeaponDamageData, bool)` is the first-person per-target hit
/// entry: `MWS_AttackHit.Update` calls it once per entry of `HitsForDamage`, and its `MeleeWeaponDamageData` names
/// the target's damageable and the hit point. The wielder is the weapon's own `Owner`, the damage is the weapon's
/// own `m_damageToDeal` (fixed by `SetNextDamageToDeal` for this hit), and the charge is the weapon's own state.
/// The one other caller in this build is `MWS_Push.Update`, which enters the same body for the shove with `isPush`
/// set; that entry is a push and not a swing hit, so the observation skips it.</item>
/// <item>`MeleeWeaponThirdPerson.DoAttackDamage(GameObject, Vector3, Vector3, float, bool)` is the same hit on a
/// machine that is not the wielder's: the bot melee action calls it per target with the damage as its own
/// argument, and the weapon's `Owner` is the attacker.</item>
/// </list>
///
/// <b>Where a remote player's swing is read.</b> A human client's swing runs the first-person entry on that
/// client's machine only; the swing's broadcast action (`MeleeWeaponFirstPerson.m_hitPacket`) reaches
/// `MeleeWeaponThirdPerson.IncomingMeleeHit`, whose body only plays feedback and spawns decals, and the
/// third-person damage body's only literal callers are the bot melee action's strike state. What the host does
/// receive is the replicated damage itself — `Dam_EnemyDamageBase.ReceiveMeleeDamage(pFullDamageData)`, which
/// carries the attacker, the limb and the gear category but not the charge, and whose damage field is a packed
/// `UFloat16` whose decode scale belongs to the receiver's own body. That path is the row's third source: the
/// damage published from it is the health the receive body really took, read around it, and `charged` stays
/// absent. The two sources publish through one ledger, so one landed hit is published once: a hit whose attacker
/// is this machine's own player or a bot is published by the hit entry that performed it, and the receive half
/// publishes only for a remote player's swing.
/// </summary>
public static class WeaponMeleeHitContract
{
    /// <summary>A melee swing connected with a target. One fact per target of one swing.</summary>
    public const string MeleeHitCapability = "forge.trigger.combat.melee_hit";

    public const string MeleeHitBinding = ModuleDefinition.ProviderId + ".binding.melee_hit";

    /// <summary>The handler name the binding resolves to. The row is observe-only, so the name is the row's own
    /// id in this provider's namespace, exactly like the other fact rows this package declares.</summary>
    public const string MeleeHitHandler = "gtfo.weapon.melee_hit";

    /// <summary>The permission the row is read under: the same combat read the shot and hit-candidate rows carry,
    /// because the hit is read from the weapon's own body and from the target's damageable, and nothing here
    /// writes.</summary>
    public const string CombatReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The domains the catalog's melee rows name, port for port the same list: a melee swing acts on an
    /// enemy through a weapon, a tool or a consumable, and its attacker is a player.</summary>
    public static readonly string[] Domains = { "enemy", "weapon", "tool", "consumable", "player" };

    /// <summary>The one declared row.</summary>
    public static readonly string CapabilityId = MeleeHitCapability;

    /// <summary>The row document, written the way the catalog writes its own rows. `source` is the attacker and
    /// `target` the thing that was hit — the names the combat family already uses, so a plan that reads a
    /// `hit_candidate` reads this row's actor ports the same way. `limb` and `charged` are nullable because both
    /// are genuinely absent in cases the game produces: a hit on a damageable that is not a body part has no limb
    /// index, and a swing the first-person weapon did not label light or heavy (a shove, or a state this build
    /// does not classify) has no charge. `damage` is required: the hit entry is only reached with a damage value
    /// fixed for it.</summary>
    public static JsonElement Row() => RuntimeJson.Parse(RowDocument);

    /// <summary>The row wrapped for a `capabilities` array.</summary>
    public static IReadOnlyList<object> Capabilities() => new object[] { RuntimeJson.Parse(RowDocument) };

    /// <summary>The one observe binding: this package commands no melee swing, it observes one.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        new
        {
            id = MeleeHitBinding, capabilityId = MeleeHitCapability, providerId = ModuleDefinition.ProviderId,
            handler = MeleeHitHandler, role = "observe", status = "implemented",
            dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
        }
    };

    /// <summary>The registration support line for that binding.</summary>
    public static IReadOnlyList<BindingSupport> Support() => new[]
    {
        new BindingSupport(MeleeHitBinding, "implementation-only", new[] { CombatReadPermission })
    };

    /// <summary>The row's output ports in declaration order, as `id:type` texts: the port list a plan wires and
    /// the thing a port-order drift would break. Used by this slice's own tests.</summary>
    public static IReadOnlyList<string> OutputPorts()
    {
        var ports = new List<string>();
        foreach (var port in Row().GetProperty("graph").GetProperty("outputs").EnumerateArray())
        {
            var id = port.GetProperty("id").GetString() ?? "";
            var type = port.GetProperty("type").GetString() ?? "";
            var carrier = "";
            if (port.TryGetProperty("schema", out var schema)) carrier = ":" + schema.GetString();
            if (port.TryGetProperty("unit", out var unit)) carrier += ":" + unit.GetString();
            if (port.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean()) carrier += "?";
            ports.Add(id + ":" + type + carrier);
        }
        return ports;
    }

    /// <summary>The literal JSON of the row.</summary>
    public const string RowDocument = """
    {
      "id": "forge.trigger.combat.melee_hit",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "近战命中",
      "version": "1.0.0",
      "parameters": { "description": "一次近战挥击打中了目标。同一次挥击命中几个目标就发几次；推击和挥空都不发。" },
      "graph": {
        "domains": ["enemy", "weapon", "tool", "consumable", "player"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "source", "type": "entity" },
          { "id": "target", "type": "entity" },
          { "id": "limb", "type": "integer", "nullable": true },
          { "id": "damage", "type": "number", "unit": "hp" },
          { "id": "charged", "type": "boolean", "nullable": true }
        ],
        "parameters": []
      }
    }
    """;
}
