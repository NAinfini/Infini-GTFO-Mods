using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The generator rows this provider implements, as one more `gtfo.map_object` category (ruling 148.4): a power
/// generator is an object the level places, so it is addressed by <see cref="MapObjectGeneratorAddress"/> and its
/// facts are published by <see cref="MapObjectModule"/> through the same registration, the same authority gate
/// and the same `map-object` attachment matcher as a door and a terminal. There is no `gtfo.level_object`
/// namespace any more and no second publisher for the two rows.
///
/// The rows themselves are unchanged: `forge.trigger.map.generator_cell` reports one cell going in or out of one
/// generator, `forge.trigger.map.generator_cluster` reports a group whose every member is powered, and
/// `forge.condition.predicate.power` reads one generator's powered flag and its group's counts. The value row's
/// resource kind is `generator`, and the id it resolves is the generator's own address text, so the resource
/// table and the facts name one object with one text.
///
/// The group's own native type is `LevelGeneration.LG_PowerGeneratorCluster`; the row's `cluster` port carries
/// the group's address, which is the same category's `group&lt;serial&gt;` key form.
/// </summary>
public static class GeneratorContract
{
    /// <summary>The Map provider these rows belong to. The spelling is the one `ModuleDefinition.ProviderId`
    /// carries, restated here so this file compiles against the framework alone, which is what lets the focused
    /// test project exercise it without the game-independent assembly's other halves.</summary>
    public const string ProviderId = "forge.module.gtfo.map";

    // ---- capability ids ------------------------------------------------------------------------------

    /// <summary>The two generator facts. `e-gen-cell` is the per-generator cell event and `e-gen-cluster` is
    /// the group event; the single-generator row the catalog already carries
    /// (`forge.trigger.interaction.generator_state`) reports a running state and neither a cell nor a group,
    /// so it is not the landing point for either skeleton row and is left where it is.</summary>
    public const string GeneratorCellCapability = "forge.trigger.map.generator_cell";
    public const string GeneratorClusterCapability = "forge.trigger.map.generator_cluster";

    /// <summary>The value row of the same subject, on the catalog id the node list's `v-gen` already has
    /// (`forge.condition.predicate.power`). A value row is a `query`: it reads native state on the host, changes
    /// nothing, and produces no command. The extra ports beside the predicate are the group counts the list row
    /// asks for; the complete shape travels in the JSON below.</summary>
    public const string GeneratorStateCapability = "forge.condition.predicate.power";

    /// <summary>The resource kind both the value row's input and the provider's resource table carry: one
    /// generator, spelled by its own address text.</summary>
    public const string GeneratorResourceKind = "generator";
    /// <summary>The schema name the value row's resource input declares, so a reference of another kind fails
    /// the port rather than being read as the nearest one.</summary>
    public const string GeneratorResourceSchema = "forge.resource.generator";

    // ---- handler and binding names -------------------------------------------------------------------

    /// <summary>The handler names the native half supplies, one per row. A fact arrives as one whole frame, so
    /// an `observe` binding resolves no shape; the names are the rows' own discriminators and are what the
    /// native publisher spells when it reports.</summary>
    public const string GeneratorCellHandler = "gtfo.map.generator_cell";
    public const string GeneratorClusterHandler = "gtfo.map.generator_cluster";
    /// <summary>The evaluator name the value row carries. A `query` row is dispatched to an evaluator, which is
    /// handed the budgeted query session and answers with the row's output ports.</summary>
    public const string GeneratorStateHandler = "gtfo.map.generator_state";

    /// <summary>The permission every row here reads through: a plan that wants to know what a generator is doing
    /// reads the map object state and nothing else. The control side stays with the action rows, which declare
    /// their own `power.cell`.</summary>
    public const string ReadPermission = "gtfo.map_object.read";

    /// <summary>The binding id of one row: the capability's own suffix under the Map provider, so either side of
    /// a row names the counterpart of the other without a second table.</summary>
    public static string Binding(string capabilityId)
    {
        int kind = capabilityId.IndexOf('.', "forge.".Length);
        if (kind < 0) throw new ArgumentException("capability id has no kind segment", nameof(capabilityId));
        return ProviderId + ".binding." + capabilityId[(kind + 1)..];
    }

    public static readonly string GeneratorCellBinding = Binding(GeneratorCellCapability);
    public static readonly string GeneratorClusterBinding = Binding(GeneratorClusterCapability);
    /// <summary>The value row's own binding: a `condition` row is answered by an evaluator, so its binding
    /// carries role `evaluate` and names the reader the registration must hold.</summary>
    public static readonly string GeneratorStateBinding = Binding(GeneratorStateCapability);

    /// <summary>Every binding this family declares, in the order the registry rows list them.</summary>
    public static readonly string[] Bindings =
    {
        GeneratorCellBinding, GeneratorClusterBinding, GeneratorStateBinding
    };

    /// <summary>Every fact this family publishes and the capability it is the observation of, in declaration
    /// order. One fact has exactly one capability, and a native hook names the fact rather than an index into
    /// this list.</summary>
    public static readonly IReadOnlyList<(string Fact, string Capability)> Rows = Array.AsReadOnly(new[]
    {
        ("generator.cell", GeneratorCellCapability),
        ("generator.group_connected", GeneratorClusterCapability)
    });

    // ---- bodies --------------------------------------------------------------------------------------

    public const string GeneratorCellCapabilityJson = """
    {
      "id": "forge.trigger.map.generator_cell",
      "owner": "forge.module.gtfo.map",
      "kind": "trigger",
      "label": "发电机插入 / 拔出电池",
      "version": "1.0.0",
      "parameters": {
        "description": "发电机的电池被插上或拔下。",
        "summary": "发电机的电池被插上或拔下。",
        "summaryEn": "Fires when a power cell is inserted into or removed from a generator.",
        "labelEn": "Generator cell changed",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.map_object"], "id": "generator", "type": "entity" },
          { "id": "cell", "type": "string", "optional": true },
          { "id": "inserted", "type": "boolean" },
          { "entityKinds": ["gtfo.player"], "id": "actor", "type": "entity", "optional": true },
          { "id": "connected", "type": "integer" },
          { "id": "total", "type": "integer" }
        ],
        "parameters": []
      }
    }
    """;

    public const string GeneratorClusterCapabilityJson = """
    {
      "id": "forge.trigger.map.generator_cluster",
      "owner": "forge.module.gtfo.map",
      "kind": "trigger",
      "label": "发电机组全部接通",
      "version": "1.0.0",
      "parameters": {
        "description": "这一组发电机全部接通了。",
        "summary": "这一组发电机全部接通了。",
        "summaryEn": "Fires when every generator of a group is powered.",
        "labelEn": "Generator cluster connected",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "entityKinds": ["gtfo.map_object"], "id": "cluster", "type": "entity" },
          { "id": "connected", "type": "integer" },
          { "id": "total", "type": "integer" }
        ],
        "parameters": []
      }
    }
    """;

    public const string GeneratorStateCapabilityJson = """
    {
      "id": "forge.condition.predicate.power",
      "owner": "forge.module.gtfo.map",
      "kind": "condition",
      "label": "发电机是否插着电池 / 发电机组接通数",
      "version": "1.0.0",
      "parameters": {
        "description": "判断供电通不通。",
        "summary": "读一台发电机插没插电池，以及它所在的那组接通了几台。",
        "summaryEn": "Reads whether one generator holds a power cell and how many generators of its group are powered.",
        "labelEn": "Generator state",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [ "map", "room", "logic" ],
        "execution": "query",
        "inputs": [
          { "id": "generator", "type": "resource", "resourceKind": "generator", "schema": "forge.resource.generator" }
        ],
        "outputs": [
          { "id": "value", "type": "boolean" },
          { "id": "connected", "type": "integer" },
          { "id": "total", "type": "integer" }
        ],
        "parameters": []
      }
    }
    """;

    /// <summary>The three capability rows in declaration order, as the array the shared declaration's
    /// capability section carries.</summary>
    public static readonly string CapabilitiesJson = "[\n" + GeneratorCellCapabilityJson + ",\n"
        + GeneratorClusterCapabilityJson + ",\n" + GeneratorStateCapabilityJson + "\n]";

    /// <summary>The three binding rows, as the array the shared declaration's binding section concatenates: the
    /// two observations of native state and the evaluated value row, in the order the capabilities are
    /// declared.</summary>
    public static object[] BindingRows() => new object[]
    {
        Row(GeneratorCellCapability, GeneratorCellHandler, "observe"),
        Row(GeneratorClusterCapability, GeneratorClusterHandler, "observe"),
        Row(GeneratorStateCapability, GeneratorStateHandler, "evaluate")
    };

    /// <summary>The three registration rows: all three read the same map-object surface, so the family carries
    /// one permission.</summary>
    public static IReadOnlyList<BindingSupport> Support() => Array.AsReadOnly(new[]
    {
        new BindingSupport(GeneratorCellBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(GeneratorClusterBinding, "implementation-only", new[] { ReadPermission }),
        new BindingSupport(GeneratorStateBinding, "implementation-only", new[] { ReadPermission })
    });

    /// <summary>The value row's port shape: a value row has no execution ports at all — its input is the
    /// resource it reads and its outputs are its readings.</summary>
    public static readonly HandlerShape GeneratorStateShape = new HandlerShape()
        .Inputs("generator").Outputs("value", "connected", "total");

    /// <summary>The one shape, keyed by handler name: what the session adds to the registration's shape table
    /// beside the native handlers' own.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [GeneratorStateHandler] = GeneratorStateShape
    };

    /// <summary>The evaluator of the value row, built from the reader the session hands over. This assembly
    /// declares the row and holds no game type, so the reader is a delegate and the game-bound registration is
    /// the only caller that supplies it.</summary>
    public static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators(Func<EvaluationContext, JsonElement> read)
    {
        ArgumentNullException.ThrowIfNull(read);
        return new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
        {
            [GeneratorStateHandler] = context => read(context)
        };
    }

    private static object Row(string capability, string handler, string role) => new
    {
        id = Binding(capability),
        capabilityId = capability,
        providerId = ProviderId,
        handler,
        role,
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };
}
