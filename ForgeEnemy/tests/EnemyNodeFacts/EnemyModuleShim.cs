using System;
using System.Collections.Generic;
using System.Text.Json;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The provider state the node-list family reads, and nothing else.
///
/// The package's own `EnemyModule` core is edited by a dozen sibling slices at once, so this suite cannot compile
/// it without also compiling their half-finished partials. What it compiles instead is a partial declaration of
/// the same class carrying exactly the members the node family touches: the entity table and the one lookup that
/// resolves a reference against it, the kernel, the module's own provider id, the authority gate, the
/// registration the family publishes through, and the two kind-level responders the value rows read through —
/// the entity observer that answers a snapshot and the zone responder the `where` row's kernel read goes to.
/// Everything the family itself does — the rows, the request checks, the native writes, the readbacks, the
/// publications and the result rows — is the production source, unchanged.
///
/// The registration this shim builds is also the production one, minus `EnemyModule.Registry()`'s own rows: the
/// node family's capability rows, binding rows, support rows, handler shapes and evaluator table all come from
/// the contracts the provider ships, so a row no handler answers, a handler with no shape or a shape with no
/// declaration fails in this scene rather than at runtime.
///
/// The shim's `_entities` table is the genuine resolution path: a reference resolves only when the world epoch,
/// the tracked life and the native pointer all still agree, which is what lets a case stand the world forward or
/// retire a life and watch a row refuse. `CanExecute` is the production gate's shape — the session's own flag and
/// `SNet.IsMaster` — so the authority cases are real, and `CanPresent` is the same shape without the master flag,
/// which is the gate the provider's one `presentation` row passes on a recipient.</summary>
internal sealed partial class EnemyModule
{
    internal const string ProviderId = "forge.module.gtfo.enemy";

    internal sealed class Entry
    {
        internal Entry(EnemyAgent enemy, EntityReference reference)
        { Enemy = enemy; Reference = reference; EnemyPointer = enemy.Pointer; }
        internal EnemyAgent Enemy { get; }
        internal EntityReference Reference { get; }
        internal IntPtr EnemyPointer { get; }
        /// <summary>The last tag state the module published for the life, owned by `EnemyNodeFacts`.</summary>
        internal bool Tagged;
    }

    private readonly Dictionary<ushort, Entry> _entities = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly RuntimeModuleHandle _registration;
    private readonly Action<string> _report;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private long _eventSequence;

    internal EnemyModule(RuntimeKernel kernel, Func<bool> canExecute, Action<string>? report = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? (_ => { });
        // The module instance is published before the registration, because the registration's own entity
        // observer and zone responder answer through it.
        Instances[kernel] = this;
        try
        {
            _registration = kernel.RegisterModule(NodeModule(), RuntimeLogLevel.Off);
        }
        catch { Instances.Remove(kernel); throw; }
    }

    internal bool IsRegistered => _registration.IsRegistered;

    /// <summary>A handle cast the way the kernel's own effect handle is: through the registration this module
    /// registered under, because a fixture has no plan and the step's handle comes from the kernel.</summary>
    internal JsonElement MintHandle() => _registration.CreateEffectHandle("encounter");

    /// <summary>The node family's own rows, exactly as `EnemyModule.Registry()` composes them, with the handler,
    /// shape and evaluator tables that answer them. The two responders are the shim's own: they answer through
    /// the module instance this scene built, which is the one that owns the entity table.</summary>
    private RuntimeModule NodeModule()
    {
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        foreach (var shape in NodeActionShapes()) shapes.Add(shape.Key, shape.Value);
        foreach (var shape in NodeValueShapes()) shapes.Add(shape.Key, shape.Value);
        // The volume row is the one action family this suite drives by hand rather than through the node
        // family's own table, so its body, shape and support row are appended here exactly as
        // `EnemyActionFamilies` appends them to the production registration.
        var handlers = NodeActionHandlers();
        handlers[VolumeHandler] = EffectVolume;
        shapes[VolumeHandler] = VolumePorts;
        var support = NodeSupport().ToList();
        support.AddRange(EnemyVolumeContract.Support());
        return new RuntimeModule(RuntimeKernel.ApiVersion, RegistryJson, handlers, support.ToArray())
        {
            Shapes = shapes,
            Evaluators = NodeEvaluators(),
            // The kind-level table that has to exist before anything else names the kind: a reference of
            // `gtfo.enemy` is current only while this module still resolves it.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                { ["gtfo.enemy"] = reference => Resolve(reference) != null },
            EntityObservers = new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["gtfo.enemy"] = Observe },
            EntityZones = new Dictionary<string, Func<EntityReference, EntityReference?>>
                { ["gtfo.enemy"] = Zone },
            // The two tables a registered kind must answer for on top of that: the inverse of a reference, and
            // the candidate set a selector reads. Both are the provider's own, exactly as its registration
            // declares them.
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                { ["gtfo.enemy"] = ResolveInstance },
            // The one resource kind the node family's observation mints a payload from: `ability` is the game's
            // own ability enum, and the provider is the production table, so a fact that names an ability names
            // the same id `forge.action.enemy.ability` resolves.
            ResourceProviders = new Dictionary<string, RuntimeResourceProvider>(StringComparer.Ordinal)
                { [EnemyAbilityResources.Kind] = EnemyAbilityResources.Provider },
            // The one row whose clock is the step's own `effect` block: the same restore callback the production
            // registration carries, so a case can drive an ending the way the kernel does.
            EffectRestores = new Dictionary<string, EffectRestoreHandler>(StringComparer.Ordinal)
                { [VolumeHandler] = RestoreVolume },
            EntityCandidates = new Dictionary<string, Func<IReadOnlyList<EntityReference>>>
                { ["gtfo.enemy"] = Candidates }
        };
    }

    private RuntimeEntitySnapshot? Observe(EntityReference reference) => ObserveEntity(reference);

    private EntityReference? Zone(EntityReference reference) => ZoneOfEnemy(reference);

    /// <summary>The reference the module minted for one native agent, or null for an instance it does not
    /// track.</summary>
    private EntityReference? ResolveInstance(object instance)
        => instance is EnemyAgent enemy && _entities.TryGetValue(enemy.GlobalID, out var entry)
           && ReferenceEquals(entry.Enemy, enemy) && Resolve(entry.Reference) != null ? entry.Reference : null;

    /// <summary>One native agent through the kernel's registered instance resolvers — the production reading the
    /// ability-used fact resolves its target with. Only the kind this scene registers answers here; a second kind
    /// the production registration would also carry is skipped exactly as it is there, because a kind with no
    /// resolver registered cannot claim an instance.</summary>
    private EntityReference? ResolveAgent(Agents.Agent? agent)
    {
        if (agent == null) return null;
        foreach (var kind in AgentKinds)
        {
            EntityReference? reference;
            try { reference = _kernel.ResolveEntityInstance(kind, agent); }
            catch (RuntimeContractException error) when (error.Code == "entity-resolver") { continue; }
            if (reference != null) return reference;
        }
        return null;
    }

    /// <summary>The kinds this provider resolves an agent instance as, in the production order. A player target
    /// is attempted before it is given up on, so a fact about one is refused for the reason the provider refuses
    /// it — an unregistered kind — rather than by skipping the lookup.</summary>
    private static readonly string[] AgentKinds = { "gtfo.enemy", "gtfo.player" };

    /// <summary>Every live life this module tracks, as the selector's own candidate source.</summary>
    private IReadOnlyList<EntityReference> Candidates()
    {
        var references = new List<EntityReference>();
        foreach (var entry in _entities.Values) if (Resolve(entry.Reference) != null) references.Add(entry.Reference);
        return references;
    }

    /// <summary>The module's registry text, assembled from the production contract strings this provider
    /// declares: the capability rows whose owner it is and the binding rows that answer them, in the order
    /// `EnemyModule.Registry()` composes them. The rows of the family that are not this package's — the tag, glue
    /// and generic entity-spawn facts the trigger contract owns — are declared by the trigger provider the scene
    /// registers before this module, which is the order the host registers them in.
    ///
    /// The other action families the production registration also carries are not compiled here at all: this
    /// suite's statement is the node-list family and the one effect-volume row it drives itself, and a row whose
    /// contract is absent cannot be named.</summary>
    private static string RegistryJson => RegistryHead
        + NodeCapabilityRowsJson
        + ",\n" + EnemyVolumeContract.CapabilityRow
        + "\n  ],\n  \"bindings\": [\n" + NodeBindingRowsJson
        + ",\n" + EnemyVolumeContract.BindingRowJson + "\n  ]\n}";

    /// <summary>The assembled registry text, for a one-off inspection of the rows this shim registers.</summary>
    internal static string DumpRegistryJson() => RegistryJson;

    private const string RegistryHead = """
    {
      "providers": [ { "id": "forge.module.gtfo.enemy", "kind": "native", "version": "1.0.0", "dependencies": [] } ],
      "capabilities": [
    """;

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy APIs require the owning simulation thread.");
    }

    private bool CanExecute { get { CheckThread(); return _canExecute() && SNetwork.SNet.IsMaster; } }

    /// <summary>The production presentation gate's shape: the same gameplay flag without the master flag, because
    /// the machine that presents a `presentation` step is a recipient rather than the host.</summary>
    internal bool CanPresent { get { CheckThread(); return _canExecute(); } }

    /// <summary>The one lookup the family uses: the reference's world, the tracked life and the native pointer
    /// all have to agree, which is the same rule the provider's own table applies.</summary>
    internal Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        const string prefix = "gtfo.enemy:";
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !ushort.TryParse(reference.Id.AsSpan(prefix.Length), out var id)
            || !_entities.TryGetValue(id, out var entry) || entry.Reference != reference) return null;
        return entry;
    }

    /// <summary>Tracks one enemy the way the provider's own spawn path does, so a case has a current reference to
    /// dispatch at. It is also the module's `TrackSpawn`: the observation calls it, and a case calls it directly
    /// when it wants a life without a spawn.</summary>
    internal EntityReference Track(EnemyAgent enemy) => TrackSpawn(enemy);

    internal EntityReference TrackSpawn(EnemyAgent enemy)
    {
        CheckThread();
        if (_entities.TryGetValue(enemy.GlobalID, out var existing) && existing.EnemyPointer == enemy.Pointer
            && Resolve(existing.Reference) != null) return existing.Reference;
        var reference = new EntityReference("gtfo.enemy:" + enemy.GlobalID, _kernel.WorldEpoch, _entities.Count + 1);
        _entities[enemy.GlobalID] = new Entry(enemy, reference);
        return reference;
    }

    /// <summary>Retires a tracked life, which is what a despawn does to the provider's own table.</summary>
    internal void Retire(EntityReference reference)
    {
        const string prefix = "gtfo.enemy:";
        if (reference.Id == null || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !ushort.TryParse(reference.Id.AsSpan(prefix.Length), out var id)) return;
        if (_entities.TryGetValue(id, out var entry) && entry.Reference == reference) _entities.Remove(id);
    }

    /// <summary>The kind-level snapshot the query rows read: the same fields the production observer publishes,
    /// read from the double's own enemy so a case can change one and watch a row answer it.</summary>
    private RuntimeEntitySnapshot? ObserveEntity(EntityReference reference)
    {
        var entry = Resolve(reference);
        if (entry == null) return null;
        var enemy = entry.Enemy;
        var damage = enemy.Damage;
        bool canHeal = enemy.Alive && damage != null && damage.IsSetup && damage.Health > 0
            && damage.Health <= damage.HealthMax;
        return new RuntimeEntitySnapshot(reference, "enemy", null, enemy.Alive ? "alive" : "dead",
            Array.Empty<string>(), Array.Empty<string>(),
            new[] { (double)enemy.Position.x, (double)enemy.Position.y, (double)enemy.Position.z },
            health: canHeal ? damage!.Health : null,
            healthMaximum: canHeal ? damage!.HealthMax : null,
            aiState: AiState(enemy));
    }

    /// <summary>The `ai_state` member the observer publishes for the double's behaviour state. Only the members
    /// the value rows ask about are mapped: sleep, its two neighbours, and the absent state.</summary>
    private static string AiState(EnemyAgent enemy)
    {
        var behaviour = enemy.AI?.m_behaviour;
        if (behaviour == null) return "disabled";
        return behaviour.m_currentStateName switch
        {
            EB_States.Hibernating or EB_States.SquidBoss_Hibernating => "hibernating",
            EB_States.Patrolling or EB_States.Patrolling_Investigate => "patrolling",
            EB_States.Dead => "dead",
            _ => "pursuing"
        };
    }

    /// <summary>The one module instance per kernel this suite drives: the provider owns one entity table per
    /// session, and the module's own registration answers through the instance that owns it.</summary>
    private static readonly Dictionary<RuntimeKernel, EnemyModule> Instances = new();

    /// <summary>Drops this instance's entry in the map, so a disposed scene leaves nothing behind for the next
    /// one's registration to answer through.</summary>
    internal void ForgetInstance() => Instances.Remove(_kernel);
}
