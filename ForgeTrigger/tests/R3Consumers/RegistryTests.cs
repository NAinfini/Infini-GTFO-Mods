using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;
using ForgeTrigger.Targeting;

/// <summary>The production module's own manifest: what this package actually advertises, exported next to the run's
/// result so the batch's row list can be checked against registration instead of against prose. The declaration
/// tables are the intent and the manifest is the registration — a row one half knows and the other does not is a
/// mismatch here and a registration error in the kernel, and a row registered without the fact behind it would look
/// exactly like one that works.</summary>
internal static class RegistryTests
{
    /// <summary>The rows this batch registers: the life-state conditions that read the world through the step's own
    /// query session, each under the `observe` role its query tier asks for.</summary>
    private static readonly string[] RegisteredRows =
    {
        "forge.condition.predicate.alive", "forge.condition.predicate.player_downed"
    };

    /// <summary>The condition rows the authoring node list does not have. They are deleted with the rows they
    /// implemented, so the guard here is that none of them comes back as a registered capability: the comparison
    /// node answers the same questions from values the plan already has.</summary>
    private static readonly string[] DeletedRows =
    {
        "forge.condition.predicate.count", "forge.condition.predicate.distance",
        "forge.condition.predicate.entity_type", "forge.condition.predicate.exists",
        "forge.condition.predicate.has_tag", "forge.condition.predicate.team_relation",
        "forge.condition.predicate.chance", "forge.condition.predicate.status",
        "forge.condition.predicate.status_immune", "forge.condition.predicate.status_resistant"
    };

    /// <summary>The catalog rows of this batch that stay unregistered, each with the fact it is waiting for. Every
    /// one of them is a row this package would own; the guard is deleted together with the registration it blocks,
    /// never before.</summary>
    private static readonly (string CapabilityId, string WaitingFor)[] HeldBack =
    {
        ("forge.selector.target.priority", "the field-value semantics its `fields` list names"),
        ("forge.selector.target.deployed", "an owner read and a candidate source for the equipment kind"),
        ("forge.selector.target.interactable", "a candidate source for the map-object kind"),
        ("forge.selector.target.inventory", "the inventory slot read"),
        ("forge.selector.target.parent", "the optional `parent` snapshot field"),
        ("forge.selector.target.children", "the kernel's index over that field"),
        ("forge.selector.target.linked", "a native connection concept to read"),
        ("forge.condition.predicate.damage_type", "the pure family's own table and its `evaluate` role"),
        ("forge.condition.predicate.shot_index", "the pure family's own table and its `evaluate` role"),
        ("forge.condition.predicate.all_targets", "the pure family's own table and its `evaluate` role"),
        ("forge.condition.predicate.any_target", "the pure family's own table and its `evaluate` role"),
        ("forge.condition.predicate.enabled", "an enabled/switched-on fact to read"),
        ("forge.condition.predicate.owned_by", "a declared owner read on the subject"),
        ("forge.condition.predicate.hit_region", "the hit's own region, which no observed entity carries"),
        ("forge.condition.predicate.door", "a declared door-state read; only an observation tag exists today"),
        ("forge.condition.predicate.power", "a powered-circuit fact"),
        ("forge.condition.predicate.inventory_item", "the inventory item read")
    };

    internal static void Run(Action<bool, string> check, string resultPath)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "trigger-manifest-no-game"));
        using var handle = kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off);
        var manifest = kernel.ExportManifest();
        // The declared tables, each read with the role its own rows register under: the pure table evaluates, and
        // every observation row carries the role its tier decided when it was declared.
        var declared = PureModule.Nodes.Select(node => (node.CapabilityId, node.BindingId, Role: "evaluate"))
            .Concat(ObservedQueryModule.Nodes.Select(node => (node.CapabilityId, node.BindingId, node.Role)))
            .Concat(ObservedSpaceNodes.Nodes.Select(node => (node.CapabilityId, node.BindingId, node.Role)))
            .ToArray();
        var registry = JsonDocument.Parse(manifest).RootElement.GetProperty("registry");
        var capabilities = registry.GetProperty("capabilities").EnumerateArray()
            .Select(row => row.GetProperty("id").GetString()!).ToArray();
        var bindings = registry.GetProperty("bindings").EnumerateArray().ToArray();
        check(capabilities.OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(declared.Select(row => row.CapabilityId).OrderBy(id => id, StringComparer.Ordinal)),
            "the registered manifest carries exactly the declared capabilities");
        check(bindings.Select(row => row.GetProperty("id").GetString()!).OrderBy(id => id, StringComparer.Ordinal)
                .SequenceEqual(declared.Select(row => row.BindingId).OrderBy(id => id, StringComparer.Ordinal)),
            "the registered manifest carries exactly the declared bindings");
        check(declared.All(row => bindings.Any(binding => binding.GetProperty("id").GetString() == row.BindingId
                && binding.GetProperty("role").GetString() == row.Role)),
            "every registered row carries the role its own declaration gave it");
        foreach (var capabilityId in RegisteredRows)
        {
            var binding = bindings.SingleOrDefault(row => row.GetProperty("capabilityId").GetString() == capabilityId);
            check(binding.ValueKind == JsonValueKind.Object
                && binding.GetProperty("role").GetString() == "observe"
                && binding.GetProperty("status").GetString() == "implemented",
                "the manifest carries this batch's registered row: " + capabilityId);
        }
        foreach (var (capabilityId, waitingFor) in HeldBack)
            check(!capabilities.Contains(capabilityId, StringComparer.Ordinal),
                "a row still waiting for " + waitingFor + " is not advertised: " + capabilityId);
        foreach (var capabilityId in DeletedRows)
            check(!capabilities.Contains(capabilityId, StringComparer.Ordinal),
                "a condition row the node list does not have is not advertised: " + capabilityId);
        // `forge.selector.target.enemies` is the one batch row this package must not declare: the Enemy provider
        // owns that capability, its candidate source and its evaluator, and a second declaration would be refused.
        check(!capabilities.Contains("forge.selector.target.enemies", StringComparer.Ordinal),
            "a capability another provider owns is not redeclared here");
        var output = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(resultPath))!, "manifest.json");
        File.WriteAllText(output, manifest);
        check(File.Exists(output), "the production manifest is exported beside the run result: " + output);
    }
}
