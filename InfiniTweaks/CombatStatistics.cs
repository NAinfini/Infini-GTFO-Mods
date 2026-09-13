using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using GameData;
using Gear;
using GTFO.API;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;

namespace InfiniTweaks;

// Native game events feed one source-owned model. No embedded or loaded StatDisplay assembly.
internal static class CombatStatistics
{
    internal static ConfigEntry<bool> Enabled = null!, Team = null!, EndScreen = null!, FullNames = null!;
    internal static ConfigEntry<float> Scale = null!, OffsetX = null!, OffsetY = null!;
    internal static ConfigEntry<string> HudFormat = null!, ResultFormat = null!;
    internal static readonly CombatStatsSync Data = new();
    internal static bool HasAttempt { get; private set; }
    internal static bool Finished { get; private set; }
    private static ulong _sequence;
    private static float _nextSync;
    private const string HelloEvent = "InfiniTweaks.CombatStats.Hello.v6", StateEvent = "InfiniTweaks.CombatStats.State.v6";
    private sealed record FireContext(IntPtr Weapon, CombatShot? Shot, ulong Player, StatSlot Slot);
    private static readonly Stack<FireContext> Firing = new();
    private readonly record struct DamageSource(ulong Player, StatSlot Slot);
    private static readonly Stack<DamageSource> DamageSources = new();
    private static readonly StatSlot[] FirearmSlots = { StatSlot.Main, StatSlot.Special };
    internal static bool Collecting => Enabled.Value && HasAttempt && !Finished && GameStateManager.CurrentStateName == eGameStateName.InLevel;
    internal static void Initialize(ConfigFile config)
    {
        Enabled = config.Bind("Statistics", "EnableStats", true, "Source-owned Dinorush-style statistics; no embedded/external StatDisplay. Restart after changing this setting.");
        Team = config.Bind("Statistics", "ShowTeamStats", true, "Show the team. Exact remote accuracy needs matching Infini Tweaks; damage uses native health loss observed on this client and does not require a modded host. Unavailable accuracy is shown as a dash.");
        EndScreen = config.Bind("Statistics", "ShowEndScreenStats", true, "Show native per-weapon and total results without opening a standalone statistics panel.");
        FullNames = config.Bind("Statistics", "FullPlayerNames", false, "Full player names instead of compact RED/GRE/BLU/PUR labels.");
        Scale = config.Bind("Statistics", "TextScale", 1f, new ConfigDescription("Relative native inventory font scale.", new AcceptableValueRange<float>(0.7f, 1.5f)));
        OffsetX = config.Bind("Statistics", "OffsetX", 100f, new ConfigDescription("Native HUD horizontal offset; positive moves right.", new AcceptableValueRange<float>(-1000, 1000)));
        OffsetY = config.Bind("Statistics", "OffsetY", 0f, new ConfigDescription("Native HUD vertical offset.", new AcceptableValueRange<float>(-1000, 1000)));
        HudFormat = Format(config, "HudFormat", CombatStatsFormat.Hud);
        ResultFormat = Format(config, "ResultFormat", CombatStatsFormat.Results);
        NetworkAPI.RegisterEvent<CombatStatsSync.Hello>(HelloEvent, ReceiveHello);
        NetworkAPI.RegisterEvent<CombatStatsSync.Snapshot>(StateEvent, Receive);
        LevelAPI.OnBuildStart += Clear;
        LevelAPI.OnEnterLevel += Enter;
    }
    private static ConfigEntry<string> Format(ConfigFile config, string name, string value)
    {
        var entry = config.Bind("Statistics", name, value, "Case-insensitive Dinorush-style tokens: Main/Primary, Special/Secondary, Tool/Class, Melee, All; Shot/Group/Full; Fired/Hit/Crit or Damage/DamageCrit. Slots may be combined. Ratios use {Crit/Hit}; optional :0, :0.0, :0.00. Maximum 1024 chars.");
        string accepted = value;
        void Validate()
        {
            if (CombatStatsFormat.Valid(entry.Value)) { accepted = entry.Value; CombatStatsFormat.ClearCache(); }
            else { Plugin.PluginLog.LogWarning("Invalid statistics " + name + "; keeping last validated template."); entry.Value = accepted; }
        }
        Validate(); entry.SettingChanged += (_, _) => Validate(); return entry;
    }
    internal static StatSlot Slot(InventorySlot value) => value switch
    {
        InventorySlot.GearStandard => StatSlot.Main, InventorySlot.GearSpecial => StatSlot.Special,
        InventorySlot.GearClass => StatSlot.Tool, InventorySlot.GearMelee => StatSlot.Melee, _ => StatSlot.Other
    };
    private static StatSlot GearSlot(uint category)
    {
        var gear = GearCategoryDataBlock.GetBlock(category);
        var item = gear != null ? ItemDataBlock.GetBlock(gear.BaseItem) : null;
        return item != null ? Slot(item.inventorySlot) : StatSlot.Other;
    }
    private static ulong Master => SNet.HasMaster ? SNet.Master.Lookup : 0;
    private static SNet_Player? Peer(ulong lookup)
    {
        if (SNet.Slots?.PlayerSlots != null)
            foreach (var slot in SNet.Slots.PlayerSlots) if (slot?.player != null && slot.player.Lookup == lookup) return slot.player;
        return null;
    }
    internal static void Clear()
    { HasAttempt = false; Finished = false; Firing.Clear(); DamageSources.Clear(); CombatStatsView.Clear(); }
    private static void Enter()
    {
        // A checkpoint restore retains expedition statistics, like StatDisplay 1.1.4+.
        if (HasAttempt) { Finished = false; _nextSync = 0; return; }
        HasAttempt = true; Finished = false; _sequence = 0; _nextSync = 0;
        Data.Reset(Master); Firing.Clear(); CombatStatsView.Clear();
    }
    internal static void Finish() { if (HasAttempt) { Finished = true; Publish(); CombatStatsView.Refresh(); } }
    internal static void Tick()
    {
        CombatStatsView.Visibility();
        if (!Enabled.Value || !HasAttempt) return;
        if (!Finished && GameStateManager.CurrentStateName is eGameStateName.ExpeditionSuccess or eGameStateName.ExpeditionFail or eGameStateName.AfterLevel) Finish();
        if (Time.unscaledTime < _nextSync) return;
        _nextSync = Time.unscaledTime + 2;
        Data.ChangeMaster(Master);
        if (SNet.Slots?.PlayerSlots != null)
            foreach (var slot in SNet.Slots.PlayerSlots)
                if (slot?.player is { } player && (player.IsLocal || SNet.IsMaster && player.IsBot))
                    foreach (var weapon in FirearmSlots)
                        Data.Row(player.Lookup, weapon).AccuracySource = player.IsLocal ? (byte)1 : (byte)2;
        Publish(); CombatStatsView.Refresh();
    }
    private static void ReceiveHello(ulong sender, CombatStatsSync.Hello value)
    {
        if (!Enabled.Value || !HasAttempt || Peer(sender) is not { } player || player.IsLocal || player.IsBot) return;
        Data.ChangeMaster(Master);
        if (Data.ReceiveHello(sender, value) is { } reply) NetworkAPI.InvokeEvent(HelloEvent, reply, player, SNet_ChannelType.GameOrderCritical);
    }
    private static void Receive(ulong sender, CombatStatsSync.Snapshot value)
    {
        if (!Enabled.Value || !HasAttempt || Peer(sender) == null || Peer(value.Player) is not { } target) return;
        Data.ChangeMaster(Master); Data.Receive(sender, value, target.IsBot);
    }
    private static void Publish()
    {
        if (!Enabled.Value || !HasAttempt || !SNet.HasLocalPlayer) return;
        if (SNet.Slots?.PlayerSlots != null)
            foreach (var slot in SNet.Slots.PlayerSlots)
                if (slot?.player is { } player && !player.IsLocal && !player.IsBot &&
                    (!Data.Peers.TryGetValue(player.Lookup, out var known) || known.InEpoch == 0 || known.OutNonce == 0))
                    NetworkAPI.InvokeEvent(HelloEvent, new CombatStatsSync.Hello { Epoch = Data.Epoch }, player, SNet_ChannelType.GameOrderCritical);
        foreach (var peer in Data.Peers)
        {
            var target = Peer(peer.Key); if (target == null || target.IsLocal || target.IsBot || peer.Value.OutNonce == 0) continue;
            foreach (var entry in Data.Rows)
            {
                if (Peer(entry.Key.Player) == null) continue;
                var packet = new CombatStatsSync.Snapshot { Epoch = Data.Epoch, ForEpoch = peer.Value.OutEpoch, Nonce = peer.Value.OutNonce,
                    Player = entry.Key.Player, Slot = entry.Key.Slot, Sequence = ++_sequence, Counts = entry.Value.Counts.AccuracyOnly() };
                if (entry.Key.Player == SNet.LocalPlayer.Lookup || SNet.IsMaster && Peer(entry.Key.Player)?.IsBot == true)
                {
                    packet.Kind = entry.Key.Player == SNet.LocalPlayer.Lookup ? (byte)1 : (byte)2;
                    if (Data.ShouldPublish(peer.Key, packet))
                    {
                        NetworkAPI.InvokeEvent(StateEvent, packet, target, SNet_ChannelType.GameOrderCritical);
                        Data.MarkPublished(peer.Key, packet);
                    }
                }
            }
        }
    }
    [HarmonyPatch]
    private static class CombatFire
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(BulletWeapon), typeof(Shotgun), typeof(BulletWeaponSynced), typeof(ShotgunSynced) })
                yield return AccessTools.DeclaredMethod(type, "Fire", new[] { typeof(bool) });
        }
        [HarmonyPrefix]
        private static void Begin(BulletWeapon __instance)
        {
            if (Firing.Count != 0 && Firing.Peek().Weapon == __instance.Pointer) { Firing.Push(Firing.Peek()); return; }
            CombatShot? shot = null; var owner = __instance.Owner?.Owner;
            if (Collecting && owner != null && (owner.IsLocal || SNet.IsMaster && owner.IsBot))
            {
                var row = Data.Row(owner.Lookup, GearSlot(__instance.GearCategoryData.persistentID));
                row.AccuracySource = owner.IsLocal ? (byte)1 : (byte)2;
                shot = new CombatShot(row, __instance.MaxRayDist);
            }
            Firing.Push(new(__instance.Pointer, shot, owner?.Lookup ?? 0, GearSlot(__instance.GearCategoryData.persistentID)));
        }
        [HarmonyFinalizer]
        private static void End() { if (Firing.Count != 0) Firing.Pop(); }
    }
    [HarmonyPatch(typeof(Weapon), nameof(Weapon.CastWeaponRay), new Type[] { typeof(Transform), typeof(Weapon.WeaponHitData), typeof(Vector3), typeof(int) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal })]
    private static class Ray
    {
        [HarmonyPrefix]
        private static void Before(ref Weapon.WeaponHitData __1) { if (Firing.Count != 0) Firing.Peek().Shot?.Cast(__1.maxRayDist); }
    }
    [HarmonyPatch(typeof(Dam_EnemyDamageLimb), nameof(Dam_EnemyDamageLimb.BulletDamage))]
    private static class Hit
    {
        [HarmonyPostfix]
        private static void After(Dam_EnemyDamageLimb __instance) { if (Firing.Count != 0) Firing.Peek().Shot?.Hit(__instance.m_type == eLimbDamageType.Weakspot); }
    }
    [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
    private static class Damage
    {
        [HarmonyPrefix]
        private static void Before(Dam_EnemyDamageBase __instance, out float __state) => __state = __instance.Health;
        [HarmonyPostfix]
        private static void After(Dam_EnemyDamageBase __instance, Agents.Agent __1, int __6, uint __9, float __state)
        {
            if (!Collecting) return;
            var player = __1?.TryCast<PlayerAgent>()?.Owner;
            var source = DamageSources.Count > 0 ? DamageSources.Peek() : Firing.Count > 0 ?
                new DamageSource(Firing.Peek().Player, Firing.Peek().Slot) : default;
            ulong lookup = source.Player != 0 ? source.Player : player?.Lookup ?? 0;
            if (lookup == 0) return;
            // Native Receive*Damage calls ProcessReceivedDamage on each peer. Count
            // this peer's actual health loss once; never merge another peer's damage totals.
            double damage = CombatCounts.EffectiveDamage(__state, __instance.Health); if (damage == 0) return;
            var row = Data.Row(lookup, source.Player != 0 ? source.Slot : GearSlot(__9)); row.Counts.Damage += damage;
            var limbs = __instance.DamageLimbs;
            if (limbs != null && __6 >= 0 && __6 < limbs.Length && limbs[__6]?.m_type == eLimbDamageType.Weakspot) row.Counts.CritDamage += damage;
        }
    }
    [HarmonyPatch]
    private static class MeleeDamageSource
    {
        [HarmonyPatch(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.DoAttackDamage)), HarmonyPrefix]
        private static void First(MeleeWeaponFirstPerson __instance) => Push(__instance.Owner, StatSlot.Melee);
        [HarmonyPatch(typeof(MeleeWeaponFirstPerson), nameof(MeleeWeaponFirstPerson.DoAttackDamage)), HarmonyFinalizer]
        private static void FirstDone() => Pop();
        [HarmonyPatch(typeof(MeleeWeaponThirdPerson), nameof(MeleeWeaponThirdPerson.DoAttackDamage)), HarmonyPrefix]
        private static void Third(MeleeWeaponThirdPerson __instance) => Push(__instance.Owner, StatSlot.Melee);
        [HarmonyPatch(typeof(MeleeWeaponThirdPerson), nameof(MeleeWeaponThirdPerson.DoAttackDamage)), HarmonyFinalizer]
        private static void ThirdDone() => Pop();
    }
    [HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireMaster))]
    private static class SentryDamageSource
    {
        [HarmonyPrefix] private static void Before(SentryGunInstance_Firing_Bullets __instance) => Push(__instance.m_core.Owner, StatSlot.Tool);
        [HarmonyFinalizer] private static void After() => Pop();
    }
    [HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), nameof(MineDeployerInstance_Detonate_Explosive.DoExplode))]
    private static class MineDamageSource
    {
        [HarmonyPrefix] private static void Before(MineDeployerInstance_Detonate_Explosive __instance) => Push(__instance.m_core.Owner, StatSlot.Tool);
        [HarmonyFinalizer] private static void After() => Pop();
    }
    private static void Push(PlayerAgent? player, StatSlot slot) => DamageSources.Push(new(player?.Owner?.Lookup ?? 0, slot));
    private static void Pop() { if (DamageSources.Count > 0) DamageSources.Pop(); }
    [HarmonyPatch(typeof(SNet_SessionHub), nameof(SNet_SessionHub.LeaveHub))]
    private static class Leave { [HarmonyPostfix] private static void After() => Clear(); }
    [HarmonyPatch(typeof(WardenObjectiveManager), nameof(WardenObjectiveManager.OnWinConditionSolved))]
    private static class Win { [HarmonyPostfix] private static void After() => Publish(); }
}
