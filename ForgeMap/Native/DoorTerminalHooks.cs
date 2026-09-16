using HarmonyLib;
using LevelGeneration;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>
/// The Harmony patches behind the door and terminal interaction rows. Each one only chooses when to read; the
/// derivation is in `DoorTerminalDerivations` and the publication in `DoorTerminalPublisher`, so a patch holds
/// no rule of its own and a test can drive the same body without a patch.
///
/// The patches are declared here rather than added to `MapObjectHooks.cs` because that file,
/// `MapNativeHooks.cs` and `MapPluginSession.cs` are shared registration points this slice must not edit; the
/// integration batch adds the types below to `MapNativeHooks.Types` from `integration.json`.
///
/// `TerminalCommandReadbackFixed` replaces a hook that could never have fired: `MapObjectHooks.cs` declares its
/// postfix as `(uint terminalID, TERM_Command command)` while the native member is
/// `static void LG_ComputerTerminalManager::WantToSendTerminalCommand(uint, TERM_Command, string, string, string)`
/// (`Modules-ASM.dll`, token 100686913). Harmony binds a postfix by signature, so the two-parameter postfix was
/// never invoked and no terminal command row has ever published. The patch below carries the whole native
/// signature, which is also how `e-term-log` and the custom-command slot are reached: both live in the
/// parameters this entry carries and in the command value itself.
/// </summary>
/// <summary>The two stages of a weak door, read from the game's own replication callbacks on the door. They are
/// the sync layer's notifications — `LG_WeakDoor.OnSyncDoorGotDamage` while the door is being hit and
/// `OnSyncDoorGotDestroyed` when it breaks — so the host that computed the damage and every client that applies
/// it both run them, and the publisher's authority gate is what keeps the fact host-only. Both carry the
/// `SNet_Player` that caused the damage, which is the row's `attacker` port; a hit with no player behind it
/// publishes no attacker rather than the last one that touched the door.</summary>
[HarmonyPatch(typeof(LG_WeakDoor), nameof(LG_WeakDoor.OnSyncDoorGotDamage))]
internal static class WeakDoorAttackedReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_WeakDoor __instance, SNet_Player instigatorPlayer)
    {
        if (!SNet.IsMaster) return;
        DoorTerminalFacts.Current?.Guard(facts => facts.WeakDoorAttacked(__instance, instigatorPlayer));
    }
}

[HarmonyPatch(typeof(LG_WeakDoor), nameof(LG_WeakDoor.OnSyncDoorGotDestroyed))]
internal static class WeakDoorBrokenReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_WeakDoor __instance, SNet_Player instigatorPlayer)
    {
        if (!SNet.IsMaster) return;
        DoorTerminalFacts.Current?.Guard(facts => facts.WeakDoorBroken(__instance, instigatorPlayer));
    }
}

[HarmonyPatch(typeof(LG_ComputerTerminalManager), nameof(LG_ComputerTerminalManager.WantToSendTerminalCommand))]
internal static class TerminalCommandReadbackFixed
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(uint terminalID, TERM_Command command, string inputString, string param1, string param2)
    {
        if (!SNet.IsMaster) return;
        DoorTerminalFacts.Current?.Guard(facts =>
            facts.TerminalCommandEntry(terminalID, (int)command, inputString, param1));
    }
}

/// <summary>The weak lock's own replicated state, which is the only place a smashed or hacked lock is visible.
/// The lock has no address of its own, so the fact is published against the door the lock belongs to; a weak
/// lock whose door cannot be addressed publishes nothing rather than a lock with an invented owner.</summary>
[HarmonyPatch(typeof(LG_WeakLock), nameof(LG_WeakLock.OnStateChange))]
internal static class WeakLockBrokenReadback
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(LG_WeakLock __instance)
    {
        if (!SNet.IsMaster) return;
        DoorTerminalFacts.Current?.Guard(facts => facts.WeakLockChanged(__instance));
    }
}
