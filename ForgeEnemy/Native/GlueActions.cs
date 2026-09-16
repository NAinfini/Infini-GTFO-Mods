using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>
/// The foam half of the native execution layer: one method per glue operation, each taking the enemy instance the
/// caller resolved and answering with the one decision it made, plus the command handler that fans one plan step
/// out over the recipients and the ledger that honours a Forge-side duration.
///
/// Every write goes through the game's own glue entry point. `ProjectileManager.WantToSpawnGlueOnEnemyAgent` is the
/// call the C-foam launcher's own path makes for a hit enemy: it takes a sync id, the agent, the glue target's sub
/// index, a local position, the volume descriptor and the effect multiplier, and the game replicates it as its
/// `m_spawnGlueOnEnemy` broadcast. The descriptor the game itself builds carries exactly the two numbers an author
/// can meaningfully choose — `volume` and `expandVolume` — plus the projectile's own current scale, which belongs
/// to the projectile in flight and not to an author, so it is left at zero exactly as this package's other neutral
/// native arguments are.
///
/// The row only ever writes foam onto an enemy. A door and a bare surface both have their own spawn entry points in
/// the same manager, and neither is reachable from this provider: see `evidence/enemy-glue-hooks.json`.
///
/// What a reference currently names is the enemy provider's own answer, handed in as <c>resolve</c>; this family
/// never keeps a pointer it would have to trust later. `internal`, not private, so the focused suite can dispatch
/// the handler through a kernel-built command context; the registration still only ever reaches it through
/// `EnemyModule`'s handler table.
/// </summary>
internal sealed class GlueActions : IDisposable
{
    /// <summary>The enemy's own glue receiver did not read at all, or is not set up.</summary>
    internal const string NoReceiver = "missing-glue-receiver";
    /// <summary>The receiver's owner is not the instance the caller resolved.</summary>
    internal const string ReceiverMismatch = "glue-receiver-owner-mismatch";
    /// <summary>The enemy itself reads dead, so nothing would hold the foam.</summary>
    internal const string NotAlive = "not-alive";

    /// <summary>The smallest volume the game's own unit can carry and still be a request.</summary>
    internal const float MinimumVolume = 0.000001f;
    internal const float MaximumVolume = 1000000f;
    internal const float MinimumStrength = 0.000001f;
    internal const float MaximumStrength = 1000000f;

    private readonly RuntimeModuleHandle _registration;
    private readonly Func<EntityReference, GlueTarget?> _resolve;
    private readonly Func<bool> _canExecute;
    private readonly GlueLedger _ledger;
    private bool _disposed;

    internal GlueActions(RuntimeModuleHandle registration, Func<EntityReference, GlueTarget?> resolve,
        Func<bool> canExecute, RuntimeKernel kernel)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _ledger = new GlueLedger(kernel ?? throw new ArgumentNullException(nameof(kernel)), registration, resolve);
    }

    /// <summary>Whether this enemy life already carries foam this provider applied.</summary>
    internal bool IsFoamed(EntityReference target) => _ledger.IsFoamed(target);

    /// <summary>Whether a volume the plan handed over is one this entry point can carry. A non-finite, zero or
    /// negative volume is not a small amount of foam; the game's own descriptor has no such value.</summary>
    internal static bool IsUsableVolume(double value)
        => double.IsFinite(value) && value >= MinimumVolume && value <= MaximumVolume;

    /// <summary>Whether an effect multiplier the plan handed over is one the entry point can carry. The game's own
    /// launcher passes its own multiplier here, which is a positive scale on the foam's effect, never zero or
    /// negative.</summary>
    internal static bool IsUsableStrength(double value)
        => double.IsFinite(value) && value >= MinimumStrength && value <= MaximumStrength;

    /// <summary>The enemy's own reading, taken once per target: the volume of foam currently attached to it. A
    /// receiver that does not read answers null, which is a refusal rather than a zero-volume reading.</summary>
    internal static float? ReadAttachedGlueVolume(Dam_EnemyDamageBase damage)
    {
        try
        {
            float volume = damage.AttachedGlueVolume;
            return float.IsFinite(volume) && volume >= 0f ? volume : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>The volume descriptor the game's own launcher builds for one hit: the amount of foam the shot
    /// carries and the potential it may expand by. `currentScale` stays zero because it is the projectile's own
    /// growing scale, not an author input.</summary>
    internal static GlueVolumeDesc Descriptor(double volume, double expandVolume)
        => new() { volume = (float)volume, expandVolume = (float)expandVolume };

    /// <summary>
    /// Applies one foam volume to one enemy through the game's own spawn entry point.
    ///
    /// The sub index names which of the enemy's glue targets the foam lands on, and that entry point takes the
    /// target's own index rather than a position in the array the enemy holds. This row declares no port for it, so
    /// the call carries the entry point's own "no particular target" value instead of this layer picking a limb on
    /// the author's behalf. The local position is the target's own local origin: the action has no geometry port
    /// and the game's own launcher derives the impact point from the projectile it flew, which a direct call has
    /// none of.
    ///
    /// The readback is the whole evidence, and the caller performs it: this layer only enters the entry point.
    /// </summary>
    internal static void Apply(Dam_EnemyDamageBase damage, double volume, double expandVolume, double strength)
    {
        uint syncID = ProjectileManager.GetNextSyncID();
        ProjectileManager.WantToSpawnGlueOnEnemyAgent(syncID, damage.Owner, -1, UnityEngine.Vector3.zero,
            Descriptor(volume, expandVolume), (float)strength);
    }

    /// <summary>
    /// Takes one volume of foam back off an enemy through the receiver's own removal entry point, and answers
    /// whether the volume it publishes actually fell.
    ///
    /// This is how a Forge-side duration ends: the native glue has no timer of its own, so the foam the plan asked
    /// for is removed by the same amount it added. Only a fall is a removal; a request the receiver did not act on
    /// answers false so the caller reports it instead of treating the foam as gone.
    /// </summary>
    internal static bool Remove(Dam_EnemyDamageBase damage, double volume, float before)
    {
        damage.RemoveGlueVolume((float)volume, 0f);
        float? after = ReadAttachedGlueVolume(damage);
        return after.HasValue && after.Value < before;
    }

    /// <summary>One row of the multi-target foam result. Field names are the wire contract's, not this assembly's:
    /// `forge.result.combat.foaming` declares target, status, committed, code, amount and target_count, so each is
    /// spelled here exactly as the contract spells it.</summary>
    private sealed record GlueRow(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("amount")] double RequestedVolume,
        [property: JsonPropertyName("target_count")] int TargetCount,
        float? GlueBefore, float? GlueAfter);

    /// <summary>The handle key the `foam` output is registered and cancelled under.</summary>
    internal const string FoamHandleKey = "foam";

    /// <summary>
    /// Multi-target foam. One row is written per recipient in the plan's own order, and a spawn whose attached
    /// volume cannot be read back is reported as an unknown commit rather than as success: the game's spawn entry
    /// point returns void, so a spawn it dropped and one it applied are indistinguishable from this side, and
    /// neither may be claimed.
    ///
    /// The host is the only authority that may submit: the entry point is a static "want to" call whose replication
    /// is the game's own broadcast, so a client that reached it would ask the game to send a spawn the master never
    /// authorized. Two facts are asked, exactly as this package's other write rows ask them: the context's own
    /// authority fact, and the provider's session gate — the plugin is loaded, the runtime is in a phase that may
    /// write, and the game says this peer is the master.
    ///
    /// Every application in one dispatch shares one `foam` handle, minted on the first application that lands and
    /// registered with the enemy that row names; cancelling it takes that volume back off every enemy the handle
    /// covers. A dispatch that applied nothing mints no handle.
    /// </summary>
    internal CommandResult Foaming(CommandContext context)
    {
        if (_disposed || !context.IsHost || !_canExecute()) return CommandResult.Rejected("authority-or-phase");
        // The source is a required, kernel-validated entity reference. This row has no use for it beyond that: the
        // game's own spawn entry point carries the effect multiplier, not a source agent, so no attacker is
        // invented for it.
        _ = context.GetEntityInput("source");
        double volume = context.Inputs.GetProperty("volume").GetDouble();
        if (!IsUsableVolume(volume)) return CommandResult.Rejected("volume-out-of-range");
        double strength = context.Inputs.GetProperty("strength").GetDouble();
        if (!IsUsableStrength(strength)) return CommandResult.Rejected("strength-out-of-range");
        long duration = context.Inputs.GetProperty("duration").GetInt64();
        // A duration of zero means "until the encounter ends", which is the game's own glue behaviour; a negative
        // duration is not a shorter one.
        if (duration < 0) return CommandResult.Rejected("duration-out-of-range");
        long? expiresAtTick = null;
        if (duration > 0)
        {
            long now = Math.Max(0, context.SimulationTick);
            if (duration > long.MaxValue - now) return CommandResult.Rejected("duration-out-of-range");
            expiresAtTick = now + duration;
        }
        var targets = context.Inputs.GetProperty("targets").EnumerateArray().Select(RuntimeJson.Entity).ToArray();
        // The rows are the whole result, and the protocol's own budget caps the answer.
        if (targets.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");

        var rows = new List<GlueRow>(targets.Length);
        int committed = 0, rejected = 0, unknown = 0;
        JsonElement foam = default;
        string? foamKey = null;
        foreach (var target in targets)
        {
            // The row's own status is the ABI's `committed` column: only a volume that actually moved confirmed it,
            // a refusal before the entry point confirmed nothing, and a spawn this side could not observe
            // afterwards stays unknown.
            GlueRow Row(string status, string code, float? before = null, float? after = null)
                => new(target, status, status switch
                {
                    "committed" => CommitStates.Confirmed,
                    "rejected" => CommitStates.None,
                    _ => CommitStates.Unknown
                }, code, volume, targets.Length, before, after);

            var resolved = _resolve(target);
            if (resolved == null) { rows.Add(Row("rejected", "stale-or-unsupported-recipient")); rejected++; continue; }
            var glue = resolved.Value;
            if (!glue.Enemy.Alive) { rows.Add(Row("rejected", NotAlive)); rejected++; continue; }
            var damage = glue.Damage;
            if (damage == null || !damage.IsSetup || damage.Pointer == IntPtr.Zero)
            { rows.Add(Row("rejected", NoReceiver)); rejected++; continue; }
            if (damage.Owner == null || damage.Owner.Pointer != glue.EnemyPointer)
            { rows.Add(Row("rejected", ReceiverMismatch)); rejected++; continue; }
            float? before = ReadAttachedGlueVolume(damage);
            if (!before.HasValue) { rows.Add(Row("rejected", NoReceiver)); rejected++; continue; }
            if (!_ledger.HasRoom) { rows.Add(Row("rejected", "glue-budget", before)); rejected++; continue; }

            // The first application of the dispatch mints the one handle every row reports. It is cast and
            // registered before the native write, so a handle this provider cannot own fails the row rather than
            // leaving foam nothing can name.
            if (foamKey == null)
            {
                try
                {
                    foam = _registration.CreateEffectHandle("encounter");
                    foamKey = foam.GetRawText();
                    _registration.RegisterNative(foam, glue.Enemy);
                    var key = foamKey;
                    _registration.RegisterCancel(foam, () => ClearFoamed(key));
                }
                catch (Exception) { rows.Add(Row("rejected", "foam-handle-unavailable", before)); rejected++; continue; }
            }

            // Entering the native spawn entry point is the only place the world is written. It is one call and the
            // readback is its only proof, so a failure after it stays unknown rather than being retried.
            float? after = null;
            try { Apply(damage, volume, volume, strength); }
            catch (Exception) { rows.Add(Row("unknown", "native-commit-exception", before)); unknown++; continue; }
            try
            {
                var current = _resolve(target);
                var currentDamage = current?.Damage;
                if (current == null || current.Value.EnemyPointer != glue.EnemyPointer
                    || currentDamage == null || currentDamage.Pointer != damage.Pointer || !currentDamage.IsSetup
                    || currentDamage.Owner == null || currentDamage.Owner.Pointer != glue.EnemyPointer)
                { rows.Add(Row("unknown", "receiver-changed-during-commit", before)); unknown++; continue; }
                after = ReadAttachedGlueVolume(currentDamage);
            }
            catch (Exception) { rows.Add(Row("unknown", "readback-exception", before)); unknown++; continue; }
            // A receiver that does not read afterwards, or whose published volume went backwards, is not a spawn
            // this side can vouch for.
            if (!after.HasValue || after.Value < before.Value)
            { rows.Add(Row("unknown", "unexpected-glue-readback", before, after)); unknown++; continue; }
            // The volume did not move: either the spawn was dropped or the receiver's own cap already held this
            // much glue. The attempt is real, the effect is not observable.
            if (after.Value == before.Value)
            { rows.Add(Row("unknown", "glue-unseen", before, after)); unknown++; continue; }

            float added = after.Value - before.Value;
            // Two applications on one life are one entry whose volumes add up, because that is what a removal has
            // to take back; the same handle covers both.
            var existing = _ledger.Find(target);
            if (existing != null) _ledger.Grow(existing, added, expiresAtTick);
            else _ledger.Add(new GlueRecord(target, glue.EnemyPointer, added, foamKey!, expiresAtTick));
            rows.Add(Row("committed", "committed", before, after));
            committed++;
        }

        var outputs = RuntimeJson.From(new { results = rows, foam = foamKey == null ? (JsonElement?)null : foam });
        if (rejected == 0 && unknown == 0) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
        {
            string code = rows.Count == 1 ? rows[0].Code
                : rows.Select(r => r.Code).Distinct().Count() == 1 ? rows[0].Code : "foaming-all-rejected";
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);
        }
        return CommandResult.FailedUnknown(outputs, rows.Count == 1 ? rows[0].Code : "foaming-all-unknown");
    }

    /// <summary>The `foam` handle's own cancel: every application made through it is taken back off the enemy it
    /// was made on, and the ledger entry leaves first so a receiver that throws mid-removal is not retried.
    ///
    /// `internal`, not private, so the focused suite can drive the cancel the kernel's handle registration calls;
    /// the handle callback is still the only production caller.</summary>
    internal void ClearFoamed(string handleKey)
    {
        if (handleKey == null) return;
        foreach (var record in _ledger.TakeByHandle(handleKey)) _ledger.Remove(record);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        // The receivers this provider can still resolve are handed back to no foam before the ledger goes: a
        // plugin that unloads mid-level must not leave foam behind that nothing can name any more.
        _ledger.Clear();
        _ledger.Dispose();
    }
}
