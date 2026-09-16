using System;
using System.Collections.Generic;
using System.Text.Json;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The module-side half of the node-list observations this package adds: the generic spawn fact of an
/// enemy, the game's own tag transaction, the moment foam lands on an enemy, and the tag's falling edge. Each
/// one publishes exactly one row and writes nothing; the fields and the publication rule live here, next to the
/// one registration that declares the binding, and the Harmony classes that call into them are in
/// `EnemyNodeHooks.cs`.
///
/// The tag is the one fact with two edges and one native entry: `ToolSyncManager.DoTagEnemy` runs on the host
/// when a scanner client asked for a tag, so the rising edge is the transaction's own; nothing in the build
/// reports the tag ending, so the falling edge is a per-frame comparison of `EnemyAgent.IsTagged` against the
/// last state this module published. The comparison is one property read per registered enemy per frame and
/// publishes only a transition.
///
/// The marker ledger is here for the same reason: a Forge mark is a local presentation object with a lifetime
/// the plan asked for, and the frame pump that already runs for the tag is what expires it.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The glue volume one native accumulation call is about to change, captured before the call so a
    /// postfix can report what it really added. The token is spent by the first postfix that closes it, and a
    /// postfix of another receiver never closes it.</summary>
    internal sealed class GlueObservation
    {
        private readonly EnemyModule _owner;
        private bool _consumed;
        internal EntityReference Target { get; }
        internal IntPtr ReceiverPointer { get; }
        internal float VolumeBefore { get; }
        internal GlueObservation(EnemyModule owner, EntityReference target, IntPtr receiver, float volumeBefore)
        { _owner = owner; Target = target; ReceiverPointer = receiver; VolumeBefore = volumeBefore; }
        internal bool TryConsume(EnemyModule owner)
        {
            if (!ReferenceEquals(_owner, owner) || _consumed) return false;
            _consumed = true;
            return true;
        }
    }

    /// <summary>One Forge-built navigation marker, and the enemy life it tracks. The marker is a native object
    /// the kernel holds through the `marker` handle, so this record keeps only what cancelling and expiring it
    /// need: the row's identity, the instance the mark was placed on, and the tick the marker ends at.</summary>
    private sealed record MarkerRecord(EntityReference Target, IntPtr EnemyPointer, NavMarker Marker,
        string HandleKey, long ExpiresAtTick);

    /// <summary>Every marker this provider has placed and not yet removed.</summary>
    private readonly List<MarkerRecord> _markers = new();

    /// <summary>The generic entity-spawn fact, published for an enemy from the enemy's own spawn hook. The
    /// reference is the one this module already tracks — the spawn registration runs first and this postfix is
    /// last — and the position is the instance's own, read once and read back. An instance this module cannot
    /// resolve publishes nothing rather than minting an identity of its own.</summary>
    internal void AfterSpawn(EnemyAgent enemy)
    {
        CheckThread();
        if (enemy == null) return;
        // The identity is registered whatever the publication gate says: a plan that never subscribes to the
        // spawn fact still dispatches at the reference this path minted.
        EntityReference reference;
        try { reference = TrackSpawn(enemy); }
        catch (Exception) { return; }
        if (!CanPublishNodeFacts(EnemyNodeTriggerContract.SpawnedBinding) || Resolve(reference) == null) return;
        var position = enemy.Position;
        // A position that does not read is an absent port, never a zero vector: the row's other port is the
        // identity, and an identity with a made-up position would be a different fact.
        var ports = new Dictionary<string, object> { ["entity"] = reference };
        if (float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z))
            ports["position"] = new[] { (double)position.x, (double)position.y, (double)position.z };
        PublishNodeFact(EnemyNodeTriggerContract.SpawnedBinding, reference, ports);
    }

    /// <summary>The tag transaction's rising edge, read from the packet the host's own action carried. The
    /// packet holds one enemy handle and nothing else, so the only reading it can produce is "this enemy is
    /// tagged"; a packet whose agent was torn down between the action and this postfix resolves to nothing and
    /// publishes nothing.</summary>
    internal void AfterTagged(ToolSyncManager.pTagEnemy data)
    {
        CheckThread();
        if (!CanPublishNodeFacts(NodeTaggedBinding)) return;
        // The packet holds a `pEnemyAgent` handle, not an agent: it is asked for the instance it names, and a
        // handle whose agent was torn down between the action and this postfix answers false and publishes
        // nothing.
        EnemyAgent? enemy;
        try { if (!data.enemy.TryGet(out enemy) || enemy == null) return; }
        catch (Exception) { return; }
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || Resolve(entry.Reference) == null) return;
        entry.Tagged = true;
        PublishNodeFact(NodeTaggedBinding, entry.Reference,
            new Dictionary<string, object> { ["enemy"] = entry.Reference, ["tagged"] = true });
    }

    /// <summary>The tag's falling edge, sampled on the one per-frame hook the behaviour facts already run on.
    /// The state is compared against what this module last published, so one tag produces one rising and one
    /// falling fact — a re-tag after a lapse is a new rising edge, which is what the game's own timer means.</summary>
    internal void ObserveTagState(EnemyAgent? enemy)
    {
        CheckThread();
        if (enemy == null || !CanPublishNodeFacts(NodeTaggedBinding)) return;
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || Resolve(entry.Reference) == null) return;
        bool tagged;
        try { tagged = enemy.IsTagged; }
        catch (Exception) { return; }
        if (tagged == entry.Tagged) return;
        entry.Tagged = tagged;
        PublishNodeFact(NodeTaggedBinding, entry.Reference,
            new Dictionary<string, object> { ["enemy"] = entry.Reference, ["tagged"] = tagged });
    }

    /// <summary>The marker's own per-frame pump: every marker whose lifetime has run out is removed from the
    /// layer, and every marker whose enemy life is gone is dropped without a native call, because a marker whose
    /// tracking object was destroyed has nothing left to remove. This runs on the same hook as the tag sample
    /// and does nothing when no marker is live.</summary>
    internal void ExpireMarks()
    {
        CheckThread();
        if (_markers.Count == 0) return;
        for (int index = _markers.Count - 1; index >= 0; index--)
        {
            var record = _markers[index];
            bool expired = _kernel.CurrentTick >= record.ExpiresAtTick;
            bool gone = Resolve(record.Target) == null;
            if (!expired && !gone) continue;
            _markers.RemoveAt(index);
            RemoveMarker(record.Marker);
        }
    }

    /// <summary>Reads the glue volume a call is about to change, so the postfix can report what it added. The
    /// reading is the enemy's own receiver, resolved through this module's table: a receiver this module cannot
    /// name is not a fact about an enemy it tracks. The publication gate is asked by the postfix, not here — the
    /// prefix has to capture whatever the call is about to change, because the change itself is what the reading
    /// is compared against.</summary>
    internal GlueObservation? BeforeGlue(Dam_EnemyDamageBase? damage)
    {
        CheckThread();
        if (damage == null || damage.Owner == null) return null;
        if (!_entities.TryGetValue(damage.Owner.GlobalID, out var entry)
            || entry.EnemyPointer != damage.Owner.Pointer || Resolve(entry.Reference) == null
            || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer) return null;
        float volume;
        try { volume = damage.AttachedGlueVolume; }
        catch (Exception) { return null; }
        return float.IsFinite(volume) && volume >= 0f
            ? new GlueObservation(this, entry.Reference, damage.Pointer, volume) : null;
    }

    /// <summary>The glue the call really added: the difference between the volume the postfix reads and the one
    /// the prefix captured. A call that changed nothing publishes nothing — the game's own accumulation entry can
    /// be entered with a volume that a receiver's own rules reduce to zero, and that is not glue landing on an
    /// enemy. `total` is the volume the receiver now carries, so the two ports describe one reading.</summary>
    internal void AfterGlue(Dam_EnemyDamageBase? damage, GlueObservation? before)
    {
        CheckThread();
        // The receiver is judged before the token is spent: a postfix that belongs to another call must not
        // close this observation, because the call it was opened for has not returned yet.
        if (before == null || damage == null || damage.Pointer != before.ReceiverPointer) return;
        if (!before.TryConsume(this) || !CanPublishNodeFacts(NodeGluedBinding)) return;
        var entry = Resolve(before.Target);
        if (entry == null || entry.Enemy.Damage == null || entry.Enemy.Damage.Pointer != damage.Pointer) return;
        float total;
        try { total = damage.AttachedGlueVolume; }
        catch (Exception) { return; }
        if (!float.IsFinite(total) || total < 0f) return;
        double added = total - before.VolumeBefore;
        if (added <= 0) return;
        PublishNodeFact(NodeGluedBinding, before.Target, new Dictionary<string, object>
        {
            ["enemy"] = before.Target, ["volume"] = added, ["total"] = (double)total
        });
    }

    /// <summary>Removes every marker one dispatch placed, which is the `marker` handle's own cancel. The native
    /// removal is called once per live marker, and the ledger entries go whether or not the call answered.</summary>
    internal void RemoveMarked(string? handleKey)
    {
        if (handleKey == null) return;
        for (int index = _markers.Count - 1; index >= 0; index--)
        {
            if (_markers[index].HandleKey != handleKey) continue;
            var record = _markers[index];
            _markers.RemoveAt(index);
            RemoveMarker(record.Marker);
        }
    }

    /// <summary>The one native removal. A layer that is gone, or a marker the game already collected, is not an
    /// error worth escaping into a native callback; the ledger entry is dropped either way.</summary>
    private static void RemoveMarker(NavMarker? marker)
    {
        if (marker == null) return;
        try { GuiManager.NavMarkerLayer?.RemoveMarker(marker); }
        catch (Exception) { }
    }

    /// <summary>Whether a plan subscribes to one node fact, on this module's own authority gate. The gate is the
    /// one every other host-owned fact in this package passes, so a peer that may not publish the damage facts
    /// may not publish these either.</summary>
    private bool CanPublishNodeFacts(string binding)
        => _kernel.StartupState == RuntimeStartupState.Ready && CanExecute && _kernel.HasSubscribers(binding);

    private void PublishNodeFact(string binding, EntityReference subject, Dictionary<string, object> ports)
    {
        if (Resolve(subject) == null) return;
        var result = _registration.Publish(new RuntimeEvent(
            "gtfo.enemy.node:" + _kernel.WorldEpoch + ":" + checked(++_eventSequence), binding,
            _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick), "gtfo.world:" + _kernel.WorldEpoch,
            RuntimeJson.From(ports)));
        // Runtime owns causal propagation and dispatch. Rejection must never reopen this native observation.
        if (result.Status == "rejected") _report("enemy node fact rejected: " + result.Code);
    }
}
