using InfiniTweaks;

internal static class CombatStatsTests
{
    internal static void Run(Action<bool, string> check)
    {
        var row = new CombatRow(); var shot = new CombatShot(row, 80);
        shot.Cast(80); shot.Hit(false); shot.Cast(50); shot.Hit(true); shot.Hit(true);
        check(row.Counts.Fired == 1 && row.Counts.Hit == 1 && row.Counts.Crit == 1 && row.Counts.FullHit == 3 && row.Counts.FullCrit == 2, "Piercing increments Full counts while one pellet hits/crits at most once.");
        shot.Cast(80); shot.Hit(true); shot.Cast(80);
        check(row.Counts.Fired == 3 && row.Counts.Hit == 2 && row.Counts.GroupFired == 1 && row.Counts.GroupHit == 1 && row.Counts.GroupCrit == 1, "Shotgun pellets share one trigger group.");
        check(row.Counts.Valid, "Collected multi-pellet totals satisfy network validation.");
        check(CombatCounts.EffectiveDamage(8, -50) == 8 && CombatCounts.EffectiveDamage(8, 10) == 0 && CombatCounts.EffectiveDamage(double.NaN, 0) == 0, "Only actual health loss, capped by pre-hit health, counts as damage.");
        var a = new CombatStatsSync(); var b = new CombatStatsSync(); a.Reset(99); b.Reset(99);
        void Connect(CombatStatsSync from, ulong sender, CombatStatsSync to, ulong recipient)
        {
            var challenge = to.ReceiveHello(sender, new() { Epoch = from.Epoch });
            check(challenge is { Kind: 1 }, "Receiver issues an epoch-bound challenge.");
            var response = from.ReceiveHello(recipient, challenge!.Value);
            check(response is { Kind: 2 }, "Sender answers the receiver challenge.");
            to.ReceiveHello(sender, response!.Value);
        }
        Connect(a, 1, b, 2);
        CombatStatsSync.Snapshot Packet() => new() { Epoch = a.Epoch, ForEpoch = a.Peers[2].OutEpoch, Nonce = a.Peers[2].OutNonce, Player = 1,
            Slot = StatSlot.Main, Kind = 1, Sequence = 1, Counts = row.Counts };
        var packet = Packet();
        check(a.ShouldPublish(2, packet) && a.ShouldPublish(2, packet), "An interrupted send does not mark its snapshot as published.");
        a.MarkPublished(2, packet);
        check(!a.ShouldPublish(2, packet), "Unchanged statistics do not send after a successful publish.");
        var changed = packet; changed.Counts.Damage++;
        check(a.ShouldPublish(2, changed), "Changed damage is published.");
        a.MarkPublished(2, changed);
        changed.Nonce++;
        check(a.ShouldPublish(2, changed), "New recipient handshake receives a full snapshot even with unchanged values.");
        check(a.ShouldPublish(3, changed), "Each recipient gets its own initial snapshot.");
        check(b.Receive(1, packet, false) && !b.Receive(1, packet, false), "Cumulative snapshots apply once, not additively.");
        packet.Player = 3; packet.Sequence++;
        check(!b.Receive(1, packet, false), "Peers cannot supply another human's accuracy.");
        packet = Packet(); packet.Kind = 3; packet.Sequence++;
        check(!b.Receive(1, packet, false), "Damage snapshots are rejected; native observations own damage.");
        packet = Packet(); packet.Counts.Crit = 900;
        check(!b.Receive(1, packet, false), "Malformed count hierarchy is rejected.");
        b.Row(1, StatSlot.Main).Counts.Damage = 42;
        b.Row(1, StatSlot.Main).Counts.CritDamage = 12;
        packet = Packet(); packet.Sequence = 4; packet.Counts.Damage = 999;
        check(b.Receive(1, packet, false) && b.Row(1, StatSlot.Main).Counts.Damage == 42, "Accuracy sync cannot overwrite or double-count locally observed damage");
        check(CombatStatsFormat.Render("{Damage}|{DamageCrit}", b, 1) == "42|12", "Client damage renders before a host handshake");
        check(packet.Counts.AccuracyOnly().Damage == 0 && packet.Counts.AccuracyOnly().CritDamage == 0, "Outgoing accuracy packets omit observed damage");
        var old = Packet(); a.Reset(99); Connect(a, 1, b, 2);
        check(!b.Receive(1, old, false) && b.Receive(1, Packet(), false), "Restarted peers reject old packets and can resume at sequence one.");
        b.Reset(99); check(!b.Receive(1, old, false), "New receiver expedition rejects all previous-session packets.");
        Connect(a, 1, b, 2); check(b.Receive(1, Packet(), false), "Fresh receiver handshake restores valid snapshots.");
        var local = new CombatStatsSync(); local.Reset(1); local.Row(1, StatSlot.Main).Counts = row.Counts; local.Row(1, StatSlot.Main).AccuracySource = 1;
        local.Row(1, StatSlot.Main).Counts.Damage = 15.5; local.Row(1, StatSlot.Main).Counts.CritDamage = 5;
        check(CombatStatsFormat.Render("{Hit/Fired:0.0}|{Crit/Hit}|{GroupHit/GroupFired}|{FullHit/Hit}|{Damage}", local, 1) == "66.7%|100%|100%|200%|15", "Dinorush-style ratios distinguish pellet, group and pierce values; damage integers are floored.");
        local.Row(1, StatSlot.Special).Counts.Fired = local.Row(1, StatSlot.Special).Counts.FullFired = 1;
        local.Row(1, StatSlot.Special).AccuracySource = 1;
        check(CombatStatsFormat.Render("{mainSPECIALfired}/{primaryGroupCrit}", local, 1) == "4/1", "Mixed-case combined slot selectors and shot dimensions resolve independently.");
        check(CombatStatsFormat.Render("{Damage}|{Hit/Fired}", b, 1).StartsWith("0|"), "Damage starts at zero without requiring any modded-host handshake.");
        check(CombatStatsFormat.Render("{Hit/Fired}", local, 55) == "—", "Unknown remote accuracy is not fabricated.");
        foreach (string invalid in new[] { "{Hit/0}", "{Unknown}", "{DamageFired}", "{Hit", "{Hit/Fired:0.000}", "{GroupFullHit}", new string('x', 1025) })
            check(!CombatStatsFormat.Valid(invalid), "Reject unsupported format: " + invalid[..Math.Min(24, invalid.Length)]);
        local.ChangeMaster(2);
        check(local.Row(1, StatSlot.Main).Counts.Damage == 15.5 && local.Row(1, StatSlot.Main).Counts.Fired == 3, "Host migration preserves observed damage and local accuracy.");
        var host = new CombatStatsSync(); var client = new CombatStatsSync(); host.Reset(99); client.Reset(99);
        Connect(host, 99, client, 2);
        CombatStatsSync.Snapshot BotPacket() => new() { Epoch = host.Epoch, ForEpoch = host.Peers[2].OutEpoch,
            Nonce = host.Peers[2].OutNonce, Player = 3, Slot = StatSlot.Main, Kind = 2, Sequence = 1, Counts = row.Counts };
        check(client.Receive(99, BotPacket(), true), "Confirmed host supplies bot accuracy.");
        var oldBot = BotPacket(); host.Reset(99); Connect(host, 99, client, 2);
        check(client.Row(3, StatSlot.Main).AccuracySource == 0 && client.Row(3, StatSlot.Main).Counts.Fired == 0,
            "A new host session clears all host-observed bot counters, not just the host's own row.");
        check(!client.Receive(99, oldBot, true) && client.Receive(99, BotPacket(), true), "Bot sequences resume at one only after a fresh host handshake.");
    }
}
