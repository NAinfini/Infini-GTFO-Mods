// The network binding's game half reads SNet and GTFO-API's NetworkAPI, and its transport claims the API's event
// names; none of that can load in this harness. What this harness does compile is GameRuntimeBridge, whose network
// contract is narrow: start with the runtime, adopt the loaded plan set once discovery has run, re-read the session
// when the local player can move, and announce the epoch the kernel really reached together with the transition that
// ended the previous world. This double records exactly those calls so a case can assert the bridge's side of it.
using System.Collections.Generic;
using ForgeRuntime.Framework;
using ForgeRuntime.Network;

namespace ForgeRuntime.GameBindings
{
    internal static class NetworkBinding
    {
        internal static readonly List<string> Calls = new();
        internal static readonly List<(long Epoch, WorldTransition Transition, string Detail)> Worlds = new();

        internal static void Start() => Calls.Add("start");
        internal static void Stop() => Calls.Add("stop");
        internal static void RefreshSession() => Calls.Add("session");
        internal static void AdoptPlans(IReadOnlyList<RuntimePlanIdentity> plans) => Calls.Add("plans:" + plans.Count);
        internal static void AdvanceWorld(long epoch, WorldTransition transition, string detail)
        {
            Calls.Add("world:" + epoch);
            Worlds.Add((epoch, transition, detail));
        }

        /// <summary>Mirrors the real binding's presentation entry point: the bridge hands it the intents one advance
        /// decided, and this double records how many arrived rather than routing them to a session it does not have.</summary>
        internal static void Present(IReadOnlyList<PresentationOutput> presentations) => Calls.Add("present:" + presentations.Count);

        /// <summary>The owner steps one advance decided, addressed to the one session each changes. Recorded the same
        /// way as the presentation intents: the bridge's side of it is the count and the moment it was handed over.</summary>
        internal static void Own(IReadOnlyList<OwnerOutput> ownerCommands) => Calls.Add("own:" + ownerCommands.Count);

        /// <summary>The host's variable state for the advance that just ran, sent once per advance after it.</summary>
        internal static void SyncVariables() => Calls.Add("variables");
    }
}
