using System;
using ForgeRuntime.Framework;
using Player;

namespace ForgeMap.Native;

/// <summary>One current player life's health receiver, read from the game's own damage base: the life the
/// reference names, the two instances the write goes through, and the health the receiver holds.</summary>
internal readonly record struct PlayerHealthSnapshot(EntityReference Target, IntPtr AgentPointer,
    IntPtr ReceiverPointer, bool Alive, float Health, float Maximum);

/// <summary>The player domain's health adapter, on build 20403457. A player's health receiver is
/// `PlayerAgent.Damage`, a `Dam_PlayerDamageBase`; that class declares no health writer of its own, so the one
/// write entry is the base `Dam_SyncedDamageBase.SendSetHealth(System.Single)` the game's own heal path
/// (`ReceiveAddHealth`) also ends in.
///
/// Three properties of that entry are part of this adapter's contract, all read from the native bodies rather
/// than assumed:
/// - it is host-only: the body tests the session's own master flag and returns without applying anything on a
///   client, which is why a commit is refused by `authority-or-phase` before this half is reached;
/// - its packet and its local application both carry an `SFloat16` encoding of the absolute value against
///   `HealthMax`, so the stored value is a quantization of what was submitted and the readback, never the
///   requested amount, is what a result reports;
/// - the player override of `ReceiveSetHealth` stores the value only while the receiver's `Owner` is that agent
///   and `Owner.Alive` is true, so a dead player cannot be written to and no health write can revive one. That
///   guard is what the `not-alive` refusal below stands on, and `Alive` is also why a downed player — still
///   alive in this model — is a legal recipient.
///
/// Every read is paired with a re-read of the same fields, and a commit re-checks the pointers it captured, so
/// a receiver replaced mid-command is refused instead of written through.</summary>
internal static class PlayerHealthReceiver
{
    /// <summary>Reads one current life's health receiver. A false answer carries the catalog code that names
    /// why the life cannot be healed right now; the caller writes exactly one row for it.</summary>
    internal static bool TryRead(EntityReference reference, out PlayerHealthSnapshot snapshot, out string code)
    {
        snapshot = default;
        var agent = PlayerIdentityModule.Current?.CurrentAgent(reference);
        if (agent == null) { code = "stale-or-unsupported-recipient"; return false; }
        var first = Capture(agent);
        if (first == null || first != Capture(agent)) { code = "stale-or-unsupported-recipient"; return false; }
        // An absent receiver and one the game has not set up are the same answer: there is no health state to
        // read or to write. A receiver that belongs to another agent is its own refusal, as it is for enemies.
        if (!first.Value.ReceiverReadable || !first.Value.Setup) { code = "missing-health-receiver"; return false; }
        if (!first.Value.OwnerMatches) { code = "health-receiver-owner-mismatch"; return false; }
        // The player's receive path stores health only while its owner is alive, so this refusal is the same
        // condition the native write would refuse on, checked before anything is submitted.
        if (!first.Value.Alive) { code = "not-alive"; return false; }
        if (!float.IsFinite(first.Value.Health) || !float.IsFinite(first.Value.Maximum)
            || first.Value.Maximum <= 0 || first.Value.Health < 0 || first.Value.Health > first.Value.Maximum)
        { code = "invalid-health-state"; return false; }
        snapshot = new PlayerHealthSnapshot(reference, first.Value.AgentPointer, first.Value.ReceiverPointer,
            first.Value.Alive, first.Value.Health, first.Value.Maximum);
        code = "";
        return true;
    }

    /// <summary>Submits one absolute health value for the exact receiver the caller read. The receiver is
    /// resolved again here, so a command whose life or damage base was replaced since the preflight is refused
    /// in place of writing through a different instance; a false answer means nothing was called and the attempt
    /// is known to be uncommitted. The call itself is the game's own write entry.</summary>
    internal static bool TrySubmit(PlayerHealthSnapshot expected, float health)
    {
        var agent = PlayerIdentityModule.Current?.CurrentAgent(expected.Target);
        var damage = agent?.Damage;
        if (agent == null || agent.Pointer != expected.AgentPointer
            || damage == null || damage.Pointer != expected.ReceiverPointer) return false;
        damage.SendSetHealth(health);
        return true;
    }

    /// <summary>The damage base's readable state, and the identity of the two instances it belongs to. A null
    /// answer is a receiver that cannot be read at all; `ReceiverReadable` says a damage base exists and is set
    /// up, `OwnerMatches` whether that damage base belongs to this agent.</summary>
    private readonly record struct Sample(IntPtr AgentPointer, IntPtr ReceiverPointer, bool Alive,
        bool ReceiverReadable, bool OwnerMatches, bool Setup, float Health, float Maximum);

    private static Sample? Capture(PlayerAgent agent)
    {
        if (agent == null || agent.Pointer == IntPtr.Zero) return null;
        var pointer = agent.Pointer;
        var damage = agent.Damage;
        var sample = new Sample(pointer, damage == null ? IntPtr.Zero : damage.Pointer, agent.Alive,
            damage != null && damage.Pointer != IntPtr.Zero, damage != null && damage.Owner != null && damage.Owner.Pointer == pointer,
            damage != null && damage.IsSetup, damage == null ? 0 : damage.Health, damage == null ? 0 : damage.HealthMax);
        // A read can destroy or replace the instances it reads; a changed identity is not a sample.
        return agent.Pointer == pointer ? sample : null;
    }
}
