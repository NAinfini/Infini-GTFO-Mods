using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Receivers;

/// <summary>Local native identity only. Serialized references never contain a pointer.</summary>
internal sealed class EnemyIdentityTable<T> : EnemyThreadBoundary where T : class
{
    private sealed record Entry(T Native, ushort Id, IntPtr Pointer, EntityReference Reference);
    private readonly Dictionary<ushort, Entry> _entries = new();
    private readonly Dictionary<IntPtr, ushort> _pointers = new();
    private readonly Func<T, ushort> _id;
    private readonly Func<T, IntPtr> _pointer;
    private readonly Func<T, bool> _exists;
    private readonly int _capacity;
    private long _epoch, _life;
    private bool _hasWorld, _active;

    internal EnemyIdentityTable(Func<T, ushort> id, Func<T, IntPtr> pointer,
        Func<T, bool> exists, int capacity = 65536)
    {
        _id = id ?? throw new ArgumentNullException(nameof(id));
        _pointer = pointer ?? throw new ArgumentNullException(nameof(pointer));
        _exists = exists ?? throw new ArgumentNullException(nameof(exists));
        if (capacity < 1 || capacity > 65536) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }

    internal int Count { get { CheckThread(); return _entries.Count; } }
    internal void BeginWorld(long epoch)
    {
        CheckThread();
        if (epoch < 0 || (_hasWorld && epoch <= _epoch))
            throw new ArgumentOutOfRangeException(nameof(epoch), "A new world must advance its epoch.");
        _entries.Clear(); _pointers.Clear();
        _epoch = epoch; _hasWorld = _active = true;
    }

    // Immediately rejects old references even before the host advances the public epoch.
    internal void Invalidate()
    {
        CheckThread(); _active = false; _entries.Clear(); _pointers.Clear();
    }

    internal EntityReference ObserveSpawn(T native)
    {
        CheckThread(); ArgumentNullException.ThrowIfNull(native);
        if (!_active) throw new InvalidOperationException("gtfo.enemy.world_inactive");
        if (!_exists(native)) throw new ArgumentException("Native enemy no longer exists.", nameof(native));
        ushort id = _id(native); IntPtr pointer = _pointer(native);
        if (pointer == IntPtr.Zero) throw new ArgumentException("Native pointer is null.", nameof(native));
        if (_entries.TryGetValue(id, out var current) && current.Pointer == pointer)
        {
            if (!IsNativeCurrent(current))
                throw new InvalidOperationException("gtfo.enemy.lifecycle_evidence_required");
            _entries[id] = current with { Native = native };
            return current.Reference;
        }
        bool replacesPointer = _pointers.TryGetValue(pointer, out var oldId);
        if (current == null && !replacesPointer && _entries.Count >= _capacity)
            throw new InvalidOperationException("gtfo.enemy.identity_budget");
        long life = checked(_life + 1); // Overflow must not evict an existing life.
        var reference = new EntityReference("gtfo.enemy:" + id.ToString(CultureInfo.InvariantCulture), _epoch, life);
        if (current != null) Remove(current);
        if (replacesPointer && _entries.TryGetValue(oldId, out var previous)) Remove(previous);
        _entries.Add(id, new Entry(native, id, pointer, reference));
        _pointers.Add(pointer, id); _life = life;
        return reference;
    }

    /// <summary>Capture synchronously at a trusted callback's entry; never reconstruct a delayed token.</summary>
    internal EntityReference? CaptureCurrent(T native)
    {
        CheckThread();
        if (!_active || native == null || !_exists(native)) return null;
        return _entries.TryGetValue(_id(native), out var entry) && entry.Pointer == _pointer(native)
            && IsNativeCurrent(entry) ? entry.Reference : null;
    }

    internal T? Resolve(EntityReference? reference)
    {
        CheckThread();
        if (!_active || reference == null || reference.WorldEpoch != _epoch
            || !TryId(reference.Id, out var id) || !_entries.TryGetValue(id, out var entry)
            || entry.Reference != reference || !IsNativeCurrent(entry)) return null;
        return entry.Native;
    }

    // No pointer-only despawn overload: a delayed raw pointer cannot prove which life ended.
    internal bool EndLife(EntityReference? expected)
    {
        CheckThread();
        if (!_active || expected == null || expected.WorldEpoch != _epoch
            || !TryId(expected.Id, out var id) || !_entries.TryGetValue(id, out var entry)
            || entry.Reference != expected) return false;
        Remove(entry); return true;
    }

    private bool IsNativeCurrent(Entry entry) => _exists(entry.Native)
        && _id(entry.Native) == entry.Id && _pointer(entry.Native) == entry.Pointer;
    private void Remove(Entry entry)
    {
        _entries.Remove(entry.Id); _pointers.Remove(entry.Pointer);
    }
    private static bool TryId(string? text, out ushort id)
    {
        id = 0;
        const string prefix = "gtfo.enemy:";
        return text != null && text.StartsWith(prefix, StringComparison.Ordinal)
            && ushort.TryParse(text.AsSpan(prefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out id)
            && text == prefix + id.ToString(CultureInfo.InvariantCulture);
    }
}
