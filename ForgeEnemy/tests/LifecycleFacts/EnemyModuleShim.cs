using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The provider state the lifecycle fact family reads, and nothing else.
///
/// The package's own `EnemyModule` core is edited by a dozen sibling slices at once, so this suite cannot compile
/// it without also compiling their half-finished partials. What it compiles instead is a partial declaration of
/// the same class carrying exactly the members the fact family touches: the entity table — whose entries are what
/// own the per-life death window and the per-limb windows — the one lookup that resolves a reference against it,
/// the kernel, the module's own provider id, the authority gates, the report sink and the one registration the two
/// facts publish through. Everything the facts themselves do is the production source, unchanged
/// (`EnemyModule.LifecycleFacts.cs`), including the release of a retired life's `enemy`-scoped values.
///
/// The registration this shim builds is the provider's own declaration with exactly this family's rows in it: the
/// two observe rows, named by the module's own constants, and the same permission each row's support carries in
/// the package. The capabilities they name stay where they belong, in the runtime's trigger contract, which
/// registers before any domain package — a row whose fact no trigger row carries is refused by the kernel at
/// registration rather than reaching a dispatch nothing owns.
///
/// The shim's `_entities` table is the genuine resolution path: a reference resolves only when the world epoch,
/// the tracked life and the native pointer all still agree, which is what lets a case retire a life, stand the
/// world forward or reuse a pointer and watch the fact family refuse.</summary>
internal sealed partial class EnemyModule : IDisposable
{
    internal const string ProviderId = "forge.module.gtfo.enemy";

    private sealed class Entry
    {
        internal Entry(EnemyAgent enemy, EntityReference reference, IntPtr enemyPointer)
        { Enemy = enemy; Reference = reference; EnemyPointer = enemyPointer; }
        internal EnemyAgent Enemy { get; }
        internal EntityReference Reference { get; }
        internal IntPtr EnemyPointer { get; }
        internal LifecycleObservation? DeathObservation;
        internal readonly Dictionary<int, LifecycleObservation> LimbObservations = new();
    }

    private readonly Dictionary<ushort, Entry> _entities = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? _observe;
    private readonly Action<string> _report;
    private readonly RuntimeModuleHandle _registration;
    private long _nextLife, _eventSequence;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal bool IsRegistered => !_disposed && _registration.IsRegistered;

    internal EnemyModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<bool> canExecute, Action<string> report,
        Func<EnemyAgent, EntityReference, RuntimeEntitySnapshot?>? observe = null, Func<EnemyAgent, uint?>? enemyType = null)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _observe = observe;
        _registration = kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, LifecycleDeclaration(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal), LifecycleSupport())
        {
            // The one kind this provider owns: a reference of `gtfo.enemy` is current only while this module still
            // resolves it, and the inverse lookup is what a subject's own entity reference is filled from.
            EntityResolvers = new Dictionary<string, Func<EntityReference, bool>>
                { ["gtfo.enemy"] = reference => Resolve(reference) != null },
            EntityInstanceResolvers = new Dictionary<string, Func<object, EntityReference?>>
                { ["gtfo.enemy"] = ResolveInstance },
            EntityObservers = observe == null ? null : new Dictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>
                { ["gtfo.enemy"] = ObserveEntity }
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

    /// <summary>A world boundary retires every tracked life; the behaviour watermark the production core clears
    /// with it belongs to a family this suite does not compile.</summary>
    internal void ClearWorld() { CheckThread(); _entities.Clear(); }

    /// <summary>The provider's declaration with exactly this family's rows in it: the provider itself and the two
    /// observation bindings the lifecycle facts publish through. The ids and the handler names are the module's
    /// own constants, so a row this suite registers cannot name a binding the provider does not; the capabilities
    /// they name stay where they belong, in the runtime's trigger contract. Nothing else the production
    /// registration carries is compiled here at all: this suite's statement is the lifecycle family.</summary>
    private static string LifecycleDeclaration()
        => "{\n  \"providers\": [ { \"id\": \"" + ProviderId
            + "\", \"kind\": \"native\", \"version\": \"1.0.0\", \"dependencies\": [] } ],\n  \"capabilities\": [],\n"
            + "  \"bindings\": [\n" + string.Join(",\n", LifecycleRows().Select(Row)) + "\n  ]\n}";

    /// <summary>Each fact and the runtime's own trigger capability it observes. Both capabilities are declared by
    /// the trigger contract that registers before any domain package, so this module owns neither.</summary>
    private static IEnumerable<(string BindingId, string CapabilityId)> LifecycleRows()
    {
        yield return (DeathStartedBinding, "forge.trigger.enemy.death_started");
        yield return (LimbBrokenBinding, "forge.trigger.combat.limb_broken");
    }

    /// <summary>One observation row in the provider's own binding format: the handler name is the binding's own
    /// last segment, which is the spelling the production rows carry for every fact of this family.</summary>
    private static string Row((string BindingId, string CapabilityId) row)
        => "    {\n      \"id\": \"" + row.BindingId + "\",\n      \"capabilityId\": \"" + row.CapabilityId
            + "\",\n      \"providerId\": \"" + ProviderId + "\",\n      \"handler\": \"gtfo.enemy."
            + row.BindingId[(row.BindingId.LastIndexOf('.') + 1)..] + "\",\n      \"role\": \"observe\",\n"
            + "      \"status\": \"implemented\",\n      \"dependencies\": [],\n      \"requires\": []\n    }";

    /// <summary>One support row per fact, carrying the same permission the package's own row carries: a lifecycle
    /// fact reads the life, a limb fact reads the parts, and both are implementation-only until the game has
    /// verified them.</summary>
    private static BindingSupport[] LifecycleSupport() => new[]
    {
        new BindingSupport(DeathStartedBinding, "implementation-only", new[] { "gtfo.enemy.lifecycle.read" }),
        new BindingSupport(LimbBrokenBinding, "implementation-only", new[] { "gtfo.enemy.limbs.read" })
    };

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
        _entities[enemy.GlobalID] = new Entry(enemy, reference, enemy.Pointer);
        return reference;
    }

    // Synchronous native entry. Deferred work must retain CaptureDespawn's token, not an EnemyAgent pointer.
    internal void TrackDespawn(EnemyAgent enemy) => CompleteDespawn(CaptureDespawn(enemy));

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
        if (entry != null && entry.EnemyPointer == observation.EnemyPointer)
        {
            _entities.Remove(entry.Enemy.GlobalID);
            // A retired life owns no variables either: a despawn is the other way an enemy life ends, and a plan
            // that wrote a per-enemy value must not have it survive into whatever takes the instance's place.
            ReleaseEnemyScope(observation.Target);
        }
    }

    /// <summary>The one lookup the facts use: the reference's world, the tracked life and the native pointer all
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
    /// the module registered.</summary>
    private EntityReference? ResolveInstance(object instance)
        => instance is EnemyAgent enemy && _entities.TryGetValue(enemy.GlobalID, out var entry)
           && ReferenceEquals(entry.Enemy, enemy) && entry.EnemyPointer == enemy.Pointer
           && Resolve(entry.Reference) != null ? entry.Reference : null;

    /// <summary>The kernel's entity observer for `gtfo.enemy`: the reader the session handed over runs against the
    /// life the module still resolves, never against a reference whose life has been retired.</summary>
    private RuntimeEntitySnapshot? ObserveEntity(EntityReference reference)
    {
        var entry = Resolve(reference);
        return entry == null || _observe == null ? null : _observe(entry.Enemy, entry.Reference);
    }
}

/// <summary>One retired life, captured before the table forgets it: the reference the fact was about and the
/// native instance it belonged to. A later despawn that does not name the same instance is not this life's.</summary>
internal sealed record DespawnObservation(ForgeEnemy.Native.EnemyModule Owner, EntityReference Target, IntPtr EnemyPointer);
