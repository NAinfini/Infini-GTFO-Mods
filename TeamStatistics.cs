using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using GameData;
using Gear;
using GTFO.API;
using HarmonyLib;
using Player;
using SNetwork;
using UnityEngine;
using Session = InfiniTweaks.StatisticsSync.Session;
using Snapshot = InfiniTweaks.StatisticsSync.Snapshot;

namespace InfiniTweaks;

// Cumulative, attempt-scoped snapshots: reordered/repeated packets cannot add
// damage twice. Only the owner supplies exact accuracy, only the host damage.
internal static class TeamStatistics
{
    private sealed class Shot
    {
        internal readonly StatisticRow Row;
        internal readonly float Range;
        internal bool HasPellet, Hit;
        internal Shot(StatisticRow row, float range) { Row = row; Range = range; }
    }
    private static readonly StatisticsSync Sync = new();
    private static Dictionary<(ulong Player, uint Weapon), StatisticRow> Rows => Sync.Rows;
    private static readonly Dictionary<ulong, string> Names = new();
    private static readonly List<ulong> Roster = new();
    private static readonly Stack<Shot?> Shots = new();
    private static uint _sequence;
    private static float _nextSend;
    internal static bool Open, Details;
    internal static string Text = "";
    private const string SessionEvent = "InfiniTweaks.Stats.Session.v2";
    private const string SnapshotEvent = "InfiniTweaks.Stats.Snapshot.v2";

    internal static void Initialize()
    {
        NetworkAPI.RegisterEvent<Session>(SessionEvent, ReceiveSession);
        NetworkAPI.RegisterEvent<Snapshot>(SnapshotEvent, Receive);
    }
    internal static void Reset()
    {
        Names.Clear(); Roster.Clear(); Shots.Clear(); Text = "";
        Open = false; _sequence = 0; _nextSend = 0;
        Sync.Reset(SNet.HasMaster ? SNet.Master.Lookup : 0, SNet.IsMaster);
    }
    private static StatisticRow Get(ulong player, uint weapon)
    {
        return Sync.Get(player, weapon);
    }
    private static bool InAttempt => Settings.Stats.Value && GameStateManager.CurrentStateName == eGameStateName.InLevel;
    private static bool Tracking => Settings.Stats.Value && Sync.Generation != 0 &&
        (GameStateManager.CurrentStateName == eGameStateName.InLevel || GameStateManager.CurrentStateName == eGameStateName.AfterLevel);
    private static void RefreshMaster() => Sync.ChangeMaster(SNet.HasMaster ? SNet.Master.Lookup : 0, SNet.IsMaster);
    private static SNet_Player? FindPeer(ulong id)
    {
        foreach (var player in PlayerManager.PlayerAgentsInLevel)
            if (player != null && player.Owner != null && player.Owner.Lookup == id) return player.Owner;
        return null;
    }
    private static void ReceiveSession(ulong sender, Session session)
    {
        if (!Tracking || session.Generation == 0) return;
        var player = FindPeer(sender);
        if (player == null || (SNet.HasLocalPlayer && sender == SNet.LocalPlayer.Lookup)) return;
        RefreshMaster();
        if (Sync.ReceiveSession(sender, session) is { } reply)
            NetworkAPI.InvokeEvent(SessionEvent, reply, player, SNet_ChannelType.GameOrderCritical);
        if (GameStateManager.CurrentStateName == eGameStateName.AfterLevel) BuildText();
    }
    private static void Receive(ulong sender, Snapshot packet)
    {
        if (!Tracking || !Names.ContainsKey(packet.Player)) return;
        RefreshMaster();
        if (Sync.Receive(sender, packet) && GameStateManager.CurrentStateName == eGameStateName.AfterLevel) BuildText();
    }
    internal static void Tick()
    {
        if (!Settings.Stats.Value || (GameStateManager.CurrentStateName != eGameStateName.InLevel && GameStateManager.CurrentStateName != eGameStateName.AfterLevel)) { Open = false; return; }
        if (Input.GetKeyDown(Settings.StatsKey.Value) && (FocusStateManager.CurrentState == eFocusState.FPS || GameStateManager.CurrentStateName == eGameStateName.AfterLevel))
        {
            if (Input.GetKey(KeyCode.LeftShift) || Input.GetKey(KeyCode.RightShift)) { Details = !Details; Open = true; }
            else Open = !Open;
            BuildText();
        }
        if (!InAttempt || Time.unscaledTime < _nextSend) return;
        _nextSend = Time.unscaledTime + 1f;
        RefreshMaster();
        foreach (var player in PlayerManager.PlayerAgentsInLevel)
        {
            if (player == null || player.Owner == null) continue;
            ulong id = player.Owner.Lookup;
            if (!Names.ContainsKey(id)) Roster.Add(id);
            Names[id] = player.Owner.NickName.Replace("<", "").Replace(">", "").Replace("\n", " ");
        }
        Publish();
        BuildText();
    }
    private static void Publish()
    {
        if (!Tracking || !SNet.HasLocalPlayer) return;
        NetworkAPI.InvokeEvent(SessionEvent, new Session { Generation = Sync.Generation }, SNet_ChannelType.GameOrderCritical);
        foreach (var target in Sync.Peers)
        {
            var peer = target.Value;
            var recipient = FindPeer(target.Key);
            if (recipient == null || peer.OutboundToken == 0) continue;
            // Sending does not mutate Rows. NetworkAPI sends to peers, not self.
            foreach (var pair in Rows)
            {
                var row = pair.Value;
                var packet = new Snapshot { Generation = Sync.Generation, Recipient = peer.OutboundGeneration, Token = peer.OutboundToken, Player = pair.Key.Player, Weapon = pair.Key.Weapon, Sequence = ++_sequence, Fired = row.Fired, Hit = row.Hit, Damage = row.Damage };
                if (pair.Key.Player == SNet.LocalPlayer.Lookup || (SNet.IsMaster && row.AccuracySource == 2))
                {
                    packet.Kind = pair.Key.Player == SNet.LocalPlayer.Lookup ? (byte)1 : (byte)2;
                    NetworkAPI.InvokeEvent(SnapshotEvent, packet, recipient, SNet_ChannelType.GameOrderCritical);
                }
                if (SNet.IsMaster)
                {
                    packet.Kind = 3;
                    NetworkAPI.InvokeEvent(SnapshotEvent, packet, recipient, SNet_ChannelType.GameOrderCritical);
                }
            }
        }
    }
    private static string WeaponName(uint id)
    {
        if (id == 0) return "Other / unattributed weapon";
        var block = GearCategoryDataBlock.GetBlock(id);
        return block == null ? $"Weapon category {id}" : block.PublicName.ToString();
    }
    private static void BuildText()
    {
        var text = new StringBuilder("CURRENT ATTEMPT     Accuracy / Enemy damage\n");
        foreach (ulong player in Roster)
        {
            if (!Settings.TeamStats.Value && (!SNet.HasLocalPlayer || player != SNet.LocalPlayer.Lookup)) continue;
            long fired = 0, hit = 0; float damage = 0; bool estimate = false;
            foreach (var pair in Rows)
            {
                if (pair.Key.Player != player) continue;
                var row = pair.Value;
                if (row.AccuracySource == 1 || Settings.EstimateStats.Value)
                { fired += row.Fired; hit += row.Hit; estimate |= row.AccuracySource == 2; }
                damage += row.Damage;
            }
            string accuracy = CasualRules.Accuracy(fired, hit) + (estimate && fired > 0 ? " ~observed" : "");
            text.AppendLine($"{Names[player]}    {accuracy} / {(Sync.HostData ? damage.ToString("0.#") + " [host]" : "—")}");
            if (!Details) continue;
            foreach (var pair in Rows)
            {
                if (pair.Key.Player != player) continue;
                var row = pair.Value;
                string acc = row.AccuracySource == 2 && !Settings.EstimateStats.Value ? "—" : CasualRules.Accuracy(row.Fired, row.Hit);
                text.AppendLine($"  {WeaponName(pair.Key.Weapon)}: {acc}{(row.AccuracySource == 2 ? " ~" : "")} / {(Sync.HostData ? row.Damage.ToString("0.#") : "—")}");
            }
        }
        text.Append("Pellets; piercing counted once. — unknown/no shots. ~ estimated.\n");
        text.Append($"{Settings.StatsKey.Value}: close · Shift+{Settings.StatsKey.Value}: weapon categories");
        Text = text.ToString();
    }
    internal static void EndAttempt()
    {
        Publish();
        BuildText(); Open = Settings.Stats.Value && Settings.EndStats.Value;
    }

    [HarmonyPatch]
    private static class FirePatch
    {
        private static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (var type in new[] { typeof(BulletWeapon), typeof(Shotgun), typeof(BulletWeaponSynced), typeof(ShotgunSynced) })
                yield return AccessTools.DeclaredMethod(type, "Fire", new[] { typeof(bool) });
        }
        [HarmonyPrefix]
        private static void Begin(BulletWeapon __instance)
        {
            Shot? shot = null;
            var owner = __instance.Owner;
            if (InAttempt && owner != null && (owner.IsLocallyOwned || SNet.IsMaster))
            {
                var row = Get(owner.Owner.Lookup, __instance.GearCategoryData.persistentID);
                byte source = owner.IsLocallyOwned && !owner.Owner.IsBot ? (byte)1 : (byte)2;
                if (row.AccuracySource != 1 || source == 1)
                { row.AccuracySource = source; shot = new Shot(row, __instance.MaxRayDist); }
            }
            Shots.Push(shot);
        }
        [HarmonyFinalizer]
        private static void End() { if (Shots.Count != 0) Shots.Pop(); }
    }
    [HarmonyPatch(typeof(Weapon), nameof(Weapon.CastWeaponRay), new Type[] { typeof(Transform), typeof(Weapon.WeaponHitData), typeof(Vector3), typeof(int) }, new ArgumentType[] { ArgumentType.Normal, ArgumentType.Ref, ArgumentType.Normal, ArgumentType.Normal })]
    private static class RayPatch
    {
        [HarmonyPrefix]
        private static void Before(ref Weapon.WeaponHitData __1)
        {
            if (Shots.Count == 0 || Shots.Peek() is not { } shot) return;
            // Native piercing casts shorten maxRayDist; each new shotgun pellet
            // starts a full-length cast. Do not count the continuation as a shot.
            if (!shot.HasPellet || __1.maxRayDist >= shot.Range)
            { shot.HasPellet = true; shot.Hit = false; shot.Row.Fired++; }
        }
        [HarmonyPostfix]
        private static void After(ref Weapon.WeaponHitData __1, bool __result)
        {
            if (!__result || Shots.Count == 0 || Shots.Peek() is not { } shot || shot.Hit) return;
            var collider = __1.rayHit.collider;
            if (collider != null && collider.GetComponent<Dam_EnemyDamageLimb>() != null)
            { shot.Hit = true; shot.Row.Hit++; }
        }
    }
    [HarmonyPatch(typeof(Dam_EnemyDamageBase), nameof(Dam_EnemyDamageBase.ProcessReceivedDamage))]
    private static class DamagePatch
    {
        [HarmonyPrefix]
        private static void Before(Dam_EnemyDamageBase __instance, out float __state) => __state = __instance.Health;
        [HarmonyPostfix]
        private static void After(Dam_EnemyDamageBase __instance, Agents.Agent __1, uint __9, float __state)
        {
            if (!InAttempt || !SNet.IsMaster || __1 == null) return;
            var player = __1.TryCast<PlayerAgent>();
            if (player?.Owner == null) return;
            float damage = CasualRules.EffectiveDamage(__state, __instance.Health);
            if (damage > 0) Get(player.Owner.Lookup, __9).Damage += damage;
        }
    }
}
