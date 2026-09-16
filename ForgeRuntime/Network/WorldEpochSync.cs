using System;

namespace ForgeRuntime.Network;

/// <summary>The client's view of the host's world: which epoch is current, and whether this message moved it.</summary>
internal readonly record struct WorldEpochReceipt(string Code, long WorldEpoch)
{
    internal bool Accepted => Code == NetworkCodes.EpochAccepted;
}

/// <summary>
/// World-epoch synchronization, pure logic. The host owns the number — it rises once per world transition — and a
/// client only ever accepts a higher value that the current master broadcast; an older value, a repeat or a message
/// from anyone else is refused with an explicit code, because a client acting on a stale epoch is exactly the
/// failure that makes late requests execute in the wrong world.
/// <para>
/// The two sides are one type because they are one rule seen from two sides: the client adopts what the host says,
/// the host records what it told each peer. Every accepted epoch change is the caller's cue to clear everything
/// scoped to the previous world.
/// </para>
/// </summary>
internal sealed class WorldEpochSync
{
    private ulong _hostSession;

    internal WorldEpochSync(SessionRole role, long worldEpoch)
    {
        Role = role;
        WorldEpoch = worldEpoch;
    }

    internal SessionRole Role { get; }

    /// <summary>On a host: the epoch it last broadcast. On a client: the epoch it last accepted from the host.</summary>
    internal long WorldEpoch { get; private set; }

    /// <summary>The host a client accepts epochs from, from SNet's master, never from a message field.</summary>
    internal ulong HostSession => _hostSession;

    internal bool Synchronized { get; private set; }

    /// <summary>
    /// Records the host this client follows, at handshake time. A different host resets the client to
    /// unsynchronized: an epoch learned from the previous host says nothing about the new one's world.
    /// </summary>
    internal void BindHost(ulong hostSession)
    {
        if (hostSession == 0) throw new NetworkContractException(NetworkCodes.FormatInvalid, "A client must bind a real host session.");
        if (_hostSession == hostSession) return;
        _hostSession = hostSession;
        Synchronized = false;
    }

    /// <summary>
    /// The host advanced to a new world. The epoch must move forward; a host that tries to reuse or rewind an epoch
    /// is refused here rather than broadcasting a number its clients would reject.
    /// </summary>
    internal WorldEpochMessage Advance(long worldEpoch, WorldChangeReason reason, string detail = "")
    {
        if (Role != SessionRole.Host) throw new NetworkContractException(NetworkCodes.SenderNotHost, "Only the host advances the world epoch.");
        if (worldEpoch <= WorldEpoch) throw new NetworkContractException(NetworkCodes.EpochNotIncreasing, "Host epoch " + worldEpoch + " is not above " + WorldEpoch + ".");
        var message = new WorldEpochMessage
        {
            ProtocolVersion = NetworkProtocol.Version,
            SenderSession = _hostSession,
            WorldEpoch = worldEpoch,
            PreviousEpoch = WorldEpoch,
            Reason = (byte)reason
        };
        // The detail is diagnostic: it is clipped to the slot rather than allowed to fail the broadcast that tells
        // clients a world ended.
        message.Detail = WireText.Clip(detail, NetworkProtocol.TextSlotBytes);
        WorldEpoch = worldEpoch;
        return message;
    }

    /// <summary>
    /// A client receives the host's broadcast. Only the current host may move a client's world, only forward, and
    /// only after the handshake that established who the host is. A refusal leaves the client's own epoch untouched,
    /// which is what lets it keep refusing the same stale message.
    /// </summary>
    internal WorldEpochReceipt Observe(WorldEpochMessage message, ulong sender, bool senderIsMaster)
    {
        if (Role != SessionRole.Client) throw new NetworkContractException(NetworkCodes.RoleMismatch, "Only a client observes the host epoch.");
        if (message.SenderSession != sender)
            throw new NetworkContractException(NetworkCodes.FormatInvalid, "Epoch message claims session " + message.SenderSession + " but arrived from " + sender + ".");
        if (message.ProtocolVersion != NetworkProtocol.Version) return Refuse(NetworkCodes.ProtocolMismatch);
        if (!senderIsMaster || sender != _hostSession) return Refuse(NetworkCodes.SenderNotHost);
        if (!Synchronized) return Refuse(NetworkCodes.EpochUnavailable);
        if (message.WorldEpoch <= WorldEpoch) return Refuse(NetworkCodes.EpochNotIncreasing);
        if (message.PreviousEpoch != WorldEpoch) return Refuse(NetworkCodes.EpochBeforeJoin);
        WorldEpoch = message.WorldEpoch;
        return new WorldEpochReceipt(NetworkCodes.EpochAccepted, WorldEpoch);
    }

    /// <summary>
    /// Adopts the epoch the host reported at handshake time, which is the baseline every later broadcast must build
    /// on. Called once the handshake in this epoch accepted the host.
    /// </summary>
    internal WorldEpochReceipt Synchronize(long worldEpoch)
    {
        if (Role != SessionRole.Client) throw new NetworkContractException(NetworkCodes.RoleMismatch, "Only a client synchronizes to the host epoch.");
        WorldEpoch = worldEpoch;
        Synchronized = true;
        return new WorldEpochReceipt(NetworkCodes.EpochAccepted, WorldEpoch);
    }

    /// <summary>A client that lost its host (leave, migration) drops back to unsynchronized; it accepts no epoch
    /// until a handshake names the next host.</summary>
    internal void Desynchronize()
    {
        Synchronized = false;
        _hostSession = 0;
    }

    private WorldEpochReceipt Refuse(string code) => new(code, WorldEpoch);
}
