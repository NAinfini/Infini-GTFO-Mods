using System;
using System.Collections.Generic;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The registration half of the node-list family this batch implements: the three observation bindings
/// its trigger rows publish through, the shapes of the nine handlers it adds, and the methods the module's own
/// constructor calls to compose all of it into its registration.
///
/// It exists so the wiring is one place rather than nine. The rows themselves are declared next to the code that
/// answers them — the trigger rows in `EnemyNodeTriggerContract`, the action rows in `EnemyNodeEffectContract`,
/// the query rows in `EnemyNodeValueContract` — and this file only names them.
///
/// One of the rows here is a canonical row this provider binds rather than owns: `forge.trigger.entity.spawned`
/// belongs to the trigger contract's provider, which registers before any domain package.
///
/// The action families written by earlier slices — the enemy control actions, the combat actions, the foam action
/// and the behaviour actions — are composed beside this family rather than inside it: `EnemyActionFamilies` is the
/// one place their rows, handlers, shapes and support rows join the same registration, and `EnemyModule`'s
/// constructor merges both tables.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The three observation bindings this package declares: the tag row, the glue row, and the generic
    /// spawn row — whose capability belongs to the trigger contract's provider.</summary>
    internal static readonly string NodeSpawnBinding = EnemyNodeTriggerContract.SpawnedBinding;
    internal static readonly string NodeTaggedBinding = EnemyNodeTriggerContract.TaggedBinding;
    internal static readonly string NodeGluedBinding = EnemyNodeTriggerContract.GluedBinding;

    /// <summary>The value source this module's query rows are answered through, attached for the module's own
    /// lifetime and detached when it goes. A session that never attached one refuses every value row by name
    /// instead of answering from an empty table.</summary>
    private EnemyNodeValueReads? _valueReads;

    /// <summary>The native agent behind a `gtfo.player` reference, for the `target` row. It is the module's own
    /// state rather than a static lookup so the one resolution path can be driven by this package's focused
    /// suite, where the game's agent table is a double: a provider that owns a kind owns the way its references
    /// are turned back into instances, and the `target` row accepts exactly the references this answers for.</summary>
    private Func<EntityReference, dynamic?> _agentOf = DefaultAgentOf;

    /// <summary>The game's own id lookup, reached through reflection so the call resolves against the table the
    /// game carries at run time — the interop assembly's own `AgentManager.GetAgent`, whose own agent type its
    /// out parameter names — rather than against whatever a compile-time reference happens to name. The
    /// reference is judged first: a kind that is not the player's, and an id that is not the canonical decimal
    /// spelling the player kind uses, are both answered with no agent.</summary>
    private static dynamic? DefaultAgentOf(EntityReference reference)
    {
        const string prefix = "gtfo.player:";
        if (reference.Id == null || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)) return null;
        var text = reference.Id.Substring(prefix.Length);
        if (!ushort.TryParse(text, System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var globalId)
            || text != globalId.ToString(System.Globalization.CultureInfo.InvariantCulture)) return null;
        dynamic table = Type.GetType("Agents.AgentManager, Modules-ASM", throwOnError: true)!;
        return table.GetAgent((int)globalId, out dynamic? agent) ? agent : null;
    }

    /// <summary>The four node actions and the six value rows, each paired with the capability its shape was
    /// resolved against.</summary>
    internal Dictionary<string, CommandHandler> NodeActionHandlers() => new(StringComparer.Ordinal)
    {
        [KillHandler] = Kill,
        [RemoveHandler] = Remove,
        [MarkHandler] = Mark,
        [TargetHandler] = Target
    };

    /// <summary>The value rows' evaluators, one per query handler name.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> NodeEvaluators() => EnemyNodeValueReads.Evaluators();

    /// <summary>The handler shapes the four node actions answer, keyed by the same names as
    /// <see cref="NodeActionHandlers"/>.</summary>
    internal static Dictionary<string, HandlerShape> NodeActionShapes() => new(StringComparer.Ordinal)
    {
        [KillHandler] = KillPorts,
        [RemoveHandler] = RemovePorts,
        [MarkHandler] = MarkPorts,
        [TargetHandler] = TargetPorts
    };

    /// <summary>The value rows' shapes, keyed by the same names as the evaluator table.</summary>
    internal static IReadOnlyDictionary<string, HandlerShape> NodeValueShapes() => EnemyNodeValueContract.ValueShapes();

    /// <summary>Every handler this provider's registration carries beyond the ones `EnemyModule` already built:
    /// the three node actions and the six value rows of this family. The four action families are the same kind
    /// of table, declared in their own contracts and merged into the registration by `EnemyActionFamilies`.</summary>
    internal Dictionary<string, CommandHandler> AllNodeHandlers() => NodeActionHandlers();

    /// <summary>Every shape those handlers resolve against.</summary>
    internal static Dictionary<string, HandlerShape> AllNodeShapes()
    {
        var shapes = NodeActionShapes();
        foreach (var shape in NodeValueShapes()) shapes[shape.Key] = shape.Value;
        return shapes;
    }

    /// <summary>The support rows every one of those bindings appends to the module's own table: the observations
    /// read the enemy this module already tracks and write nothing, and each action writes the one thing its own
    /// permission names.</summary>
    internal static IEnumerable<BindingSupport> NodeSupport()
    {
        yield return new BindingSupport(NodeSpawnBinding, "implementation-only", Array.Empty<string>());
        foreach (var row in EnemyNodeValueContract.ValueSupport()) yield return row;
        foreach (var row in EnemyNodeTriggerContract.Support()) yield return row;
        yield return new BindingSupport(EnemyAbilityUsedContract.BindingId, "implementation-only",
            new[] { EnemyAbilityUsedContract.ReadPermission });
        yield return new BindingSupport(KillBinding, "implementation-only", new[] { EnemyNodeEffectContract.KillPermission });
        yield return new BindingSupport(RemoveBinding, "implementation-only", new[] { EnemyNodeEffectContract.RemovePermission });
        yield return new BindingSupport(MarkBinding, "implementation-only", new[] { EnemyNodeEffectContract.MarkPermission });
        yield return new BindingSupport(TargetBinding, "implementation-only", new[] { EnemyNodeEffectContract.TargetPermission });
    }

    /// <summary>The capability rows this module's registration appends, in registry order: the value rows, the
    /// four action rows, and the one trigger row this package declares itself. The rest of this family's trigger
    /// rows are `forge.trigger.*` rows declared by the runtime's trigger contract, so no registration here carries
    /// them.</summary>
    internal static string NodeCapabilityRowsJson
        => string.Join(",\n", System.Linq.Enumerable.Select(EnemyNodeValueContract.ValueRows(), RuntimeJson.From))
           + ",\n" + EnemyNodeEffectContract.CapabilityRowsJson
           + ",\n" + EnemyAbilityUsedContract.CapabilityRow;

    /// <summary>The binding rows this module's registration appends, in the same order as the capabilities
    /// above, with the generic spawn row's own binding: that row's capability is the trigger contract's, and
    /// this package is the provider that observes it.</summary>
    internal static string NodeBindingRowsJson
        => string.Join(",\n", System.Linq.Enumerable.Select(EnemyNodeValueContract.ValueBindings(), RuntimeJson.From))
           + ",\n" + EnemyNodeEffectContract.BindingRowsJson
           + ",\n" + EnemyNodeTriggerContract.SpawnedBindingRowJson
           + ",\n" + EnemyNodeTriggerContract.BindingRowsJson
           + ",\n" + EnemyAbilityUsedContract.BindingRowJson;

    /// <summary>Attaches the value source for this module's lifetime and registers the marker pump. Called once,
    /// where the module is built; a second call would install a second resolver for the same rows. The agent
    /// lookup may be handed over at the same time, which is how this package's focused suite drives the `target`
    /// row's accepted path with the double's own agent table.</summary>
    internal void AttachNodeFamily(RuntimeKernel kernel, Func<EntityReference, dynamic?>? agentOf = null)
    {
        _valueReads = EnemyNodeValueReads.Attach(kernel, reference => Resolve(reference)?.Enemy);
        if (agentOf != null) _agentOf = agentOf;
    }

    /// <summary>Detaches the value source and drops every marker this module placed, so a package that unloads
    /// mid-level leaves no marker behind that nothing can name.</summary>
    internal void DetachNodeFamily()
    {
        for (int index = _markers.Count - 1; index >= 0; index--) RemoveMarker(_markers[index].Marker);
        _markers.Clear();
        if (_valueReads != null) EnemyNodeValueReads.Detach(_valueReads);
        _valueReads = null;
    }
}
