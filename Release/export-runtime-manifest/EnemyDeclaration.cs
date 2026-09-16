using System.Text.Json;
using ForgeEnemy;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The ForgeEnemy provider's declaration, assembled from the game-independent contract sources instead of
/// from the native module that answers it. A release-time process and the GameBindings harness both need the rows
/// this provider registers, and neither can compile `Native/EnemyModule*.cs`: the module is the game-bound half and
/// reads the game's own types. So the declaration half is composed here — the same rows, in the same order, from the
/// same contracts `EnemyModule.Registry` composes them from — and every body is left to the caller: the export
/// process hands over a stand-in that refuses to run, a harness hands over its own double.
///
/// The base family (the five combat rows, the four enemy lifecycle rows, the two scout rows and the damage action) is
/// the one half that is not spelled in a contract file: it is declared as the JSON block `Native/EnemyModule.cs`
/// carries, and it is reproduced here verbatim because the export set has no other source for it. A row changed
/// there has to be changed here; the website's own catalog comparison catches the drift, because a plan is compiled
/// against the exported manifest and refuses a row the game really registers.</summary>
internal static class EnemyDeclaration
{
    internal const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>The base family's binding ids, named rather than spelled at each call site. They are the ids the rows
    /// below declare and the ones `Native/EnemyModule.cs` names.</summary>
    internal const string DamageBinding = ProviderId + ".binding.damage_applied";
    internal const string DamageActionBinding = ProviderId + ".binding.damage";
    internal const string HealBinding = ProviderId + ".binding.heal";
    internal const string HealthChangedBinding = ProviderId + ".binding.health_changed";
    internal const string DeathStartedBinding = ProviderId + ".binding.death_started";

    /// <summary>The provider head and the opening of the capability list: the same block the module carries.</summary>
    private const string RegistryHead = """
    {
      "providers": [
        {
          "id": "forge.module.gtfo.enemy",
          "kind": "native",
          "version": "1.0.0",
          "dependencies": []
        }
      ],
      "capabilities": [
    """;

    /// <summary>The base family's binding rows, verbatim from `Native/EnemyModule.cs`. Every one of them binds a
    /// capability the runtime's own contract providers declare, so no capability row travels with them.</summary>
    private const string BaseBindingRows = """
        {"id":"forge.module.gtfo.enemy.binding.damage_applied","capabilityId":"forge.trigger.combat.damage_applied","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.damage_applied","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.damage","capabilityId":"forge.action.combat.damage","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.damage","role":"execute","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.heal","capabilityId":"forge.action.combat.heal","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.heal","role":"execute","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.health_changed","capabilityId":"forge.trigger.combat.health_changed","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.health_changed","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.death_started","capabilityId":"forge.trigger.enemy.death_started","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.death_started","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.limb_broken","capabilityId":"forge.trigger.combat.limb_broken","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.limb_broken","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.awakened","capabilityId":"forge.trigger.enemy.awakened","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.awakened","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.target_acquired","capabilityId":"forge.trigger.enemy.target_acquired","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.target_acquired","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.target_lost","capabilityId":"forge.trigger.enemy.target_lost","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.target_lost","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.scout_detection","capabilityId":"forge.trigger.enemy.scout_detection","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.scout_detection","role":"observe","status":"implemented","dependencies":[],"requires":[]},
        {"id":"forge.module.gtfo.enemy.binding.scout_scream","capabilityId":"forge.trigger.enemy.scout_scream","providerId":"forge.module.gtfo.enemy","handler":"gtfo.enemy.scout_scream","role":"observe","status":"implemented","dependencies":[],"requires":[]}
    """;

    /// <summary>One support row per base binding, with the native read or write each of them performs — the same
    /// table `EnemyModule`'s constructor builds. The kernel refuses a registration whose implemented binding has no
    /// support row, so these travel with the rows above and not with the module.</summary>
    private static readonly (string Binding, string[] Permissions)[] BaseSupport =
    {
        (Binding("damage_applied"), new[] { "gtfo.enemy.health.read" }),
        (Binding("heal"), new[] { "gtfo.enemy.health.write" }),
        (Binding("health_changed"), new[] { "gtfo.enemy.health.read" }),
        (Binding("death_started"), new[] { "gtfo.enemy.lifecycle.read" }),
        (Binding("limb_broken"), new[] { "gtfo.enemy.limbs.read" }),
        (Binding("awakened"), new[] { "gtfo.enemy.behavior.read" }),
        (Binding("target_acquired"), new[] { "gtfo.enemy.targeting.read" }),
        (Binding("target_lost"), new[] { "gtfo.enemy.targeting.read" }),
        (Binding("scout_detection"), new[] { "gtfo.enemy.detection.read" }),
        (Binding("scout_scream"), new[] { "gtfo.enemy.behavior.read" }),
        (Binding("damage"), new[] { "gtfo.enemy.health.read", "gtfo.enemy.health.write" })
    };

    private static string Binding(string name) => ProviderId + ".binding." + name;

    /// <summary>The provider's registration: the declared rows above, every handler replaced by the caller's stand-in
    /// (or by the refusing one when the caller has none), and one shape per handler derived from the capability row
    /// the binding implements. Deriving the shape instead of restating a port table is what keeps the declaration
    /// honest — a shape names the graph's own value ports and parameters, so it cannot describe a layout the row does
    /// not declare.</summary>
    internal static RuntimeModule Module(RuntimeKernel kernel, Func<string, CommandHandler>? command = null)
    {
        string registry = RegistryJson();
        var declared = RuntimeJson.Parse(registry);
        // The capabilities these bindings point at are not all this provider's own: the combat, control and trigger
        // rows belong to the runtime's contract modules, which register before any package. The kernel's own manifest
        // is where both halves are visible while the registration window is open, so the capability a binding
        // implements is read from there and this provider's own rows are overlaid on it.
        var capabilities = RuntimeJson.Parse(kernel.ExportManifest())
            .GetProperty("registry").GetProperty("capabilities").EnumerateArray()
            .ToDictionary(row => row.GetProperty("id").GetString()!, StringComparer.Ordinal);
        foreach (var row in declared.GetProperty("capabilities").EnumerateArray())
            capabilities[row.GetProperty("id").GetString()!] = row;
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        var evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal);
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        // The same rule the registry applies when it wires a binding to a table: an `execute` action needs a command
        // handler and a shape, and an `evaluate` or attribute-reading `observe` row needs an evaluator and a shape.
        // Everything else — a trigger row, a control step — is answered by publication or by the kernel itself.
        foreach (var binding in declared.GetProperty("bindings").EnumerateArray())
        {
            if (binding.GetProperty("status").GetString() != "implemented") continue;
            string capabilityId = binding.GetProperty("capabilityId").GetString()!;
            if (!capabilities.TryGetValue(capabilityId, out var capability))
                throw new InvalidDataException("No registration declares the capability " + capabilityId
                    + " that " + binding.GetProperty("id").GetString() + " implements.");
            string kind = capability.GetProperty("kind").GetString()!;
            string role = binding.GetProperty("role").GetString()!;
            string handler = binding.GetProperty("handler").GetString()!;
            if (role == "execute" && kind == "action")
            {
                handlers[handler] = command?.Invoke(handler) ?? Refusing(handler);
                shapes.TryAdd(handler, ShapeOf(capability));
            }
            else if (role is "evaluate" or "observe" && kind is "selector" or "condition" or "state")
            {
                evaluators[handler] = RefusingEvaluator(handler);
                shapes.TryAdd(handler, ShapeOf(capability));
            }
        }
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry, handlers, Support(),
            new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.enemy"] = static _ => true })
        {
            Shapes = shapes,
            Evaluators = evaluators
        };
    }

    /// <summary>The registry text: the base block above, then the selector, node-list and action-family rows in the
    /// order `EnemyModule.Registry` appends them.</summary>
    internal static string RegistryJson()
    {
        string nodeCapabilities = string.Join(",\n", EnemyNodeValueContract.ValueRows().Select(RuntimeJson.From))
            + ",\n" + EnemyNodeEffectContract.CapabilityRowsJson;
        string nodeBindings = string.Join(",\n", EnemyNodeValueContract.ValueBindings().Select(RuntimeJson.From))
            + ",\n" + EnemyNodeEffectContract.BindingRowsJson
            + ",\n" + EnemyNodeTriggerContract.SpawnedBindingRowJson
            + ",\n" + EnemyNodeTriggerContract.BindingRowsJson;
        string familyCapabilities = string.Join(",\n", EnemyControlContract.CapabilityRows)
            + ",\n" + EnemyCombatContract.CapabilityRows
            + ",\n" + GlueContract.CapabilityRows
            + ",\n" + EnemyBehaviorContract.CapabilityRows;
        string familyBindings = string.Join(",\n", EnemyControlContract.BindingRows)
            + ",\n" + EnemyCombatContract.BindingRows
            + ",\n" + GlueContract.BindingRows
            + ",\n" + EnemyBehaviorContract.BindingRows;
        return RegistryHead
            + EnemySelectorContract.CapabilityRowJson
            + ",\n" + nodeCapabilities
            + ",\n" + familyCapabilities
            + "\n  ],\n  \"bindings\": [\n" + BaseBindingRows + ",\n"
            + AttackInstanceContract.BindingRowsJson + ",\n"
            + nodeBindings + ",\n"
            + familyBindings + ",\n"
            + EnemyWaveContract.BindingRowsJson + ",\n"
            + EnemySelectorContract.BindingRowJson + "\n  ]\n}";
    }

    /// <summary>Every support row the registration carries, in the order the module appends them.</summary>
    private static BindingSupport[] Support()
    {
        var rows = new List<BindingSupport>();
        foreach (var (binding, permissions) in BaseSupport) rows.Add(new(binding, "implementation-only", permissions));
        // The selector needs no permission beyond the `gtfo.enemy` kind this provider already owns.
        rows.Add(new(EnemySelectorContract.BindingId, "implementation-only", Array.Empty<string>()));
        // The two damage-transaction rows: the kill a committed hit settled and the part it went into.
        rows.AddRange(AttackInstanceContract.Support());
        // The node-list family: the generic spawn row, the six value rows and the tag and glue rows, each with the
        // permission its own contract declares, plus the three actions' write permissions.
        rows.Add(new(EnemyNodeTriggerContract.SpawnedBinding, "implementation-only", Array.Empty<string>()));
        rows.AddRange(EnemyNodeValueContract.ValueSupport());
        rows.AddRange(EnemyNodeTriggerContract.Support());
        rows.Add(new(EnemyNodeEffectContract.KillBinding, "implementation-only", new[] { EnemyNodeEffectContract.KillPermission }));
        rows.Add(new(EnemyNodeEffectContract.MarkBinding, "implementation-only", new[] { EnemyNodeEffectContract.MarkPermission }));
        rows.Add(new(EnemyNodeEffectContract.TargetBinding, "implementation-only", new[] { EnemyNodeEffectContract.TargetPermission }));
        // The action families and the wave trigger rows.
        rows.AddRange(EnemyControlContract.Support());
        rows.AddRange(EnemyCombatContract.Support);
        rows.AddRange(GlueContract.Support);
        rows.AddRange(EnemyBehaviorContract.Support());
        rows.AddRange(EnemyWaveContract.Support());
        return rows.ToArray();
    }

    /// <summary>The shape one handler resolves against: the capability's own value ports and parameters, in the
    /// declared order. A capability that expands its ports per plan has no registration-time layout, so its shape
    /// names nothing — which is the one shape the resolver accepts for such a graph.</summary>
    private static HandlerShape ShapeOf(JsonElement capability)
    {
        var shape = new HandlerShape();
        if (!capability.TryGetProperty("graph", out var graph)) return shape;
        if (graph.TryGetProperty("variadic", out _) || graph.TryGetProperty("portGroups", out _)) return shape;
        var inputs = ValuePorts(graph, "inputs");
        var outputs = ValuePorts(graph, "outputs");
        var parameters = graph.TryGetProperty("parameters", out var declared)
            ? declared.EnumerateArray().Select(row => row.GetProperty("id").GetString()!).ToArray()
            : Array.Empty<string>();
        if (inputs.Length > 0) shape.Inputs(inputs);
        if (outputs.Length > 0) shape.Outputs(outputs);
        if (parameters.Length > 0) shape.Parameters(parameters);
        return shape;
    }

    private static string[] ValuePorts(JsonElement graph, string side) => graph.TryGetProperty(side, out var ports)
        ? ports.EnumerateArray().Where(port => port.GetProperty("type").GetString() != "execution")
            .Select(port => port.GetProperty("id").GetString()!).ToArray()
        : Array.Empty<string>();

    /// <summary>The stand-in a body that lives in the game-bound assembly is registered with here. Nothing in a
    /// release-time process dispatches a command, so the row must still be exported — but an invented body would be a
    /// second implementation. This one refuses the moment it would run, which is the only honest answer available
    /// without the game.</summary>
    internal static CommandHandler Refusing(string handler) => _ =>
        throw new InvalidOperationException("The exported declaration does not execute the " + handler + " handler.");

    /// <summary>The same stand-in for the rows that are read on demand: a selector, a condition or a value row.</summary>
    private static EvaluatorHandler RefusingEvaluator(string handler) => _ =>
        throw new InvalidOperationException("The exported declaration does not evaluate the " + handler + " handler.");

    /// <summary>One control row's own shape, read back from the capability row that declares it: the stand-in module
    /// below has to expose the three port fields the control contract's `Shapes()` names, and deriving them from the
    /// row keeps them from being a second description of the same layout.</summary>
    internal static HandlerShape ShapeOfRow(string capabilityId)
    {
        foreach (var row in EnemyControlContract.CapabilityRows)
        {
            var parsed = RuntimeJson.Parse(row);
            if (parsed.GetProperty("id").GetString() == capabilityId) return ShapeOf(parsed);
        }
        throw new InvalidDataException("No control capability row declares " + capabilityId + ".");
    }
}

/// <summary>The declaration half of the native module, for the one file that needs the type: the control family's
/// contract, whose rows are read here and whose handler methods are never called by this process. It carries the
/// same ids `Native/EnemyControlActions.cs` carries and no behaviour at all; the port fields are the rows' own
/// layouts, read back from those rows.</summary>
internal sealed partial class EnemyModule
{
    internal const string AwakenBinding = EnemyDeclaration.ProviderId + ".binding.awaken";
    internal const string SleepBinding = EnemyDeclaration.ProviderId + ".binding.sleep";
    internal const string MoveToBinding = EnemyDeclaration.ProviderId + ".binding.move_to";
    internal const string AwakenHandler = "gtfo.enemy.awaken";
    internal const string SleepHandler = "gtfo.enemy.sleep";
    internal const string MoveToHandler = "gtfo.enemy.move_to";
    internal const string AwakenCapability = "forge.action.enemy.awaken";
    internal const string SleepCapability = "forge.action.enemy.sleep";
    internal const string MoveToCapability = "forge.action.enemy.move_to";

    internal static readonly HandlerShape AwakenPorts = EnemyDeclaration.ShapeOfRow(AwakenCapability);
    internal static readonly HandlerShape SleepPorts = EnemyDeclaration.ShapeOfRow(SleepCapability);
    internal static readonly HandlerShape MoveToPorts = EnemyDeclaration.ShapeOfRow(MoveToCapability);

    internal CommandResult Awaken(CommandContext context) => throw NotExecuted(AwakenHandler);
    internal CommandResult Sleep(CommandContext context) => throw NotExecuted(SleepHandler);
    internal CommandResult MoveTo(CommandContext context) => throw NotExecuted(MoveToHandler);

    private static InvalidOperationException NotExecuted(string handler) =>
        new("The exported declaration does not execute the " + handler + " handler.");
}
