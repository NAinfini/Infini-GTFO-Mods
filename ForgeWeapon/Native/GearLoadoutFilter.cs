using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace ForgeWeapon.Native;

/// <summary>One gear as the game offered it for a slot, in the two facts a policy matches on: the record text the
/// game itself put on the gear, and its category component. Reading them out of a native gear is the caller's
/// business — this type is what makes the projection a pure function of plain values.</summary>
internal readonly struct GearLoadoutIdentity
{
    internal GearLoadoutIdentity(string? record, uint categoryComponent)
    { Record = record; CategoryComponent = categoryComponent; }

    /// <summary>`GearIDRange.PlayfabItemInstanceId`, read as the game spelled it. The offline record text is
    /// `OfflineGear_ID_&lt;blockId&gt;`; null when the gear carries no such record.</summary>
    internal string? Record { get; }

    /// <summary>`GearIDRange.GetCompID(eGearComponent.Category)`. The website writes a workshop gear's own block id
    /// into that component, so the component travels with a copy of the gear wherever it goes.</summary>
    internal uint CategoryComponent { get; }
}

/// <summary>Reads the two identity facts off one offered gear. The caller supplies this because only it knows its
/// own gear type; the filter never touches a native object.</summary>
internal delegate GearLoadoutIdentity GearLoadoutIdentityReader<in T>(T gear);

/// <summary>What one slot's projection decided. Every outcome carries the list the caller publishes, so a caller
/// never has to reconstruct "the original" itself.</summary>
internal enum GearLoadoutOutcome : byte
{
    /// <summary>The projection was applied: the offer is the policy's entries, in the game's own order.</summary>
    Applied,
    /// <summary>The slot is not one a policy covers, so the game's own offer is returned untouched.</summary>
    Returned,
    /// <summary>A policy entry was not recognised in the offer: the whole slot stays as the game built it.</summary>
    Unmatched,
    /// <summary>Nothing in the offer was recognised, which for a non-empty policy is a fallback too.</summary>
    Empty,
    /// <summary>Reading an offer's identity threw: the whole slot stays as the game built it.</summary>
    Failed
}

/// <summary>One slot's projection: what to publish, why, and the lines to report. A decision is data — the filter
/// writes no log and changes no game state, so the same call always answers the same thing.</summary>
internal sealed class GearLoadoutDecision<T>
{
    internal GearLoadoutDecision(GearLoadoutOutcome outcome, IReadOnlyList<T> keep, IReadOnlyList<string> diagnostics)
    { Outcome = outcome; Keep = keep; Diagnostics = diagnostics; }

    internal GearLoadoutOutcome Outcome { get; }

    /// <summary>Exactly what the caller publishes for this slot, in the game's own order: the projection when the
    /// outcome is <see cref="GearLoadoutOutcome.Applied"/>, and the untouched offer in every other outcome.</summary>
    internal IReadOnlyList<T> Keep { get; }

    /// <summary>The lines this decision wants reported, in order. Reporting them is the caller's job, because the
    /// filter has no log and no state to report once per process with.</summary>
    internal IReadOnlyList<string> Diagnostics { get; }

    /// <summary>True when <see cref="Keep"/> is the filtered offer rather than the game's own list.</summary>
    internal bool Filtered => Outcome == GearLoadoutOutcome.Applied;

    /// <summary>Writes this decision into one live list in place — `Clear` and then the items one by one — so the
    /// list instance the game built keeps its identity and every other holder of it sees the same contents. This is
    /// idempotent: applying a decision twice leaves the list exactly as the first application left it.</summary>
    internal void ApplyTo(List<T> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.Clear();
        for (int index = 0; index < Keep.Count; index++) live.Add(Keep[index]);
    }
}

/// <summary>The loadout offer projection: which of the gears the game offers for a slot are the ones a policy names.
///
/// The rule is an allow-list, one slot at a time. A gear belongs to a policy entry when either the game's own
/// offline record text spells that entry's block id (`OfflineGear_ID_&lt;n&gt;`, the same spelling the `gear-block`
/// mount uses) or the gear's category component is that entry's block id — the route a workshop gear's own packet
/// takes. Nothing is parsed loosely: a second spelling of one block id is a different string and matches nothing.
///
/// The important property is the positive one: a projection is published only when every entry of that slot's
/// policy was recognised in the game's own offer. If one entry is missing, the whole slot is returned untouched and
/// named, because a partial list would be a list the author never wrote; the same holds when an identity read
/// throws. Order is the game's, duplicates are the game's (counted and reported, never resolved here), and a
/// repeated projection of an already projected offer changes nothing.</summary>
internal static class GearLoadoutFilter
{
    /// <summary>The game's own record of the offline gear block a gear came from:
    /// `GearManager.LoadOfflineGearDatas` spells it as this prefix plus `PlayerOfflineGearDataBlock.persistentID`,
    /// and the same spelling is what the favorites file stores as `LastEquipped_*`.</summary>
    internal const string GearBlockPrefix = "OfflineGear_ID_";

    /// <summary>The block id the game itself spelled on a gear, or null. Plain decimal text only: digits that parse
    /// to a `uint` and are that value's own text again, which is what the website writes with `String(blockId)`.
    /// Empty text, a sign, surrounding space, a leading zero, a radix prefix and a decimal point are other
    /// spellings and are refused, so one block id can never have a second spelling in this package.</summary>
    internal static string? OfflineGearBlockId(string? record)
    {
        if (record == null || !record.StartsWith(GearBlockPrefix, StringComparison.Ordinal)) return null;
        var block = record.Substring(GearBlockPrefix.Length);
        return uint.TryParse(block, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && string.Equals(block, value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal) ? block : null;
    }

    /// <summary>One slot's projection. `offered` is the game's own list for that slot, `policy` the accepted policy
    /// the caller activated; the returned decision always carries the list to publish, so the caller only ever has
    /// to apply one. The caller's own gate — "not in an expedition" — runs before this function, because it needs
    /// game state this pure projection deliberately does not read.</summary>
    internal static GearLoadoutDecision<T> Filter<T>(LoadoutSlot slot, IReadOnlyList<T> offered,
        GearLoadoutIdentityReader<T> identity, ForgeLoadoutPolicy policy)
    {
        ArgumentNullException.ThrowIfNull(offered);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(policy);
        var original = Copy(offered);
        // Rule 1 of the projection: a slot no policy covers — melee, hacking tool, consumable, or a value outside
        // the three the contract defines — is none of this filter's business.
        if (slot is not (LoadoutSlot.GearStandard or LoadoutSlot.GearSpecial or LoadoutSlot.GearClass))
            return new GearLoadoutDecision<T>(GearLoadoutOutcome.Returned, original, Array.Empty<string>());
        var ids = policy.Slot(slot).OfflineGearIds;
        var wanted = new HashSet<uint>(ids);
        try
        {
            var keep = new List<T>(offered.Count);
            var keptIds = new List<uint>(offered.Count);
            var matched = new HashSet<uint>();
            int duplicates = 0;
            for (int index = 0; index < offered.Count; index++)
            {
                var gear = offered[index];
                // One identity read per gear: the read is the native call, so it is never repeated to print a line.
                if (!Match(identity(gear), wanted, out var hit)) continue;
                if (!matched.Add(hit)) duplicates++;
                keep.Add(gear);
                keptIds.Add(hit);
            }
            // The safety property: every entry of this slot's policy has to be recognised in the game's own offer.
            // Anything less means the identity rule does not describe what the game offers here, and the slot stays
            // exactly as the game built it rather than becoming a list the author never wrote.
            var missing = new List<uint>();
            foreach (var id in ids)
                if (!matched.Contains(id)) missing.Add(id);
            if (missing.Count != 0)
            {
                return new GearLoadoutDecision<T>(GearLoadoutOutcome.Unmatched, original, new[]
                {
                    "weapon.loadout-policy-unmatched slot=" + slot + " missing=" + Join(missing)
                });
            }
            // Rule 4: a policy is never allowed to produce an empty slot. The contract's slots are non-empty, so
            // this can only follow an unmatched entry, but the guarantee is stated here rather than inferred.
            if (keep.Count == 0)
                return new GearLoadoutDecision<T>(GearLoadoutOutcome.Empty, original, Array.Empty<string>());
            var lines = new List<string>(2)
            {
                "weapon.loadout-slot slot=" + slot + " offered=" + Number(offered.Count) + " kept=" + Number(keep.Count)
                + " duplicates=" + Number(duplicates) + " dropped=" + Number(offered.Count - keep.Count)
                + " ids=" + Join(keptIds)
            };
            if (offered.Count != keep.Count && slot is LoadoutSlot.GearStandard or LoadoutSlot.GearSpecial)
                lines.Add("weapon.loadout-vanilla-dropped count=" + Number(offered.Count - keep.Count));
            return new GearLoadoutDecision<T>(GearLoadoutOutcome.Applied, keep, lines);
        }
        catch (Exception error)
        {
            // A failed identity read must never publish half a list. The offer stays as the game built it, the
            // reason is named once, and the outcome tells the caller to stop trusting this policy for the session.
            return new GearLoadoutDecision<T>(GearLoadoutOutcome.Failed, original, new[]
            {
                "weapon.loadout-policy-failed: " + error.GetType().Name + ": " + error.Message
            });
        }
    }

    /// <summary>Which entry of the policy one gear is, or false when it is none of them. The game's own offline
    /// record text is asked first and the category component second; one gear is one entry, so an offer can never
    /// satisfy two policy entries at once, and a gear with both facts agreeing is still counted once.</summary>
    private static bool Match(in GearLoadoutIdentity identity, HashSet<uint> wanted, out uint hit)
    {
        hit = 0;
        var block = OfflineGearBlockId(identity.Record);
        if (block != null && uint.TryParse(block, NumberStyles.None, CultureInfo.InvariantCulture, out var value)
            && wanted.Contains(value))
        { hit = value; return true; }
        if (wanted.Contains(identity.CategoryComponent)) { hit = identity.CategoryComponent; return true; }
        return false;
    }

    private static T[] Copy<T>(IReadOnlyList<T> offered)
    {
        var items = new T[offered.Count];
        for (int index = 0; index < items.Length; index++) items[index] = offered[index];
        return items;
    }

    private static string Join(List<uint> values)
    {
        var text = new StringBuilder(values.Count * 6);
        for (int index = 0; index < values.Count; index++)
        {
            if (index != 0) text.Append(',');
            text.Append(Number(values[index]));
        }
        return text.ToString();
    }

    private static string Number(uint value) => value.ToString(CultureInfo.InvariantCulture);
    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>The original contents of one game-owned gear list, captured before a policy narrows it, so the exact
/// items in the exact order can be put back when the policy stops applying. It holds the references the game itself
/// put in the list, so restoring is a copy and never a re-derivation.</summary>
internal sealed class GearLoadoutSlotSnapshot<T>
{
    private readonly T[] _items;

    private GearLoadoutSlotSnapshot(T[] items) { _items = items; }

    internal static GearLoadoutSlotSnapshot<T> Capture(IReadOnlyList<T> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        var items = new T[live.Count];
        for (int index = 0; index < items.Length; index++) items[index] = live[index];
        return new GearLoadoutSlotSnapshot<T>(items);
    }

    internal int Count => _items.Length;
    /// <summary>The captured contents as the caller's own array, in their original order: restoring is a copy of
    /// what the game itself put in the list, never a re-derivation.</summary>
    internal T[] Items => _items;

    /// <summary>Puts the captured items back into one live list, in their original order and in place — the list
    /// instance stays the game's own, exactly as <see cref="GearLoadoutDecision{T}.ApplyTo"/> leaves it.</summary>
    internal void RestoreInto(List<T> live)
    {
        ArgumentNullException.ThrowIfNull(live);
        live.Clear();
        for (int index = 0; index < _items.Length; index++) live.Add(_items[index]);
    }
}
