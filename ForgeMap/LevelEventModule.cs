using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The publication half of the level-event rows: what one native report means, the identity of the fact
/// it produces, and the transition that makes it new. Everything that is a decision about publishing lives here;
/// the game-bound half only reports what it read and never decides whether that is news.
///
/// Two of the six rows repeat within one world — a reactor objective advances its chain more than once, and a
/// player walks into zone after zone — so a fact's identity is built from the state the report carries as well as
/// its subject: the same reading twice is one event, and a different reading is its own. The remaining four are
/// one transition per world, and the row says so by publishing on the transition alone.
///
/// The module is game-independent on purpose. It takes the level reference as the string its provider already
/// resolved, the status and chain as the numbers the game's own enum spells, and the three actions as delegates
/// the game-bound half supplies; that is what lets a focused test assert the published ports, the repeat and
/// absence semantics and the world-epoch cleanup without a copy of the game.</summary>
public sealed class LevelEventModule : IDisposable
{
    /// <summary>The one entity namespace the level-scope rows publish under. A row whose fact is about a player,
    /// a zone, a portal or an HSU reports that object through its own ports; this kind is the level session's own
    /// namespace and holds the rows that are about the session itself.</summary>
    public const string EntityKind = "gtfo.level";

    /// <summary>The refusals one of the three actions reports. Each names the one check that refused it, so a
    /// result row and a log line name the same decision without re-deriving it.</summary>
    internal const string AuthorityCode = "authority-or-phase";
    internal const string OperationUnknownCode = "objective-timer-operation-unknown";
    internal const string SecondsInvalidCode = "objective-timer-seconds-invalid";
    internal const string ModeUnknownCode = "dimension-mode-unknown";
    internal const string DimensionInvalidCode = "dimension-index-invalid";
    internal const string EndingUnknownCode = "expedition-ending-unknown";
    /// <summary>A documented recipient port a plan supplied that the native event cannot address: the three rows
    /// of this family act on the whole session, so a plan's own objective, player or participant set is refused
    /// by name rather than served as the session-wide call it is not.</summary>
    internal const string ObjectiveTargetCode = "objective-target-unsupported";
    internal const string PlayersTargetCode = "players-unsupported";
    internal const string ParticipantsTargetCode = "participants-unsupported";
    public const string CommitExceptionCode = "native-commit-exception";

    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly Action<string> _report;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private RuntimeLifecycleSubscription? _lifecycle;
    private long _publishedFacts;
    private bool _disposed;

    /// <summary>One fact's last published state and the transition number that makes its event id. A fact that
    /// repeated the same state has no new transition, which is what keeps a per-frame zone reading from
    /// flooding the kernel.</summary>
    private sealed record State(string Key, long Transition);

    public LevelEventModule(RuntimeKernel kernel, RuntimeModuleHandle registration, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _lifecycle = _registration.ObserveLifecycle(OnLifecycle);
    }

    public bool IsRegistered => !_disposed && _registration.IsRegistered;

    /// <summary>Facts this half actually handed to the kernel (status `queued`). A repeated reading and a client
    /// reading never contribute, which is what makes the count assertable.</summary>
    public long PublishedFacts => _publishedFacts;

    /// <summary>Test-only observation point: the exact event this half hands to the kernel, before it is queued.
    /// Production wires nothing here — a fact's real observation is the dispatch a plan's own binding produces.</summary>
    internal Action<RuntimeEvent>? FactObserver { get; set; }

    /// <summary>Drops this world's own tables. The kernel's world epoch is what invalidates an earlier id; this
    /// only releases the memory of a level that no longer exists, so the next world publishes its own facts rather
    /// than answering from the one that is gone.</summary>
    public void ClearWorld()
    {
        CheckThread();
        _states.Clear();
    }

    // ---- triggers -----------------------------------------------------------------------------------

    /// <summary>The elevator has landed and the expedition is running. One transition per world: the game's own
    /// start entry is called once for the level, and a second call would be the same expedition starting twice.
    /// `levelReference` is the identity the `level` attachment matcher already compares, read by the game-bound
    /// half; a report that could not read one publishes nothing rather than a reference nothing can match.</summary>
    public void ExpeditionStarted(string? levelReference)
    {
        CheckThread();
        if (string.IsNullOrEmpty(levelReference)) return;
        Publish(LevelEventContract.ExpeditionStartedFact, LevelEventContract.ExpeditionStartedCapability, "session",
            "started", Payload(("map", RuntimeJson.From(LevelEventContract.MapReference(levelReference!)))));
    }

    /// <summary>A reactor objective advanced to the next entry of its own event chain. The report is the chain
    /// index the objective machine already keeps, so a wave is a chain step and the fact carries both numbers: a
    /// plan that wants "the third wave" reads `wave`, and one that wants "every wave" subscribes to the row.</summary>
    public void ReactorWaveAdvanced(string layer, int wave, int previous)
    {
        CheckThread();
        if (string.IsNullOrEmpty(layer)) return;
        if (wave <= previous) return;
        Publish(LevelEventContract.ReactorWaveFact, LevelEventContract.ReactorWaveCapability, layer,
            "wave:" + wave.ToString(CultureInfo.InvariantCulture),
            Payload(("wave", RuntimeJson.From(wave)), ("previous", RuntimeJson.From(previous))));
    }

    /// <summary>The HSU objective's own item was taken out. One transition per world: the objective item can be
    /// solved once, and the game's own entry is what the vanilla `ActivateHSU_Events` list is attached to. The
    /// container is reported when the report carries one.</summary>
    public void HsuSampled(string layer, string? containerReference)
    {
        CheckThread();
        if (string.IsNullOrEmpty(layer)) return;
        var ports = new List<(string, JsonElement?)>
        {
            ("objective", RuntimeJson.From(LevelEventContract.ObjectiveReference(layer)))
        };
        if (!string.IsNullOrEmpty(containerReference))
            ports.Add(("container", RuntimeJson.From(new { resourceKind = "item", resourceId = containerReference })));
        Publish(LevelEventContract.HsuSampledFact, LevelEventContract.HsuSampledCapability, layer, "sampled",
            Payload(ports.ToArray()));
    }

    /// <summary>The checkpoint recall finished. One transition per world: the game reports the recall's own
    /// completion, and the row is the "extra handling after a load" hook the checklist asks for — the framework
    /// restores the variables itself.</summary>
    public void CheckpointRestored()
        => Publish(LevelEventContract.CheckpointRestoredFact, LevelEventContract.CheckpointRestoredCapability,
            "checkpoint", "restored", RuntimeJson.EmptyObject);

    /// <summary>One player entered a zone. The subject is the player and the state is the zone, so walking into
    /// three zones is three facts and the same zone twice is one. The zone reference is the three-layer address
    /// the level's own zone table resolves, which is the same one every other level-scope row uses.</summary>
    public void ZoneEntered(EntityReference player, string? zoneReference)
    {
        CheckThread();
        if (string.IsNullOrEmpty(zoneReference)) return;
        if (player.WorldEpoch != _kernel.WorldEpoch) return;
        Publish(LevelEventContract.ZoneEnteredFact, LevelEventContract.ZoneEnteredCapability, player.Id,
            "zone:" + zoneReference, Payload(
                ("player", RuntimeJson.From(player)),
                ("zone", RuntimeJson.From(zoneReference!))));
    }

    /// <summary>A dimension portal sent players through. The state is the destination dimension, so a portal
    /// that sends the team to the same dimension twice is one fact and a portal that sends them somewhere else is
    /// its own. The portal is an entity this provider resolves; a portal with no identity publishes nothing.</summary>
    public void PortalWarped(string portalId, int dimension, int previous)
    {
        CheckThread();
        if (string.IsNullOrEmpty(portalId)) return;
        Publish(LevelEventContract.PortalWarpedFact, LevelEventContract.PortalWarpedCapability, portalId,
            "dimension:" + dimension.ToString(CultureInfo.InvariantCulture), Payload(
                ("portal", RuntimeJson.From(new EntityReference(portalId, _kernel.WorldEpoch, 1))),
                ("dimension", RuntimeJson.From(dimension)),
                ("previous", RuntimeJson.From(previous))));
    }

    // ---- actions ------------------------------------------------------------------------------------

    /// <summary>The countdown command: `add` moves the objective's own countdown by the seconds the input
    /// carries, `reset` puts it back to the value the objective's data block set. Each operation is one native
    /// entry (`AddToTimer` and `ResetTimer`), which is why they are one row with one structural parameter rather
    /// than two rows the author has to choose between. Both write through the game's own level-event executor, so
    /// the change replicates the way a vanilla mount point's does.</summary>
    internal CommandResult ExecuteTimer(CommandContext context, Action<float> addTimer, Action resetTimer)
    {
        if (!context.IsHost) return Refused(AuthorityCode);
        ArgumentNullException.ThrowIfNull(addTimer);
        ArgumentNullException.ThrowIfNull(resetTimer);
        if (Present(context.Inputs, "objectives")) return Refused(ObjectiveTargetCode);
        string operation = Text(context.Parameters, "operation");
        if (Array.IndexOf(LevelEventContract.TimerOperations, operation) < 0) return Refused(OperationUnknownCode);
        float? seconds = Number(context.Inputs, "seconds");
        if (operation == "add" && (seconds == null || !float.IsFinite(seconds.Value) || seconds.Value <= 0))
            return Refused(SecondsInvalidCode);
        try
        {
            if (operation == "add") addTimer(seconds!.Value);
            else resetTimer();
        }
        catch (Exception error)
        {
            ReportOnce(CommitExceptionCode, "objective timer action threw: " + error.GetType().Name);
            return Failure(CommitExceptionCode);
        }
        return Issued("objective-timer-issued");
    }

    /// <summary>The dimension command: `flash` and `warp` send the whole team, `clear` empties a dimension. The
    /// dimension index is the game's own `eDimensionIndex` value; `clear` says whether the destination is emptied
    /// before the move, which is the field the vanilla `DimensionWarpTeam` events use.</summary>
    internal CommandResult ExecuteDimension(CommandContext context, Action<string, int, bool> move)
    {
        if (!context.IsHost) return Refused(AuthorityCode);
        ArgumentNullException.ThrowIfNull(move);
        if (Present(context.Inputs, "players")) return Refused(PlayersTargetCode);
        string mode = Text(context.Parameters, "mode");
        if (Array.IndexOf(LevelEventContract.DimensionModes, mode) < 0) return Refused(ModeUnknownCode);
        if (!context.Parameters.TryGetProperty("clear", out var clear) || clear.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Refused(ModeUnknownCode);
        double? dimension = Number(context.Inputs, "dimension");
        if (mode != "clear" && (dimension == null || dimension.Value < 0 || dimension.Value > int.MaxValue))
            return Refused(DimensionInvalidCode);
        int index = dimension == null ? 0 : (int)dimension.Value;
        try
        {
            move(mode, index, clear.GetBoolean());
        }
        catch (Exception error)
        {
            ReportOnce(CommitExceptionCode, "dimension action threw: " + error.GetType().Name);
            return Failure(CommitExceptionCode);
        }
        return Issued("dimension-" + mode + "-issued");
    }

    /// <summary>The expedition-end command: `instant_win` ends the expedition now, `win_on_death` makes the next
    /// wipe the win the objective's own completion check looks for. Both are the game's own members
    /// (`ForceInstantWin` and `WinOnDeath`), which is why they are one row.</summary>
    internal CommandResult ExecuteExpeditionEnd(CommandContext context, Action<string> end)
    {
        if (!context.IsHost) return Refused(AuthorityCode);
        ArgumentNullException.ThrowIfNull(end);
        if (Present(context.Inputs, "participants")) return Refused(ParticipantsTargetCode);
        string ending = Text(context.Parameters, "ending");
        if (Array.IndexOf(LevelEventContract.ExpeditionOutcomes, ending) < 0) return Refused(EndingUnknownCode);
        try
        {
            end(ending);
        }
        catch (Exception error)
        {
            ReportOnce(CommitExceptionCode, "expedition end action threw: " + error.GetType().Name);
            return Failure(CommitExceptionCode);
        }
        return Issued("expedition-" + ending + "-issued");
    }

    // ---- publication --------------------------------------------------------------------------------

    /// <summary>Publishes one fact of one subject. A repeated reading of one state has no new transition and
    /// therefore no new event id. The host guard is the caller's: every one of these rows is a report a native
    /// callback makes, and the callback is the side that knows whether it is the master.</summary>
    private bool Publish(string fact, string capability, string subject, string stateKey, JsonElement outputs)
    {
        if (_disposed || !_registration.IsRegistered) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        string key = fact + ":" + subject;
        if (_states.TryGetValue(key, out var last) && last.Key == stateKey) return false;
        long transition = last == null ? 1 : last.Transition + 1;
        _states[key] = new State(stateKey, transition);
        // Nothing is listening on this capability's binding: the kernel would answer `no-consumer` for the event
        // this call is about to build, so the event value is never built. The reading above is still remembered,
        // because a state observed while nobody listened is a state this module has already reported.
        if (_gates.TryGetValue(LevelEventContract.Binding(capability), out var gate) && !gate.HasSubscribers) return false;
        var published = new RuntimeEvent(
            EntityKind + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture) + ":" + subject
                + ":" + transition.ToString(CultureInfo.InvariantCulture),
            LevelEventContract.Binding(capability), _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick),
            "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture), outputs);
        FactObserver?.Invoke(published);
        var result = _registration.Publish(published);
        if (result.Status == "queued") { _publishedFacts++; return true; }
        if (result.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + result.Code, "level event fact rejected: " + result.Code);
        return false;
    }

    /// <summary>One payload, port by port. A port whose value the native side could not read is left out rather
    /// than published as a JSON null, which is the framework's own way to say "not observable".</summary>
    private static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return RuntimeJson.From(payload);
    }

    private void OnLifecycle(RuntimeLifecycleEvent value)
    {
        if (value.Kind == RuntimeLifecycleKind.WorldChanged) ClearWorld();
    }

    private static string Text(JsonElement source, string id)
        => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(id, out var value)
            && value.ValueKind == JsonValueKind.String ? value.GetString()! : "";

    /// <summary>Whether a plan supplied one of the recipient ports a row declares. An absent port, a JSON null and
    /// an empty array all mean the plan asked for no target, which is the only form these three rows serve.</summary>
    private static bool Present(JsonElement inputs, string port)
        => inputs.ValueKind == JsonValueKind.Object && inputs.TryGetProperty(port, out var value)
            && value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined)
            && (value.ValueKind != JsonValueKind.Array || value.GetArrayLength() != 0);

    private static float? Number(JsonElement source, string id)
        => source.ValueKind == JsonValueKind.Object && source.TryGetProperty(id, out var value)
            && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) ? (float)number : null;

    /// <summary>One refusal in the shape every handler of this family reports: nothing was written and the code
    /// names the one check that refused it. Exposed because the game-bound half refuses a request the same way
    /// when its own registration is not there.</summary>
    internal static CommandResult Refused(string code)
        => CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", RuntimeJson.EmptyObject);

    private static CommandResult Failure(string code)
        => CommandResult.Create(CommandStatuses.Failed, CommitStates.Unknown, code, "", RuntimeJson.EmptyObject);

    /// <summary>One result row in the catalog's own field order, with this row's own extra field. `target` is left
    /// out: the target of these three actions is the objective machine, the team or the expedition, and none of
    /// them is an entity this runtime tracks, so writing a reference here would name an object no kind resolves.
    /// The row still carries its own field, so a plan that reads `result.seconds` or `result.dimension` gets the
    /// number the action was issued with rather than a second copy of the status.</summary>
    private static CommandResult Issued(string code)
    {
        var row = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["status"] = CommandStatuses.Succeeded,
            ["committed"] = CommitStates.Confirmed,
            ["code"] = code,
            ["target_count"] = 1
        };
        var outputs = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["results"] = new[] { row }
        };
        return CommandResult.Create(CommandStatuses.Succeeded, CommitStates.Confirmed, code, "",
            RuntimeJson.From(outputs));
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new InvalidOperationException("Level event facts must be published from the thread that owns the session.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _lifecycle = null;
    }
}
