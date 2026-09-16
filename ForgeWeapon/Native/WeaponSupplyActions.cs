using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using ForgeRuntime.Framework;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>The two ammunition rows of `WeaponSupplyContract`, submitted through the game's own ammunition gift
/// and storage bodies. The evidence for every member named here is `evidence/weapon-supply.json`; nothing in this
/// file computes an amount the game does not itself apply, and every write is read back before it is reported.
///
/// <b>Add</b> — `PlayerBackpackManager.GiveAmmoToPlayer(player, standardRel, specialRel, classRel)` is the body the
/// resource pack reaches, and it submits a `pAmmoGive` through the manager's own
/// `SNet_AuthorativeAction&lt;pAmmoGive&gt; m_giveAmmoPacket` (+0x20), whose receive half applies the three relative
/// amounts with `PlayerAmmoStorage.PickupAmmo`. The row's absolute round count is therefore converted against the
/// pool's own cap, read immediately before the call, and the bullets that really landed are read back from the
/// same pool afterwards — the result's `amount` is that readback, never the request.
///
/// <b>Consume</b> — the gift body's own `InventorySlotAmmo.AddAmmo` clamps only at the pool cap
/// (`Min(AmmoInPack + amount, AmmoMaxCap)`), so it cannot subtract safely; the body that clamps both ends is
/// `PlayerAmmoStorage.UpdateBulletsInPack(AmmoType, int)`, the one the resource pack itself calls on a backpack
/// this machine owns. A recipient whose pool is another machine's — a remote client's mirrored backpack, which its
/// owner syncs over — is refused by name rather than written through a copy that would be overwritten.
///
/// The recipient is resolved the way this package resolves every player: the reference is asked for the native
/// `SNet_Player` through the injected lookup, the game's own `TryGetBackpack` answers the backpack, and the
/// backpack's owner has to resolve back to the very reference that was asked about. A name, a slot or an agent is
/// never used to find a player.</summary>
internal sealed class WeaponSupplyAdapter
{
    /// <summary>What the runtime must answer for one `gtfo.player` reference: the native player it names, or null
    /// when this environment cannot name one. The signature is the same one the inventory actions take, so the
    /// integration forwards one lookup to both.</summary>
    internal delegate object? PlayerLookup(EntityReference reference);

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly PlayerLookup _playerOf;
    private readonly Action<string> _report;

    internal WeaponSupplyAdapter(RuntimeKernel kernel, Func<bool> canExecute, PlayerLookup playerOf, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _playerOf = playerOf ?? throw new ArgumentNullException(nameof(playerOf));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Whether this side may submit an ammunition write at all: the game's own gift is an authoritative
    /// action, and the storage write is a local one, so both are host work. The gate is the same one the inventory
    /// writes use — the host's readiness and the game's master flag.</summary>
    internal bool Authoritative()
    {
        if (!_canExecute() || !SNet.IsMaster) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    /// <summary>The result codes. Each names one refusal or one uncertainty; `ammo-committed` is the only code that
    /// says the pool read back the change.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    internal const string RecipientCode = "ammo-recipient-unknown";
    internal const string AmountCode = "amount-out-of-range";
    internal const string TypeCode = "ammo-type-unknown";
    internal const string PolicyCode = "ammo-policy-unknown";
    internal const string CapCode = "ammo-cap-unsupported";
    internal const string ProvenanceCode = "ammo-provenance-unsupported";
    internal const string DiscardCode = "ammo-overflow-discard-unsupported";
    internal const string GiftPoolCode = "ammo-pool-has-no-gift";
    internal const string PoolCode = "ammo-pool-unavailable";
    internal const string NotOwnedCode = "ammo-pool-not-owned-here";
    internal const string OverflowCode = "ammo-pool-overflow";
    internal const string InsufficientCode = "ammo-pool-insufficient";
    internal const string EmptyCode = "ammo-pool-empty";
    internal const string ReadbackCode = "ammo-readback-exception";
    internal const string UnobservedCode = "ammo-change-unobserved";
    internal const string CommitExceptionCode = "native-commit-exception";
    internal const string CommittedCode = "ammo-committed";

    /// <summary>What one recipient's submission did. `Reference` is the reference the row was asked about, so a
    /// caller can only ever publish the identity its own kind handed back.</summary>
    internal sealed record Outcome(EntityReference Reference, string Status, string CommitState, string Code, int Amount);

    internal CommandResult HandleAdd(CommandContext context) => HandleAdd(this, context);
    internal CommandResult HandleConsume(CommandContext context) => HandleConsume(this, context);

    /// <summary>The `forge.action.weapon.ammo_add` handler: one recipient, one pool, one round count, and the two
    /// structural parameters the catalog declares. Everything the native path cannot carry is refused before the
    /// first native call, and the overflow policy is applied against the pool's own cap read for the decision.</summary>
    internal static CommandResult HandleAdd(WeaponSupplyAdapter adapter, CommandContext context)
    {
        if (!adapter.Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (!Recipient(context, out var recipient, out string code)) return CommandResult.Rejected(code);
        if (Present(context.Inputs, "cap")) return CommandResult.Rejected(CapCode);
        if (Present(context.Inputs, "provenance")) return CommandResult.Rejected(ProvenanceCode);
        if (!TryAmount(context.Inputs, out int amount)) return CommandResult.Rejected(AmountCode);
        if (!TryAmmoType(context.Parameters, out AmmoType type, out code)) return CommandResult.Rejected(code);
        if (!TryPolicy(context.Parameters, WeaponSupplyContract.OverflowPolicies, "overflow_policy", out string policy, out code))
            return CommandResult.Rejected(code);
        // A policy this row cannot honour is refused by name: the native gift clamps at the pool's cap, and a
        // "discard" that silently clamped would be a different action under the same name.
        if (policy == "discard") return CommandResult.Rejected(DiscardCode);
        var outcome = adapter.Add(recipient, type, amount, policy == "reject");
        return Aggregate(outcome);
    }

    /// <summary>The `forge.action.weapon.ammo_consume` handler: the same single-recipient shape, minus the overflow
    /// policy and plus the failure policy the catalog declares. A rejection keeps the pool untouched; `partial`
    /// spends what the pool holds and reports the number.</summary>
    internal static CommandResult HandleConsume(WeaponSupplyAdapter adapter, CommandContext context)
    {
        if (!adapter.Authoritative()) return CommandResult.Rejected(AuthorityCode);
        if (!Recipient(context, out var recipient, out string code)) return CommandResult.Rejected(code);
        if (!TryAmount(context.Inputs, out int amount)) return CommandResult.Rejected(AmountCode);
        if (!TryAmmoType(context.Parameters, out AmmoType type, out code)) return CommandResult.Rejected(code);
        if (!TryPolicy(context.Parameters, WeaponSupplyContract.FailurePolicies, "failure_policy", out string policy, out code))
            return CommandResult.Rejected(code);
        var outcome = adapter.Consume(recipient, type, amount, policy == "partial");
        return Aggregate(outcome);
    }

    /// <summary>Adds <paramref name="amount"/> rounds to one pool through the game's own gift. The pool's cap and
    /// its current bullets are read for this request; the gift carries the same amount as a fraction of that cap,
    /// and the bullets that really landed are the difference the pool reports afterwards.</summary>
    internal Outcome Add(EntityReference recipient, AmmoType type, int amount, bool rejectOverflow)
    {
        // The gift carries exactly three pools; a request for one of the other three is refused by name instead of
        // submitting a gift that can only move zero bullets.
        if (!GiftPools.Contains(type)) return Refuse(recipient, GiftPoolCode);
        if (!TryPool(recipient, out var player, out _, out string refusal)) return Refuse(recipient, refusal);
        if (!TryRead(player, type, out int before, out int cap)) return Refuse(recipient, ReadbackCode);
        if (rejectOverflow && before + amount > cap) return Refuse(recipient, OverflowCode);
        // The relative amount is the request against the pool's own cap, which is the unit the game's gift carries
        // (pAmmoGive.ammo*Rel × the pool's capacity reaches PlayerAmmoStorage.PickupAmmo).
        float relative = (float)amount / cap;
        if (!float.IsFinite(relative) || relative <= 0f) return Refuse(recipient, AmountCode);
        if (!Authoritative()) return Refuse(recipient, AuthorityCode);
        try
        {
            PlayerBackpackManager.GiveAmmoToPlayer(player, Standard(type, relative), Special(type, relative), Class(type, relative));
        }
        catch (Exception error)
        {
            // The gift is an authoritative action, so whether it reached the player is not observable from here; the
            // commit stays unknown and is never retried.
            _report("weapon.ammo-add-commit-exception: " + error.GetType().Name);
            return new Outcome(recipient, CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode, 0);
        }
        if (!TryRead(player, type, out int after, out _)) return Unknown(recipient, ReadbackCode, 0);
        int landed = after - before;
        if (landed <= 0) return Unknown(recipient, UnobservedCode, 0);
        return Commit(recipient, landed);
    }

    /// <summary>Spends <paramref name="amount"/> rounds out of one pool through the storage body that clamps into
    /// the pool's own bounds. The pool has to belong to this machine: a remote client owns and syncs its own
    /// ammunition, so a write to the host's mirrored copy is refused rather than silently reverted.</summary>
    internal Outcome Consume(EntityReference recipient, AmmoType type, int amount, bool partial)
    {
        if (!TryPool(recipient, out var player, out var storage, out string refusal)) return Refuse(recipient, refusal);
        if (!OwnedHere(player)) return Refuse(recipient, NotOwnedCode);
        if (!TryRead(player, type, out int before, out _)) return Refuse(recipient, ReadbackCode);
        if (before <= 0) return Refuse(recipient, EmptyCode);
        if (!partial && before < amount) return Refuse(recipient, InsufficientCode);
        int spend = Math.Min(amount, before);
        if (!Authoritative()) return Refuse(recipient, AuthorityCode);
        try { storage.UpdateBulletsInPack(type, -spend); }
        catch (Exception error)
        {
            _report("weapon.ammo-consume-commit-exception: " + error.GetType().Name);
            return new Outcome(recipient, CommandStatuses.Failed, CommitStates.Unknown, CommitExceptionCode, 0);
        }
        if (!TryRead(player, type, out int after, out _)) return Unknown(recipient, ReadbackCode, spend);
        int landed = before - after;
        if (landed != spend) return Unknown(recipient, ReadbackCode, landed > 0 ? landed : 0);
        return Commit(recipient, landed);
    }

    /// <summary>The player and ammunition storage behind a recipient reference. The chain is the game's own and the
    /// identity is checked in both directions, exactly as the inventory writes check theirs.</summary>
    private bool TryPool(EntityReference recipient, out SNet_Player player, out PlayerAmmoStorage storage, out string code)
    {
        player = null!;
        storage = null!;
        if (recipient == null || !IsKind(recipient, PlayerKind)) { code = RecipientCode; return false; }
        if (!_kernel.IsEntityCurrent(recipient)) { code = RecipientCode; return false; }
        object? resolved;
        try { resolved = _playerOf(recipient); }
        catch (Exception) { code = RecipientCode; return false; }
        if (resolved is not SNet_Player named) { code = RecipientCode; return false; }
        try
        {
            if (!PlayerBackpackManager.TryGetBackpack(named, out var backpack) || backpack == null)
            { code = PoolCode; return false; }
            var owner = backpack.Owner;
            if (owner == null || _kernel.ResolveEntityInstance(PlayerKind, (object)owner) != recipient)
            { code = RecipientCode; return false; }
            var found = backpack.AmmoStorage;
            if (found == null || found.Pointer == IntPtr.Zero) { code = PoolCode; return false; }
            player = owner;
            storage = found;
            code = "";
            return true;
        }
        catch (Exception) { code = PoolCode; return false; }
    }

    /// <summary>The pool's own counts, read through the manager's player-keyed wrappers so a pool is always read
    /// for the player the row named rather than for this machine's own backpack.</summary>
    private static bool TryRead(SNet_Player player, AmmoType type, out int bullets, out int cap)
    {
        bullets = 0;
        cap = 0;
        try
        {
            bullets = PlayerBackpackManager.GetBulletsInPack(type, player);
            cap = PlayerBackpackManager.GetAmmoMaxCap(type, player);
        }
        catch (Exception) { return false; }
        return bullets >= 0 && cap > 0 && bullets <= cap;
    }

    /// <summary>Whether the pool belongs to this machine: its own player's pool, or a bot's, which the host drives.
    /// A remote client's pool is the client's own and is never written through the host's mirrored copy.</summary>
    private static bool OwnedHere(SNet_Player player)
    {
        try { return player.IsLocal || player.IsBot; }
        catch (Exception) { return false; }
    }

    /// <summary>The one pool the row selected, as the game's gift spells its three amounts.</summary>
    private static float Standard(AmmoType type, float relative) => type == AmmoType.Standard ? relative : 0f;
    private static float Special(AmmoType type, float relative) => type == AmmoType.Special ? relative : 0f;
    private static float Class(AmmoType type, float relative) => type == AmmoType.Class ? relative : 0f;

    /// <summary>The three pools `pAmmoGive` carries a field for. The other three members of the native enum are
    /// real pools the storage reads, but the game's own gift has no amount field for them.</summary>
    internal static readonly HashSet<AmmoType> GiftPools = new() { AmmoType.Standard, AmmoType.Special, AmmoType.Class };

    private Outcome Commit(EntityReference recipient, int amount)
        => new(recipient, CommandStatuses.Succeeded, CommitStates.Confirmed, CommittedCode, amount);

    private Outcome Refuse(EntityReference recipient, string code)
        => new(recipient, CommandStatuses.Rejected, CommitStates.None, code, 0);

    private Outcome Unknown(EntityReference recipient, string code, int amount)
        => new(recipient, CommandStatuses.Failed, CommitStates.Unknown, code, amount);

    /// <summary>The one recipient kind these rows answer for, spelled as the player domain spells it.</summary>
    internal const string PlayerKind = "gtfo.player";

    private static bool IsKind(EntityReference reference, string kind)
        => reference.Id != null && reference.Id.StartsWith(kind + ":", StringComparison.Ordinal);

    /// <summary>One result line, in the catalog's own field order and with the catalog's own field names.</summary>
    internal sealed record Row(EntityReference Target, string Status, string Committed, string Code,
        [property: JsonPropertyName("amount")] int Amount);

    /// <summary>The one result row a single-recipient write answers with, and the aggregation the command's own
    /// status is decided by: the recipient's own outcome decides, because there is only ever one of them.</summary>
    internal static CommandResult Aggregate(Outcome outcome)
    {
        var outputs = RuntimeJson.From(new { results = new[] { new Row(outcome.Reference, outcome.Status, outcome.CommitState, outcome.Code, outcome.Amount) } });
        return outcome.CommitState switch
        {
            CommitStates.Confirmed => CommandResult.Succeeded(outputs),
            CommitStates.Unknown => CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, outcome.Code, "", outputs),
            _ => CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, outcome.Code, "", outputs)
        };
    }

    /// <summary>The row's single recipient. The port is declared `one`, so a payload that carries an array is a
    /// plan the row was not written for and is refused by name.</summary>
    private static bool Recipient(CommandContext context, out EntityReference reference, out string code)
    {
        reference = null!;
        code = RecipientCode;
        if (!context.Inputs.TryGetProperty("player", out var value) || value.ValueKind != JsonValueKind.Object) return false;
        reference = RuntimeJson.Entity(value);
        return true;
    }

    /// <summary>The requested round count: a positive integer inside the byte-sized bound a pool can hold. A value
    /// outside it is a refusal, never a clamp.</summary>
    private static bool TryAmount(JsonElement inputs, out int amount)
    {
        amount = 0;
        if (!inputs.TryGetProperty("amount", out var value) || value.ValueKind != JsonValueKind.Number) return false;
        if (!value.TryGetInt32(out int parsed) || parsed < 1 || parsed > MaximumAmount) return false;
        amount = parsed;
        return true;
    }

    /// <summary>The largest round count one request may carry. Every pool in this build is a few hundred rounds at
    /// most, and a request beyond this is not a count any pool could answer.</summary>
    internal const int MaximumAmount = 9999;

    /// <summary>The `ammo_type` parameter as the native enum. The catalog's member order and the native
    /// `Player.AmmoType` declaration order are the same six members, so the index is the native value and there is
    /// no second mapping table to drift.</summary>
    private static bool TryAmmoType(JsonElement parameters, out AmmoType type, out string code)
    {
        type = AmmoType.None;
        code = TypeCode;
        string? name = Text(parameters, "ammo_type");
        if (name == null) return false;
        for (int index = 0; index < WeaponSupplyContract.AmmoTypes.Count; index++)
        {
            if (!string.Equals(WeaponSupplyContract.AmmoTypes[index], name, StringComparison.Ordinal)) continue;
            type = (AmmoType)index;
            code = "";
            return true;
        }
        return false;
    }

    /// <summary>One structural policy's member, checked against the row's own declared list.</summary>
    private static bool TryPolicy(JsonElement parameters, IReadOnlyList<string> allowed, string port, out string policy, out string code)
    {
        policy = "";
        code = PolicyCode;
        string? name = Text(parameters, port);
        if (name == null) return false;
        foreach (string member in allowed) if (string.Equals(member, name, StringComparison.Ordinal)) { policy = name; code = ""; return true; }
        return false;
    }

    /// <summary>A structural parameter's text, or null when the node's constant bag does not carry it.</summary>
    private static string? Text(JsonElement bag, string name)
        => bag.ValueKind == JsonValueKind.Object && bag.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>An input the plan actually supplied: absent or a present null is the same "not asked for".</summary>
    private static bool Present(JsonElement inputs, string port)
        => inputs.TryGetProperty(port, out var value) && value.ValueKind != JsonValueKind.Null
           && value.ValueKind != JsonValueKind.Undefined;
}
