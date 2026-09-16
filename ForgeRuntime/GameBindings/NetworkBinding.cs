using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using ForgeRuntime.Network;
using SNetwork;
// The kernel's own PlanIdentity is a different type with the same name: this file is the boundary that maps one
// onto the other, so the wire type is the one named explicitly.
using PlanIdentity = ForgeRuntime.Network.PlanIdentity;

namespace ForgeRuntime.GameBindings;

/// <summary>
/// The network layer's game-side binding: one <see cref="NetworkHost"/> per session, configured from facts this
/// process already owns — the role SNet reports, the runtime identity, the loaded plan snapshot, the kernel's world
/// epoch — and the transport's event names registered on the first attach. Every decision is in
/// <see cref="NetworkBindingLogic"/>; this half only reads the game and performs the actions.
/// <para>
/// GTFO-API registers an event name once per process and faults on a repeat, so attaching, detaching and attaching
/// again must not re-register: the first attach registers, and a later session becomes the owner of the same
/// registration. A session this binding has left cannot be reached again — the events keep calling into
/// <see cref="RefreshSession"/> only while a binding exists.
/// </para>
/// </summary>
internal static class NetworkBinding
{
    private static NetworkBindingLogic? _binding;

    /// <summary>
    /// Starts the binding. No host exists until SNet reports a session; a suspended host never reaches this call, so
    /// it registers neither a game event nor a protocol event. The plan set a new host compares and announces is the
    /// one plan discovery adopted, and a discovery that lands while a session is already open announces it then.
    /// </summary>
    internal static void Start()
    {
        if (_binding != null) throw new InvalidOperationException("The Forge network binding is already started.");
        _binding = new NetworkBindingLogic(Attach, Suspend, Warn);
        // The game's events are IL2CPP delegates: a method group does not convert to one directly, so the system
        // delegate is materialized first and the game's implicit conversion takes it from there.
        SNet_Events.OnPlayerJoin += (Action)SessionChanged;
        SNet_Events.OnPlayerLeave += (Action)SessionChanged;
    }

    /// <summary>Releases the SNet subscription and the session's host. Idempotent: startup cleanup runs it whether or
    /// not the binding ever started, and a second call must not throw.</summary>
    internal static void Stop()
    {
        SNet_Events.OnPlayerJoin -= (Action)SessionChanged;
        SNet_Events.OnPlayerLeave -= (Action)SessionChanged;
        _binding?.Stop();
        _binding = null;
    }

    /// <summary>
    /// Re-reads the SNet session. The membership events drive it, and the host's own level entry does too: the local
    /// player is in a live session by definition once it can move, and re-reading a session that is already attached
    /// changes nothing. This is the only place the session facts are read; a session that already exists when this
    /// process starts is picked up by whichever of those events comes first.
    /// </summary>
    internal static void RefreshSession()
    {
        var binding = _binding;
        if (binding == null) return;
        // A machine that cannot name the address its player is reached at is in no usable session: it is the same
        // rule the session identity's own zero follows, applied to the half a requested step is delivered by.
        var address = LocalAddress();
        var hasLocalPlayer = SNet.HasLocalPlayer && address.Length > 0;
        binding.Observe(new NetworkSessionFacts(hasLocalPlayer, hasLocalPlayer ? SNet.LocalPlayer.Lookup : 0, address, SNet.IsMaster, MasterSession()));
    }

    /// <summary>
    /// The address this machine's player is reached at, and the game's own answer to which player that is: the
    /// player slot. It is deliberately not the session identity's account id — that value is what the transport
    /// attributes a send to and what a peer's claimed session is compared against, while this one is the address a
    /// requested step names to reach a player. A machine whose slot the game cannot answer for has no address, and
    /// `RefreshSession` reads that as no usable session rather than as an address that matches everybody.
    /// </summary>
    private static string LocalAddress()
    {
        if (!SNet.HasLocalPlayer || SNet.LocalPlayer == null) return "";
        try { return SNet.LocalPlayer.PlayerSlotIndex().ToString(CultureInfo.InvariantCulture); }
        catch (Exception) { return ""; }
    }

    /// <summary>The plan set this process compares and advertises, taken from the kernel once plan discovery has run.
    /// A session that opens later is configured with it; a session that is already open announces it, which is what
    /// tells a host's clients that it can compare plan sets from now on.</summary>
    internal static void AdoptPlans(IReadOnlyList<RuntimePlanIdentity> plans)
    {
        var binding = _binding;
        if (binding == null || plans == null) return;
        var identity = new PlanIdentity[plans.Count];
        for (var index = 0; index < plans.Count; index++)
            identity[index] = new PlanIdentity(plans[index].PlanId, plans[index].ResourceId, plans[index].ResourceRevision, plans[index].BindingPins);
        binding.AdoptPlans(identity);
    }

    /// <summary>The epoch the kernel really reached, sent by the host alone.</summary>
    internal static void AdvanceWorld(long epoch, WorldTransition transition, string detail) => _binding?.Announce(epoch, transition, detail);

    /// <summary>
    /// The presentation steps the advance just decided, sent to the players their provider named. The binding
    /// owns the sending because it owns the session; the kernel owns the decision because it owns the plan. A
    /// process with no session presents nothing: a presentation write reaches a screen through a peer, and there
    /// is no peer to reach.
    /// </summary>
    internal static void Present(IReadOnlyList<PresentationOutput> presentations)
    {
        if (_binding?.Host is not { } host || presentations.Count == 0) return;
        NetworkBindingCommands.Present(host, presentations);
    }

    /// <summary>
    /// The owner steps the advance just decided, each sent to the one session that holds what it changes. The
    /// binding owns the sending because it owns the session; the kernel owns the decision because it owns the plan
    /// and the holder resolver. A process with no session executes nothing: an owner write reaches a world through
    /// the session that holds it, and there is no session to reach.
    /// </summary>
    internal static void Own(IReadOnlyList<OwnerOutput> ownerCommands)
    {
        if (_binding?.Host is not { } host || ownerCommands.Count == 0) return;
        NetworkBindingCommands.Own(host, ownerCommands);
    }

    /// <summary>
    /// The variable state this advance produced, sent by the host. The kernel owns the values, so the binding reads
    /// them from it once per tick and hands the layer the two forms it can send: the whole table for a late joiner
    /// and this advance's own delta. A client reaches this call too and sends nothing: the host owns the values.
    /// </summary>
    internal static void SyncVariables()
    {
        if (_binding?.Host is not { } host || host.Role != SessionRole.Host) return;
        var kernel = GameRuntimeBridge.Kernel;
        if (kernel == null) return;
        host.VariablesSource ??= () => (kernel.ExportVariables(), kernel.ExportVariableDelta());
        NetworkBindingCommands.BroadcastVariables(host, kernel);
    }

    private static void SessionChanged() => GameRuntimeBridge.Guard(RefreshSession);

    private static NetworkHost Attach(NetworkSessionFacts facts, IReadOnlyList<PlanIdentity>? plans)
    {
        var kernel = GameRuntimeBridge.Kernel ?? throw new InvalidOperationException("A session cannot be attached without the runtime kernel.");
        var host = new NetworkHost(new NetworkHostConfiguration(
            facts.IsMaster ? SessionRole.Host : SessionRole.Client,
            new RuntimeIdentitySummary(kernel.Identity.Id, kernel.Identity.Version, kernel.Identity.ApiVersion, kernel.Identity.GameBuild),
            plans,
            kernel.WorldEpoch));
        // SNet is the only thing that says who the master is, and it is asked per message rather than remembered: a
        // session that changes master changes the answer.
        host.SenderIsMaster = sender => sender != 0 && sender == MasterSession();
        // The domain halves this session runs. The responder is what answers a requested step the host addresses to
        // this side — a screen to present or an owner write to perform — and it is bound here, on the one attach
        // path, so the binding itself installs it on the host it is building: installing a receiver directly on this
        // host would be overwritten by that install and answer nothing. No client-local executor is bound, because
        // this build has no domain consumer for one, and the layer's own no-consumer gate is what a request like
        // that is owed.
        _binding!.BindCommands(null,
            (request, payload, session, address) => NetworkBindingCommands.Execute(kernel, session, address, request, payload));
        // The host's variable state travels over the same layer, and the layer decides which bodies are worth
        // applying: a body from another world or another sender never reaches the kernel.
        host.VariablesObserver = (message, payload) => GameRuntimeBridge.Guard(() => NetworkBindingCommands.ApplyVariables(kernel, message, payload));
        host.Attach(facts.LocalSession, facts.LocalAddress);
        return host;
    }

    private static void Suspend(string code, string detail) => GameRuntimeBridge.Suspend(code, detail);

    private static void Warn(string detail) => Plugin.PluginLog.LogWarning(Plugin.PluginName + ": " + detail);

    private static ulong MasterSession() => SNet.HasMaster && SNet.Master != null ? SNet.Master.Lookup : 0;
}
