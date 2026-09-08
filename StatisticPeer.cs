using System;

namespace InfiniTweaks;

// A receiver-issued challenge binds snapshots to BOTH participants' current
// tracking sessions. A delayed announcement cannot replace an active stream:
// the announced incarnation must answer a fresh challenge first.
internal sealed class StatisticPeer
{
    private ulong _generation, _token, _pendingGeneration, _pendingToken;
    internal ulong OutboundGeneration, OutboundToken;

    internal static ulong NewToken()
    {
        ulong token;
        do { token = BitConverter.ToUInt64(Guid.NewGuid().ToByteArray(), 0); } while (token == 0);
        return token;
    }

    internal ulong ChallengeFor(ulong generation)
    {
        if (generation == 0) return 0;
        if (generation == _generation) return _token;
        if (generation != _pendingGeneration)
        { _pendingGeneration = generation; _pendingToken = NewToken(); }
        return _pendingToken;
    }

    // 0: stale/unrequested response; 1: existing stream; 2: new stream.
    internal int Confirm(ulong generation, ulong token)
    {
        if (Accepts(generation, token)) return 1;
        if (generation == 0 || token == 0 || generation != _pendingGeneration || token != _pendingToken) return 0;
        _generation = generation; _token = token;
        _pendingGeneration = _pendingToken = 0;
        return 2;
    }

    internal bool Accepts(ulong generation, ulong token) =>
        generation != 0 && token != 0 && generation == _generation && token == _token;
}
