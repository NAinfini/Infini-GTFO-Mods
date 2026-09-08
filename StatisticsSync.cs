using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace InfiniTweaks;

// Game-independent protocol state, shared by the runtime and multi-peer tests.
internal sealed class StatisticsSync
{
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Session
    {
        public ulong Generation, Recipient, Token;
        public byte Kind; // 0 announcement, 1 receiver challenge, 2 confirmation
    }
    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    internal struct Snapshot
    {
        public ulong Generation, Recipient, Token, Player;
        public uint Sequence, Weapon;
        public long Fired, Hit;
        public float Damage;
        public byte Kind; // 1 owner accuracy, 2 host-observed accuracy, 3 host damage
    }

    internal readonly Dictionary<(ulong Player, uint Weapon), StatisticRow> Rows = new();
    internal readonly Dictionary<ulong, StatisticPeer> Peers = new();
    internal ulong Generation { get; private set; }
    internal bool HostData { get; private set; }
    private ulong _master;

    internal void Reset(ulong master, bool isMaster)
    {
        Rows.Clear(); Peers.Clear(); Generation = StatisticPeer.NewToken();
        _master = master; HostData = isMaster;
    }

    internal void ChangeMaster(ulong master, bool isMaster)
    {
        if (_master == master) return;
        foreach (var row in Rows.Values) row.ClearHostData();
        _master = master; HostData = isMaster;
    }

    internal StatisticRow Get(ulong player, uint weapon)
    {
        if (!Rows.TryGetValue((player, weapon), out var row)) Rows.Add((player, weapon), row = new StatisticRow());
        return row;
    }

    internal Session? ReceiveSession(ulong sender, Session session)
    {
        if (Generation == 0 || session.Generation == 0 || session.Kind > 2) return null;
        if (session.Kind != 0 && session.Recipient != Generation) return null;
        if (!Peers.TryGetValue(sender, out var peer)) Peers.Add(sender, peer = new StatisticPeer());
        switch (session.Kind)
        {
            case 0:
                return new Session { Kind = 1, Generation = Generation, Recipient = session.Generation, Token = peer.ChallengeFor(session.Generation) };
            case 1 when session.Token != 0:
                peer.OutboundGeneration = session.Generation; peer.OutboundToken = session.Token;
                return new Session { Kind = 2, Generation = Generation, Recipient = session.Generation, Token = session.Token };
            case 2:
                int confirmation = peer.Confirm(session.Generation, session.Token);
                if (confirmation == 0) return null;
                bool host = sender == _master;
                if (confirmation == 2)
                    foreach (var pair in Rows)
                    {
                        if (pair.Key.Player == sender) pair.Value.ClearAccuracy();
                        if (host) pair.Value.ClearHostData();
                    }
                if (host) HostData = true;
                break;
        }
        return null;
    }

    internal bool Receive(ulong sender, Snapshot packet)
    {
        if (Generation == 0 || packet.Recipient != Generation || !Peers.TryGetValue(sender, out var peer) || !peer.Accepts(packet.Generation, packet.Token)) return false;
        if (packet.Kind == 1 ? sender != packet.Player : sender != _master) return false;
        return Get(packet.Player, packet.Weapon).Apply(packet.Kind, packet.Sequence, packet.Fired, packet.Hit, packet.Damage);
    }
}
