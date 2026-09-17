using System;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;
using ForgeTrigger.Pure;

/// <summary>The one enum comparison the batch registers, exercised through the exact public helper its handler
/// calls and through the contract the kernel resolves for it. Both operands are members of the set the row's own
/// structural parameter chose, so the row is declared once rather than once per set; what is checked here is that
/// the answer is that equality (an unwritten value included) and that every set the parameter offers resolves to
/// that set's own members. Port ids, output shape and the catalog kind are checked by the contract suite.</summary>
internal static class EnumTests
{
    internal static void Run(Action<bool, string> check)
    {
        void Reject(string name, string code, Action action)
        {
            try { action(); check(false, name + " accepted " + code); }
            catch (RuntimeContractException error) { check(error.Code == code, name + " rejected " + code + ": " + error.Code); }
        }

        check(PureConditions.EnumEquals("direct", "direct") && !PureConditions.EnumEquals("direct", "melee"),
            "the answer is the equality of the two members");
        check(!PureConditions.EnumEquals(null, "direct"),
            "a value the plan did not write equals nothing, not the set's first member");
        check(!PureConditions.EnumEquals("direct", "DIRECT"),
            "members compare by ordinal, so no casing is folded into an equality the set never declared");

        // The declaration is the row: one `condition` whose two ports read the set its parameter chooses, so a
        // second copy of the same comparison per set would be a second row rather than a second declaration here.
        var row = PureModule.Nodes.Single(node => node.CapabilityId == "forge.condition.enum.compare");
        var graph = row.Graph;
        var inputs = graph.GetProperty("inputs").EnumerateArray().ToArray();
        var parameter = graph.GetProperty("parameters").EnumerateArray().Single();
        check(row.Kind == "condition" && row.HandlerName == "trigger.condition.enum_compare"
                && row.BindingId == "forge.module.trigger.binding.enum_compare"
                && graph.GetProperty("execution").GetString() == "pure"
                && inputs.Select(port => port.GetProperty("id").GetString()).SequenceEqual(new[] { "value", "equals" })
                && inputs.All(port => port.GetProperty("type").GetString() == "enum"
                    && port.GetProperty("schemaParameter").GetString() == "enum_set")
                && inputs[0].GetProperty("nullable").GetBoolean() && !inputs[1].TryGetProperty("nullable", out _)
                && graph.GetProperty("outputs")[0].GetProperty("type").GetString() == "boolean"
                && parameter.GetProperty("id").GetString() == "enum_set"
                && parameter.GetProperty("type").GetString() == "enum"
                && parameter.GetProperty("role").GetString() == "structural"
                && parameter.GetProperty("required").GetBoolean()
                && parameter.GetProperty("values").EnumerateArray().Select(value => value.GetString())
                    .SequenceEqual(RuntimeEnumSets.Names),
            "the row is one pure comparison whose ports read the set its own parameter chooses");

        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "enum-test-no-game"));
        using (kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off))
        {
            // Every set the parameter offers resolves to that set's own name, which is what one row covering all
            // of them means: the resolved port is the identity both sides compare, not a copy of the declaration,
            // and the parameter keeps offering the whole list to the next node instance.
            var resolved = RuntimeEnumSets.Names
                .Select(set => (Set: set, Contract: kernel.ResolveGraphContract("forge.condition.enum.compare", "1.0.0",
                    JsonSerializer.SerializeToElement(new { enum_set = set }))))
                .ToArray();
            check(resolved.All(entry => entry.Contract.GetProperty("inputs").EnumerateArray()
                    .All(port => port.GetProperty("schema").GetString() == entry.Set)
                && RuntimeEnumSets.Members(entry.Set).Count > 0
                && entry.Contract.GetProperty("parameters").EnumerateArray().Single()
                    .GetProperty("values").EnumerateArray().Select(value => value.GetString())
                    .SequenceEqual(RuntimeEnumSets.Names)),
                "every set the parameter offers resolves both ports to that set's own members");

            // A set the vocabulary does not declare and a run that wrote no set at all are both refusals rather
            // than a port whose schema one side would have to guess.
            Reject("resolve", "port-type", () => kernel.ResolveGraphContract("forge.condition.enum.compare", "1.0.0",
                JsonSerializer.SerializeToElement(new { enum_set = "not_a_set" })));
            Reject("resolve", "port-type", () => kernel.ResolveGraphContract("forge.condition.enum.compare", "1.0.0",
                JsonSerializer.SerializeToElement(new { })));
        }

        check(kernel.QueuedEvents == 0 && kernel.LoadedPlans == 0, "the comparison schedules no work");
    }
}
