using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeWeapon;

namespace ForgeWeapon.Tests.WeaponPrimitives;

// The production registration of this package, composed exactly as the native session composes it: every contract
// this batch adds travels through `ModuleDefinition.Create`, so a row, its binding, its support line and its
// handler shape are exercised here together and none of them can be declared apart from the others. Nothing in
// this suite touches a game type — the registration and the two read bodies are the whole subject.
public sealed class WeaponPrimitiveContractTests
{
    private static void Require(bool condition, string detail) { if (!condition) throw new Exception(detail); }

    /// <summary>The ids this batch adds, and the binding each one is implemented through.</summary>
    private static readonly (string Capability, string Binding, string Handler)[] Rows =
    {
        (WeaponStateTriggerContract.ChargeStateCapability, WeaponStateTriggerContract.ChargeStateBinding, WeaponStateTriggerContract.ChargeStateHandler),
        (WeaponStateTriggerContract.AimStateCapability, WeaponStateTriggerContract.AimStateBinding, WeaponStateTriggerContract.AimStateHandler),
        (CombatPrimitiveContract.ShotResolvedCapability, CombatPrimitiveContract.ShotResolvedBinding, CombatPrimitiveContract.ShotResolvedHandler),
        (CombatPrimitiveContract.ProjectileLaunchCapability, CombatPrimitiveContract.ProjectileLaunchBinding, CombatPrimitiveContract.ProjectileLaunchHandler)
    };

    /// <summary>The whole registration, built the way the native half builds it — every row this batch adds, and a
    /// body everywhere `ModuleDefinition.Create` declares an action row, because the registry refuses an
    /// implemented binding whose handler the module does not carry. The bodies never run here: the subject is
    /// whether the rows, their bindings, their support lines and their shapes can be declared at all.
    /// </summary>
    private static RuntimeKernel Register(out RuntimeModuleHandle module)
    {
        var kernel = new RuntimeKernel(new("test.weapon.primitives", "1.0.0", RuntimeKernel.ApiVersion,
            "synthetic-no-game"));
        kernel.BeginWorld(7);
        // The runtime's own trigger provider, which declares the shapes this package's observe rows bind to. It is
        // registered first for the same reason the host registers its built-ins first: a binding whose capability
        // nobody declared is refused as `binding-capability`, and half this provider's rows are bindings over the
        // shared shapes rather than rows of its own.
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        CommandHandler unused = _ => CommandResult.Rejected("not-run");
        module = kernel.RegisterModule(ModuleDefinition.Create(unused, unused, null, unused, unused, null, unused),
            RuntimeLogLevel.Off);
        return kernel;    }

    [Fact]
    public void registration_declares_every_row_with_its_binding_and_support()
    {
        var kernel = Register(out var module);
        using (module)
        {
            var manifest = kernel.ExportManifest();
            foreach (var (capability, binding, handler) in Rows)
            {
                Require(manifest.Contains(capability), "capability not declared: " + capability);
                Require(manifest.Contains(binding), "binding not declared: " + binding);
                Require(manifest.Contains(handler), "handler not named: " + handler);
            }
        }
    }

    [Fact]
    public void the_charge_row_carries_the_four_phases_and_an_optional_ratio()
    {
        var row = WeaponStateTriggerContract.ChargeStateRow();
        Require(row.GetProperty("id").GetString() == WeaponStateTriggerContract.ChargeStateCapability, "row id");
        Require(row.GetProperty("graph").GetProperty("execution").GetString() == "host", "charge row is host tier");
        var ports = WeaponStateTriggerContract.OutputPorts(row);
        Require(ports.Contains("equipment:entity"), "charge row has no equipment port [" + string.Join("|", ports) + "]");
        Require(ports.Contains("phase:string"), "charge row has no phase port [" + string.Join("|", ports) + "]");
        Require(ports.Contains("ratio:number?"), "the charge ratio is not optional [" + string.Join("|", ports) + "]");
        Require(WeaponStateTriggerContract.ChargePhases.SequenceEqual(new[] { "start", "progress", "end", "swing" }),
            "the charge phase vocabulary moved");
    }

    [Fact]
    public void the_aim_row_carries_the_actor_and_the_two_phases()
    {
        var row = WeaponStateTriggerContract.AimStateRow();
        var ports = WeaponStateTriggerContract.OutputPorts(row);
        Require(ports.Contains("actor:entity"), "aim row has no actor port [" + string.Join("|", ports) + "]");
        Require(ports.Contains("equipment:entity"), "aim row has no equipment port [" + string.Join("|", ports) + "]");
        Require(WeaponStateTriggerContract.AimPhases.SequenceEqual(new[] { "enter", "exit" }),
            "the aim phase vocabulary moved");
    }

    [Fact]
    public void the_shot_resolution_row_separates_the_three_outcomes()
    {
        var row = CombatPrimitiveContract.ShotResolvedRow();
        Require(row.GetProperty("id").GetString() == CombatPrimitiveContract.ShotResolvedCapability, "row id");
        Require(row.GetProperty("graph").GetProperty("execution").GetString() == "host", "shot row is host tier");
        var ports = CombatPrimitiveContract.OutputPorts(row);
        Require(ports.Contains("outcome:string"), "shot row has no outcome port [" + string.Join("|", ports) + "]");
        Require(ports.Contains("target:entity?"), "a world hit has no target to name [" + string.Join("|", ports) + "]");
        Require(ports.Contains("position:vector3:m?"), "a miss has no position to report [" + string.Join("|", ports) + "]");
        Require(ports.Contains("normal:vector3:m?"), "a miss has no normal to report [" + string.Join("|", ports) + "]");
        Require(ports.Contains("hit_count:integer"), "the shot tally is missing [" + string.Join("|", ports) + "]");
        Require(CombatPrimitiveContract.Outcomes.SequenceEqual(new[] { "hit", "world", "miss" }),
            "the outcome vocabulary moved");
    }

    [Fact]
    public void the_three_holder_rows_declare_the_owner_tier_and_the_result_shape()
    {
        var rows = WeaponHolderActionsContract.Capabilities();
        Require(rows.Count == 3, "the holder provider no longer declares exactly three rows");
        foreach (var row in rows)
        {
            var id = row.GetProperty("id").GetString();
            Require(row.GetProperty("graph").GetProperty("execution").GetString() == "owner",
                id + " is not an owner-tier row");
            var fields = row.GetProperty("graph").GetProperty("outputs").EnumerateArray()
                .First(port => port.GetProperty("id").GetString() == "result")
                .GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("id").GetString()).ToArray();
            Require(fields.Take(4).SequenceEqual(new[] { "target", "status", "committed", "code" }),
                id + " result does not start with the four fixed columns");
            // The magazine rows answer with the clip they left behind; the fire row has no own field, because one
            // `Fire` body is one shot and a count of shots would always read one.
            var expected = id == WeaponHolderActionsContract.AutoFireCapability ? 4 : 5;
            Require(fields.Length == expected, id + " result does not carry the expected field count");
        }
        Require(WeaponHolderActionsContract.KindOf(WeaponHolderActionsContract.AutoFireCapability)
            == WeaponHolderActionsContract.HolderAction.AutoFire, "the fire row is not routed to its own body");
        var fire = WeaponHolderActionsContract.Shapes()[WeaponHolderActionsContract.AutoFireHandler];
        // The row reads the two ports every holder row reads and no `amount`: the state it requires is a
        // structural parameter, so a frame that names no amount is a valid fire request.
        Require(fire.InputPorts.SequenceEqual(new[] { "equipment", "holder" }),
            "the fire row reads unexpected ports: " + string.Join("|", fire.InputPorts));
    }

    [Fact]
    public void the_launch_row_is_a_host_action_over_native_profiles()
    {
        var row = CombatPrimitiveContract.ProjectileLaunchRow();
        Require(row.GetProperty("graph").GetProperty("execution").GetString() == "host", "the launch row is host tier");
        // The row's own number is a result field, not a top-level port: an action answers through its result, and
        // `target_count` is how many copies really left the origin.
        var outputs = row.GetProperty("graph").GetProperty("outputs").EnumerateArray().ToArray();
        var fields = outputs.Single(port => port.GetProperty("id").GetString() == "result")
            .GetProperty("fields").EnumerateArray().Select(field => field.GetProperty("id").GetString()!).ToArray();
        Require(fields.SequenceEqual(new[] { "target", "status", "committed", "code", "target_count" }),
            "the launch result fields moved: " + string.Join("|", fields));
        Require(outputs.Single(port => port.GetProperty("id").GetString() == "result")
            .GetProperty("schema").GetString() == "forge.result.combat.projectile_launch", "the launch result schema moved");
        var parameters = row.GetProperty("graph").GetProperty("parameters").EnumerateArray()
            .ToDictionary(port => port.GetProperty("id").GetString()!, port => port);
        var profile = parameters["profile"].GetProperty("values").EnumerateArray().Select(v => v.GetString()!).ToArray();
        Require(profile.SequenceEqual(CombatPrimitiveContract.ProjectileProfiles),
            "the declared profiles are not the vocabularies this contract publishes");
        Require(parameters["profile"].GetProperty("required").GetBoolean(), "a launch without a profile names no projectile");
        var shape = CombatPrimitiveContract.Shapes(_ => CommandResult.Rejected("not-run"))[CombatPrimitiveContract.ProjectileLaunchHandler];
        Require(shape.InputPorts.SequenceEqual(new[] { "origin", "direction", "source" }),
            "the launch handler reads unexpected ports: " + string.Join("|", shape.InputPorts));
        // The three parameters the row declares are the three the body reads: a parameter the handler never reads
        // is one the kernel would refuse at registration, so reaching this point is the assertion.
        Require(parameters.Count == 3, "the launch row declares unexpected parameters");
    }

    [Fact]
    public void every_new_binding_has_a_handler_the_registration_can_resolve()
    {
        // Built with the launch body, exactly as the native session builds it: the launch row is declared only
        // when a body for it is supplied, so a registration without one is a row this case must not look for.
        var module = ModuleDefinition.Create(projectileLaunch: _ => CommandResult.Rejected("not-run"));        var registration = RuntimeJson.Parse(module.RegistryJson);
        var bindings = registration.GetProperty("bindings").EnumerateArray().ToArray();
        var capabilities = registration.GetProperty("capabilities").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()).ToHashSet(StringComparer.Ordinal);
        foreach (var (capability, binding, handler) in Rows)
        {
            var row = bindings.FirstOrDefault(candidate => candidate.GetProperty("id").GetString() == binding);
            Require(row.ValueKind == JsonValueKind.Object, "binding missing from the registration: " + binding);
            Require(row.GetProperty("capabilityId").GetString() == capability, binding + " binds another capability");
            Require(row.GetProperty("handler").GetString() == handler, binding + " names another handler");
            Require(row.GetProperty("status").GetString() == "implemented", binding + " is not implemented");
            Require(capabilities.Contains(capability), binding + " binds a capability nobody declares");
        }
    }
}
