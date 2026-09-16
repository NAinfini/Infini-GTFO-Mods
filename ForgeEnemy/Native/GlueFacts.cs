using System;
using System.Collections.Generic;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The native facts one enemy life resolves to, as the foam ledger needs them: the agent itself, the
/// instance it was when the foam was applied, and its glue receiver. The provider hands these in from its own
/// entity table, so this family never keeps a second reference registry of its own.</summary>
/// <param name="Enemy">The native agent this life currently resolves to.</param>
/// <param name="EnemyPointer">The native instance pointer of that agent.</param>
/// <param name="Damage">The agent's own glue receiver, or null when the agent holds none or it is not set up.</param>
internal readonly record struct GlueTarget(EnemyAgent Enemy, IntPtr EnemyPointer, Dam_EnemyDamageBase? Damage);

/// <summary>One foam application this provider made, as the ledger remembers it.</summary>
/// <param name="Target">The enemy life the foam was applied to.</param>
/// <param name="EnemyPointer">The native instance that life resolved to when the foam was applied.</param>
/// <param name="Volume">The volume this application added, which is exactly the volume a removal takes back.</param>
/// <param name="HandleKey">The `foam` handle this application is cleared through, as the handle's own text.</param>
/// <param name="ExpiresAtTick">The tick the foam is taken back at, or null when it lasts until the encounter ends.</param>
internal sealed record GlueRecord(EntityReference Target, IntPtr EnemyPointer, double Volume, string HandleKey,
    long? ExpiresAtTick);

/// <summary>
/// The foam family's own state: the foam this provider applied, and the one place a Forge-side duration is
/// honoured.
///
/// The game's glue has no timer of its own — it is added by volume and taken back by volume — so an application
/// with a duration is remembered here and taken back when the duration runs out. The ledger is keyed by the enemy
/// life, so a retired life, a replaced native instance and a new world all leave nothing behind: a record whose
/// life no longer resolves is dropped instead of being removed from whatever now stands in its place.
///
/// The tick comes from the kernel's own lifecycle observation, which is the framework's existing "the simulation
/// advanced" notification. Expiry publishes no fact: the rows this action answers are the command's own result, and
/// an internal cleanup with no plan-facing meaning does not get a binding invented for it.
///
/// Resolution is handed in rather than owned: what an `gtfo.enemy` reference currently names is the enemy
/// provider's own answer, and this ledger only ever asks it again — never a stale pointer it kept.
/// </summary>
internal sealed class GlueLedger : IDisposable
{
    /// <summary>The most foam applications this provider keeps at once. A plan that asks for one more is refused by
    /// name rather than silently dropping the oldest.</summary>
    internal const int MaximumRecords = 4096;

    private readonly RuntimeKernel _kernel;
    private readonly Func<EntityReference, GlueTarget?> _resolve;
    private readonly RuntimeLifecycleSubscription _lifecycle;
    private readonly List<GlueRecord> _records = new();
    private readonly List<GlueRecord> _expired = new();
    private bool _disposed;

    internal GlueLedger(RuntimeKernel kernel, RuntimeModuleHandle owner, Func<EntityReference, GlueTarget?> resolve)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        ArgumentNullException.ThrowIfNull(owner);
        _lifecycle = owner.ObserveLifecycle(OnLifecycle);
    }

    internal int Count => _records.Count;

    /// <summary>Whether this enemy life already carries foam this provider applied.</summary>
    internal bool IsFoamed(EntityReference target) => Find(target) != null;

    /// <summary>Whether this provider may add another application right now.</summary>
    internal bool HasRoom => _records.Count < MaximumRecords;

    internal GlueRecord? Find(EntityReference target)
    {
        for (int index = 0; index < _records.Count; index++)
            if (_records[index].Target == target) return _records[index];
        return null;
    }

    internal void Add(GlueRecord record) => _records.Add(record);

    /// <summary>Another volume landing on a life already carrying this provider's foam: the entry's volume grows
    /// so the removal takes back everything this provider put on, and the later expiry wins.</summary>
    internal void Grow(GlueRecord record, double volume, long? expiresAtTick)
    {
        int index = _records.IndexOf(record);
        if (index < 0) return;
        _records[index] = record with { Volume = record.Volume + volume, ExpiresAtTick = expiresAtTick };
    }

    /// <summary>Every application made through one `foam` handle, dropped from the ledger and handed back so the
    /// caller can finish clearing what they hold. The entry leaves first: a receiver that throws mid-removal must
    /// not leave a record behind that a second cancel would try to clear again.</summary>
    internal List<GlueRecord> TakeByHandle(string handleKey)
    {
        var taken = new List<GlueRecord>();
        for (int index = _records.Count - 1; index >= 0; index--)
        {
            if (_records[index].HandleKey != handleKey) continue;
            taken.Add(_records[index]);
            _records.RemoveAt(index);
        }
        return taken;
    }

    /// <summary>
    /// Ends every application whose duration has run out, through the receiver's own removal entry point and by the
    /// same volume it added. Nothing is reported from here: the removal is this provider's own cleanup, and a
    /// receiver that no longer answers is a life that is already gone.
    /// </summary>
    internal void Expire()
    {
        if (_disposed || _records.Count == 0) return;
        long tick = _kernel.CurrentTick;
        _expired.Clear();
        for (int index = _records.Count - 1; index >= 0; index--)
        {
            var record = _records[index];
            if (record.ExpiresAtTick is not { } expires || expires > tick) continue;
            _expired.Add(record);
            _records.RemoveAt(index);
        }
        foreach (var record in _expired) Remove(record);
        _expired.Clear();
    }

    /// <summary>Takes one record's volume back off the receiver this provider can still resolve for it. A life that
    /// no longer resolves, one whose native instance was replaced, and one whose receiver has since gone all leave
    /// the foam where it is: there is nothing left to take it off.</summary>
    internal void Remove(GlueRecord record)
    {
        var target = _resolve(record.Target);
        if (target is not { Damage: { } damage } resolved || resolved.EnemyPointer != record.EnemyPointer) return;
        try
        {
            float? attached = GlueActions.ReadAttachedGlueVolume(damage);
            if (attached.HasValue) GlueActions.Remove(damage, record.Volume, attached.Value);
        }
        catch (Exception) { }
    }

    /// <summary>Hands every receiver this provider can still resolve back to no foam, without reading the world
    /// for evidence: this runs on the way out, and a receiver that no longer answers is not an error to report.</summary>
    public void Clear()
    {
        foreach (var record in _records.ToArray())
        {
            var target = _resolve(record.Target);
            if (target is not { Damage: { } damage } resolved || resolved.EnemyPointer != record.EnemyPointer) continue;
            try { damage.ClearGlue(); }
            catch (Exception) { }
        }
        _records.Clear();
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        if (_disposed) return;
        if (value.Kind == RuntimeLifecycleKind.WorldChanged) { _records.Clear(); return; }
        if (value.Kind == RuntimeLifecycleKind.TickAdvanced) Expire();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle.Dispose();
        _records.Clear();
    }
}
