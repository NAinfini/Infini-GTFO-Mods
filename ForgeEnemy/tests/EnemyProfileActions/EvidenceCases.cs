using System.Text.Json;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using static T;

/// <summary>Cases about the declaration itself: the row this slice declares is the website catalog's own shape,
/// its handler shape resolves against that row through the kernel's own contract resolver, and only a row with a
/// native write behind it is declared at all. What the native write is, and why the four absent rows are absent,
/// is in `ForgeEnemy/evidence/enemy-profile-actions.json`.
///
/// The handler is registered by the integration patch, not by this slice, so these cases resolve the row the
/// contract carries rather than reading it back out of a live manifest.</summary>
internal static class EvidenceCases
{
    /// <summary>The catalog's `forge.action.enemy.phase_set` row, read from the website's
    /// `catalog/capability-catalog.json`. Only the parts this provider is responsible for are carried here — the
    /// port ids in their declared order, the parameters, and the result field ids — because that is what the
    /// registry row has to agree with. A sibling slice owns the catalog side; this constant is what the check
    /// compares against until the row travels to the single trigger owner.</summary>
    private const string CatalogShape = """
    {
      "inputs": ["in", "enemies", "phase", "expected_phase"],
      "outputs": ["next", "result"],
      "parameters": ["reset_policy"],
      "resetValues": ["keep", "reset"],
      "resultSchema": "forge.result.enemy.phase_set",
      "resultFields": ["target", "status", "committed", "code", "phase", "target_count"]
    }
    """;

    /// <summary>The capabilities this slice surveyed and did not implement. Every one of them must be listed in
    /// the contract with a reason, so an absent row is never mistaken for an oversight.</summary>
    private static readonly string[] Absent =
    {
        "forge.action.enemy.attack", "forge.action.enemy.behavior_interrupt",
        "forge.action.enemy.limb_profile", "forge.action.enemy.perception_profile"
    };

    internal static void Run()
    {
        Case("evidence.declared-row-is-the-catalog-shape", () =>
        {
            var expected = RuntimeJson.Parse(CatalogShape);
            var row = RuntimeJson.Parse(EnemyProfileContract.CapabilityRows);
            Check(row.GetProperty("id").GetString() == PhaseSetCases.Capability, "The declared row has the wrong id.");
            Check(row.GetProperty("owner").GetString() == EnemyProfileContract.ProviderId, "The declared row has the wrong owner.");
            Check(row.GetProperty("kind").GetString() == "action", "The declared row is not an action.");
            Check(row.GetProperty("version").GetString() == "1.0.0", "The declared row has the wrong version.");
            var graph = row.GetProperty("graph");
            Check(graph.GetProperty("execution").GetString() == "host", "The declared row is not host-executed.");
            Check(Ids(graph.GetProperty("inputs")).SequenceEqual(Ids(expected.GetProperty("inputs"))),
                "Declared inputs drifted from the catalog.");
            Check(Ids(graph.GetProperty("outputs")).SequenceEqual(Ids(expected.GetProperty("outputs"))),
                "Declared outputs drifted from the catalog.");
            Check(Ids(graph.GetProperty("parameters")).SequenceEqual(Ids(expected.GetProperty("parameters"))),
                "Declared parameters drifted from the catalog.");
            Check(Ids(graph.GetProperty("parameters").EnumerateArray()
                    .Single(p => p.GetProperty("id").GetString() == "reset_policy").GetProperty("values"))
                .SequenceEqual(Ids(expected.GetProperty("resetValues"))), "reset_policy values drifted from the catalog.");
            var result = graph.GetProperty("outputs").EnumerateArray().Single(o => o.GetProperty("id").GetString() == "result");
            Check(result.GetProperty("schema").GetString() == expected.GetProperty("resultSchema").GetString(),
                "The result schema is not the catalog's.");
            Check(Ids(result.GetProperty("fields")).SequenceEqual(Ids(expected.GetProperty("resultFields"))),
                "Result fields drifted from the catalog.");
            Check(graph.GetProperty("recipients").GetProperty("input").GetString() == "enemies"
                && graph.GetProperty("recipients").GetProperty("cardinality").GetString() == "many",
                "The recipient collection is not the declared many-valued `enemies`.");
        });

        Case("evidence.handler-shape-resolves-against-the-row", () =>
        {
            // The kernel resolves a handler's declared names against the capability graph it belongs to, once per
            // binding, at registration: `Scene.RowKernel` registered the real handler with the real shape, so a
            // name the row does not carry would already have failed there. What is left to check here is that the
            // resolution is *this* row's layout — the declared value ports in their declared order, the result
            // output and the structural parameter the handler reads.
            var kernel = Scene.RowKernel();
            var contract = kernel.ResolveGraphContract(PhaseSetCases.Capability, "1.0.0",
                RuntimeJson.From(new { reset_policy = 0 }));
            var shape = EnemyProfileContract.PhaseSetShape();
            var graphInputs = Ids(contract.GetProperty("inputs")).Where(id => id != "in").ToArray();
            Check(shape.InputPorts.SequenceEqual(graphInputs),
                $"The handler shape's inputs are not the row's value inputs in frame order: {string.Join(",", shape.InputPorts)} vs {string.Join(",", graphInputs)}.");
            Check(shape.OutputPorts.SequenceEqual(new[] { "result" }), "The handler shape does not declare the result output.");
            Check(shape.ParameterIds.SequenceEqual(new[] { "reset_policy" }), "The handler shape does not declare reset_policy.");
            Check(graphInputs.SequenceEqual(new[] { "enemies", "phase", "expected_phase" }),
                "The row's own value inputs are not the ones the handler reads, in order.");
        });

        Case("evidence.binding-row-names-the-declared-handler", () =>
        {
            var binding = RuntimeJson.Parse(EnemyProfileContract.BindingRows);
            Check(binding.GetProperty("id").GetString() == EnemyProfileContract.PhaseSetBinding, "The binding id is not the provider's own name.");
            Check(binding.GetProperty("capabilityId").GetString() == PhaseSetCases.Capability, "The binding implements the wrong capability.");
            Check(binding.GetProperty("providerId").GetString() == EnemyProfileContract.ProviderId, "The binding names the wrong provider.");
            Check(binding.GetProperty("handler").GetString() == EnemyProfileContract.PhaseSetHandler, "The binding names a handler the contract does not declare.");
            Check(binding.GetProperty("role").GetString() == "execute", "The binding is not an execute row.");
            Check(binding.GetProperty("status").GetString() == "implemented", "The binding claims a status other than implemented.");
            Check(EnemyProfileContract.Handlers.SequenceEqual(new[] { EnemyProfileContract.PhaseSetHandler }),
                "The contract declares a handler row it does not implement.");
            Check(EnemyProfileContract.Bindings.SequenceEqual(new[] { EnemyProfileContract.PhaseSetBinding }),
                "The contract declares a binding row it does not implement.");
        });

        Case("evidence.support-rows-stay-implementation-only", () =>
        {
            Check(EnemyProfileContract.Support.Length == 1, "The contract declares more than one support row.");
            var support = EnemyProfileContract.Support[0];
            Check(support.BindingId == EnemyProfileContract.PhaseSetBinding, "The support row belongs to another binding.");
            Check(support.Verification == "implementation-only", "An unverified native binding was promoted.");
            Check(support.RequiredPermissions.SequenceEqual(new[] { "gtfo.enemy.behavior.write" }),
                "The declared permission is not the native write this handler performs.");
        });

        Case("evidence.absent-rows-carry-a-reason", () =>
        {
            var reasons = EnemyProfileContract.Unimplemented.ToDictionary(x => x.Capability, x => x.Reason, StringComparer.Ordinal);
            foreach (var capability in Absent)
                Check(reasons.TryGetValue(capability, out var reason) && reason.StartsWith("未实现：", StringComparison.Ordinal),
                    $"{capability} has no recorded reason.");
            Check(reasons.Count == Absent.Length, "An unimplemented row is listed twice.");
            foreach (var capability in Absent)
                Check(!EnemyProfileContract.CapabilityRows.Contains(capability, StringComparison.Ordinal),
                    $"{capability} has a declared row but no handler.");
        });
    }

    private static IEnumerable<string> Ids(JsonElement rows)
        => rows.EnumerateArray().Select(row => row.ValueKind == JsonValueKind.String
            ? row.GetString()!
            : row.GetProperty("id").GetString()!);
}
