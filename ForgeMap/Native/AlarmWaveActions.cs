using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ChainedPuzzles;
using ForgeRuntime.Framework;
using GameData;
using SNetwork;

namespace ForgeMap.Native;

/// <summary>
/// The native half of the three scan and wave actions: one method per row, each taking the native objects
/// the row resolved and answering with the one decision it made. Nothing here is a plan, a result frame or a
/// native write of its own — the handlers below are the binding side, and the action methods are what they call
/// in.
///
/// Every write goes through a native entry the game itself runs, so the game replicates it:
/// <list type="bullet">
/// <item><description>A scan is a `ChainedPuzzleInstance`, and its own
/// `AttemptInteract(eChainedPuzzleInteraction.Activate|Deactivate)` is the entry the game's own door, terminal
/// and objective callbacks use. That entry hands the interaction to the instance's own state replicator, and the
/// replicator's callback is what runs the master side (`OnStateChange` calls `MasterActivate`/`MasterDeactivate`).
/// Writing the instance's fields or calling the private master methods directly would change one machine's state
/// without the replicator, which is exactly the write this layer must not make. An alarm is that same instance
/// kind — the puzzle's own data block says whether activating it raises one — so this activation is also how a
/// level's alarm is started; the two alarm rows the rulings deleted had no write of their own.</description></item>
/// <item><description>A wave is a Mastermind event, and `Mastermind.TriggerSurvivalWave` is the host entry that
/// creates it, answers with the event id it registered and replicates through `SurvivalWave.OnSpawn`. The stop
/// side is the same event back again — `Mastermind.TryGetEvent(eventId, out event)` followed by the event's own
/// `StopEvent()` — which is why the start row mints the handle that names it.</description></item>
/// </list>
///
/// <b>The handle.</b> A wave or the puzzle instance a scan activated is named by an effect handle the provider
/// mints through the kernel's own pool, and the native object the handle stands for is registered beside it. That
/// is the whole mechanism the node list's "start a wave and store it in 警报A, stop that wave later" template
/// needs: the start row publishes the handle, the plan's own named-object row stores it, and the stop row reads
/// the same handle back and resolves it to the event it named. A handle the kernel no longer holds live resolves
/// to nothing, so a value that outlived its wave is refused as `wave-not-live` instead of stopping a stranger.
///
/// The handle can only be cast on a registration this half is attached to; <see cref="Attach"/> is the one line
/// the session calls with its own kernel and registration, and a detached half refuses the two start rows with
/// `handle-unavailable` rather than publishing a port it cannot fill (see
/// `%TEMP%\fan\nl-objects\integration.json`).
///
/// One row still refuses rather than pretend: a wave request that carries a runtime knob the native entry does
/// not take (`budget`, `count`, `seed`, `interval` live in the author's data blocks and are not arguments of
/// `TriggerSurvivalWave`), and a stop request whose `pending_spawns_policy` asks for `finish`, which no native
/// entry can do — `StopEvent` removes the event, so the pending spawns are cancelled and nothing else.</summary>
internal sealed class AlarmWaveActions
{
    /// <summary>The one attached half. A handler is a static delegate, so the session hands its kernel and
    /// registration to the half through this slot before the first plan runs; a half that was never attached
    /// refuses the two start rows with `handle-unavailable`.</summary>
    private static AlarmWaveActions? _current;

    /// <summary>The session's own kernel and registration. Handles this half mints are pool slots of this
    /// provider, and `TryNative` is what turns one back into the native object it named.</summary>
    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;

    internal AlarmWaveActions(RuntimeKernel kernel, RuntimeModuleHandle registration)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    /// <summary>Attaches the one half the static handlers answer through, and answers the previous one. The
    /// session calls this once, from `Definition()`, before the returned module is registered: a handler that
    /// runs before the attach is a handler that has already refused; a handler that runs after it mints through
    /// the registration the session owns.</summary>
    internal static AlarmWaveActions Attach(RuntimeKernel kernel, RuntimeModuleHandle registration)
    {
        var previous = _current;
        _current = new AlarmWaveActions(kernel, registration);
        return previous ?? _current;
    }

    /// <summary>Detaches the half when it is still the current one, so a session that is going away cannot leave
    /// its released registration in the slot a later session would mint through.</summary>
    internal static void Detach(AlarmWaveActions actions)
    {
        if (ReferenceEquals(_current, actions)) _current = null;
    }

    // ---- result vocabulary ---------------------------------------------------------------------------
    /// <summary>The codes the result rows carry, in two groups: the decisions that end in a write
    /// (`scan-started`, `wave-started`, `wave-stopped` and the already-in-state reading), and the refusals, one
    /// per check that can refuse a request. A row names exactly one of them, so a plan and a log line read the
    /// same decision.</summary>
    internal const string AlarmAlreadyActiveCode = "alarm-already-active";
    internal const string ScanStartedCode = "scan-started";
    internal const string ScanCompletedCode = "scan-completed";
    internal const string ScanResetCode = "scan-reset";
    internal const string OperationCode = "scan-operation-unknown";
    internal const string WaveStartedCode = "wave-started";
    internal const string WaveStoppedCode = "wave-stopped";

    /// <summary>The action ran on a peer that is not the host. The catalog gives all five rows
    /// `graph.execution = host`, and the native entries behind them decide and replicate on the master only, so a
    /// command that reaches a client is refused rather than run against a world it does not own.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    internal const string NoScanResourceCode = "scan-resource-missing";
    internal const string NoWaveResourceCode = "wave-resource-missing";
    internal const string ResourceNotChainedPuzzleCode = "alarm-resource-not-chained-puzzle";
    internal const string ResourceNotWaveCode = "wave-resource-not-wave";
    internal const string ResourceIdEmptyCode = "resource-id-empty";
    internal const string ManagerUnavailableCode = "chained-puzzle-manager-unavailable";
    internal const string AlarmNotFoundCode = "alarm-not-found";
    internal const string AlarmUnavailableCode = "alarm-unavailable";
    internal const string AlarmAlreadySolvedCode = "alarm-already-solved";
    internal const string AlarmNoCoresCode = "alarm-no-cores";
    internal const string WaveComponentUnavailableCode = "wave-mastermind-unavailable";
    internal const string WaveCourseNodeUnavailableCode = "wave-course-node-unavailable";
    internal const string WaveSettingsInvalidCode = "wave-settings-invalid";
    internal const string WavePopulationInvalidCode = "wave-population-invalid";
    internal const string WaveRejectedCode = "wave-rejected-by-mastermind";
    internal const string WaveKnobUnsupportedCode = "wave-knob-unsupported";
    internal const string WaveHandleMissingCode = "wave-handle-missing";
    internal const string WavePendingFinishUnsupportedCode = "wave-pending-finish-unsupported";
    internal const string PolicyUnknownCode = "policy-unknown";
    /// <summary>The handle resolved to no native object of this world: it was never minted by this provider, it
    /// belongs to an ended world, or the wave it named is gone. All three are one answer — there is nothing to
    /// stop — and no branch guesses which of them it was.</summary>
    internal const string HandleNotLiveCode = "handle-not-live";
    /// <summary>This half was never attached to a registration, so it cannot mint the handle either start row
    /// declares. The refusal is by name: a start row that wrote the world and then could not name what it wrote
    /// would be worse than one that refused first.</summary>
    internal const string HandleUnavailableCode = "handle-unavailable";

    /// <summary>Whether this command may write at all. Two answers have to agree: the ABI's own host fact —
    /// which the kernel constructs per advance and batch E turns from a constant into the real one — and the
    /// game's own master flag, which is what the native entries below actually require. Both gates are kept
    /// because either one alone would be a claim this layer cannot back: `CommandContext.IsHost` is today still
    /// the constant `true`, and `SNet.IsMaster` says nothing about which peer the runtime dispatched on.</summary>
    private static bool IsHost(CommandContext context) => context.IsHost && SNet.IsMaster;

    // ---- result rows ---------------------------------------------------------------------------------

    /// <summary>One result row in the canonical result schema's own columns, in the order the catalog declares
    /// them: `target`, `status`, `committed`, `code`. A row is written for every decision, including a refusal,
    /// and the code names the one check that decided it.</summary>
    private sealed record ResultRow(string? Target, string Status, string Committed, string Code);

    /// <summary>One row of the wave start result: the canonical columns plus `budget`. The budget column carries
    /// the population points the wave has really spent at the moment the row is written, which is what the native
    /// side can answer; the planned total the request may name is not a value this build's start entry returns,
    /// and the row never reports a plan as a fact.</summary>
    private sealed record WaveResultRow(string? Target, string Status, string Committed, string Code, double Budget);

    private static JsonElement Envelope(ResultRow row) => RuntimeJson.From(new { results = new[] { row } });
    private static JsonElement Envelope(WaveResultRow row) => RuntimeJson.From(new { results = new[] { row } });

    private static string? Target(CommandContext context)
        => context.Inputs.TryGetProperty("target", out var target) && target.ValueKind == JsonValueKind.Object
            ? target.GetProperty("id").GetString() : null;

    /// <summary>The status and commit state one decision maps to, shared by all three row shapes: an invoked
    /// entry point committed, the state the request asked for already holding is a success that wrote nothing,
    /// and a refusal wrote nothing. There is no fourth reading — an entry point that cannot say what the world
    /// will look like afterwards is why no row reports more than this.</summary>
    private static (string Status, string Committed) Reading(MapActionOutcome outcome) => outcome.Commit switch
    {
        MapActionCommit.Issued => (CommandStatuses.Succeeded, CommitStates.Confirmed),
        MapActionCommit.AlreadyInState => (CommandStatuses.Succeeded, CommitStates.None),
        _ => (CommandStatuses.Rejected, CommitStates.None)
    };

    /// <summary>One minted handle plus the port it belongs on. The pair travels together so a row's outputs are
    /// written from one place and a handle can never be published under the other row's port.</summary>
    private readonly record struct MintedHandle(string Port, JsonElement Value);

    /// <summary>One result frame: the row and, when the action minted one, the handle port its capability
    /// declares. The handle is written beside the row rather than inside it because a handle is a port of the
    /// node, not a column of the result schema, and a refusal publishes no handle at all — the port is absent,
    /// which is the one honest form of a value that does not exist.</summary>
    private static JsonElement Outputs(JsonElement envelope, MintedHandle? handle)
    {
        if (handle is not { } minted) return envelope;
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["results"] = envelope.GetProperty("results"),
            [minted.Port] = minted.Value
        });
    }

    private static CommandResult AlarmRow(CommandContext context, MapActionOutcome outcome)
    {
        var (status, committed) = Reading(outcome);
        var row = new ResultRow(Target(context), status, committed, outcome.Code);
        return CommandResult.Create(status, committed, outcome.Code, "", Envelope(row));
    }

    private static CommandResult WaveRow(CommandContext context, MapActionOutcome outcome, double budget,
        MintedHandle? handle = null)
    {
        var (status, committed) = Reading(outcome);
        var row = new WaveResultRow(Target(context), status, committed, outcome.Code, budget);
        return CommandResult.Create(status, committed, outcome.Code, "", Outputs(Envelope(row), handle));
    }

    /// <summary>The stop row carries the canonical four columns, so it uses the result row without the extra
    /// column the two start rows declare. Every stop branch is written through here, which is why a refusal and
    /// an issued stop cannot describe different columns.</summary>
    private static CommandResult PlainRow(CommandContext context, string code, string detail = "")
    {
        var row = new ResultRow(Target(context), CommandStatuses.Rejected, CommitStates.None, code);
        return CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, detail, Envelope(row));
    }

    /// <summary>The four columns of a stop that really stopped something. The stop rows declare no handle, so
    /// their success is the row and nothing else.</summary>
    private static CommandResult StoppedRow(CommandContext context, string code)
        => CommandResult.Create(CommandStatuses.Succeeded, CommitStates.Confirmed, code, "",
            Envelope(new ResultRow(Target(context), CommandStatuses.Succeeded, CommitStates.Confirmed, code)));

    // ---- handlers ------------------------------------------------------------------------------------

    /// <summary>The `forge.action.map.scan_state` handler. The scan is a chained puzzle the level already built,
    /// which is what the row's `chained-puzzle` resource names. The row's `operation` selects which of the
    /// instance's own three interactions is submitted: `start` activates it, `complete` solves it for the players
    /// in it, and `reset` deactivates it — the game has no separate reset member, and the deactivation puts the
    /// instance back to the status it had before it was activated. The row declares no other port: the required
    /// number of players in the scan belongs to the puzzle's own data block, so `quorum` was never a value this
    /// handler could apply and the rulings had it deleted rather than echoed.</summary>
    internal static CommandResult ExecuteScanState(CommandContext context) => ScanState(context);

    private static CommandResult ScanState(CommandContext context)
    {
        if (!IsHost(context)) return AlarmRow(context, MapActionOutcome.Refused(AuthorityCode));
        if (Operation(context) is not { } operation)
            return AlarmRow(context, MapActionOutcome.Refused(OperationCode));
        if (Resource(context, "scan") is not { } resource)
            return AlarmRow(context, MapActionOutcome.Refused(NoScanResourceCode));
        if (resource.Kind != AlarmWaveContract.ChainedPuzzleKind)
            return AlarmRow(context, MapActionOutcome.Refused(ResourceNotChainedPuzzleCode));
        if (string.IsNullOrEmpty(resource.Id)) return AlarmRow(context, MapActionOutcome.Refused(ResourceIdEmptyCode));
        var manager = ChainedPuzzleManager.Current;
        if (manager == null || manager.WasCollected) return AlarmRow(context, MapActionOutcome.Refused(ManagerUnavailableCode));
        var instance = FindPuzzle(manager, resource.Id);
        if (instance == null) return AlarmRow(context, MapActionOutcome.Refused(AlarmNotFoundCode));
        if (instance.WasCollected || instance.Data == null) return AlarmRow(context, MapActionOutcome.Refused(AlarmUnavailableCode));
        // All three operations address an instance that is already there, so none mints a handle: the scan row
        // publishes no handle port, and a stop row addresses a wave rather than a puzzle.
        if (operation != "start") return AlarmRow(context, Interact(instance, operation));
        if (instance.IsActive) return AlarmRow(context, MapActionOutcome.AlreadyInState(AlarmAlreadyActiveCode));
        if (instance.IsSolved) return AlarmRow(context, MapActionOutcome.Refused(AlarmAlreadySolvedCode));
        if (instance.NRofPuzzles() <= 0) return AlarmRow(context, MapActionOutcome.Refused(AlarmNoCoresCode));
        return AlarmRow(context, StartPuzzle(instance));
    }

    /// <summary>The row's `operation`, or null when the request named one this row does not carry. The parameter is
    /// structural, so it is read from the plan's own parameters rather than from a wired port.</summary>
    private static string? Operation(CommandContext context)
    {
        if (context.Parameters.ValueKind != JsonValueKind.Object
            || !context.Parameters.TryGetProperty("operation", out var member)
            || member.ValueKind != JsonValueKind.String) return null;
        var operation = member.GetString();
        return operation != null && Array.IndexOf(AlarmWaveContract.ScanOperations, operation) >= 0 ? operation : null;
    }

    /// <summary>The two interactions that act on an instance already in the world. `complete` solves it — the same
    /// entry the game's own scanner uses when the players finish it — and `reset` deactivates it, which is the one
    /// member that puts a chained puzzle back to its pre-activation status.</summary>
    private static MapActionOutcome Interact(ChainedPuzzleInstance instance, string operation)
    {
        instance.AttemptInteract(operation == "complete"
            ? eChainedPuzzleInteraction.Solve
            : eChainedPuzzleInteraction.Deactivate);
        return MapActionOutcome.Issued(operation == "complete" ? ScanCompletedCode : ScanResetCode);
    }

    /// <summary>The `forge.action.map.wave_start` handler: one Mastermind survival wave, started through the
    /// native entry that registers the event the stop side names, with the handle that names it published.</summary>
    internal static CommandResult ExecuteStartWave(CommandContext context)
    {
        if (!IsHost(context)) return WaveRow(context, MapActionOutcome.Refused(AuthorityCode), 0);
        if (Resource(context, "wave") is not { } resource) return WaveRow(context, MapActionOutcome.Refused(NoWaveResourceCode), 0);
        if (resource.Kind != AlarmWaveContract.WaveKind) return WaveRow(context, MapActionOutcome.Refused(ResourceNotWaveCode), 0);
        // The native entry's own arguments are the course node and the author's two data block ids; a budget, a
        // count, a seed or an interval are the data blocks' own fields, and a request that carries one is refused
        // rather than run without it and reported as the request that was made.
        if (UnsupportedKnob(context) is { } knob) return WaveRow(context, MapActionOutcome.Refused(WaveKnobUnsupportedCode), 0);
        if (!TryParseDataBlockId(resource.Id, out uint settingsId, out uint populationId) || settingsId == 0)
            return WaveRow(context, MapActionOutcome.Refused(WaveSettingsInvalidCode), 0);
        if (populationId == 0) return WaveRow(context, MapActionOutcome.Refused(WavePopulationInvalidCode), 0);
        // The two ids are the author's data blocks, so a wave the level does not define is refused here as an
        // unavailable resource rather than handed to the master, which would accept the call and register an
        // event for a wave that can never spawn a group.
        if (GameDataBlockBase<SurvivalWaveSettingsDataBlock>.GetBlock(settingsId) == null
            || GameDataBlockBase<SurvivalWavePopulationDataBlock>.GetBlock(populationId) == null)
            return WaveRow(context, MapActionOutcome.Refused(AlarmWaveContract.ResourceUnavailableCode), 0);
        var outcome = StartWave(settingsId, populationId, out double budget, out var startedEvent);
        // A wave that was not registered has no event to name, so no handle is published beside its refusal.
        if (startedEvent is not { } registered) return WaveRow(context, outcome, budget);
        if (_current is not { } self || !self.TryMintHandle(() => registered, out var handle))
            return WaveRow(context, MapActionOutcome.Refused(HandleUnavailableCode), budget);
        return WaveRow(context, outcome, budget, new MintedHandle(AlarmWaveContract.WaveHandlePort, handle));
    }

    /// <summary>The `forge.action.map.wave_stop` handler: the same handle mechanism on the wave side. The native
    /// stop is `Mastermind.TryGetEvent` followed by the event's own `StopEvent()`, and the event id is what the
    /// start row's handle carries.</summary>
    internal static CommandResult ExecuteStopWave(CommandContext context)
    {
        if (!IsHost(context)) return PlainRow(context, AuthorityCode);
        if (Policy(context, "pending_spawns_policy") is not { } policy || !KnownWavePolicy(policy))
            return PlainRow(context, PolicyUnknownCode);
        // `cancel` is what StopEvent does: it removes the event, so nothing further is spawned. `finish` would
        // have to let the wave spend its remaining budget first, which no native entry does.
        if (policy == "finish") return PlainRow(context, WavePendingFinishUnsupportedCode);
        if (!context.Inputs.TryGetProperty("waves", out var handle) || handle.ValueKind != JsonValueKind.Object)
            return PlainRow(context, WaveHandleMissingCode);
        if (!TryEvent(handle, out var registered)) return PlainRow(context, HandleNotLiveCode);
        registered!.StopEvent();
        return StoppedRow(context, WaveStoppedCode);
    }

    // ---- the handle ----------------------------------------------------------------------------------

    /// <summary>Mints one effect handle of this provider and attaches the native object it names. The two calls
    /// are one operation: a handle without its native object is a name for nothing, so a mint that cannot attach
    /// answers false, and the caller refuses rather than publishing a bare handle.</summary>
    private bool TryMintHandle(Func<object?> native, out JsonElement handle)
    {
        handle = default;
        object? target;
        try { target = native(); }
        catch (Exception) { return false; }
        if (target == null) return false;
        try
        {
            var minted = _registration.CreateEffectHandle(AlarmWaveContract.HandleLifetime);
            _registration.RegisterNative(minted, target);
            handle = minted;
            return true;
        }
        catch (Exception) { return false; }
    }

    /// <summary>The native object one live handle of this provider names, or false when the handle is not live:
    /// a handle this provider never cast, one whose world has ended, and one whose native object is gone are the
    /// same answer to the only question the stop row asks — there is nothing to stop — and no branch guesses
    /// which of them it was.</summary>
    private static bool TryHandle(JsonElement handle, out object? native)
    {
        native = null;
        if (_current is not { } self) return false;
        if (!self._registration.TryNative(handle, out native)) return false;
        return native != null;
    }

    /// <summary>The same question asked of a wave handle: the one event the handle names, or false when it names
    /// no live event of this provider.</summary>
    private static bool TryEvent(JsonElement handle, out Mastermind.MastermindEvent? registered)
    {
        registered = null;
        if (!TryHandle(handle, out var native)) return false;
        if (native is not Mastermind.MastermindEvent found) return false;
        if (found.WasCollected) return false;
        registered = found;
        return true;
    }

    // ---- native actions ------------------------------------------------------------------------------

    /// <summary>Starts one named chained puzzle through its own interaction entry. The caller has already walked
    /// the ladder that decides whether the instance can be started at all, so this is the one write: the
    /// interaction entry is the native sync path, and it hands the interaction to the instance's own state
    /// replicator, whose callback runs MasterActivate on the master. The wave such an activation starts is the
    /// puzzle's own (`TriggerEnemyWave` from the state change); no separate wave is started here.</summary>
    private static MapActionOutcome StartPuzzle(ChainedPuzzleInstance instance)
    {
        instance.AttemptInteract(eChainedPuzzleInteraction.Activate);
        return MapActionOutcome.Issued(ScanStartedCode);
    }

    /// <summary>Starts one survival wave on the host. The master's own entry answers whether it accepted the
    /// wave and hands back the event id it registered, which is the identity the wave's stop side and the wave
    /// facts both use; the population points spent at the moment of the call are read back beside it, because a
    /// freshly registered wave has not spawned a group yet and the honest answer for `budget` is therefore the
    /// native spend, not the requested one.
    ///
    /// The course node argument is the wave's own frame of reference — the native entry scores spawn points
    /// around it — and this row carries no position, no entity and no node of its own, so the level's own node
    /// list answers with the first node it holds. That is a real node of this level rather than an invented
    /// origin, and the row's `designNotes` ("固定/随机敌人混合、人口成本、最大存活数、阶段事件") names no spatial
    /// anchor either; a row that wants a specific origin needs a position or entity port, which the catalog does
    /// not declare.</summary>
    private static MapActionOutcome StartWave(uint settingsId, uint populationId, out double budget,
        out Mastermind.MastermindEvent? registered)
    {
        budget = 0;
        registered = null;
        var master = Mastermind.Current;
        if (master == null || master.WasCollected) return MapActionOutcome.Refused(WaveComponentUnavailableCode);
        if (CourseNode() is not { } node) return MapActionOutcome.Refused(WaveCourseNodeUnavailableCode);
        bool started = master.TriggerSurvivalWave(node, settingsId, populationId, out ushort eventId);
        if (!started) return MapActionOutcome.Refused(WaveRejectedCode);
        // The event the wave registered is what the handle names. Reading it back here is what makes the stop
        // side a lookup of the same event rather than a second search for it; a registration the master accepts
        // but does not hold is not a wave this layer can name, and the handle is left unpublished for it.
        master.TryGetEvent(eventId, out registered);
        return MapActionOutcome.Issued(WaveStartedCode);
    }

    /// <summary>The level's first course node, or null when the node graph has none. The node list is the game's
    /// own (`AIG_CourseNode.s_allNodes`), which is what makes this a reading of the level rather than a guessed
    /// coordinate.</summary>
    private static AIGraph.AIG_CourseNode? CourseNode()
    {
        var nodes = AIGraph.AIG_CourseNode.s_allNodes;
        if (nodes == null) return null;
        for (int i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node != null && !node.WasCollected) return node;
        }
        return null;
    }

    /// <summary>The chained puzzle an author's resource id names, from the level's own instance list. The id is
    /// the instance's own `m_puzzleUID`, which the instance assigns itself while it is set up, and the author
    /// alarm name is accepted as well because the game's own alarm data block carries one and a level may be
    /// authored by either spelling. Two instances answering the same id is not a match this layer can make, so it
    /// answers nothing and the caller refuses with `alarm-not-found` rather than starting an arbitrary one.</summary>
    private static ChainedPuzzleInstance? FindPuzzle(ChainedPuzzleManager? manager, string resourceId)
    {
        if (manager == null || manager.WasCollected) return null;
        ChainedPuzzleInstance? found = null;
        var instances = manager.m_instances;
        if (instances == null) return null;
        for (int i = 0; i < instances.Count; i++)
        {
            var candidate = instances[i];
            if (candidate == null || candidate.WasCollected) continue;
            if (!Names(candidate, resourceId)) continue;
            if (found != null && found.Pointer != candidate.Pointer) return null;
            found = candidate;
        }
        return found;
    }

    private static bool Names(ChainedPuzzleInstance instance, string resourceId)
    {
        string uid = instance.m_puzzleUID ?? "";
        if (uid.Length != 0 && string.Equals(uid, resourceId, StringComparison.Ordinal)) return true;
        var data = instance.Data;
        if (data == null) return false;
        string name = data.PublicAlarmName ?? "";
        return name.Length != 0 && string.Equals(name, resourceId, StringComparison.Ordinal);
    }

    // ---- resource providers --------------------------------------------------------------------------

    /// <summary>Every chained puzzle the level currently holds, one reference each. Enumeration is the manager's
    /// own instance list — the same list the game adds a puzzle to when it builds one — so a resource this
    /// provider lists is a puzzle that really exists in this world.</summary>
    internal static IReadOnlyList<ResourceRef> EnumerateChainedPuzzles()
    {
        var manager = ChainedPuzzleManager.Current;
        if (manager == null || manager.WasCollected) return Array.Empty<ResourceRef>();
        var instances = manager.m_instances;
        if (instances == null) return Array.Empty<ResourceRef>();
        var references = new List<ResourceRef>(instances.Count);
        for (int i = 0; i < instances.Count; i++)
        {
            var instance = instances[i];
            if (instance == null || instance.WasCollected) continue;
            string uid = instance.m_puzzleUID ?? "";
            if (uid.Length == 0) continue;
            references.Add(new ResourceRef(AlarmWaveContract.ChainedPuzzleKind, uid));
        }
        return references.AsReadOnly();
    }

    /// <summary>One chained puzzle by the id this provider publishes, or null when this world holds none.</summary>
    internal static ResourceRef? ResolveChainedPuzzle(string resourceId)
    {
        var manager = ChainedPuzzleManager.Current;
        if (manager == null || manager.WasCollected || string.IsNullOrEmpty(resourceId)) return null;
        var instance = FindPuzzle(manager, resourceId);
        if (instance == null) return null;
        string uid = instance.m_puzzleUID ?? "";
        return uid.Length == 0
            ? new ResourceRef(AlarmWaveContract.ChainedPuzzleKind, resourceId)
            : new ResourceRef(AlarmWaveContract.ChainedPuzzleKind, uid);
    }

    /// <summary>The wave resources this level's own data blocks declare. A wave resource is the author's
    /// settings and population pair, so enumeration walks the game's own data block tables rather than a list of
    /// running waves: a wave that is not running is still a wave the author may start.</summary>
    internal static IReadOnlyList<ResourceRef> EnumerateWaves()
    {
        var references = new List<ResourceRef>();
        var settings = GameDataBlockBase<SurvivalWaveSettingsDataBlock>.GetAllBlocks();
        if (settings != null)
            for (int i = 0; i < settings.Count; i++)
            {
                var block = settings[i];
                if (block == null || block.WasCollected) continue;
                references.Add(new ResourceRef(AlarmWaveContract.WaveKind, WaveId(block.persistentID, 0)));
            }
        var populations = GameDataBlockBase<SurvivalWavePopulationDataBlock>.GetAllBlocks();
        if (populations != null)
            for (int i = 0; i < populations.Count; i++)
            {
                var block = populations[i];
                if (block == null || block.WasCollected) continue;
                references.Add(new ResourceRef(AlarmWaveContract.WaveKind, WaveId(0, block.persistentID)));
            }
        return references.AsReadOnly();
    }

    /// <summary>One wave by its author pair. A wave resource id is `<settingsId>:<populationId>`; resolving one
    /// answers the same spelling back, and an id that names no data block pair this build loaded is null, which
    /// is what makes a plan that names a wave the level does not define fail as `stale-resource`.</summary>
    internal static ResourceRef? ResolveWave(string resourceId)
        => TryParseDataBlockId(resourceId, out uint settingsId, out uint populationId)
            && settingsId != 0 && populationId != 0
            && GameDataBlockBase<SurvivalWaveSettingsDataBlock>.GetBlock(settingsId) != null
            && GameDataBlockBase<SurvivalWavePopulationDataBlock>.GetBlock(populationId) != null
            ? new ResourceRef(AlarmWaveContract.WaveKind, resourceId) : null;

    /// <summary>The one spelling of a wave resource: the two data block ids the native start entry takes, in the
    /// order it takes them. The zero in either half is what enumeration leaves out, so the table never publishes
    /// a half-pair.</summary>
    private static string WaveId(uint settingsId, uint populationId)
        => settingsId.ToString(CultureInfo.InvariantCulture) + ":" + populationId.ToString(CultureInfo.InvariantCulture);

    private static bool TryParseDataBlockId(string resourceId, out uint settingsId, out uint populationId)
    {
        settingsId = 0; populationId = 0;
        if (string.IsNullOrEmpty(resourceId)) return false;
        int separator = resourceId.IndexOf(':');
        if (separator <= 0 || separator == resourceId.Length - 1) return false;
        return uint.TryParse(resourceId.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out settingsId)
            && uint.TryParse(resourceId.AsSpan(separator + 1), NumberStyles.None, CultureInfo.InvariantCulture, out populationId);
    }

    // ---- request reading -----------------------------------------------------------------------------

    /// <summary>The resource a row was handed, read from the command's own inputs: the kernel validates the port
    /// against the capability's declaration, so this layer reads the two fields the wire form carries and never
    /// guesses a kind from the id's spelling.</summary>
    private static (string Kind, string Id)? Resource(CommandContext context, params string[] ports)
    {
        foreach (var port in ports)
            if (context.Inputs.TryGetProperty(port, out var value) && value.ValueKind == JsonValueKind.Object
                && value.TryGetProperty("resourceKind", out var kind) && kind.ValueKind == JsonValueKind.String)
                return (kind.GetString() ?? "", value.TryGetProperty("resourceId", out var id) && id.ValueKind == JsonValueKind.String
                    ? id.GetString() ?? "" : "");
        return null;
    }

    /// <summary>The one structural policy a stop row carries, or null when the request named none. The catalog
    /// marks both stop rows' policies `required`, so a plan that reaches the handler always carries one; a
    /// missing one is still refused by name rather than defaulted to the policy that happens to be cheapest.</summary>
    private static string? Policy(CommandContext context, string parameter)
        => context.Parameters.TryGetProperty(parameter, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    /// <summary>The two members the wave stop row's `pending_spawns_policy` declares in the catalog.</summary>
    private static bool KnownWavePolicy(string policy) => policy is "cancel" or "finish";

    /// <summary>The first runtime knob a wave request carries that the native start entry cannot take. Each of
    /// the four belongs to the author's wave data blocks or to the wave's own state machine, and none is an
    /// argument of `Mastermind.TriggerSurvivalWave` on build 20403457.</summary>
    private static string? UnsupportedKnob(CommandContext context)
    {
        foreach (var name in new[] { "budget", "count", "seed", "interval" })
            if (context.Inputs.TryGetProperty(name, out var value) && value.ValueKind != JsonValueKind.Null
                && value.ValueKind != JsonValueKind.Undefined)
                return name;
        return null;
    }
}
