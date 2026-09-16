// The shipped assembly's type name, so the transport calls GTFO-API exactly as it does in the game while the test
// assembly routes those calls to the double. This file is the only place that name appears.
using System;
using SNetwork;

namespace GTFO.API
{
    internal static class NetworkAPI
    {
        internal static void RegisterEvent<T>(string eventName, Action<ulong, T> onReceive) where T : struct
            => ForgeRuntime.Network.Tests.NetworkApiDouble.RegisterEvent(eventName, onReceive);

        internal static void RegisterFreeSizedEvent(string eventName, Action<ulong, byte[]> onReceiveBytes)
            => ForgeRuntime.Network.Tests.NetworkApiDouble.RegisterFreeSizedEvent(eventName, onReceiveBytes);

        // The game attributes a send to the local session itself. The rig stands in for that attribution with
        // NetworkApiDouble.Sender, which the harness sets to the participant it is sending as, so the transport keeps
        // calling exactly the shipped signature.
        internal static void InvokeEvent<T>(string eventName, T payload, SNet_ChannelType channel = SNet_ChannelType.GameOrderCritical)
            => ForgeRuntime.Network.Tests.NetworkApiDouble.InvokeEvent(eventName, payload, channel);

        internal static void InvokeFreeSizedEvent(string eventName, byte[] payload, SNet_ChannelType channel = SNet_ChannelType.GameOrderCritical)
            => ForgeRuntime.Network.Tests.NetworkApiDouble.InvokeFreeSizedEvent(eventName, payload, channel);
    }
}
