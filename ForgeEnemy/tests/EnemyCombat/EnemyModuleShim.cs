using System;
using System.Collections.Generic;
using System.Globalization;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The provider state the three combat rows read, and nothing else.
///
/// The package's own `EnemyModule` core is edited by a dozen sibling slices at once, so this suite cannot compile
/// it without also compiling their half-finished partials. What it compiles instead is a partial declaration of
/// the same class carrying exactly the members the combat handlers touch: the entity table and the one lookup that
/// resolves a reference against it, the kernel, the module's own provider id, the authority gate and the report
/// sink. Every decision the family itself makes — the request checks, the native writes, the readbacks and the
/// result rows — is the production source, unchanged.
///
/// The suite's own declaration is registered here as a self-contained provider, so the kernel resolves the module
/// the re-filed rows belong to without this suite editing the production registry. It is the same registration the
/// production constructor performs, minus `Registry()`'s rows: the focused suite stands its own declaration up
/// beside the provider's so both can run while the provider's core is mid-edit.
///
/// The shim's `_entities` table is the genuine resolution path: a reference resolves only when the world epoch,
/// the tracked life and the native pointer all still agree, which is what lets a case stand the world forward or
/// retire a life and watch a row refuse. `CanExecute` is the production gate's shape — the session's own flag and
/// `SNet.IsMaster` — so the authority cases are real.</summary>
internal sealed partial class EnemyModule : IDisposable
{
    internal const string ProviderId = "forge.module.gtfo.enemy";

    internal sealed class Entry
    {
        internal Entry(EnemyAgent enemy, EntityReference reference, IntPtr enemyPointer)
        { Enemy = enemy; Reference = reference; EnemyPointer = enemyPointer; }
        internal EnemyAgent Enemy { get; }
        internal EntityReference Reference { get; }
        internal IntPtr EnemyPointer { get; }
    }

    private readonly Dictionary<ushort, Entry> _entities = new();
    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;
    private readonly RuntimeModuleHandle _registration;
    private long _nextLife;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed;

    internal bool IsRegistered => !_disposed && _registration.IsRegistered;

    internal EnemyModule(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<bool> canExecute, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _registration = kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, """
        {"providers":[{"id":"forge.module.gtfo.enemy","kind":"native","version":"1.0.0","dependencies":[]}],
        "capabilities":[],"bindings":[]}
        """, new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>()), logLevel);
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

    /// <summary>The one lookup the family uses: the reference's world, the tracked life and the native pointer
    /// all have to agree, which is the same rule the provider's own table applies.</summary>
    internal Entry? Resolve(EntityReference reference)
    {
        CheckThread();
        const string prefix = "gtfo.enemy:";
        if (reference.WorldEpoch != _kernel.WorldEpoch || !reference.Id.StartsWith(prefix, StringComparison.Ordinal)
            || !ushort.TryParse(reference.Id.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var id)
            || !_entities.TryGetValue(id, out var entry) || entry.Reference != reference || entry.Enemy == null
            || entry.Enemy.GlobalID != id || entry.Enemy.Pointer != entry.EnemyPointer) return null;
        return entry;
    }
}
