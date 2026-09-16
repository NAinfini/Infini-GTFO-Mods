using System;
using System.Collections.Generic;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The provider state the three control actions read, and nothing else.
///
/// The package's own `EnemyModule` core is edited by a dozen sibling slices at once, so this suite cannot compile
/// it without also compiling their half-finished partials. What it compiles instead is a partial declaration of
/// the same class carrying exactly the members the control actions touch: the entity table and the one lookup that
/// resolves a reference against it, the kernel, the module's own provider id, and the authority gate. Everything
/// else the actions do — the request checks, the native writes, the readbacks, the result rows and the aggregate —
/// is the production source, unchanged.
///
/// The shim's `_entities` table is the genuine resolution path: a reference resolves only when the world epoch,
/// the tracked life and the native pointer all still agree, which is what lets a case stand the world forward or
/// retire a life and watch the action refuse. `CanExecute` is the production gate's shape — the registration, the
/// startup state, the session's own flag and `SNet.IsMaster` — so the authority and phase cases are real.</summary>
internal sealed partial class EnemyModule
{
    internal const string ProviderId = "forge.module.gtfo.enemy";

    internal sealed class Entry
    {
        internal Entry(EnemyAgent enemy, EntityReference reference) { Enemy = enemy; Reference = reference; }
        internal EnemyAgent Enemy { get; }
        internal EntityReference Reference { get; }
    }

    private readonly Dictionary<ushort, Entry> _entities = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    internal EnemyModule(RuntimeKernel kernel, Func<bool> canExecute)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy APIs require the owning simulation thread.");
    }

    private bool CanExecute { get { CheckThread(); return _canExecute() && SNetwork.SNet.IsMaster; } }

    /// <summary>The one lookup the actions use: the reference's world, the tracked life and the native pointer all
    /// have to agree, which is the same rule the provider's own table applies.</summary>
    private Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        const string prefix = "gtfo.enemy:";
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !ushort.TryParse(reference.Id.AsSpan(prefix.Length), out var id)
            || !_entities.TryGetValue(id, out var entry) || entry.Reference != reference) return null;
        return entry;
    }

    /// <summary>Tracks one enemy the way the provider's own spawn path does, so a case has a current reference to
    /// dispatch at.</summary>
    internal EntityReference Track(EnemyAgent enemy)
    {
        var reference = new EntityReference("gtfo.enemy:" + enemy.GlobalID, _kernel.WorldEpoch, _entities.Count + 1);
        _entities[enemy.GlobalID] = new Entry(enemy, reference);
        return reference;
    }

    /// <summary>Retires a tracked life, which is what a despawn does to the provider's table.</summary>
    internal void Retire(EnemyAgent enemy) => _entities.Remove(enemy.GlobalID);
}
