using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Gear;
using Globals;
using Player;

namespace ForgeWeapon.Native;

/// <summary>One list of the game's own gear pool, as the pool narrowing reads and rewrites it. The game's pool is an
/// IL2CPP `List&lt;GearIDRange&gt;` while a test's is a plain list, and this is the four members both answer; the
/// interop wrapper lives beside the hook that builds it, so nothing here touches a native type.</summary>
internal interface IGearPoolSlot
{
    int Count { get; }
    GearIDRange this[int index] { get; }
    void Clear();
    void Add(GearIDRange gear);
}

/// <summary>One slot of the game's own gear pool by the slot's own number, or null when the game has no list for it.
/// This is the one place the narrowing meets the pool, so the pool it rewrites is whatever the caller hands it —
/// the live `GearManager.m_gearPerSlot` lists in the game, a fixture's own lists in a test.</summary>
internal delegate IGearPoolSlot? GearPoolSlotSource(int slot);

/// <summary>What the pool narrowing did, so the caller that owns the live lists can publish it. <see cref="Applied"/>
/// is the only case in which <see cref="Items"/> is anything but the pool as the game built it.</summary>
internal sealed class GearPoolNarrowing
{
    internal GearPoolNarrowing(GearIDRange[]?[] applied, GearIDRange[][] items)
    { Applied = applied; Items = items; }

    /// <summary>One slot of the game's own pool when it is the policy's, null when that slot stays the game's own:
    /// the whole pool is narrowed or none of it is, so a null slot is never written back.</summary>
    internal GearIDRange[]?[] Applied { get; }

    /// <summary>Every slot's contents as they were when this narrowing read them, for the caller's own report.</summary>
    internal GearIDRange[][] Items { get; }
}

/// <summary>The loadout policy on the live gear pool: which rundown's policy is in force, what the game's own offer
/// is projected onto, and the one piece of native state this package narrows.
///
/// The projection — the list a slot's picker is built from — is answered per call by <see cref="Offer"/> and is a
/// pure function of the game's own array. The pool itself is narrowed in <see cref="Narrow"/>, once per gear-loading
/// pass and before `RescanFavorites` reads it, because the gear the game equips by itself is the one the saved
/// favorites file names: with the pool narrowed that name matches nothing and the game falls back to the first
/// entry of the same pool, while a gear that is offered but not equipped can never be picked. Both go through the
/// accepted <see cref="LoadoutPolicyData"/> snapshot and the accepted <see cref="GearLoadoutFilter"/>; neither
/// writes the favorites file, calls `SaveFavoritesData`/`RegisterGearInSlotAsEquipped`, nor touches a shared data
/// block — the only state changed is the three lists the game itself owns, and their original contents are kept so
/// a rundown that no longer has a policy gets exactly the game's own list back.
///
/// Every failure is a whole-slot and whole-policy fallback: a policy entry the game's own offer does not carry, or
/// an identity read that throws, leaves the game's list untouched, names the reason once, and (on a fault) latches
/// this session off until restart rather than filtering with an identity rule it has just failed to apply. One
/// rundown is one policy for the whole process, and which policy that is never depends on file order.</summary>
internal sealed class GearLoadoutSession
{
    /// <summary>The three slots a policy covers, in the game's own order. A policy can never be in force for one
    /// of them and not the others: either the whole pool is the policy's or it is the game's own.</summary>
    private static readonly LoadoutSlot[] Slots = { LoadoutSlot.GearStandard, LoadoutSlot.GearSpecial, LoadoutSlot.GearClass };
    private readonly LoadoutPolicySnapshot _policies;
    private readonly Action<string> _report, _info;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    /// <summary>The original contents of each narrowed list, captured the first time this session narrowed it.
    /// Empty means the pool is the game's own — either it was never narrowed or it has been put back.</summary>
    private readonly Dictionary<LoadoutSlot, GearLoadoutSlotSnapshot<GearIDRange>> _original = new();
    /// <summary>The pool instance this session narrowed, kept so a restore writes into the same lists the narrowing
    /// wrote into rather than into whatever the game's manager holds by then. Null whenever nothing is narrowed.</summary>
    private GearPoolSlotSource? _pool;
    /// <summary>One line per distinct fact, so a per-frame projection and a repeated loading pass do not turn the
    /// same decision into a flood; the report is per process, which is the lifetime of one session.</summary>
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private ForgeLoadoutPolicy? _policy;
    /// <summary>The rundown the policy above was resolved for, and whether one has been resolved at all: the game
    /// reports the zero id before a rundown is loaded, so "no policy" and "not asked yet" share a null policy.</summary>
    private uint _rundown;
    private bool _resolved;
    private bool _failed, _disposed;

    /// <summary>The policies are read once before this session exists; the per-file rejection lines were already
    /// reported by the reader, which is why only the accepted snapshot arrives here.</summary>
    internal GearLoadoutSession(LoadoutPolicySnapshot policies, Action<string> report, Action<string> info)
    {
        _policies = policies ?? throw new ArgumentNullException(nameof(policies));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>The one policy this session may ever apply, or null. Resolved from `Global.RundownIdToLoad` on
    /// every call it is needed, so a rundown change is answered by the same code path as the first lookup and a
    /// client that has the policy for the rundown it loaded is the only client that filters. Inside an expedition
    /// no gear can be changed, so the projection steps aside there and leaves the game's list whole.</summary>
    internal LoadoutSlotPolicy? SlotPolicy(LoadoutSlot slot)
    {
        CheckThread();
        if (_disposed || _failed || GameStateManager.IsInExpedition) return null;
        return Policy()?.Slot(slot);
    }

    /// <summary>The game's own offer for one slot, projected onto the policy's entries. The returned list is the
    /// projection when there is one and the game's own items — in the game's own order — in every other case, so a
    /// caller never has to decide what "the original" was. Null means this slot is not the policy's business at
    /// all: no policy is in force for the loaded rundown, the game is inside an expedition, the slot is not one of
    /// the three, or the game handed over no list.</summary>
    internal IReadOnlyList<GearIDRange>? Offer(InventorySlot slot, IReadOnlyList<GearIDRange>? offered)
    {
        CheckThread();
        if (offered == null || !IsCovered(slot)) return null;
        var policy = SlotPolicy((LoadoutSlot)slot);
        if (policy == null) return null;
        var decision = GearLoadoutFilter.Filter((LoadoutSlot)slot, offered, Identity, Policy()!);
        Report(decision);
        // A fault means the identity rule itself did not hold on this offer. The offer stays the game's own and
        // this session stops taking part until restart rather than publishing a list built on a broken rule.
        if (decision.Outcome == GearLoadoutOutcome.Failed)
            Fault(new InvalidOperationException(decision.Diagnostics.Count == 0 ? "The projection failed." : decision.Diagnostics[0]));
        return decision.Keep;
    }

    /// <summary>One hook body's whole error path: every failure a policy can produce is a whole-policy fallback.
    /// The pool goes back to the game's own contents, the policy stops taking part for the rest of the process
    /// rather than filtering with a rule that has just failed, and one line names what happened. Nothing is
    /// rethrown: a hook body that throws inside a native call is a fault the game cannot bound.</summary>
    internal void Fault(Exception error)
    {
        ArgumentNullException.ThrowIfNull(error);
        string description;
        try { description = Describe(error); }
        catch (Exception unavailable) { description = "weapon.loadout-policy-failed: " + unavailable.GetType().Name; }
        try { Restore(); }
        catch (Exception restore) { description += "; restore=" + restore.GetType().Name; }
        _failed = true;
        try { _report(description); }
        catch (Exception reporting) { _report("weapon.loadout-policy-failed: reporting this failure also failed: " + reporting.GetType().Name); }
    }

    /// <summary>The loading hook's whole body: this narrowing rewrites the game's own pool lists when the loaded
    /// rundown has a policy, and the caller publishes what it is handed. A pool that cannot be applied — a slot the
    /// game has not built, a policy entry the pool does not carry, a read that throws — leaves every slot exactly
    /// as the game built it, so the narrowing never reports a half-filtered pool and never throws.</summary>
    internal GearPoolNarrowing Narrow(GearPoolSlotSource pool)
    {
        ArgumentNullException.ThrowIfNull(pool);
        CheckThread();
        if (_disposed || _failed) return Unchanged(pool);
        // The pool is read once, all three slots together: the contents this reports are the contents the decision
        // was made from, and a pool that changes under the narrowing cannot make the two disagree.
        GearIDRange[][] live;
        try
        {
            live = new GearIDRange[Slots.Length][];
            for (int index = 0; index < live.Length; index++) live[index] = Items(Required(pool, index));
        }
        catch (Exception error) { return Unreadable(error, pool); }
        try
        {
            var policy = Policy();
            if (policy == null)
            {
                Restore();
                return new GearPoolNarrowing(new GearIDRange[]?[Slots.Length], live);
            }
            var decisions = new GearLoadoutDecision<GearIDRange>[Slots.Length];
            for (int index = 0; index < Slots.Length; index++)
            {
                var decision = GearLoadoutFilter.Filter(Slots[index], live[index], Identity, policy);
                Report(decision);
                // One slot that cannot be projected means this pool is not the policy's pool, so the whole pool goes
                // back to the game's own contents: a half-filtered pool would be a pool no author ever wrote.
                if (!decision.Filtered)
                {
                    Restore();
                    return new GearPoolNarrowing(new GearIDRange[]?[Slots.Length], live);
                }
                decisions[index] = decision;
            }
            // The snapshot is the pool as the game built it, taken the one time this session narrows a slot:
            // re-narrowing an already narrow list would capture the policy's own list as "the original".
            _pool = pool;
            for (int index = 0; index < Slots.Length; index++)
            {
                if (!_original.ContainsKey(Slots[index]))
                    _original[Slots[index]] = GearLoadoutSlotSnapshot<GearIDRange>.Capture(live[index]);
            }
            // The lists are rewritten in place: `m_gearPerSlot` and its list instances belong to the game and to
            // every other holder of them, so the policy's contents go into the same instance rather than into a
            // replacement the game would never see.
            var applied = new GearIDRange[]?[Slots.Length];
            for (int index = 0; index < Slots.Length; index++)
            {
                var keep = decisions[index].Keep;
                var items = new GearIDRange[keep.Count];
                for (int item = 0; item < items.Length; item++) items[item] = keep[item];
                WritePool(Required(pool, index), items);
                applied[index] = items;
            }
            return new GearPoolNarrowing(applied, live);
        }
        catch (Exception error)
        {
            Fault(error);
            return new GearPoolNarrowing(new GearIDRange[]?[Slots.Length], live);
        }
    }

    /// <summary>Puts every narrowed list back and forgets the policy that narrowed it, so the next call resolves
    /// the rundown from scratch. Restoring before the policy is dropped is what makes the operation safe to repeat:
    /// a second restore finds no snapshot and does nothing.</summary>
    internal void Restore()
    {
        var pool = _pool;
        _pool = null;
        if (_original.Count == 0) { _policy = null; return; }
        foreach (var captured in _original)
        {
            try
            {
                var live = pool == null ? null : pool((int)captured.Key);
                if (live != null) WritePool(live, captured.Value.Items);
            }
            // A list that cannot be written any more cannot hold the policy's contents either; the restore goes on
            // with the other slots rather than abandoning them half restored.
            catch (Exception) { }
        }
        _original.Clear();
        _policy = null;
    }

    /// <summary>The pool is the game's own again: used by the shutdown path, where nothing is reported because the
    /// process is going away.</summary>
    internal void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        Restore();
        _disposed = true;
        _reported.Clear();
    }

    private void Resolve(uint rundown)
    {
        _resolved = true;
        _rundown = rundown;
        if (!_policies.TryGet(rundown, out var policy))
        {
            _policy = null;
            Once("inactive:" + Number(rundown), "weapon.loadout-policy-inactive reason=rundown-mismatch rundown=" + Number(rundown));
            return;
        }
        _policy = policy;
        Once("active:" + Number(rundown), "weapon.loadout-policy-active rundown=" + Number(rundown)
            + " policy=" + policy.RelativePath + " slots=" + SlotSummary(policy));
    }

    /// <summary>The policy for the rundown this process has loaded right now, re-resolved whenever that rundown is not
    /// the one this session last resolved. The loaded rundown can change without this package being told — the game
    /// reads it into its own static — so a cached policy is only ever the policy of the rundown it was resolved for:
    /// a rundown no policy is installed for answers null, which is what puts a narrowed pool back.</summary>
    private ForgeLoadoutPolicy? Policy()
    {
        var rundown = Global.RundownIdToLoad;
        if (_resolved && rundown == _rundown) return _policy;
        Resolve(rundown);
        return _policy;
    }

    /// <summary>One gear's two identity facts, read once per projection: the record text the game itself put on
    /// the gear — the same text the `gear-block` mount reads — and the category component a workshop gear carries
    /// its own block id in. A null entry in the game's array reads as a gear with neither fact, so it is simply not
    /// one of the policy's.</summary>
    private static GearLoadoutIdentity Identity(GearIDRange? gear)
    {
        if (gear == null) return new GearLoadoutIdentity(null, 0);
        return new GearLoadoutIdentity(gear.PlayfabItemInstanceId, gear.GetCompID(eGearComponent.Category));
    }

    /// <summary>One slot of the pool by the index the game's own array uses. A slot the game has not built is a
    /// pool this narrowing does not recognise, which is a whole-pool fallback like any other.</summary>
    private static IGearPoolSlot Required(GearPoolSlotSource pool, int index)
        => pool((int)Slots[index]) ?? throw new InvalidOperationException("The game's gear pool has no list for " + Slots[index] + ".");

    /// <summary>The pool cannot be read at all, which is a fault like any other: the whole policy stops taking part,
    /// one line names the failure, and the caller is told nothing was written. The contents are empty because there
    /// is nothing to report — the pool is the game's own, untouched.</summary>
    private GearPoolNarrowing Unreadable(Exception error, GearPoolSlotSource pool)
    {
        Fault(error);
        return Unchanged(pool);
    }

    /// <summary>The game's own list items as a list this package can project over, without holding a second live
    /// reference to the game's list: the items are read once each, and the list itself is only ever rewritten
    /// through the same instance the game owns.</summary>
    private static GearIDRange[] Items(IGearPoolSlot live)
    {
        var items = new GearIDRange[live.Count];
        for (int index = 0; index < items.Length; index++) items[index] = live[index];
        return items;
    }

    /// <summary>One slot's contents written into the game's own list in place: `Clear` and then the items one by
    /// one, so the list instance keeps its identity and every other holder of it sees the same contents.</summary>
    private static void WritePool(IGearPoolSlot live, GearIDRange[] items)
    {
        live.Clear();
        for (int index = 0; index < items.Length; index++) live.Add(items[index]);
    }

    /// <summary>Nothing was written, so the caller publishes nothing: every slot is reported empty rather than read
    /// back a second time. Used on the paths where the pool could not be read at all, and where reading it again
    /// could only fail in the same way.</summary>
    private static GearPoolNarrowing Unchanged(GearPoolSlotSource pool)
    {
        var items = new GearIDRange[Slots.Length][];
        for (int index = 0; index < items.Length; index++) items[index] = Array.Empty<GearIDRange>();
        return new GearPoolNarrowing(new GearIDRange[]?[Slots.Length], items);
    }

    private static bool IsCovered(InventorySlot slot)
        => slot is InventorySlot.GearStandard or InventorySlot.GearSpecial or InventorySlot.GearClass;

    /// <summary>The activation line's own summary: which entries each covered slot may offer, in the policy's own
    /// ascending order, so one line carries the whole activation an operator has to compare against the picker.</summary>
    private static string SlotSummary(ForgeLoadoutPolicy policy)
    {
        var text = new StringBuilder(64);
        for (int index = 0; index < Slots.Length; index++)
        {
            if (index != 0) text.Append(',');
            text.Append(Slots[index]).Append('=').Append(Ids(policy.Slot(Slots[index]).OfflineGearIds));
        }
        return text.ToString();
    }

    private static string Ids(uint[] values)
    {
        var text = new StringBuilder(values.Length * 6);
        for (int index = 0; index < values.Length; index++)
        {
            if (index != 0) text.Append('|');
            text.Append(Number(values[index]));
        }
        return text.ToString();
    }

    /// <summary>Every line a decision asked for, once per distinct fact. The projection runs per frame while a
    /// picker is open, so the per-slot count line is keyed by the slot and the outcome rather than by its own
    /// numbers: a later pass with different counts is the same decision and does not become a second line.</summary>
    private void Report<T>(GearLoadoutDecision<T> decision)
    {
        foreach (var line in decision.Diagnostics)
        {
            if (line.StartsWith("weapon.loadout-slot ", StringComparison.Ordinal))
            {
                var end = line.IndexOf(" offered=", StringComparison.Ordinal);
                Once(end < 0 ? line : line[..end], line);
                continue;
            }
            if (line.StartsWith("weapon.loadout-policy-unmatched ", StringComparison.Ordinal))
            {
                var end = line.IndexOf(" missing=", StringComparison.Ordinal);
                Once(end < 0 ? line : line[..end], line);
                continue;
            }
            Once(line, line);
        }
    }

    private void Once(string key, string message)
    {
        if (!_reported.Add(key)) return;
        try { _info(message); }
        catch (Exception reporting) { _report("weapon.loadout-report-failed: " + reporting.GetType().Name); }
    }

    private static string Describe(Exception error)
    {
        string message;
        try { message = error.Message ?? ""; }
        catch (Exception unavailable) { message = "<message unavailable: " + unavailable.GetType().Name + ">"; }
        var description = "weapon.loadout-policy-failed: " + error.GetType().Name + ": " + message;
        return description.Length <= 2048 ? description : description[..2048];
    }

    private void CheckThread()
    {
        // The pool is game state owned by the simulation thread, and every hook that reaches this session runs
        // there; a call from anywhere else is refused rather than allowed to narrow a list another thread reads.
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("The loadout policy requires the session's own simulation thread.");
    }

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
}
