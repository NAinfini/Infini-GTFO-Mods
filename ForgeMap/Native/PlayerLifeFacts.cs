using System;

using System.Collections.Generic;

using System.Globalization;

using System.Text.Json;

using ForgeRuntime.Framework;

using HarmonyLib;

using Player;

using SNetwork;

using UnityEngine;


namespace ForgeMap.Native;


/// <summary>What the player-life observation needs from the Map provider's own player identity. The identity

/// half owns which native agent is which recorded life and how to read it; this half owns nothing but the

/// transitions it publishes, so it asks rather than reaching into the identity table. Every method is answered

/// on the owning simulation thread, and a reference the identity no longer holds answers null instead of a

/// guess.</summary>

internal interface IPlayerLifeWorld

{

    /// <summary>Whether this process may publish a host fact right now: the registration is live, the runtime

    /// is ready and this peer is the authority. A refusal means the caller does not read native state either.</summary>

    bool Authoritative { get; }


    /// <summary>Records the lives the level currently holds. Life-changing native callbacks call this before

    /// they read, because a life a spawn path just created is otherwise not in the table yet; a read of a

    /// reference the table does not hold then answers null like any other stale identity.</summary>

    void Reconcile();


    /// <summary>The current position of a recorded life in metres, or null when it cannot be read. The read is

    /// also what a later teleport reports as its origin, so a life whose position was never read reports no

    /// teleport at all instead of an invented origin.</summary>

    double[]? Position(EntityReference reference);


    /// <summary>The position a teleport argument names, in metres, or null when it is not a finite position.</summary>

    double[]? Position(object? locationData);


    /// <summary>The recorded life behind a native `PlayerAgent`, or null when this process tracks no such life.

    /// This is the only way a callback that carries a native agent reaches an entity reference.</summary>

    EntityReference? ReferenceOf(object? agent);


    /// <summary>The recorded life behind the `SNet_Player` a replication callback carried, or null when the

    /// identity tracks no life for that player.</summary>

    EntityReference? ReferenceOf(SNet_Player? player);


    /// <summary>One read of a recorded life right now: whether the agent is alive, whether its locomotion machine

    /// is in the game's downed state, and whether the downed state has already run its revive. Answers null for a

    /// life the identity no longer holds.</summary>

    RuntimePlayerLife? LifeOf(EntityReference reference);


    /// <summary>The actor the game recorded on a downed life's revive interaction, or null when there is none to

    /// read. This is what a `revived` fact reports as its rescuer.</summary>

    EntityReference? ReviverOf(EntityReference reference);


    /// <summary>One native transition: a life entered the downed state (`downed`). The argument is the downed

    /// state that reported it. True when the fact was accepted by the kernel, false when the read was refused,

    /// the transition was already reported, or the event was not this half's to publish.</summary>

    bool Downed(object? downedState);


    /// <summary>One native transition: the downed state ran its revive (`revived`). The argument is the downed

    /// state that reported it. The row's rescuer port is nullable, so a revive whose actor this process cannot

    /// name still publishes and carries null.</summary>

    bool Revived(object? downedState);


    /// <summary>One native transition: a rescue of a downed life began (`revive_started`), reported by the

    /// revive interaction's own state callback with the actor and the downed target it carried. True only for

    /// the transition that starts one rescue.</summary>

    bool ReviveStarted(object? rescuer, object? target);


    /// <summary>One native transition: a rescue that had started ended without the revive having run

    /// (`revive_cancelled`), with the reason from <see cref="PlayerLifeContract"/>'s own vocabulary.</summary>

    bool ReviveCancelled(string reason, object? rescuer, object? target);


    /// <summary>One native transition: a life's alive flag was written and is now false (`died`). The value the

    /// native setter was called with is passed so the transition is judged from the write, not a second read.</summary>

    bool Died(bool alive, object? agent);


    /// <summary>One native transition: a life was teleported from the position it held to the position the call

    /// named (`teleported`). A teleport whose origin or destination cannot be read publishes nothing.</summary>

    bool Teleported(object? agent, object? destination);


    /// <summary>One native transition: the replication path spawned a player (`respawned`). The position the game

    /// handed the spawn is what the fact reports, so it is read from the spawn data and not from the agent the

    /// spawn may not have created yet.</summary>

    bool Respawned(object? spawnData);


    /// <summary>The publication log of this half: one line per fact the kernel accepted, and one per refusal

    /// worth reporting once. A test asserts on it; production reads it in the game's own log.</summary>

    IReadOnlyList<string> Journal { get; }


    /// <summary>Drops this half's per-world tables. The kernel's world epoch is what invalidates the events

    /// already published; this only releases the memory of a world that no longer exists. Idempotent, because a

    /// world change and a teardown can both report it.</summary>

    void BeginWorld();

}


/// <summary>One read of one player life right now, as the native side reports it. This is a value: a callback

/// that read it never keeps it across a native call.</summary>

internal readonly record struct RuntimePlayerLife(bool Alive, bool Downed, bool Revived);


/// <summary>The player-life observation half of the Map provider: `forge.trigger.player.*` for one life's

/// transitions. It publishes through the Map registration the session already owns, one fact per native

/// transition, and it publishes nothing it did not read.

///

/// Publication rules, shared with the map-object half of this provider:

/// <list type="bullet">

/// <item>Only the host publishes. A client's copy of a life transition is a second publisher of a fact the

/// kernel answers per host, so a client read is refused and reported once per reason.</item>

/// <item>One native transition is one event id: the fact, the world epoch, the life the fact is about and a

/// per-life transition number. The kernel's own ledger answers a repeated report with `duplicate` instead of

/// dispatching twice, and the transition number is claimed before the publish, so a game callback that runs

/// twice for one transition (a host body and its sync body, a state entered through two entries) costs nothing

/// and can never reuse an id the kernel has already seen.</item>

/// <item>A fact whose required port cannot be read is not published at all. `player` is required on every row

/// here, so an unresolvable player publishes nothing; a port the catalog marks nullable carries JSON null when

/// the native side genuinely names nobody. A port that is merely unreadable is left out of the payload, which

/// is the framework's own "not observable" and never a claim that the port was written with nothing.</item>

/// <item>Every id and every diagnostic is built from the recorded entity id, the fact name and the reason word.

/// The account key a life is keyed by internally is never formatted, never logged and never placed in an id.</item>

/// </list>

///

/// The native entry points this half hooks are frozen in `evidence/player-life-facts.json`; a hook decides only

/// when to read, never what a fact says.</summary>

internal sealed class PlayerLifeFacts : IPlayerLifeWorld

{

    private const string Prefix = PlayerIdentityModule.EntityKind + ":";

    private const string WorldScope = "gtfo.world:";


    /// <summary>What this half already published or already decided about one life in one world. One life is

    /// one subject: the entity id carries the per-world player number, so a replaced life is a new subject with

    /// its own transitions, and a world change clears the table.</summary>

    private sealed class LifeRecord

    {

        /// <summary>A downed transition was reported for this life and no revive has ended it yet.</summary>

        internal bool Downed;

        /// <summary>A rescue is running right now, with the actor that started it.</summary>

        internal EntityReference? Rescuer;

        /// <summary>The revive of the running rescue already ran, so its end is a revive and not a cancellation.</summary>

        internal bool ReviveRan;

        /// <summary>The life's death was reported, so the same state is not a second death.</summary>

        internal bool Dead;

        /// <summary>The position this half read for the life, which is what a teleport reports as its origin.</summary>

        internal double[]? Position;

        internal long Transitions;

    }


    private readonly RuntimeModuleHandle _registration;

    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;

    private readonly RuntimeKernel _kernel;

    private readonly Func<bool> _authority;

    private readonly Action<string> _report;

    private readonly Action<string> _log;

    private readonly Dictionary<string, LifeRecord> _lives = new(StringComparer.Ordinal);

    private readonly List<string> _journal = new();

    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    private readonly int _threadId = Environment.CurrentManagedThreadId;

    private RuntimeLifecycleSubscription? _lifecycle;

    private long _publishedFacts;

    private bool _disposed;


    /// <summary>The player identity every read goes through. Production leaves it null, which means the module

    /// the Map provider registered — the one place the recorded lives and their native agents are known. A

    /// fixture that drives the half without the game-bound session sets it to its own identity, so the half

    /// performs the same reads against the same shape and no read has a second implementation.</summary>

    internal IPlayerLifeWorld? World { get; init; }


    /// <summary>The binding a fact is published through. Production leaves it null, which means the contract's own

    /// derivation from the Map provider; a focused test that cannot be the provider sets it, so the ids it

    /// registers are the ids this half publishes and the registry still refuses a row without a binding.</summary>

    internal Func<string, string>? BindingOverride { get; init; }


    private IPlayerLifeWorld? Lives => World ?? PlayerIdentityModule.Current;


    private string BindingOf(string fact) => BindingOverride?.Invoke(fact) ?? PlayerLifeContract.BindingOf(fact);


    /// <summary>Attaches this half to the Map registration the session already owns. The subscription that drops

    /// the per-world tables is taken here, and a failure to take it is a failure to start: a half that kept a

    /// dead world's transitions would report a teleport from a level that no longer exists.</summary>

    internal PlayerLifeFacts(RuntimeModuleHandle registration, RuntimeKernel kernel, Func<bool> authority,

        Action<string> report, Action<string> log)

    {

        _registration = registration ?? throw new ArgumentNullException(nameof(registration));

        _gates = registration.SubscriptionGates();

        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));

        _authority = authority ?? throw new ArgumentNullException(nameof(authority));

        _report = report ?? throw new ArgumentNullException(nameof(report));

        _log = log ?? throw new ArgumentNullException(nameof(log));

        _lifecycle = _registration.ObserveLifecycle(value =>

        {

            if (value.Kind == RuntimeLifecycleKind.WorldChanged

                || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) BeginWorld();

        });

    }


    /// <summary>Events this half handed to the kernel with status `queued`: the count a test asserts on. A

    /// client, an unreadable life and a repeated transition never contribute.</summary>

    internal long PublishedFacts => _publishedFacts;


    public IReadOnlyList<string> Journal => _journal;


    public bool Authoritative => !_disposed && _registration.IsRegistered

        && _kernel.StartupState == RuntimeStartupState.Ready && _authority();


    // ----------------------------------------------------------------- reading


    /// <summary>The identity records the level's lives; nothing else about this half's own reads changes. It is

    /// what the world contract asks of an identity, and it is refused on a peer that may not read.</summary>

    public void Reconcile()

    {

        CheckThread();

        if (!Authoritative) return;

        Lives?.Reconcile();

    }


    /// <summary>Whether one reference is a life this process may read right now. Only the host reads, so a

    /// client's callback reads nothing at all, and a life the identity no longer holds answers null exactly as a

    /// life the world never held does.</summary>

    private bool Current(EntityReference reference)

    {

        CheckThread();

        return Authoritative && LifeOf(reference) != null;

    }


    public double[]? Position(EntityReference reference)

    {

        CheckThread();

        if (!Authoritative) return null;

        return Lives?.Position(reference);

    }


    public double[]? Position(object? locationData)

        => locationData is pPlayerLocationData location ? Finite(location.goodPosition) : null;


    public EntityReference? ReferenceOf(object? agent)

    {

        CheckThread();

        if (!Authoritative) return null;

        return Lives?.ReferenceOf(agent);

    }


    public EntityReference? ReferenceOf(SNet_Player? player)

    {

        CheckThread();

        if (!Authoritative) return null;

        return Lives?.ReferenceOf(player);

    }


    public RuntimePlayerLife? LifeOf(EntityReference reference)

    {

        CheckThread();

        if (!Authoritative) return null;

        return Lives?.LifeOf(reference);

    }


    public EntityReference? ReviverOf(EntityReference reference)

    {

        CheckThread();

        if (!Authoritative) return null;

        return Lives?.ReviverOf(reference);

    }


    // ------------------------------------------------------------- transitions


    /// <summary>A life entered the game's downed state. The state's own enter bodies differ between the host and

    /// a client, so the reading is taken from the locomotion machine's current state rather than from which body

    /// ran: the fact is published once per downed episode, whichever body reported it.</summary>

    public bool Downed(object? downedState)

    {

        CheckThread();

        return downedState is PLOC_Downed downed && Note(downed.m_owner);

    }


    /// <summary>The downed state ran its revive. The row is published from the revive itself and not from the

    /// state leaving, so a revive whose state transition is deferred still reports the moment the game restored

    /// the player, with the actor the rescue was started by.</summary>

    public bool Revived(object? downedState)

    {

        CheckThread();

        if (downedState is not PLOC_Downed downed) return false;

        if (downed.m_owner is not PlayerAgent player) return false;

        var reference = ReferenceOf(player);

        if (reference is not { } life || !Current(life)) return false;

        Track(life);

        var entry = _lives[life.Id];

        // A revive is the end of a downed episode: a life that was never reported downed has no episode to end.

        if (!entry.Downed || entry.ReviveRan) return false;

        var actor = ReviverOf(life) ?? entry.Rescuer;

        if (!Publish(life, "revived", entry, PlayerLifeContract.RevivedPayload(life, actor))) return false;

        entry.ReviveRan = true; entry.Downed = false; entry.Rescuer = null; entry.Dead = false;

        return true;

    }


    /// <summary>A rescue began. The rescuer port is required on this row, so a rescue whose actor this process

    /// cannot name publishes nothing: a fact that says "someone" would be a fact about nobody.</summary>

    public bool ReviveStarted(object? rescuer, object? target)

    {

        CheckThread();

        if (!Subject(target, out var life, out var entry)) return false;

        if (entry.Rescuer != null || entry.ReviveRan) return false;

        var actor = ReferenceOf(rescuer);

        if (actor == null)

        {

            ReportOnce("revive-rescuer", "a revive interaction named a rescuer this process tracks no life for; "

                + "no revive_started fact was published.");

            return false;

        }

        if (!Publish(life, "revive_started", entry, PlayerLifeContract.ReviveStartedPayload(life, actor))) return false;

        entry.Rescuer = actor;

        return true;

    }


    /// <summary>A running rescue ended without the revive. Only a rescue this half saw start can be cancelled:

    /// an interaction that ends without ever having started one is not a rescue the players watched happen.</summary>

    public bool ReviveCancelled(string reason, object? rescuer, object? target)

    {

        CheckThread();

        if (!Subject(target, out var life, out var entry)) return false;

        if (entry.Rescuer == null || entry.ReviveRan) return false;

        var actor = ReferenceOf(rescuer) ?? entry.Rescuer;

        if (!Publish(life, "revive_cancelled", entry, PlayerLifeContract.ReviveCancelledPayload(life, actor, reason))) return false;

        entry.Rescuer = null;

        return true;

    }


    /// <summary>A life's alive flag was written false. The transition is judged from the write and from the

    /// identity's own read, so a write to an agent this provider does not track publishes nothing.</summary>

    public bool Died(bool alive, object? agent)

    {

        CheckThread();

        if (alive) return false;

        if (agent is not PlayerAgent player) return false;

        var reference = ReferenceOf(player);

        if (reference is not { } life || !Current(life)) return false;

        Track(life);

        var entry = _lives[life.Id];

        if (entry.Dead) return false;

        if (LifeOf(life) is not { Alive: false }) return false;

        if (!Publish(life, "died", entry, PlayerLifeContract.DiedPayload(life))) return false;

        entry.Dead = true; entry.Downed = false; entry.Rescuer = null; entry.ReviveRan = false;

        return true;

    }


    /// <summary>A life was warped. The origin is the position this half read for the life and the destination is

    /// the position the call named; a life whose position was never read, or a call that names no finite

    /// position, publishes nothing rather than an invented end of the move.</summary>

    public bool Teleported(object? agent, object? destination)

    {

        CheckThread();

        if (agent is not PlayerAgent player) return false;

        var reference = ReferenceOf(player);

        if (reference is not { } life || !Current(life)) return false;

        Track(life);

        var entry = _lives[life.Id];

        if (entry.Position is not { } from) return false;

        if (Position(destination) is not { } to) return false;

        if (!Publish(life, "teleported", entry, PlayerLifeContract.TeleportedPayload(life, from, to))) return false;

        entry.Position = to;

        return true;

    }


    /// <summary>The replication path spawned a player. This is the one native path that creates a player in a

    /// level — the level's first landing and a checkpoint reload both go through it — so `respawned` is defined

    /// as exactly this: a player the replication manager (re)created. See the evidence file.</summary>

    public bool Respawned(object? spawnData)

    {

        CheckThread();

        if (spawnData is not pPlayerSpawnData spawn) return false;

        if (Lives is not { } identity) return false;

        // The identity records the life the spawn just created, and the replication data names which player it

        // is; a life for which no entity is recorded yet publishes nothing.

        // The identity records the life the spawn just created before this half asks it which life that is.

        identity.Reconcile();

        if (identity.ReferenceOf(spawn.snetPlayer) is not { } life) return false;

        Track(life);

        var entry = _lives[life.Id];

        // The position the game handed the spawn is the one position this fact can report: the agent the spawn

        // creates may not exist yet at this point in the native call.

        if (Position(spawn.locationData) is not { } to) return false;

        if (!Publish(life, "respawned", entry, PlayerLifeContract.RespawnedPayload(life, to))) return false;

        entry.Position = to;

        return true;

    }


    // ------------------------------------------------------------ the one pass


    /// <summary>The downed transition of one read. The reading is taken once per native callback and judged

    /// against what this half already reported for that life, so one transition is one fact and a repeated

    /// callback for the same state is nothing.</summary>

    private bool Note(object? agent)

    {

        CheckThread();

        if (agent is not PlayerAgent player) return false;

        var reference = ReferenceOf(player);

        if (reference is not { } life || !Current(life)) return false;

        Track(life);

        var entry = _lives[life.Id];

        if (entry.Downed) return false;

        // Downed is the locomotion machine's own state, and the state's enter bodies differ between the host and

        // a synced peer: the transition is judged from that state instead of from which body reported it. A dead

        // agent is never downed, so a life that ended is not reported as a downing.

        if (LifeOf(life) is not { Alive: true, Downed: true }) return false;

        if (!Publish(life, "downed", entry, PlayerLifeContract.DownedPayload(life))) return false;

        entry.Downed = true; entry.ReviveRan = false; entry.Rescuer = null; entry.Dead = false;

        return true;

    }


    private bool Subject(object? target, out EntityReference reference, out LifeRecord entry)

    {

        entry = null!; reference = default;

        var life = ReferenceOf(target);

        if (life is not { } named || !Current(named)) return false;

        Track(named);

        reference = named; entry = _lives[named.Id];

        return true;

    }


    /// <summary>The one subject per life, created with the life's position so the first transition that needs an

    /// origin has a real one. A life the identity no longer holds is never tracked.</summary>

    private void Track(EntityReference reference)

    {

        if (_lives.ContainsKey(reference.Id)) return;

        _lives[reference.Id] = new LifeRecord { Position = Position(reference) };

    }


    /// <summary>Publishes one fact of one life. The event id is the fact's own identity — the fact, the world,

    /// the life and that life's transition number — so a repeated native report of one transition is the

    /// kernel's own `duplicate` answer and a different transition of the same fact in the same world is its own

    /// id.</summary>

    private bool Publish(EntityReference reference, string fact, LifeRecord entry, JsonElement outputs)

    {

        string eventId = Prefix + reference.Id + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)

            + ":" + entry.Transitions.ToString(CultureInfo.InvariantCulture);

        // The transition number is claimed before the publish: a refused publish must not let the next report

        // reuse an id the kernel may already have seen.

        entry.Transitions += 1;

        // Nothing is listening on this fact's binding: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built. The transition above is still claimed,
        // because a report seen while nobody listened is one this half has already made.
        string binding = BindingOf(fact);

        if (_gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers) return false;

        var answer = _registration.Publish(new RuntimeEvent(eventId, binding, _kernel.WorldEpoch,

            Math.Max(0, _kernel.CurrentTick), Scope(), outputs));

        if (answer.Status == "queued")

        {

            _publishedFacts++;

            Record("player." + fact + " id=" + reference.Id + " transition="

                + (entry.Transitions - 1).ToString(CultureInfo.InvariantCulture));

            return true;

        }

        if (answer.Status == "rejected")

            ReportOnce("publish:" + fact + ":" + answer.Code, "player " + fact + " fact rejected: " + answer.Code);

        return false;

    }


    private string Scope() => WorldScope + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);


    private static double[]? Finite(Vector3 position)

        => float.IsFinite(position.x) && float.IsFinite(position.y) && float.IsFinite(position.z)

            ? new double[] { position.x, position.y, position.z } : null;


    private void Record(string line)

    {

        _journal.Add(line);

        _log("map." + line);

    }


    private void ReportOnce(string key, string message)

    {

        if (_reported.Add(key)) { _journal.Add(message); _report("Map player life observation: " + message); }

    }


    private void CheckThread()

    {

        if (Environment.CurrentManagedThreadId != _threadId)

            throw new RuntimeContractException("wrong-thread", "Player life observation requires the runtime's own simulation thread.");

    }


    /// <summary>Drops this half's per-world tables. The registration belongs to the session, which disposes it

    /// for every half of this provider; this half only releases what it read in the world that just ended.</summary>

    public void BeginWorld()

    {

        CheckThread();

        _lives.Clear();

        _reported.Clear();

    }


    public void Dispose()

    {

        CheckThread();

        if (_disposed) return;

        _disposed = true;

        _lifecycle?.Dispose();

        _lifecycle = null;

        _lives.Clear();

    }

}


/// <summary>Every Harmony patch class the player-life observation installs, and the one entry each of them reaches

/// the life half through. Each patch is a public body the game itself calls for the transition it reports, and

/// each one only decides when to read:

/// <list type="bullet">

/// <item>`PLOC_Downed.Enter` and `PLOC_Downed.SyncEnter` are the downed state's own two enter bodies — the host

/// runs one, a synced peer runs the other — and both reach the downed transition once, because the fact is

/// judged from the locomotion machine's current state.</item>

/// <item>`PLOC_Downed.OnPlayerRevived` is the revive the state itself ran, which is the only native signal that

/// separates a revive from the dead transition.</item>

/// <item>`Interact_Revive.OnInteractorStateChanged` is the revive interaction's own state callback: the player it

/// carries is the rescuer, and the state says whether that player is interacting or has stopped.</item>

/// <item>`Agent.Alive`'s setter is the write that ends a life; the hook reads the flag the native body was handed

/// instead of reading it a second time.</item>

/// <item>`PlayerAgent.WarpTo` is the call that moves a player, and it carries the destination it was given.</item>

/// <item>`PlayerReplicationManager.OnSpawn` is the replication path that (re)creates a player, and it carries the

/// spawn data whose position is the one this fact can report.</item>

/// </list>

/// </summary>

internal static class PlayerLifeHooks

{

    /// <summary>Every patch class this half installs, as one list the plugin adds to the set it processes.</summary>

    internal static IReadOnlyList<Type> Types { get; } = Array.AsReadOnly(new[]

    {

        typeof(DownedStateReadback), typeof(SyncedDownedStateReadback), typeof(RevivedStateReadback),

        typeof(ReviveInteractionReadback), typeof(PlayerDiedReadback), typeof(PlayerWarpedReadback),

        typeof(PlayerRespawnedReadback)

    });


}


[HarmonyPatch(typeof(PLOC_Downed), nameof(PLOC_Downed.Enter))]

internal static class DownedStateReadback

{

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]

    private static void Postfix(PLOC_Downed __instance)

        => Plugin.Session?.GuardLifeFacts(facts => facts.Downed(__instance));

}


[HarmonyPatch(typeof(PLOC_Downed), nameof(PLOC_Downed.SyncEnter))]

internal static class SyncedDownedStateReadback

{

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]

    private static void Postfix(PLOC_Downed __instance)

        => Plugin.Session?.GuardLifeFacts(facts => facts.Downed(__instance));

}


[HarmonyPatch(typeof(PLOC_Downed), nameof(PLOC_Downed.OnPlayerRevived))]

internal static class RevivedStateReadback

{

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]

    private static void Postfix(PLOC_Downed __instance)

        => Plugin.Session?.GuardLifeFacts(facts => facts.Revived(__instance));

}


[HarmonyPatch(typeof(Interact_Revive), nameof(Interact_Revive.OnInteractorStateChanged))]

internal static class ReviveInteractionReadback

{

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]

    private static void Postfix(Interact_Revive __instance, PlayerAgent sourceAgent, bool state)

        => Plugin.Session?.GuardLifeFacts(facts =>

        {

            // The argument is the interacting player; the instance's own target agent is the downed player whose

            // revive interaction this is. Both are handed to the half as native agents and resolved back through

            // the identity there, never off the argument's own fields.

            var target = __instance.m_interactTargetAgent;

            _ = state

                ? facts.ReviveStarted(sourceAgent, target)

                : facts.ReviveCancelled(PlayerLifeContract.LeftReason, sourceAgent, target);

        });

}


[HarmonyPatch(typeof(Agents.Agent), "set_Alive")]

internal static class PlayerDiedReadback

{

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]

    private static void Postfix(Agents.Agent __instance, bool value)

    {

        if (value) return;

        Plugin.Session?.GuardLifeFacts(facts => facts.Died(false, __instance));

    }

}


[HarmonyPatch(typeof(PlayerAgent), nameof(PlayerAgent.WarpTo))]

internal static class PlayerWarpedReadback

{

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]

    private static void Postfix(PlayerAgent __instance, pPlayerLocationData locationData)

        => Plugin.Session?.GuardLifeFacts(facts =>

        {

            _ = facts.Teleported(__instance, locationData);

        });

}


[HarmonyPatch(typeof(PlayerReplicationManager), nameof(PlayerReplicationManager.OnSpawn))]

internal static class PlayerRespawnedReadback

{

    [HarmonyPostfix, HarmonyPriority(Priority.Last)]

    private static void Postfix(pPlayerSpawnData spawnData)

        => Plugin.Session?.GuardLifeFacts(facts => facts.Respawned(spawnData));

}

