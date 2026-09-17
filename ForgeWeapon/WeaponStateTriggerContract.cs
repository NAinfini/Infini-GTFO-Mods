using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The two weapon-state rows this package owns: a charge that changes phase, and the entry/exit of aiming down
/// sights. Both are read on the machine that holds the weapon — the only machine whose copy of the weapon really
/// changes state — and both publish a phase port instead of filtering, because a trigger row's own parameters are
/// authoring metadata the kernel never compares against a fact (the runtime matches a fact to an entry point by
/// binding, mount targets and its own `gate` block only). The phase is therefore carried as a value the plan
/// filters with a condition, and never as a parameter that would look like a filter and be inert.
///
/// <b>Why `phase` is a string.</b> A closed vocabulary would be an enum port with a shared set, and the shared
/// sets live in the runtime's `RuntimeGraphContracts.EnumSetTable` — a table this package may not extend and whose
/// members the website's `graphEnumSets` mirrors. Until `charge_phase` and `aim_phase` exist there, the phase is a
/// string spelled exactly the way those sets would spell it, so the day the sets are declared the two ports change
/// their `type` and nothing else. See the report for the exact set definitions this row needs.
/// </summary>
public static class WeaponStateTriggerContract
{
    /// <summary>A weapon's charge changed phase: it started charging, it is charging, it stopped charging, or the
    /// charged attack was really swung.</summary>
    public const string ChargeStateCapability = "forge.trigger.weapon.charge_state";
    /// <summary>A wielder entered or left aiming down sights.</summary>
    public const string AimStateCapability = "forge.trigger.combat.aim_state";

    public const string ChargeStateBinding = ModuleDefinition.ProviderId + ".binding.charge_state";
    public const string AimStateBinding = ModuleDefinition.ProviderId + ".binding.aim_state";

    /// <summary>The handler names the bindings resolve to. Both rows are observe-only, so the name is the row's own
    /// id in this provider's namespace, exactly like every other fact row this package declares.</summary>
    public const string ChargeStateHandler = "gtfo.weapon.charge_state";
    public const string AimStateHandler = "gtfo.weapon.aim_state";

    /// <summary>The four charge phases, in the order the vocabulary lists them: the charge began, the charge is
    /// running, the charge ended without a swing, and the charged attack was released. A weapon that charges
    /// through a threshold the archetype names reports the phase the state machine is really in, never a phase
    /// inferred from how long a button was held.</summary>
    public static readonly IReadOnlyList<string> ChargePhases = Array.AsReadOnly(new[]
    {
        "start", "progress", "end", "swing"
    });

    /// <summary>The four charge phases by name, so a body that publishes one and a plan that compares against it
    /// spell the value once.</summary>
    public const string ChargeStart = "start";
    public const string ChargeProgress = "progress";
    public const string ChargeEnd = "end";
    public const string ChargeSwing = "swing";

    /// <summary>The two aim phases: the sights came up, and the sights went down.</summary>
    public static readonly IReadOnlyList<string> AimPhases = Array.AsReadOnly(new[] { "enter", "exit" });

    /// <summary>The two aim phases by name.</summary>
    public const string AimEnter = "enter";
    public const string AimExit = "exit";

    /// <summary>The permission both rows are read under: they read the weapon's own state and its owner, and
    /// nothing here writes.</summary>
    public const string ReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The domains the catalog's charge and aim rows name: a weapon, a tool or a consumable is charged or
    /// aimed by the player holding it, and the state is the equipment's own.</summary>
    public static readonly string[] Domains = { "weapon", "tool", "consumable", "player" };

    /// <summary>The charge row. `ratio` is optional because a charge that just started has no ratio to report yet
    /// — the native state machine enters the charging state before any time has been accumulated, and an absent
    /// port means "not read yet" rather than a zero standing in for one.</summary>
    public const string ChargeStateRowDocument = """
    {
      "id": "forge.trigger.weapon.charge_state",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "武器蓄力状态",
      "version": "1.0.0",
      "parameters": { "description": "武器蓄力开始、进行、结束或真正挥出时触发，并给出当前蓄力比例。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable", "player"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "phase", "type": "string" },
          { "id": "ratio", "type": "number", "optional": true }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>The aim row. The actor is the player reference the wield rows already carry, and the equipment is
    /// the weapon whose sights moved: the two ports together are what a plan mounts on and what it acts on.</summary>
    public const string AimStateRowDocument = """
    {
      "id": "forge.trigger.combat.aim_state",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "瞄准状态变化",
      "version": "1.0.0",
      "parameters": { "description": "举镜瞄准进入或退出时触发。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable", "player"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "entityKinds": ["gtfo.player"], "id": "actor", "type": "entity" },
          { "id": "phase", "type": "string" }
        ],
        "parameters": []
      }
    }
    """;

    public static JsonElement ChargeStateRow() => RuntimeJson.Parse(ChargeStateRowDocument);
    public static JsonElement AimStateRow() => RuntimeJson.Parse(AimStateRowDocument);

    /// <summary>The two rows as one `capabilities` array.</summary>
    public static IReadOnlyList<object> Capabilities() => new object[]
    {
        RuntimeJson.Parse(ChargeStateRowDocument), RuntimeJson.Parse(AimStateRowDocument)
    };

    /// <summary>The two observe bindings: this package charges and aims nothing, it observes the weapon's own
    /// state.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        Binding(ChargeStateBinding, ChargeStateCapability, ChargeStateHandler),
        Binding(AimStateBinding, AimStateCapability, AimStateHandler)
    };

    /// <summary>The registration support lines for those bindings.</summary>
    public static IReadOnlyList<BindingSupport> Support() => new[]
    {
        new BindingSupport(ChargeStateBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(AimStateBinding, "implementation-only", new[] { ReadPermission })
    };

    /// <summary>The row's output ports in declaration order, as `id:type` texts: the port list a plan wires and
    /// the thing a port-order drift would break. Used by this family's own tests.</summary>
    public static IReadOnlyList<string> OutputPorts(JsonElement row)
    {
        var ports = new List<string>();
        foreach (var port in row.GetProperty("graph").GetProperty("outputs").EnumerateArray())
        {
            var id = port.GetProperty("id").GetString() ?? "";
            var type = port.GetProperty("type").GetString() ?? "";
            var carrier = "";
            if (port.TryGetProperty("schema", out var schema)) carrier = ":" + schema.GetString();
            if (port.TryGetProperty("unit", out var unit)) carrier += ":" + unit.GetString();
            if (port.TryGetProperty("optional", out var optional) && optional.GetBoolean()) carrier += "?";
            ports.Add(id + ":" + type + carrier);
        }
        return ports;
    }

    private static object Binding(string bindingId, string capabilityId, string handler) => new
    {
        id = bindingId, capabilityId, providerId = ModuleDefinition.ProviderId, handler, role = "observe",
        status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };
}
