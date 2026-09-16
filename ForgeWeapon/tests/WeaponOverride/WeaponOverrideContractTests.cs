using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponOverride;

/// <summary>
/// The declared rows of the instance-override family, checked against the runtime's own contract rules rather
/// than against a copy of themselves.
///
/// The strongest case here registers the three rows with their bindings exactly the way the integration batch
/// will and lets `RuntimeKernel.RegisterModule` validate every port, resource kind, handle kind, result schema,
/// shape and recipient block: a row that drifted from the framework's vocabulary fails there rather than at a plan
/// load in the game.
/// </summary>
[Trait("Category", "WeaponOverride")]
public sealed class WeaponOverrideContractTests
{
    /// <summary>The rows this family declares, in catalog order. Every one of them carries a body, so the
    /// declared set and the wired set are the same set.</summary>
    public static TheoryData<string> Wired => new()
    {
        WeaponOverrideContract.FireRateCapability,
        WeaponOverrideContract.SpreadCapability,
        WeaponOverrideContract.RecoilCapability
    };

    [Fact]
    public void the_declared_set_is_the_wired_set()
    {
        Assert.Equal(3, WeaponOverrideContract.All.Count);
        Assert.Equal(3, WeaponOverrideContract.Documents.Length);
        Assert.Equal(WeaponOverrideContract.All.OrderBy(id => id, StringComparer.Ordinal),
            WeaponOverrideContract.WiredBindings.Keys.OrderBy(id => id, StringComparer.Ordinal));
    }

    [Fact]
    public void every_document_parses_and_names_its_own_capability()
    {
        Assert.Equal(WeaponOverrideContract.All.Count, WeaponOverrideContract.Rows().Count);
        foreach (var document in WeaponOverrideContract.Documents)
        {
            var row = RuntimeJson.Parse(document);
            var id = row.GetProperty("id").GetString();
            Assert.False(string.IsNullOrEmpty(id));
            Assert.Contains(id!, WeaponOverrideContract.All);
            Assert.Equal("action", row.GetProperty("kind").GetString());
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("label").GetString()));
            Assert.False(string.IsNullOrWhiteSpace(row.GetProperty("version").GetString()));
        }
    }

    [Fact]
    public void every_wired_row_travels_with_its_own_binding_and_handler()
    {
        foreach (var capability in WeaponOverrideContract.All)
        {
            var (binding, handler) = WeaponOverrideContract.WiredBindings[capability];
            Assert.StartsWith(ModuleDefinition.ProviderId + ".binding.", binding, StringComparison.Ordinal);
            Assert.StartsWith("gtfo.weapon.", handler, StringComparison.Ordinal);
        }
        Assert.Equal(WeaponOverrideContract.All.Count,
            WeaponOverrideContract.WiredBindings.Values.Select(entry => entry.Binding).Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(WeaponOverrideContract.All.Count,
            WeaponOverrideContract.WiredBindings.Values.Select(entry => entry.Handler).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void every_result_port_carries_the_four_fixed_columns_first()
    {
        foreach (var row in WeaponOverrideContract.Rows())
        {
            var result = row.GetProperty("graph").GetProperty("outputs").EnumerateArray()
                .Single(port => port.GetProperty("id").GetString() == "result");
            var names = result.GetProperty("fields").EnumerateArray()
                .Select(field => field.GetProperty("id").GetString()).ToArray();
            Assert.Equal(new[] { "target", "status", "committed", "code" }, names.Take(4));
            var suffix = row.GetProperty("id").GetString()!.Substring("forge.action.weapon.".Length);
            Assert.Equal("forge.result.weapon." + suffix, result.GetProperty("schema").GetString());
        }
    }

    /// <summary>
    /// The rows, their bindings, their shapes and their handlers, registered as one module: the registration is
    /// the same call the integration batch makes, and the kernel is what validates the vocabulary. A shape that
    /// names a port its row does not declare fails here, which is the one place that agreement is checked.
    /// </summary>
    [Fact]
    public void the_rows_register_with_their_bindings_and_shapes()
    {
        var owner = RuntimeJson.Parse(WeaponOverrideContract.Documents[0]).GetProperty("owner").GetString()!;
        var registry = RuntimeJson.From(new
        {
            providers = new[] { new { id = owner, kind = "native", version = "0.2.0", dependencies = Array.Empty<string>() } },
            capabilities = WeaponOverrideContract.Rows().Select(row => (object)row).ToArray(),
            bindings = WeaponOverrideContract.Bindings().ToArray()
        }).GetRawText();

        var handlers = WeaponOverrideContract.Handlers(_ => CommandResult.Rejected("fixture"),
            _ => CommandResult.Rejected("fixture"), _ => CommandResult.Rejected("fixture"));

        var module = new RuntimeModule(RuntimeKernel.ApiVersion, registry, handlers, WeaponOverrideContract.Support())
        {
            Shapes = WeaponOverrideContract.Shapes()
        };

        var kernel = new RuntimeKernel(new RuntimeIdentity(owner, "0.2.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        kernel.BeginWorld(3);
        using var registration = kernel.RegisterModule(module, RuntimeLogLevel.Off);

        Assert.True(registration.IsRegistered);
    }

    [Fact]
    public void every_declared_shape_names_ports_its_row_declares()
    {
        var shapes = WeaponOverrideContract.Shapes();
        Assert.Equal(WeaponOverrideContract.All.Count, shapes.Count);
        foreach (var capability in WeaponOverrideContract.All)
        {
            var handler = WeaponOverrideContract.WiredBindings[capability].Handler;
            Assert.True(shapes.TryGetValue(handler, out var shape), handler);
            var graph = RuntimeJson.Parse(WeaponOverrideContract.Documents
                .Single(document => RuntimeJson.Parse(document).GetProperty("id").GetString() == capability))
                .GetProperty("graph");
            var declared = graph.GetProperty("inputs").EnumerateArray()
                .Select(port => port.GetProperty("id").GetString()!).ToArray();
            Assert.All(shape!.InputPorts, port => Assert.Contains(port, declared));
            var parameters = graph.GetProperty("parameters").EnumerateArray()
                .Select(parameter => parameter.GetProperty("id").GetString()!).ToArray();
            Assert.All(shape.ParameterIds, parameter => Assert.Contains(parameter, parameters));
        }
    }
}
