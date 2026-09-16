// Stand-in for the GTFO-API network surface. The real assembly ships in BepInExPack_GTFO and cannot load here, so
// this double reproduces the exact member names and signatures the transport calls, keeps its own registrations,
// and records every send. A test uses the recording to route a message from one participant to another.
//
// A send here records only: the API hands the payload to SNet, and the receiving machine's registration is what
// dispatches it. The rig models that by having the check deliver a recorded send to the receiver it means, so a
// send in one process never doubles as a delivery in the same process. <see cref="Deliver"/> is the other
// direction: it runs a registered handler as if the datagram had arrived from another machine, which is the only
// way to exercise the transport's receive path.
//
// Two properties of the shipped API are reproduced on purpose. A name is claimed once per process and a second
// claim faults: the transport's one registration per process is what this double proves, because the rig stands in
// for both participants in one process. And a registration is process-lifetime — there is no unregister — so the
// recording is the only thing that can be cleared between cases.
using System;
using System.Collections.Generic;
using SNetwork;

namespace ForgeRuntime.Network.Tests;

internal static class NetworkApiDouble
{
    internal sealed record Send(string EventName, bool FreeSized, object Payload, SNet_ChannelType Channel, ulong Sender);

    private static readonly Dictionary<string, List<Action<ulong, object>>> Typed = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, List<Action<ulong, byte[]>>> Sized = new(StringComparer.Ordinal);
    private static readonly List<Send> _sent = new();

    /// <summary>Every send, in order. It grows as a check sends, so a check that wants to know whether a send
    /// happened compares <see cref="SentCount"/> before and after rather than expecting an empty list.</summary>
    internal static IReadOnlyList<Send> Sent => _sent;

    internal static int SentCount => _sent.Count;

    /// <summary>The session the game would attribute a send to. In the game this is the local SNet session; in the
    /// test rig it is whichever participant the check is currently sending as.</summary>
    internal static ulong Sender { get; set; }

    /// <summary>Every handler the process has registered, across all names. A registration cannot be taken back, so
    /// this count only grows; a check that re-attaches compares it to prove nothing was claimed twice.</summary>
    internal static int RegistrationCount
    {
        get
        {
            var count = 0;
            foreach (var handlers in Typed.Values) count += handlers.Count;
            foreach (var handlers in Sized.Values) count += handlers.Count;
            return count;
        }
    }

    /// <summary>How many handlers an event holds. The shipped API faults on a second claim of a name, so a layer
    /// that re-registers does not merely count two here: it throws.</summary>
    internal static int HandlerCount(string eventName)
    {
        if (eventName == null) return 0;
        if (Typed.TryGetValue(eventName, out var typed)) return typed.Count;
        if (Sized.TryGetValue(eventName, out var sized)) return sized.Count;
        return 0;
    }

    internal static void RegisterEvent<T>(string eventName, Action<ulong, T> onReceive) where T : struct
    {
        if (onReceive == null) throw new ArgumentNullException(nameof(onReceive));
        var name = Claim(eventName);
        if (!Typed.TryGetValue(name, out var handlers)) Typed[name] = handlers = new List<Action<ulong, object>>();
        handlers.Add((sender, payload) => onReceive(sender, (T)payload));
    }

    internal static void RegisterFreeSizedEvent(string eventName, Action<ulong, byte[]> onReceiveBytes)
    {
        if (onReceiveBytes == null) throw new ArgumentNullException(nameof(onReceiveBytes));
        var name = Claim(eventName);
        if (!Sized.TryGetValue(name, out var handlers)) Sized[name] = handlers = new List<Action<ulong, byte[]>>();
        handlers.Add(onReceiveBytes);
    }

    internal static void InvokeEvent<T>(string eventName, T payload, SNet_ChannelType channel = SNet_ChannelType.GameOrderCritical)
    {
        var name = Require(eventName);
        if (!Typed.ContainsKey(name))
            throw new ArgumentException("Event " + eventName + " is not registered.", nameof(eventName));
        _sent.Add(new Send(eventName, false, payload!, channel, Sender));
    }

    internal static void InvokeFreeSizedEvent(string eventName, byte[] payload, SNet_ChannelType channel = SNet_ChannelType.GameOrderCritical)
    {
        var name = Require(eventName);
        if (!Sized.ContainsKey(name))
            throw new ArgumentException("Event " + eventName + " is not registered.", nameof(eventName));
        if (payload == null || payload.Length == 0) throw new ArgumentException("Free-sized payload must not be empty.", nameof(payload));
        _sent.Add(new Send(eventName, true, payload, channel, Sender));
    }

    /// <summary>
    /// Runs the registered handler for an event as the game would on an inbound datagram. A typed payload is the
    /// struct the event was registered with, and a free-sized payload is its encoded body, so this crosses exactly
    /// the boundary the shipped API crosses.
    /// </summary>
    internal static void Deliver(string eventName, ulong sender, object payload)
    {
        var name = Require(eventName);
        if (Typed.TryGetValue(name, out var typed)) { typed[0](sender, payload); return; }
        if (Sized.TryGetValue(name, out var sized)) { sized[0](sender, (byte[])payload); return; }
        throw new ArgumentException("Event " + eventName + " is not registered.", nameof(eventName));
    }

    /// <summary>Clears the recording, and only the recording: the registrations are the process's, exactly as the
    /// shipped API's are, so a case that opens a fresh participant reuses them instead of claiming the names again.</summary>
    internal static void ClearRecording()
    {
        _sent.Clear();
        Sender = 0;
    }

    private static string Require(string eventName)
    {
        if (string.IsNullOrEmpty(eventName)) throw new ArgumentNullException(nameof(eventName));
        return eventName;
    }

    /// <summary>The one claim a name gets, with the shipped API's own fault text for a repeat.</summary>
    private static string Claim(string eventName)
    {
        var name = Require(eventName);
        if (Typed.ContainsKey(name) || Sized.ContainsKey(name))
            throw new ArgumentException("An event with the name " + name + " has already been registered.", nameof(eventName));
        return name;
    }
}
