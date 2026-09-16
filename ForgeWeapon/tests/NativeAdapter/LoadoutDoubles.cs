using ForgeWeapon.Native;
using ForgeWeapon.Tests.LoadoutPolicy;
using Gear;
using Player;

namespace ForgeWeapon.Tests.NativeAdapter;

/// <summary>One loadout case's whole native world: the gear pool the game owns, the rundown this process loaded and
/// whether it is inside an expedition. The three are process-wide statics in the game, so a case builds its own and
/// <see cref="Reset"/> puts them back before the next one starts.</summary>
internal sealed class LoadoutWorld
{
    /// <summary>The ids the fixture's policy allows, one set per covered slot: the first entry is what a saved
    /// favorite that no longer matches falls back to.</summary>
    internal static readonly uint[] PolicyStandard = { 10001, 10002, 10003, 10004 };
    internal static readonly uint[] PolicySpecial = { 10005, 10006, 10007, 10008, 10009 };
    /// <summary>The tool slot's allow-list is the vanilla tools and the craftable ones together — the four sentries,
    /// the mine deployer, the C-foam launcher and the bio tracker, then the workshop tools — which is what the
    /// "tools and custom ids coexist under one allow-list" case is asserted on. A policy lists a slot's entries by
    /// ascending block id, so this is the order the fixture's own canonical file carries too.</summary>
    internal static readonly uint[] PolicyClass = { 6, 17, 18, 20, 23, 24, 25, 20001, 20002, 20003, 20004, 20005, 20006 };

    /// <summary>The game's own pool for one slot before any policy narrows it: the policy's gears in the game's own
    /// order, preceded by the vanilla rows the policy does not name. Standard holds the two records a favorites file
    /// on this machine actually stores; the tool slot holds a vanilla tool the policy leaves out.</summary>
    internal static readonly uint[] OfferedStandard = { 31, 34, 10001, 10002, 10003, 10004 };
    internal static readonly uint[] OfferedSpecial = { 33, 10005, 10006, 10007, 10008, 10009 };
    internal static readonly uint[] OfferedClass = { 2, 6, 17, 18, 20, 23, 24, 25, 20001, 20002, 20003, 20004, 20005, 20006 };

    private LoadoutWorld(string root, LoadoutPolicySnapshot policies)
    {
        Root = root;
        Policies = policies;
        // The manager is published before its pool is built: the pool lists are made by the same case that reads
        // them through it, exactly as the game's own `GearManager.Current` is already set when it builds its own.
        Manager = new GearManager();
        GearManager.Current = Manager;
        Manager.m_gearPerSlot = new GearPoolList?[12]
        {
            null, Pool(OfferedStandard), Pool(OfferedSpecial), Pool(OfferedClass),
            null, null, null, null, null, null, null, null
        };
        Globals.Global.RundownIdToLoad = PolicyInstall.Rundown;
        GameStateManager.IsInExpedition = false;
    }

    /// <summary>The accepted policy this world's pool is narrowed by, read from a real fixture file by the
    /// production loader: the projection is exercised against an accepted snapshot, never a hand-built one. The
    /// canonical text is the suite's own writer's, and a case that wants another policy passes its own text.</summary>
    internal static LoadoutWorld Policy(string? canonical = null, bool writeFile = true)
    {
        var install = new PolicyInstall();
        if (writeFile) install.WritePolicy("Pack", canonical ?? install.Canonical(
            standard: PolicyStandard, special: PolicySpecial, gearClass: PolicyClass));
        var snapshot = install.LoadWritten(out var rejected);
        if (rejected.Count != 0) throw new InvalidOperationException("The fixture policy was not accepted: " + string.Join(" | ", rejected));
        return new LoadoutWorld(install.Root, snapshot);
    }

    /// <summary>A world whose install has no policy at all: the game's own pool and nothing else, which is what the
    /// "rundown mismatch" and "no policy" cases answer with.</summary>
    internal static LoadoutWorld WithoutPolicy() => Policy(writeFile: false);

    internal string Root { get; }
    internal LoadoutPolicySnapshot Policies { get; }
    internal GearManager Manager { get; }
    internal readonly List<string> Reports = new(), Infos = new();

    internal GearPoolList Pool(uint slot) => Manager.m_gearPerSlot[slot]!;
    internal uint[] Blocks(uint slot) => Pool(slot).BlockIds();
    internal GearPoolList Pool(InventorySlot slot) => Pool((uint)slot);
    internal uint[] Blocks(InventorySlot slot) => Pool((uint)slot).BlockIds();

    /// <summary>The offline block id one offered gear carries, as a policy's allow-list names it.</summary>
    internal static uint Block(GearIDRange gear) => GearPoolList.Block(gear);

    /// <summary>The same block id, or zero for a gear that carries no record text at all — a workshop gear, whose
    /// identity is its category component instead.</summary>
    internal static uint BlockOrZero(GearIDRange gear) => GearPoolList.BlockOrZero(gear);

    /// <summary>Takes one gear out of the game's own pool, which is what an offer the policy cannot be projected
    /// onto looks like: the game built its pool without that row.</summary>
    internal void Drop(InventorySlot slot, uint blockId)
    {
        var live = Pool(slot);
        var items = live.Items().Where(gear => Block(gear) != blockId).ToArray();
        live.Clear();
        foreach (var gear in items) live.Add(gear);
    }

    /// <summary>The pool the loading hook reads: the game's own manager and the slot source every access of the
    /// narrowing goes through. Both are handed to the session exactly as production hands them. The interop wrapper
    /// is the one the game's own session builds, so the list the narrowing rewrites is the fixture's own instance.</summary>
    internal GearPoolSlotSource Live() => slot => slot < Manager.m_gearPerSlot.Length && Manager.m_gearPerSlot[slot] != null
        ? new Il2CppGearPoolSlot(Manager.m_gearPerSlot[slot]!)
        : null;

    /// <summary>The session under test, wired to this world's own report lines.</summary>
    internal GearLoadoutSession Session() => new(Policies, Reports.Add, Infos.Add);

    /// <summary>The game's own fallback after `RescanFavorites`: the gear a `LastEquipped_*` name selects is matched
    /// against the pool's own record texts and, when nothing matches, the slot falls back to the pool's first entry.
    /// This is the one behaviour the pool narrowing exists to use, so the fixture models the two steps rather than
    /// asserting on the pool alone.</summary>
    internal GearIDRange Equipped(InventorySlot slot, string savedName)
    {
        var pool = Pool(slot).Items();
        return pool.FirstOrDefault(gear => gear.PlayfabItemInstanceId == savedName) ?? pool[0];
    }

    /// <summary>One gear as the game builds it from an offline row: the record text `LoadOfflineGearDatas` writes,
    /// and no category component — the game leaves that to the block's own category.</summary>
    internal static GearIDRange Offline(uint blockId)
    {
        var gear = GearIDRange.Create();
        gear.PlayfabItemInstanceId = "OfflineGear_ID_" + blockId.ToString(System.Globalization.CultureInfo.InvariantCulture);
        return gear;
    }

    /// <summary>One workshop gear as the website writes it: its own block id in the category component, which is the
    /// identity a copied gear carries wherever it goes.</summary>
    internal static GearIDRange Workshop(uint blockId)
    {
        var gear = GearIDRange.Create();
        gear.Components[eGearComponent.Category] = blockId;
        return gear;
    }

    internal static GearIDRange[] Offer(params uint[] blockIds) => Array.ConvertAll(blockIds, Offline);

    private static GearPoolList Pool(uint[] blockIds) => GearPoolList.Create(Offer(blockIds));

    /// <summary>The process-wide game state a case inherits: the pool, the loaded rundown and the expedition gate.
    /// A case calls this before it builds its world, so it starts from the same state every time.</summary>
    internal static void Reset()
    {
        GearManager.Current = null;
        Globals.Global.RundownIdToLoad = 0;
        GameStateManager.IsInExpedition = false;
    }
}
