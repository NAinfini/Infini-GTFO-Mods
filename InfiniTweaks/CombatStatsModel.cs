using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace InfiniTweaks;

[Flags]
internal enum StatSlot : byte { None = 0, Main = 1, Special = 2, Tool = 4, Melee = 8, Other = 16, All = 31 }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
internal struct CombatCounts
{
    public long Fired, Hit, Crit, GroupFired, GroupHit, GroupCrit, FullFired, FullHit, FullCrit;
    public double Damage, CritDamage;
    internal bool Valid => Fired >= 0 && Fired <= 1_000_000_000 && Hit >= 0 && Hit <= Fired && Crit >= 0 && Crit <= Hit &&
        GroupFired >= 0 && GroupFired <= Fired && GroupHit >= 0 && GroupHit <= GroupFired && GroupCrit >= 0 && GroupCrit <= GroupHit &&
        FullFired >= Fired && FullFired <= 1_000_000_000 && FullHit >= Hit && FullHit <= 1_000_000_000 && FullCrit >= Crit && FullCrit <= FullHit &&
        double.IsFinite(Damage) && Damage >= 0 && Damage <= 1e12 && double.IsFinite(CritDamage) && CritDamage >= 0 && CritDamage <= Damage;
    internal void Add(CombatCounts value)
    {
        Fired += value.Fired; Hit += value.Hit; Crit += value.Crit;
        GroupFired += value.GroupFired; GroupHit += value.GroupHit; GroupCrit += value.GroupCrit;
        FullFired += value.FullFired; FullHit += value.FullHit; FullCrit += value.FullCrit;
        Damage += value.Damage; CritDamage += value.CritDamage;
    }
    internal void Accuracy(CombatCounts value)
    {
        double damage = Damage, critDamage = CritDamage; this = value;
        Damage = damage; CritDamage = critDamage;
    }
    internal CombatCounts AccuracyOnly()
    { var value = this; value.Damage = value.CritDamage = 0; return value; }
    internal static double EffectiveDamage(double before, double after) =>
        double.IsFinite(before) && double.IsFinite(after) ? Math.Max(0, Math.Min(before, before - after)) : 0;
}

internal sealed class CombatRow
{
    internal CombatCounts Counts;
    internal byte AccuracySource;
    internal ulong AccuracySequence;
}

// A trigger group owns one or more pellets; piercing adds Full hits only.
internal sealed class CombatShot
{
    private readonly CombatRow _row;
    private readonly float _range;
    private bool _pellet, _hit, _crit, _group, _groupHit, _groupCrit;
    internal CombatShot(CombatRow row, float range) { _row = row; _range = range; }
    internal void Cast(float distance)
    {
        if (!float.IsFinite(distance) || distance <= 0 || (_pellet && distance < _range - 0.001f)) return;
        _pellet = true; _hit = _crit = false;
        _row.Counts.Fired++; _row.Counts.FullFired++;
        if (!_group) { _group = true; _row.Counts.GroupFired++; }
    }
    internal void Hit(bool weakspot)
    {
        if (!_pellet) return;
        _row.Counts.FullHit++;
        if (!_hit) { _hit = true; _row.Counts.Hit++; }
        if (!_groupHit) { _groupHit = true; _row.Counts.GroupHit++; }
        if (!weakspot) return;
        _row.Counts.FullCrit++;
        if (!_crit) { _crit = true; _row.Counts.Crit++; }
        if (!_groupCrit) { _groupCrit = true; _row.Counts.GroupCrit++; }
    }
}

// Fresh, recipient-bound session handshake rejects delayed previous-expedition snapshots.
internal sealed class CombatStatsSync
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Hello { public ulong Epoch, ForEpoch, Nonce; public byte Kind; }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Snapshot
    {
        public ulong Epoch, ForEpoch, Nonce, Sequence, Player;
        public CombatCounts Counts;
        public StatSlot Slot;
        public byte Kind; // 1 owner accuracy, 2 host bot accuracy; damage is observed locally
    }
    internal sealed class Peer
    {
        internal ulong Candidate, Challenge, InEpoch, InNonce, OutEpoch, OutNonce;
    }
    internal readonly Dictionary<ulong, Peer> Peers = new();
    internal readonly Dictionary<(ulong Player, StatSlot Slot), CombatRow> Rows = new();
    private readonly Dictionary<(ulong Peer, ulong Player, StatSlot Slot, byte Kind), Snapshot> Published = new();
    internal bool ShouldPublish(ulong peer, Snapshot value)
    {
        var key = (peer, value.Player, value.Slot, value.Kind);
        if (Published.TryGetValue(key, out var previous) && previous.Epoch == value.Epoch &&
            previous.ForEpoch == value.ForEpoch && previous.Nonce == value.Nonce && previous.Counts.Equals(value.Counts)) return false;
        return true;
    }
    internal void MarkPublished(ulong peer, Snapshot value) => Published[(peer, value.Player, value.Slot, value.Kind)] = value;
    internal ulong Epoch { get; private set; }
    internal ulong Master { get; private set; }
    private static ulong Token()
    {
        ulong value;
        do { value = BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)); } while (value == 0);
        return value;
    }
    internal void Reset(ulong master)
    { Peers.Clear(); Rows.Clear(); Published.Clear(); Epoch = Token(); Master = master; }
    internal CombatRow Row(ulong player, StatSlot slot)
    {
        if (!Rows.TryGetValue((player, slot), out var row)) Rows.Add((player, slot), row = new());
        return row;
    }
    internal CombatCounts Total(ulong player, StatSlot slots, out bool accuracy)
    {
        var result = new CombatCounts(); accuracy = false;
        foreach (var pair in Rows)
            if (pair.Key.Player == player && (pair.Key.Slot & slots) != 0)
            { result.Add(pair.Value.Counts); accuracy |= pair.Value.AccuracySource != 0; }
        return result;
    }
    internal void ChangeMaster(ulong master)
    {
        if (Master == master) return;
        Published.Clear();
        Master = master;
        foreach (var row in Rows.Values)
        {
            if (row.AccuracySource == 2) { row.Counts.Accuracy(default); row.AccuracySource = 0; row.AccuracySequence = 0; }
        }
    }
    internal Hello? ReceiveHello(ulong sender, Hello value)
    {
        if (Epoch == 0 || value.Epoch == 0 || value.Kind > 2 || value.Kind != 0 && value.ForEpoch != Epoch) return null;
        if (!Peers.TryGetValue(sender, out var peer)) Peers.Add(sender, peer = new());
        if (value.Kind == 0)
        {
            if (peer.InEpoch == value.Epoch) return null;
            if (peer.Candidate != value.Epoch) { peer.Candidate = value.Epoch; peer.Challenge = Token(); }
            return new Hello { Kind = 1, Epoch = Epoch, ForEpoch = value.Epoch, Nonce = peer.Challenge };
        }
        if (value.Kind == 1 && value.Nonce != 0)
        {
            peer.OutEpoch = value.Epoch; peer.OutNonce = value.Nonce;
            return new Hello { Kind = 2, Epoch = Epoch, ForEpoch = value.Epoch, Nonce = value.Nonce };
        }
        if (value.Kind != 2 || value.Epoch != peer.Candidate || value.Nonce == 0 || value.Nonce != peer.Challenge) return null;
        if (peer.InEpoch != value.Epoch)
            foreach (var pair in Rows)
            {
                var row = pair.Value;
                if (pair.Key.Player == sender || sender == Master && row.AccuracySource == 2)
                { row.Counts.Accuracy(default); row.AccuracySource = 0; row.AccuracySequence = 0; }
            }
        peer.InEpoch = value.Epoch; peer.InNonce = value.Nonce;
        return null;
    }
    internal bool Receive(ulong sender, Snapshot value, bool targetIsBot)
    {
        if (value.ForEpoch != Epoch || value.Sequence == 0 || !Peers.TryGetValue(sender, out var peer) ||
            value.Epoch != peer.InEpoch || value.Nonce == 0 || value.Nonce != peer.InNonce || !value.Counts.Valid ||
            value.Slot is not (StatSlot.Main or StatSlot.Special or StatSlot.Tool or StatSlot.Melee or StatSlot.Other)) return false;
        if (value.Kind == 1 ? value.Player != sender : value.Kind != 2 || sender != Master || !targetIsBot) return false;
        var row = Row(value.Player, value.Slot);
        if (value.Kind == 2 && row.AccuracySource == 1 || row.AccuracySource == value.Kind && value.Sequence <= row.AccuracySequence) return false;
        row.Counts.Accuracy(value.Counts); row.AccuracySequence = value.Sequence; row.AccuracySource = value.Kind;
        return true;
    }
}
