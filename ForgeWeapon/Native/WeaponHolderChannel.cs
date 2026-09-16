using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using Gear;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>
/// The native half of the holder-client command channel: who holds an equipment instance, and the two writes the
/// holder's own machine performs.
///
/// The two writes are the game's own entry points and nothing else. A reload is
/// <see cref="PlayerInventoryBase.TriggerReload"/>, the same body the player's own reload key reaches, so a
/// Forge-requested reload is indistinguishable from a pressed one and the clip transfer, the animation and the
/// replication are the game's. A clip write is <see cref="BulletWeapon.SetCurrentClip"/>, the setter the game's
/// own reload and pickup paths use, capped against the weapon's own <c>GetMaxClip</c> rather than a number this
/// package invents.
///
/// Both bodies run on the machine that owns the inventory, which is exactly why the rows declare the `owner`
/// tier: the host sends the intent, the holder executes it here, and the game's replication carries the result.
/// The session resolver answers the host's question "which player session holds this instance"; the local checks
/// answer the holder's question "is this request mine". Neither one guesses an answer it does not have, and the
/// equipment comparison is the package's own identity table rather than a pointer, so a request can only ever
/// reach the instance it named.
/// </summary>
internal static class WeaponHolderChannel
{
    /// <summary>The live adapter, wired by <see cref="WeaponNativeSession.Start"/>. Null before the session is
    /// built, which is what keeps a resolver or a handler from running over a half-built package.</summary>
    internal static EquipmentNativeAdapter? Adapter { get; set; }

    /// <summary>The kernel the session resolvers read player references through, wired with the adapter.</summary>
    internal static RuntimeKernel? Kernel { get; set; }

    /// <summary>Whether this machine is inside a running game this package may write to. It is the same gate the
    /// package's other writers use, handed in by the session rather than read from the kernel here.</summary>
    internal static Func<bool> CanExecute { get; set; } = () => false;

    /// <summary>The address of the player sitting at this machine, or the empty text when this machine has no local
    /// player. A request naming any other address is not this side's work, and an empty address never matches a
    /// request that named somebody.</summary>
    internal static Func<string> LocalSession { get; set; } = () => "";

    /// <summary>The player kind `gtfo.player` references are resolved through, supplied by the SDK so this file
    /// names no domain it does not own.</summary>
    internal const string PlayerKind = EquipmentNativeAdapter.PlayerKind;

    /// <summary>
    /// Which player session one equipment instance belongs to: the answer the host's `owner` dispatch needs.
    ///
    /// The reference is resolved against this package's own two tables. A deployed world instance carries its
    /// live native item in the placement table, and that item's own owner chain (item to agent to player slot to
    /// SNet lookup) is the game's answer, not a cached one. Anything else — an equipment life, which is the
    /// backpack slot a plan addresses with `equipment` and not this route's subject — answers null, because the
    /// holder's own machine is told apart by the second half of <see cref="IsLocalHolder"/> rather than by a
    /// second player table kept here.
    ///
    /// Null is a real answer (a recalled or destroyed placement, a bot, a machine with no player) and the tier
    /// turns it into a named refusal rather than sending the step somewhere.
    /// </summary>
    internal static string? SessionOf(EntityReference equipment)
    {
        var adapter = Adapter;
        if (adapter == null || equipment == null || !CanExecute()) return null;
        return SessionOf(adapter.PlacedItemOf(equipment));
    }

    /// <summary>The same answer for a native object the caller already holds.</summary>
    internal static string? SessionOf(Item? item)
        => SessionOf(item == null ? null : item.Owner);

    /// <summary>The address one native agent is reached at: the game's own player slot. It is the number the game
    /// itself addresses a player by, and it is deliberately not the account id `Lookup` carries — that value is an
    /// identity a machine keeps to itself, while an owner request has to name the machine that holds the item. A
    /// player the game has no slot for has no address and is answered with null.</summary>
    internal static string? SessionOf(PlayerAgent? agent)
    {
        if (agent == null) return null;
        var owner = agent.Owner;
        if (owner == null) return null;
        try
        {
            int slot = owner.PlayerSlotIndex();
            return slot < 0 ? null : slot.ToString();
        }
        catch (Exception) { return null; }
    }

    /// <summary>The local player's own agent, read from the session layer rather than from a Forge reference:
    /// which player a machine is, is the one fact the session layer owns, and asking it keeps this file from
    /// keeping a second table of players. The session layer hands its agent back through an interface, so the
    /// concrete agent is cast the same way the equipment adapter casts its own.</summary>
    private static PlayerAgent? LocalAgent()
        => SNet.HasLocalPlayer && SNet.LocalPlayer != null && SNet.LocalPlayer.HasPlayerAgent
            ? SNet.LocalPlayer.PlayerAgent?.TryCast<PlayerAgent>() : null;

    /// <summary>The local player's own inventory, or null when this machine has no local player. Every write in
    /// this file goes through it, so a machine that is not the holder writes nothing.</summary>
    private static PlayerInventoryBase? LocalInventory()
    {
        var agent = LocalAgent();
        return agent == null ? null : agent.Inventory;
    }

    /// <summary>Whether one request is this machine's to execute. The addressed address has to be this machine's
    /// own local player, and the equipment has to belong to that same local player here; a request for a client's
    /// weapon arriving on the host — or the reverse — is refused instead of executed against the wrong inventory,
    /// which is the one failure this whole channel exists to prevent.</summary>
    internal static bool IsLocalHolder(EntityReference equipment)
    {
        if (equipment == null) return false;
        var local = LocalSession();
        if (local.Length == 0) return false;
        if (!string.Equals(SessionOf(LocalAgent()), local, StringComparison.Ordinal)) return false;
        var equipmentSession = SessionOf(equipment);
        // An equipment life has no session of its own here; it is this machine's when it resolves to the local
        // player's own inventory, which the write itself re-checks against the instance identity table.
        return equipmentSession == null || string.Equals(equipmentSession, local, StringComparison.Ordinal);
    }

    /// <summary>
    /// The reload write: the game's own reload entry point on this machine's inventory, reached only when the
    /// instance the request names is the item this inventory is holding and the inventory's own check accepts a
    /// reload. The clip is read back after the body ran, so the answer reports what the game did rather than what
    /// the plan asked for.
    /// </summary>
    internal static bool TryReload(EntityReference equipment, out int clip, out string code)
    {
        clip = 0;
        code = WeaponHolderChannelContract.ReloadRefusedCode;
        var inventory = LocalInventory();
        if (inventory == null) return false;
        var weapon = Wielded(inventory);
        if (weapon == null || !Matches(weapon, equipment)) return false;
        if (!inventory.CanReloadCurrent()) return false;
        inventory.TriggerReload();
        clip = weapon.TryCast<BulletWeapon>()?.GetCurrentClip() ?? 0;
        code = "";
        return true;
    }

    /// <summary>
    /// The clip write: the weapon's own setter, on the instance the request names, with the cap the weapon itself
    /// reports. A count above that cap is refused instead of clamped, because a plan that asked for more bullets
    /// than the magazine holds asked for a state the weapon cannot be in; `fill` asks for the cap itself and needs
    /// no amount.
    /// </summary>
    internal static bool TrySetClip(EntityReference equipment, string policy, bool hasAmount, int amount,
        out int clip, out string code)
    {
        clip = 0;
        code = WeaponHolderChannelContract.StaleEquipmentCode;
        var inventory = LocalInventory();
        if (inventory == null) return false;
        var item = Wielded(inventory);
        if (item == null || !Matches(item, equipment)) return false;
        var weapon = item.TryCast<BulletWeapon>();
        if (weapon == null) { code = WeaponHolderChannelContract.UnsupportedPolicyCode; return false; }
        var capacity = weapon.GetMaxClip();
        int wanted;
        switch (policy)
        {
            case "set":
                if (!hasAmount) { code = WeaponHolderChannelContract.UnsupportedPolicyCode; return false; }
                wanted = amount;
                break;
            case "fill":
                wanted = capacity;
                break;
            default:
                code = WeaponHolderChannelContract.UnsupportedPolicyCode;
                return false;
        }
        if (wanted < 0) { code = WeaponHolderChannelContract.UnsupportedPolicyCode; return false; }
        if (wanted > capacity) { code = WeaponHolderChannelContract.ClipOverCapacityCode; return false; }
        weapon.SetCurrentClip(wanted);
        clip = weapon.GetCurrentClip();
        code = "";
        return true;
    }

    /// <summary>The item this inventory is holding, or null when nothing is wielded. Read through the inventory's
    /// own property rather than a scroll slot, so a request can never reach a weapon the holder does not have in
    /// hand.</summary>
    private static Item? Wielded(PlayerInventoryBase inventory)
    {
        var item = inventory.WieldedItem;
        return item == null ? null : item.TryCast<Item>();
    }

    /// <summary>Whether a native item is the equipment instance a request named. The comparison is the identity
    /// table's, not the pointer's: the reference was minted for one equipment life, and the item in hand has to be
    /// the instance that life currently points at.</summary>
    private static bool Matches(Item item, EntityReference equipment)
    {
        var adapter = Adapter;
        if (adapter == null) return false;
        var current = adapter.EntityOf(item);
        return current != null && string.Equals(current.Id, equipment.Id, StringComparison.Ordinal);
    }
}
