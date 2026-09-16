using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using AIGraph;
using ForgeRuntime.Framework;
using LevelGeneration;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>The inventory actions this package executes for `gtfo.player`, submitted through the two host entry
/// points in `PlayerBackpackManager` that are the game's own replication points — the ones whose bodies send the
/// inventory packet and then apply the change locally, which is what makes them host calls rather than field
/// writes. Evidence: evidence/inventory-actions.json decodes `MasterAddItem(pItemData_WithOwner)` and
/// `TryMasterRemovePocketItemWithID`; both send `m_addItemPacket` / `m_removeItemPacket` in the body.
///
/// What each catalog row can honestly become is decided by that evidence, not by the catalog text:
///
/// - `forge.action.inventory.give` — `MasterAddItem` is a real host add with an explicit target player and a
///   per-item slot, so the row is submitted. The item itself has to come from an `item` resource bound to the
///   game's own persistent id; that binding is the runtime resource registry (`RuntimeModule.ResourceProviders`),
///   which does not exist yet, so it is an injected resolver and an unbound environment refuses by name instead
///   of guessing an id.
/// - `forge.action.inventory.consume` — `TryMasterRemovePocketItemWithID` is the game's own host removal of one
///   pocket item, sent and applied in the same body, so one unit per call is submitted and read back.
/// - `drop` — no row and no body: the node list carries no drop node, so the binding row was deleted at
///   integration (ruling 110.5) and the native body went with it (ruling 133.3). The game's own drop path is not
///   reachable from a plan, and an implementation nothing declares is not kept.
/// - `pickup`, `equip`, `transfer`, `stack_set` — refused with the missing capability named. See
///   evidence/inventory-actions.json for the entry point each one lacks.
///
/// Nothing here writes a backpack field directly: `PlayerBackpack.AddPocketItem` / `RemovePocketItem` are local
/// list edits with no packet in their bodies, so a host that used them would desync every client.</summary>
internal sealed class InventoryActionAdapter
{
    /// <summary>The one item id the resolver answers with: the game's own `ItemDataBlock.persistentID`, which is
    /// the number the game itself carries in `pItemData.itemID_gearCRC` for a non-gear item.</summary>
    internal delegate bool ItemResolver(string resourceId, out uint itemId);

    /// <summary>What the runtime resource registry must answer for one `forge.resource.item` reference: the
    /// native item id, or false when this environment cannot bind that resource. The signature is batch C's
    /// (`RuntimeModule.ResourceProviders` → `Resolve(resourceId)`), narrowed to the one value an item action
    /// needs, so the integration only has to forward its own answer.</summary>
    private readonly ItemResolver _resolveItem;

    /// <summary>The native object a `gtfo.player` reference names, or null when this environment cannot name one.
    /// The kernel's entity tables are one-way — a native object is mapped to the reference its owner allocated,
    /// never back — so this half is the missing direction, and the runtime's own entry point for it is batch C's
    /// `RuntimeKernel.ResolveEntity(object)` plus its object-resolver list. Until that exists this is an injected
    /// lookup: an action that cannot name the player behind a reference refuses the recipient by name instead of
    /// guessing one from a slot, a name or an agent.</summary>
    internal delegate object? PlayerLookup(EntityReference reference);

    private readonly PlayerLookup _playerOf;

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Action<string> _report;

    /// <summary>The one slot an item that is not equipment lives in, spelled as the game spells it. A pocket
    /// item is exactly this slot: the native removal entry point builds its `pItemData` with this byte, and the
    /// add path only needs it for the same item class.</summary>
    internal const byte PocketSlot = (byte)InventorySlot.InPocket;
    /// <summary>What one `give` call may hand out. A pocket item occupies a slot of its own in this build — the
    /// native add addresses one slot and the removal path takes one item per call — so there is no stack an add
    /// could grow and a request for more than one is refused rather than clamped to a number nobody asked for.</summary>
    internal const int MaximumQuantity = 1;
    /// <summary>What one `consume` call may spend at most. The native removal takes one unit per call, so this is
    /// the bound the repeat loop is allowed to run to; a larger request is refused rather than clamped.</summary>
    internal const int MaximumCount = 64;
    /// <summary>The largest charge count a `pItemData` can carry: `custom.ammo` is a float, and a value beyond
    /// this is not a charge count any weapon or consumable in this build uses.</summary>
    internal const float MaximumCharges = 9999f;

    /// <summary>The result codes. Each names a distinct refusal or uncertainty; none of them is a silent
    /// success. `inventory-committed` is the only code that says the game's own entry point returned and the
    /// backpack read back the change.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    internal const string TargetKindCode = "inventory-unsupported-recipient";
    internal const string StaleCode = "stale-or-unsupported-recipient";
    internal const string NoBackpackCode = "target-has-no-backpack";
    internal const string ResourceCode = "item-resource-unresolved";
    internal const string QuantityCode = "quantity-out-of-range";
    internal const string ChargesCode = "charges-out-of-range";
    internal const string SlotCode = "slot-unsupported-for-item";
    internal const string StateChangedCode = "state-changed-before-commit";
    internal const string CommitExceptionCode = "native-commit-exception";
    internal const string NoReadbackCode = "commit-unread-back";
    internal const string CommittedCode = "inventory-committed";
    internal const string NotAttemptedCode = "not-attempted-after-unknown-commit";
    // The four rows whose native entry point this build does not have, each refused by the capability it misses.
    internal const string PickupCode = "pickup-source-identity-missing";
    internal const string EquipCode = "equip-entry-point-absent";
    internal const string TransferCode = "requires-transaction";
    internal const string StackSetCode = "stack-write-has-no-sync-entry";

    internal InventoryActionAdapter(RuntimeKernel kernel, Func<bool> canExecute, ItemResolver resolveItem,
        PlayerLookup playerOf, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _resolveItem = resolveItem ?? throw new ArgumentNullException(nameof(resolveItem));
        _playerOf = playerOf ?? throw new ArgumentNullException(nameof(playerOf));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Whether this side may submit an inventory write at all. The game's own inventory packets are
    /// host-sent: the bodies that send them are on the master, and a client's own `Want*` path asks the host
    /// instead. The gate is therefore the same one the equipment writes and the mark action use — the host's
    /// readiness and the game's master flag — checked before any native call.</summary>
    internal bool Authoritative()
    {
        if (!_canExecute() || !SNet.IsMaster) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    /// <summary>One recipient's line in a result, in the row order the catalog's result schemas share: the four
    /// fixed columns, then that action's own quantity column and the command's target count. Field names are the
    /// wire contract's own spelling.</summary>
    internal sealed record Row(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("quantity")] int? Quantity,
        [property: JsonPropertyName("count")] int? Count,
        [property: JsonPropertyName("target_count")] int TargetCount);

    /// <summary>What one recipient's submission did. `Reference` is the reference the action was asked about, so
    /// a caller can only ever publish the identity its own kind handed back.</summary>
    internal sealed record Outcome(EntityReference Reference, string Status, string CommitState, string Code, int Fact);

    internal CommandResult HandleGive(CommandContext context) => HandleGive(this, context);
    internal CommandResult HandleConsume(CommandContext context) => HandleConsume(this, context);

    /// <summary>The recipient port each catalog row declares its recipients on. The names are the catalog's own,
    /// not this package's: `give` collects inventories, the item actions collect items, and reading a different
    /// name than the row declares would be a handler that answers a shape nobody wrote.</summary>
    internal const string GiveRecipients = "inventories";
    internal const string ItemRecipients = "items";

    /// <summary>The `forge.action.inventory.give` command handler: one item resource, one quantity, one charge
    /// count, and the recipients the runtime already validated as `gtfo.player`.
    ///
    /// The whole request check runs before the first recipient, because a command that cannot be carried out as
    /// asked must not half-apply. The quantity bound is the catalog's own `reject` policy for a request larger
    /// than one call can carry; the item id is resolved once, so a resource this environment cannot bind refuses
    /// every recipient with the same code instead of adding a different item per recipient.</summary>
    internal static CommandResult HandleGive(InventoryActionAdapter adapter, CommandContext context)
    {
        if (!adapter.Authoritative()) return CommandResult.Rejected(AuthorityCode);
        var recipients = Recipients(context, GiveRecipients);
        if (recipients.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");
        if (!TryInteger(context.Inputs, "quantity", 1, MaximumQuantity, out int quantity))
            return CommandResult.Rejected(QuantityCode);
        if (!TryInteger(context.Inputs, "charges", 1, (int)MaximumCharges, out int charges))
            return CommandResult.Rejected(ChargesCode);
        string? resourceId = ResourceId(context.Inputs);
        if (resourceId == null) return CommandResult.Rejected(ResourceCode);
        if (!adapter._resolveItem(resourceId, out uint itemId)) return CommandResult.Rejected(ResourceCode);

        var outcomes = new List<Outcome>(recipients.Length);
        bool stopCommitting = false;
        foreach (var recipient in recipients)
        {
            // A write whose outcome is unknown stops the run: continuing would compound one uncertain change into
            // several, and every remaining recipient still gets its own row saying it was not attempted.
            if (stopCommitting)
            {
                outcomes.Add(new Outcome(recipient, CommandStatuses.Rejected, CommitStates.None, NotAttemptedCode, 0));
                continue;
            }
            var outcome = adapter.Give(recipient, itemId, quantity, charges);
            outcomes.Add(outcome);
            if (outcome.CommitState == CommitStates.Unknown) stopCommitting = true;
        }
        return Aggregate("give", outcomes);
    }

    /// <summary>The `forge.action.inventory.consume` command handler: one pocket item id from an `item`
    /// resource, spent `count` times for each recipient. The native call removes one unit and sends the removal
    /// packet in the same body, so the loop is that call repeated, and every iteration decides its own row —
    /// a recipient that runs out mid-loop keeps the units already removed and reports the refusal.</summary>
    internal static CommandResult HandleConsume(InventoryActionAdapter adapter, CommandContext context)
    {
        if (!adapter.Authoritative()) return CommandResult.Rejected(AuthorityCode);
        var recipients = Recipients(context, ItemRecipients);
        if (recipients.Length > CommandResult.MaximumFacts) return CommandResult.Rejected("too-many-targets");
        if (!TryInteger(context.Inputs, "count", 1, MaximumCount, out int count))
            return CommandResult.Rejected(QuantityCode);
        if (context.Inputs.TryGetProperty("charges", out var charges) && charges.ValueKind != JsonValueKind.Null)
            return CommandResult.Rejected(ChargesCode);
        string? resourceId = ResourceId(context.Inputs);
        if (resourceId == null) return CommandResult.Rejected(ResourceCode);
        if (!adapter._resolveItem(resourceId, out uint itemId)) return CommandResult.Rejected(ResourceCode);

        var outcomes = new List<Outcome>(recipients.Length);
        bool stopSpending = false;
        foreach (var recipient in recipients)
        {
            if (stopSpending)
            {
                outcomes.Add(new Outcome(recipient, CommandStatuses.Rejected, CommitStates.None, NotAttemptedCode, 0));
                continue;
            }
            var outcome = adapter.Consume(recipient, itemId, count);
            outcomes.Add(outcome);
            if (outcome.CommitState == CommitStates.Unknown) stopSpending = true;
        }
        return Aggregate("consume", outcomes);
    }

    /// <summary>The item resource's own id, or null when the port is absent or carries something that is not a
    /// resource frame. The item id is never derived from a display name or an index: only the resource the plan
    /// bound can name one, and an environment that cannot resolve that resource refuses the whole command. The
    /// frame is read here rather than through a helper because the runtime's own resource reader is part of the
    /// batch that adds the registry, and this action must compile against the framework as it stands.</summary>
    private static string? ResourceId(JsonElement inputs)
    {
        if (!inputs.TryGetProperty("item", out var item) || item.ValueKind != JsonValueKind.Object) return null;
        if (!item.TryGetProperty("resourceKind", out var kind) || kind.ValueKind != JsonValueKind.String
            || kind.GetString() != ItemResourceKind) return null;
        if (!item.TryGetProperty("resourceId", out var id) || id.ValueKind != JsonValueKind.String) return null;
        var text = id.GetString();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    /// <summary>Adds one item to a recipient's backpack through the game's own host add entry point. The slot is
    /// read before the submission, so a recipient whose slot is occupied is refused rather than overwritten, and it
    /// is read again after, so a slot that does not hold the new item id is an unknown commit rather than a
    /// success.</summary>
    internal Outcome Give(EntityReference recipient, uint itemId, int quantity, int charges)
    {
        if (quantity != 1) return Refuse(recipient, QuantityCode);
        if (!TryBackpack(recipient, out var backpack, out string refusal)) return Refuse(recipient, refusal);
        var slots = backpack!.Slots;
        if (slots == null) return Refuse(recipient, NoBackpackCode);
        if (slots.Length <= PocketSlot) return Refuse(recipient, SlotCode);
        if (slots[PocketSlot] != null) return Refuse(recipient, StateChangedCode);
        var data = new pItemData
        {
            custom = new pItemData_Custom { ammo = charges, byteId = 0, byteState = 0 },
            itemID_gearCRC = itemId,
            replicatorRef = default,
            slot = (InventorySlot)PocketSlot,
            originLayer = default(LG_LayerType),
            originCourseNode = default(pCourseNode)
        };
        if (!Authoritative()) return Refuse(recipient, AuthorityCode);
        try
        {
            // The owner is a `pPlayer` handle, so the player this action already resolved is put into it the way
            // the game's own removal path does; `sendOnlyTo` stays null, which is the broadcast the catalog's
            // recipient list means.
            var dataWithOwner = new pItemData_WithOwner { owningPlayer = default(SNetStructs.pPlayer), data = data };
            dataWithOwner.owningPlayer.SetPlayer(backpack.Owner);
            PlayerBackpackManager.MasterAddItem(dataWithOwner, null);
        }
        catch (Exception error)
        {
            // The game's own body sends its packet before it applies the change, so whether the item reached this
            // recipient is not observable from here; the commit stays unknown and is not retried.
            _report("weapon.give-commit-exception: " + error.GetType().Name);
            return new Outcome(recipient, CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode, 0);
        }
        var after = ReadSlot(recipient, PocketSlot);
        if (after == null) return Unknown(recipient, NoReadbackCode, 1);
        if (after.Value != itemId) return Unknown(recipient, StateChangedCode, 1);
        return Commit(recipient, 1);
    }
    /// <summary>Spends <paramref name="count"/> units of one pocket item through the game's own host removal
    /// entry point. The count is read before the first call and after every call, so the units a run actually
    /// removed are reported even when a later call finds nothing left.</summary>
    internal Outcome Consume(EntityReference recipient, uint itemId, int count)
    {
        if (!TryBackpack(recipient, out var backpack, out string refusal)) return Refuse(recipient, refusal);
        // The lookup proved both halves, so the owner is the player the reference resolved to.
        SNet_Player owner = backpack!.Owner!;
        int before;
        try { before = backpack!.CountPocketItem(itemId); }
        catch (Exception error) { return ReadFailure(recipient, error); }
        if (before <= 0) return Refuse(recipient, StateChangedCode);
        int spent = 0;
        for (int index = 0; index < count; index++)
        {
            if (!Authoritative()) return spent == 0 ? Refuse(recipient, AuthorityCode) : Unknown(recipient, AuthorityCode, spent);
            bool removed;
            try { removed = PlayerBackpackManager.TryMasterRemovePocketItemWithID(itemId, owner); }
            catch (Exception error)
            {
                _report("weapon.consume-commit-exception: " + error.GetType().Name);
                return new Outcome(recipient, CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode, spent);
            }
            if (!removed) break;
            spent++;
        }
        if (spent == 0) return Refuse(recipient, StateChangedCode);
        if (spent < count) return Unknown(recipient, StateChangedCode, spent);
        var after = ReadCount(recipient, itemId);
        if (after == null || after.Value != before - spent) return Unknown(recipient, NoReadbackCode, spent);
        return Commit(recipient, spent);
    }

    /// <summary>The recipient's own backpack, through the game's own lookup. The reference is asked for the native
    /// player it names, the game's own backpack lookup is asked for that player's backpack, and the backpack's
    /// owner has to resolve back to the very reference that was asked about. A name, a slot or an agent is never
    /// used to find a player: a reference this environment cannot name is refused, not guessed at.</summary>
    private bool TryBackpack(EntityReference recipient, out PlayerBackpack? backpack, out string code)
    {
        backpack = null;
        if (recipient == null || !IsKind(recipient, PlayerKind)) { code = TargetKindCode; return false; }
        if (!_kernel.IsEntityCurrent(recipient)) { code = StaleCode; return false; }
        object? context;
        try { context = _playerOf(recipient); }
        catch (Exception) { code = StaleCode; return false; }
        if (context is not SNet_Player player) { code = StaleCode; return false; }
        try
        {
            if (!PlayerBackpackManager.TryGetBackpack(player, out var found) || found == null)
            { code = NoBackpackCode; return false; }
            // The owner has to be the same player the reference resolved to; a backpack that answers for another
            // owner is a disagreement this action refuses to write through.
            var owner = found.Owner;
            if (owner == null || _kernel.ResolveEntityInstance(PlayerKind, (object)owner) != recipient)
            { code = StateChangedCode; return false; }
            backpack = found;
            code = "";
            return true;
        }
        catch (Exception) { code = StaleCode; return false; }
    }

    /// <summary>One slot's item id after a write, or null when the slot could not be read at all. An empty slot
    /// answers zero, which is a real answer: it is what "the item is no longer there" looks like.</summary>
    private uint? ReadSlot(EntityReference recipient, int slot)
    {
        if (!TryBackpack(recipient, out var backpack, out _)) return null;
        var slots = backpack!.Slots;
        if (slots == null || slot >= slots.Length) return null;
        var item = slots[slot];
        return item == null ? 0u : item.ItemID;
    }

    private int? ReadCount(EntityReference recipient, uint itemId)
    {
        if (!TryBackpack(recipient, out var backpack, out _)) return null;
        try { return backpack!.CountPocketItem(itemId); }
        catch (Exception) { return null; }
    }

    private Outcome ReadFailure(EntityReference recipient, Exception error)
    {
        _report("weapon.inventory-read-exception: " + error.GetType().Name);
        return new Outcome(recipient, CommandStatuses.Failed, CommitStates.Unknown, NoReadbackCode, 0);
    }

    private Outcome Commit(EntityReference recipient, int fact)
        => new(recipient, CommandStatuses.Succeeded, CommitStates.Confirmed, CommittedCode, fact);

    private Outcome Refuse(EntityReference recipient, string code)
        => new(recipient, CommandStatuses.Rejected, CommitStates.None, code, 0);

    private Outcome Unknown(EntityReference recipient, string code, int fact)
        => new(recipient, CommandStatuses.Failed, CommitStates.Unknown, code, fact);

    /// <summary>The one recipient kind this action answers for, spelled as the player domain spells it.</summary>
    internal const string PlayerKind = "gtfo.player";
    /// <summary>The one resource kind the `item` port carries; another kind on that port is not an item request.</summary>
    internal const string ItemResourceKind = "item";

    private static EntityReference[] Recipients(CommandContext context, string port)
        => context.Inputs.GetProperty(port).EnumerateArray().Select(RuntimeJson.Entity).ToArray();

    /// <summary>One line per recipient, in the plan's order and with no dedupe, for the `results` array the
    /// catalog's result rows declare. The row carries both quantity spellings the remaining actions use so one row
    /// type serves both; only the action's own column is ever set.</summary>
    internal static IReadOnlyList<Row> Rows(string action, IReadOnlyList<Outcome> outcomes)
    {
        var rows = new List<Row>(outcomes.Count);
        foreach (var outcome in outcomes)
            rows.Add(new Row(outcome.Reference, outcome.Status, outcome.CommitState, outcome.Code,
                Quantity: action == "give" ? outcome.Fact : null,
                Count: action == "consume" ? outcome.Fact : null,
                TargetCount: outcomes.Count));
        return rows;
    }

    /// <summary>The command-level conclusion of a run of recipients, by the same rule the other actions use:
    /// everything confirmed is a success, nothing confirmed is a rejection or an unknown failure, and anything
    /// in between is partial with the weaker commit state.</summary>
    internal static CommandResult Aggregate(string action, IReadOnlyList<Outcome> outcomes)
    {
        var outputs = RuntimeJson.From(new { results = Rows(action, outcomes) });
        int committed = 0, unknown = 0;
        foreach (var outcome in outcomes)
        {
            if (outcome.CommitState == CommitStates.Confirmed) committed++;
            else if (outcome.CommitState == CommitStates.Unknown) unknown++;
        }
        if (committed == outcomes.Count) return CommandResult.Succeeded(outputs);
        if (committed > 0) return CommandResult.Partial(outputs, unknown > 0 ? CommitStates.Unknown : CommitStates.Confirmed);
        if (unknown == 0)
            return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, Single(outcomes, action + "-all-rejected"), "", outputs);
        return CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, Single(outcomes, action + "-all-unknown"), "", outputs);
    }

    private static string Single(IReadOnlyList<Outcome> outcomes, string fallback)
    {
        if (outcomes.Count == 1) return outcomes[0].Code;
        var first = outcomes[0].Code;
        foreach (var outcome in outcomes) if (outcome.Code != first) return fallback;
        return first;
    }

    private static bool IsKind(EntityReference reference, string kind)
        => reference.Id != null && reference.Id.StartsWith(kind + ":", StringComparison.Ordinal);

    /// <summary>A value input that is either absent — the plan asked for the default — or a positive integer
    /// inside the bound. A non-numeric or out-of-range value is a refusal, never a clamp.</summary>
    private static bool TryInteger(JsonElement inputs, string port, int minimum, int maximum, out int value)
    {
        value = minimum;
        if (!inputs.TryGetProperty(port, out var element) || element.ValueKind == JsonValueKind.Null) return true;
        if (element.ValueKind != JsonValueKind.Number || !element.TryGetInt32(out int parsed)) return false;
        if (parsed < minimum || parsed > maximum) return false;
        value = parsed;
        return true;
    }
}
