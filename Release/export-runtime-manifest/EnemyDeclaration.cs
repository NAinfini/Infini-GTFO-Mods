using System.Text.Json;
using ForgeEnemy;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The ForgeEnemy provider's export-time registration: the rows and support lines are the package's own
/// <see cref="EnemyRegistration"/>, the same composition the game-bound module registers, and every body is
/// replaced by a stand-in because neither a release-time process nor the GameBindings harness can compile
/// `Native/EnemyModule*.cs`. The rows therefore cannot drift from the game's: there is one declaration and this
/// file only decides what answers it here.
///
/// The rows' own shapes are derived from the capability rows the bindings implement rather than restated: a shape
/// names the graph's own value ports and parameters, so a derived one cannot describe a layout the row does not
/// declare.</summary>
internal static class EnemyDeclaration
{
    /// <summary>The provider's registration: the shared declaration's own rows and support lines, every handler
    /// replaced by the caller's stand-in (or by the refusing one when the caller has none), and one shape per
    /// handler derived from the capability row the binding implements.</summary>
    internal static RuntimeModule Module(RuntimeKernel kernel, Func<string, CommandHandler>? command = null)
    {
        string registry = EnemyRegistration.RegistryJson();
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
        return new RuntimeModule(RuntimeKernel.ApiVersion, registry, handlers, EnemyRegistration.Support(),
            new Dictionary<string, Func<EntityReference, bool>> { ["gtfo.enemy"] = static _ => true })
        {
            Shapes = shapes,
            Evaluators = evaluators
        };
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
}
