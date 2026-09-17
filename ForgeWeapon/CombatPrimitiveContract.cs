using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The one combat row of this provider that is not a weapon state: one shot's own resolution
/// (`forge.trigger.combat.shot_resolved`).
///
/// <b>The trigger.</b> A plan that wants to react to "this bullet hit nothing" cannot compose it: absence of a
/// hit is not an event, and <c>forge.trigger.combat.hit_candidate</c> only ever fires when a ray did reach
/// something. The row therefore publishes all three outcomes of one native shot — it reached a damageable
/// target, it reached world geometry (with the point and the surface normal it struck), or it resolved with no
/// impact at all — and <c>position</c> and <c>normal</c> are absent exactly when nothing was struck. The unit is
/// one `Fire`: a shotgun's several pellets are one shot with a <c>hit_count</c>, never several shots, because
/// that is what the native body produced. A miss is published from the firing body's own end, so it is one
/// event per shot and can never be doubled by a later hit.
///
/// <b>Why `outcome` is a string.</b> The runtime's `RuntimeGraphContracts.EnumSets` is the one table an enum
/// port's set may come from and this package may not extend it, so a closed vocabulary here would be an enum
/// port naming a set nothing declares. The member names are spelled exactly the way the set
/// <c>shot_outcome</c> should carry them — `hit`, `world`, `miss` — so the day that set is declared the port
/// changes its `type` and nothing else. The same holds for <c>phase</c> in
/// <see cref="WeaponStateTriggerContract"/> and for the `charge_phase` / `aim_phase` sets the report asks for.
/// </summary>
public static class CombatPrimitiveContract
{
    /// <summary>One shot reached a damageable target, reached world geometry, or reached nothing.</summary>
    public const string ShotResolvedCapability = "forge.trigger.combat.shot_resolved";
    /// <summary>One of the game's own projectiles, launched from a point and a direction.</summary>
    public const string ProjectileLaunchCapability = "forge.action.combat.projectile_launch";

    public const string ShotResolvedBinding = ModuleDefinition.ProviderId + ".binding.shot_resolved";
    public const string ProjectileLaunchBinding = ModuleDefinition.ProviderId + ".binding.projectile_launch";

    /// <summary>The handler names. The trigger's is a fact body the native half publishes through; the action's is
    /// the body the native half executes.</summary>
    public const string ShotResolvedHandler = "gtfo.weapon.shot_resolved";
    public const string ProjectileLaunchHandler = "gtfo.weapon.projectile_launch";

    /// <summary>The permission all three rows are read under: the shot's own combat reading, the same one the
    /// shot facts already carry.</summary>
    public const string ReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The permission the launch row writes under: it creates a world object, which is the same kind of
    /// write a deployed device is.</summary>
    public const string LaunchPermission = ModuleDefinition.DeployableReadPermission;

    /// <summary>The projectile profiles this build really has, in the order the native `ProjectileType` declares
    /// them. The list is the native enum's own members and not a wish list: each one is a prefab the game's
    /// `ProjectileManager` loads for itself, so a launch asks for a kind the game already knows how to fire.
    /// `NotTargetingSmallPellet` is left out because the game uses it as an impact object rather than as a
    /// projectile of its own, and advertising it would promise a flight this build may not run.</summary>
    public static readonly IReadOnlyList<string> ProjectileProfiles = Array.AsReadOnly(new[]
    {
        "targeting_small", "targeting_medium", "targeting_large", "semi_targeting_quick",
        "not_targeting_small_fast", "glue_flying", "infection_bomb"
    });

    /// <summary>The three outcomes a shot resolves to, in the order the vocabulary lists them. The spelling is
    /// the member set the port's `type` will carry once the set exists; the native half publishes one of these
    /// and never a fourth.</summary>
    public const string Hits = "hit";
    public const string World = "world";
    public const string Misses = "miss";
    public static readonly IReadOnlyList<string> Outcomes = Array.AsReadOnly(new[] { Hits, World, Misses });

    /// <summary>The one-shot row. `target` is absent for a shot that struck world geometry and for a miss, which
    /// is why the port is nullable: an absent target is the outcome's own meaning and never a broken read. The
    /// candidate row next to this one already carries `target`, so a plan that wants to damage what was hit reads
    /// the same port name on both.</summary>
    public const string ShotResolvedRowDocument = """
    {
      "id": "forge.trigger.combat.shot_resolved",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "一发子弹结算完成",
      "version": "1.0.0",
      "parameters": {
        "description": "一发子弹走完，结果是命中目标、打在世界表面，还是什么都没打到。",
        "summary": "一发子弹走完并给出结果：命中可受伤目标、打在世界表面（含位置与法线）、或者落空。整次开火不在这里，看「确实打出了一发」。",
        "summaryEn": "Fires when one shot has resolved: it struck a damageable target, it struck the world, or it struck nothing.",
        "labelEn": "Shot resolved",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": ["weapon", "tool", "consumable", "player"],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.player"], "id": "source", "type": "entity", "nullable": true },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "outcome", "type": "string" },
          { "id": "target", "type": "entity", "nullable": true },
          { "id": "position", "type": "vector3", "unit": "m", "nullable": true },
          { "id": "normal", "type": "vector3", "unit": "m", "nullable": true },
          { "id": "hit_count", "type": "integer" }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>
    /// The launch row. It is the host's own action because the game's projectile spawn is the host's: the prefab
    /// comes out of the manager's own table and the flight is simulated where the world is, so a client that
    /// spawned one locally would be running a projectile nobody else has. `profile` is a native `ProjectileType`
    /// member and not an inline projectile definition — a profile this build has no prefab for is refused by name
    /// rather than substituted, which is the difference between launching a real projectile and inventing one.
    /// `count` launches the same profile that many times from one point, and `spread` is the cone the copies are
    /// scattered over, which is what a shotgun-like pattern is made of.
    /// </summary>
    public const string ProjectileLaunchRowDocument = """
    {
      "id": "forge.action.combat.projectile_launch",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "发射抛射体",
      "version": "1.0.0",
      "parameters": {
        "description": "从指定位置朝指定方向发射一个或多个原生抛射体。",
        "summary": "从指定起点按给定方向发射一个或多个原生抛射体，可带散布。",
        "summaryEn": "Launches one or more of the game's own projectiles from a point and a direction.",
        "labelEn": "Launch projectile",
        "support": "authoring-contract-only"
      },
      "graph": {
        "domains": ["weapon", "tool", "consumable", "enemy", "map"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "id": "origin", "type": "vector3", "unit": "m" },
          { "id": "direction", "type": "vector3" },
          { "entityKinds": ["gtfo.player", "gtfo.enemy", "gtfo.equipment"], "id": "source", "type": "entity" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.combat.projectile_launch", "fields": [
            { "id": "target", "type": "entity" },
            { "id": "status", "type": "enum", "schema": "execution_outcome" },
            { "id": "committed", "type": "enum", "schema": "commit_state" },
            { "id": "code", "type": "string" },
            { "id": "target_count", "type": "integer" }
          ] }
        ],
        "parameters": [
          { "id": "profile", "type": "enum", "role": "structural", "required": true, "values": [
            "targeting_small", "targeting_medium", "targeting_large", "semi_targeting_quick",
            "not_targeting_small_fast", "glue_flying", "infection_bomb"
          ] },
          { "id": "count", "type": "integer", "role": "structural", "required": false },
          { "id": "spread", "type": "number", "role": "structural", "required": false }
        ],
        "recipients": {
          "input": "source", "target": "entity", "cardinality": "one",
          "requires": ["weapon.projectile"], "result": "result"
        }
      }
    }
    """;

    public static JsonElement ShotResolvedRow() => RuntimeJson.Parse(ShotResolvedRowDocument);
    public static JsonElement ProjectileLaunchRow() => RuntimeJson.Parse(ProjectileLaunchRowDocument);

    /// <summary>The two rows as one `capabilities` array. Every id is this provider's own: no contract in the
    /// runtime declares any of them, so a binding for one could not resolve its capability.</summary>
    public static IReadOnlyList<object> Capabilities() => new object[]
    {
        RuntimeJson.Parse(ShotResolvedRowDocument), RuntimeJson.Parse(ProjectileLaunchRowDocument)
    };

    /// <summary>The shot-resolution binding, and the launch binding only when a body for it is really supplied: a
    /// binding whose handler the registration does not carry is refused as `unused`, and a handler whose binding
    /// is not declared is refused the same way, so the row and its body travel together.</summary>
    public static IReadOnlyList<object> Bindings(CommandHandler? projectileLaunch = null)
    {
        var rows = new List<object>
        {
            Binding(ShotResolvedBinding, ShotResolvedCapability, ShotResolvedHandler)
        };
        if (projectileLaunch != null)
            rows.Add(Binding(ProjectileLaunchBinding, ProjectileLaunchCapability, ProjectileLaunchHandler, "execute"));
        return rows;
    }

    /// <summary>The registration support lines for those bindings: the trigger under the combat read permission
    /// and, when it is really declared, the launch under the deployable write it is.</summary>
    public static IReadOnlyList<BindingSupport> Support(CommandHandler? projectileLaunch = null)
    {
        var rows = new List<BindingSupport>
        {
            new(ShotResolvedBinding, "implementation-only", new[] { ReadPermission })
        };
        if (projectileLaunch != null)
            rows.Add(new BindingSupport(ProjectileLaunchBinding, "implementation-only", new[] { LaunchPermission }));
        return rows;
    }

    /// <summary>The ports the launch handler really reads, resolved at registration against the row's own ports.
    /// The trigger publishes facts instead of running a body, so it declares no shape.</summary>
    public static readonly HandlerShape ProjectileLaunchShape = new HandlerShape()
        .Inputs("origin", "direction", "source").Outputs("result").Parameters("profile", "count", "spread");

    /// <summary>The handler shapes this family registers: the action body, beside the row that carries it. The
    /// launch body travels in only when the native half really supplies one, so a process that declared this
    /// provider without its game half declares the row and answers nothing.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes(CommandHandler? projectileLaunch = null)
    {
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        if (projectileLaunch != null) shapes[ProjectileLaunchHandler] = ProjectileLaunchShape;
        return shapes;
    }

    /// <summary>The row's output ports in declaration order, as `id:type` texts. Used by this family's own
    /// tests, which is what a port-order drift breaks.</summary>
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
            if (port.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean()) carrier += "?";
            ports.Add(id + ":" + type + carrier);
        }
        return ports;
    }

    private static object Binding(string bindingId, string capabilityId, string handler, string role = "observe") => new
    {
        id = bindingId, capabilityId, providerId = ModuleDefinition.ProviderId, handler, role,
        status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };
}
