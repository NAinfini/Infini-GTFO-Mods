using System;
using System.Collections.Generic;
using SNetwork;

namespace ForgeRuntime.Network.Tests;

/// <summary>
/// The one place a test touches the GTFO-API double. The production transport calls the shipped type by its real
/// name — inside the test assembly that name forwards to the double — and this wrapper keeps the double out of the
/// test bodies.
/// </summary>
internal static class Api
{
    internal static IReadOnlyList<NetworkApiDouble.Send> Sent => NetworkApiDouble.Sent;

    internal static int SentCount => NetworkApiDouble.SentCount;

    /// <summary>A send is only observable relative to where a check started looking: nothing clears the recording
    /// behind a check's back, because the double's callbacks run inside the send that is being examined.</summary>
    internal static int Mark() => NetworkApiDouble.SentCount;

    internal static NetworkApiDouble.Send Since(int mark) => NetworkApiDouble.Sent[mark];

    internal static void ClearRecording() => NetworkApiDouble.ClearRecording();

    /// <summary>Runs a registered handler as the game would on an inbound datagram from <paramref name="sender"/>.</summary>
    internal static void Deliver(string eventName, ulong sender, object payload) => NetworkApiDouble.Deliver(eventName, sender, payload);

    /// <summary>Every handler the process has registered, across all names. A registration cannot be taken back and
    /// a second claim of a name faults, so a re-attach may only reuse what is already there.</summary>
    internal static int RegistrationCount => NetworkApiDouble.RegistrationCount;

    /// <summary>How many handlers the game-side registration holds for an event. The layer claims each message kind
    /// exactly once per process, and the game faults on a duplicate, so this is the count the shipped API would have
    /// accepted.</summary>
    internal static int HandlerCount(string eventName) => NetworkApiDouble.HandlerCount(eventName);
}
