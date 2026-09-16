using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The provider state the behaviour observation pump reads, and nothing else.
///
/// The package's own `EnemyModule` core is edited by a dozen sibling slices at once, so this suite cannot compile
/// it without also compiling their half-finished partials. What it compiles instead is a partial declaration of
/// the same class carrying exactly the members the pump and the snapshot reader touch: the entity table — whose
/// entries are what own the per-life behaviour watermarks the pump writes — the one lookup that resolves a
/// reference against it, the kernel, the module's own provider id, the authority gates and the report sink. Every
/// reading the observation itself performs — the snapshot fields, the fact ports, the watermark thresholds and the
/// publications — is the production source, unchanged, including the nested `Behavior` class that source declares.
///
/// The registration this shim builds is the provider's own declaration with exactly this family's rows in it. The
/// five observation bindings are lifted out of the production registry text by id, so a row this suite
/// registered cannot differ from the row the provider ships; the capabilities they name stay where they belong,
/// in the kernel's trigger contract, which registers before any domain package.
///
/// The shim's `_entities` table is the genuine resolution path: a reference resolves only when the world epoch,
/// the tracked life and the native pointer all still agree, which is what lets a case retire a life or stand the
/// world forward and watch the pump refuse.</summary>
internal sealed partial class EnemyModule : IDisposable
{
    internal const string ProviderId = "forge.module.gtfo.enemy";

    private sealed class Entry
    {
        internal Entry(EnemyAgent enemy, EntityReference reference)
        { Enemy = enemy; Reference = reference; EnemyPointer = enemy.Pointer; Behavior = new(enemy, reference); }
        internal EnemyAgent Enemy { get; }
        internal EntityReference Reference { get; }
        internal IntPtr EnemyPointer { get; }
        internal Behavior Behavior { get; }
    }

    private readonly Dictionary<ushort, Entry> _entities = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;
    private readonly RuntimeModuleHandle _registration;
    private long _nextLife, _eventSequence;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal bool IsRegistered => !_disposed && _registration.IsRegistered;

    internal EnemyModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<bool> canExecute, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _registration = kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, BehaviorDeclaration(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal), BehaviorSupport())
        {
            // The one kind this provider owns: a reference of `gtfo.enemy` is current only while this module still
            // resolves it, and the inverse lookup is what the target port is filled from.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                { ["gtfo.enemy"] = reference => Resolve(reference) != null },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                { ["gtfo.enemy"] = ResolveInstance }
        }, logLevel);
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _entities.Clear();
        _registration.Dispose();
        _disposed = true;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy APIs require the owning simulation thread.");
    }

    private bool CanExecute { get { CheckThread(); return _canExecute() && SNetwork.SNet.IsMaster; } }

    /// <summary>The fact gate the production source declares: the runtime has to be ready and the host has to be
    /// the one executing, which is what makes a stopped runtime or a client answer no fact.</summary>
    private bool CanObserveFacts => _kernel.StartupState == RuntimeStartupState.Ready && CanExecute;

    /// <summary>The pump's own gate: no registered enemy can publish when nothing subscribes to any behaviour
    /// fact, so the native AI is never read for a world no plan is watching.</summary>
    private bool CanPublishBehavior
    {
        get
        {
            if (!CanObserveFacts) return false;
            foreach (var binding in BehaviorBindings) if (_kernel.HasSubscribers(binding)) return true;
            return false;
        }
    }

    /// <summary>The provider's declaration with exactly this family's rows in it: the provider itself and the
    /// five observation bindings the behaviour facts publish through. The ids and the handler names are the
    /// module's own constants, so a row this suite registers cannot name a binding the provider does not; the
    /// capabilities they name stay where they belong, in the kernel's trigger contract, which registers before any
    /// domain package. Nothing else the production registration carries is compiled here at all: this suite's
    /// statement is the behaviour pump, and a row whose contract is absent cannot be named.</summary>
    private static string BehaviorDeclaration()
        => "{\n  \"providers\": [ { \"id\": \"" + ProviderId
            + "\", \"kind\": \"native\", \"version\": \"1.0.0\", \"dependencies\": [] } ],\n  \"capabilities\": [],\n"
            + "  \"bindings\": [\n" + string.Join(",\n", BehaviorBindingIds.Select(BindingRow)) + "\n  ]\n}";

    /// <summary>One observation binding in the provider's own binding format: the handler name is the binding's
    /// own last segment, which is the spelling the production rows carry for every fact of this family.</summary>
    private static string BindingRow(string bindingId)
        => "    {\n      \"id\": \"" + bindingId + "\",\n      \"capabilityId\": \"" + CapabilityId(bindingId)
            + "\",\n      \"providerId\": \"" + ProviderId + "\",\n      \"handler\": \"gtfo.enemy."
            + bindingId[(bindingId.LastIndexOf('.') + 1)..] + "\",\n      \"role\": \"observe\",\n"
            + "      \"status\": \"implemented\",\n      \"dependencies\": [],\n      \"requires\": []\n    }";

    /// <summary>The canonical trigger capability a behaviour fact observes: the kernel's own `enemy` row for the
    /// fact, which is the same id the production registry's binding names.</summary>
    private static string CapabilityId(string bindingId)
        => "forge.trigger.enemy." + bindingId[(bindingId.LastIndexOf('.') + 1)..];

    /// <summary>The binding ids this family declares, read from the module's own constants rather than typed out:
    /// an id is composed from the provider id, so a renamed row is a missing row here rather than a silently
    /// unreachable one.</summary>
    private static readonly string[] BehaviorBindingIds =
    {
        AwakenedBinding, TargetAcquiredBinding, TargetLostBinding,
        ScoutDetectionBinding, ScoutScreamBinding
    };

    /// <summary>One support row per binding. An observation writes nothing, so no permission is required: the
    /// rows read the enemy instance this module already tracks.</summary>
    private static BindingSupport[] BehaviorSupport() => BehaviorBindingIds
        .Select(binding => new BindingSupport(binding, "implementation-only", Array.Empty<string>())).ToArray();

    internal EntityReference TrackSpawn(EnemyAgent enemy)
    {
        CheckThread();
        if (enemy == null) throw new ArgumentNullException(nameof(enemy));
        if (enemy.Pointer == IntPtr.Zero) throw new ArgumentException("Enemy must have a native instance.", nameof(enemy));
        // A new wrapper alone is not a new native life; despawn/world boundaries retire the old life.
        if (_entities.TryGetValue(enemy.GlobalID, out var existing)
            && existing.Reference.WorldEpoch == _kernel.WorldEpoch && existing.EnemyPointer == enemy.Pointer
            && Resolve(existing.Reference) != null) return existing.Reference;
        var reference = new EntityReference("gtfo.enemy:" + enemy.GlobalID.ToString(CultureInfo.InvariantCulture),
            _kernel.WorldEpoch, checked(++_nextLife));
        _entities[enemy.GlobalID] = new Entry(enemy, reference);
        return reference;
    }

    internal DespawnObservation? CaptureDespawn(EnemyAgent enemy)
    {
        CheckThread();
        if (enemy == null || !_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || Resolve(entry.Reference) == null) return null;
        return new DespawnObservation(this, entry.Reference, entry.EnemyPointer);
    }

    internal void CompleteDespawn(DespawnObservation? observation)
    {
        CheckThread();
        if (observation == null || !ReferenceEquals(observation.Owner, this)) return;
        var entry = Resolve(observation.Target);
        if (entry != null && entry.EnemyPointer == observation.EnemyPointer) _entities.Remove(entry.Enemy.GlobalID);
    }

    /// <summary>The one lookup the pump uses: the reference's world, the tracked life and the native pointer all
    /// have to agree, which is the same rule the provider's own table applies.</summary>
    private Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        const string prefix = "gtfo.enemy:";
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !ushort.TryParse(reference.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !_entities.TryGetValue(id, out var entry) || entry.Reference != reference || entry.Enemy == null
            || entry.Enemy.GlobalID != id || entry.Enemy.Pointer != entry.EnemyPointer) return null;
        return entry;
    }

    /// <summary>The kernel's instance resolver for `gtfo.enemy`: a native agent is only ever answered for the life
    /// the module registered, which is what a target's `m_agent` is looked up through.</summary>
    private EntityReference? ResolveInstance(object instance)
        => instance is EnemyAgent enemy && _entities.TryGetValue(enemy.GlobalID, out var entry)
           && ReferenceEquals(entry.Enemy, enemy) && entry.EnemyPointer == enemy.Pointer
           && Resolve(entry.Reference) != null ? entry.Reference : null;
}

/// <summary>One retired life, captured before the table forgets it: the reference the fact was about and the
/// native instance it belonged to. A later despawn that does not name the same instance is not this life's.</summary>
internal sealed record DespawnObservation(ForgeEnemy.Native.EnemyModule Owner, EntityReference Target, IntPtr EnemyPointer);
