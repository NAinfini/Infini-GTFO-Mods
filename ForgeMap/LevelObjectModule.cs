using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The publication half of the level-object facts, game-independent: one entry point per native callback, each
/// taking the values the callback's subject already answered and turning them into one fact through the
/// registration the session owns.
///
/// The game-bound half is the only side that can read a level, so it reads the subject, builds the key and
/// counts a group's members; everything that is a decision about publishing — the identity of one fact, the
/// transition that makes it new, the refusal to publish a reading of nothing, the per-world tables — lives
/// here. That split is what keeps this file compilable against the framework alone, which is how the focused
/// test project exercises every branch of it.
///
/// Every entity these facts publish lives in one namespace, `gtfo.level_object`, and every id in it is built
/// from the key the game-bound half read off the subject: `<category>/<zone...>/<serial>`. The namespace is
/// deliberately not `gtfo.map_object`, whose address grammar is the authoring-time one (a door's zone role, a
/// terminal's placement index); a serial number is a level-build value no plan can be written with in advance,
/// so these objects are consumable as entity values and are not yet mountable addresses.
/// </summary>
public sealed class LevelObjectModule : IDisposable
{
    /// <summary>The one entity namespace of the level objects this slice owns.</summary>
    public const string EntityKind = "gtfo.level_object";

    private const string Faction = "level-object";
    private const string LifeState = "alive";

    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly Action<string> _report;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private readonly Dictionary<string, State> _states = new(StringComparer.Ordinal);
    private readonly Dictionary<string, double> _scanProgress = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private long _publishedFacts;
    private bool _disposed;

    /// <summary>One subject's last published reading and its transition number. A repeated reading of the same
    /// state has no new transition and therefore no new event id.</summary>
    private sealed record State(string Key, long Transition);

    public LevelObjectModule(RuntimeKernel kernel, RuntimeModuleHandle registration, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    public bool IsRegistered => !_disposed && _registration.IsRegistered;

    /// <summary>Facts this half actually handed to the kernel (status `queued`). A repeated reading, a client
    /// reading and a subject with no identity never contribute, which is what makes the count assertable.</summary>
    public long PublishedFacts => _publishedFacts;

    /// <summary>Drops this world's own tables. The kernel's world epoch is what invalidates an earlier id; this
    /// only releases the memory of a level that no longer exists, so the next world reads its own level rather
    /// than answering from the one that is gone.</summary>
    public void ClearWorld()
    {
        CheckThread();
        _states.Clear();
        _scanProgress.Clear();
    }

    // ---- scan ---------------------------------------------------------------------------------------

    /// <summary>One progress reading of one scan, from the instance's own master-side callback. The callback is
    /// the host's own report and its argument is the fraction the native side computed, so the fact reports
    /// that value rather than re-deriving it: the instance's state is written by the body this callback runs
    /// before, and reading it back here would report the state after that write. A reading at or below zero is
    /// the state the callback reports while a scan is only being approached, so it is remembered for the value
    /// row and not published as progress.</summary>
    public void ScanProgress(string scanUid, double progress, IReadOnlyList<EntityReference> participants)
    {
        CheckThread();
        if (string.IsNullOrEmpty(scanUid)) return;
        _scanProgress[scanUid] = progress;
        if (progress <= 0) return;
        Publish(LevelObjectContract.ScanProgressBinding, "scan.progress", scanUid,
            "progress:" + progress.ToString("R", CultureInfo.InvariantCulture),
            Payload(
                ("scan", RuntimeJson.From(Scan(scanUid))),
                ("progress", RuntimeJson.From(progress)),
                ("participants", participants.Count == 0 ? null : RuntimeJson.From(participants)),
                ("count", RuntimeJson.From(participants.Count))));
    }

    /// <summary>One scan whose own replicated state changed. The started and the completed row are the same
    /// instance kind's two readings, which is why one entry point answers both. `active` is the game's own
    /// `IsActive`, `solved` its own `IsSolved`; the game-bound half reads them after the native body returned,
    /// so a fact never claims a state the instance is not in.</summary>
    public void ScanStateChanged(string scanUid, bool active, bool solved)
    {
        CheckThread();
        if (string.IsNullOrEmpty(scanUid)) return;
        if (solved)
        {
            Publish(LevelObjectContract.ScanCompletedBinding, "scan.completed", scanUid, "solved",
                Payload(("scan", RuntimeJson.From(Scan(scanUid)))));
            return;
        }
        if (!active) return;
        Publish(LevelObjectContract.ScanStartedBinding, "scan.started", scanUid, "active",
            Payload(("scan", RuntimeJson.From(Scan(scanUid)))));
    }

    // ---- containers and level items -----------------------------------------------------------------

    /// <summary>One container state change, from the container's own replicated state. The reading is the
    /// game's own status value, which is what the row's enum carries; a value outside the declared set is not
    /// published at all, so a member this provider does not know is never reported as one it does.</summary>
    public void ContainerStateChanged(string containerKey, string state)
    {
        CheckThread();
        if (string.IsNullOrEmpty(containerKey) || string.IsNullOrEmpty(state)) return;
        if (!LevelObjectContract.ContainerStateNames.Contains(state)) return;
        Publish(LevelObjectContract.ContainerStateBinding, "container.state", containerKey, "status:" + state,
            Payload(
                ("container", RuntimeJson.From(LevelObject(containerKey))),
                ("state", RuntimeJson.From(state))));
    }

    /// <summary>One level item changing hands, from the item's own replicated state: `picked-up` when it went
    /// into a player's hands and `placed-in-level` when it came back to the floor. The actor is the player the
    /// state named, resolved by the game-bound half through the player domain's own instance lookup.</summary>
    public void ItemStateChanged(string itemKey, bool pickedUp, EntityReference? actor)
    {
        CheckThread();
        if (string.IsNullOrEmpty(itemKey)) return;
        Publish(LevelObjectContract.ItemPickupBinding, "item.pickup", itemKey,
            pickedUp ? "picked-up" : "placed-in-level",
            Payload(
                ("item", RuntimeJson.From(LevelObject(itemKey))),
                ("picked_up", RuntimeJson.From(pickedUp)),
                ("actor", actor == null ? null : RuntimeJson.From(actor))));
    }

    // ---- value rows ---------------------------------------------------------------------------------

    /// <summary>One scan's current state and progress, for `forge.condition.predicate.scan`. The reading itself
    /// — the game's own `eChainedPuzzleStatus` name and the fraction the master last reported — is the
    /// game-bound half's; the one thing this half adds is the row's predicate answer, `value`, which is whether
    /// that reading is the state the row asked for. A row that names no state, or a state the game cannot be in,
    /// is refused by name rather than answered `false`, so a plan never reads "not solved" out of a typo.</summary>
    public JsonElement ReadScanState(EvaluationContext context)
    {
        CheckThread();
        if (ValueRowReading.ResourceId(context, "scan") is not { } id)
            throw new RuntimeContractException("scan-resource-missing", "The scan input names no resource.");
        if (NamedState(context) is not { } requested)
            throw new RuntimeContractException("scan-state-missing", "The scan row names no state to test.");
        var reader = ScanStateReader ?? throw new RuntimeContractException("scan-unavailable", id);
        var reading = reader(id, _scanProgress.TryGetValue(id, out var progress) ? progress : 0d);
        var state = Text(reading, "state", id);
        if (!LevelObjectContract.ScanStateNames.Contains(state))
            throw new RuntimeContractException("scan-state-unreadable", state);
        return RuntimeJson.From(new Dictionary<string, JsonElement>(StringComparer.Ordinal)
        {
            ["value"] = RuntimeJson.From(string.Equals(state, requested, StringComparison.Ordinal)),
            ["state"] = RuntimeJson.From(state),
            ["progress"] = ValueRowReading.Require(reading, "progress", id)
        });
    }

    /// <summary>The state one scan row asks about. The member is validated against the declared set here, so a
    /// member the game has no status for is refused before the native read rather than compared and answered
    /// `false`.</summary>
    private static string? NamedState(EvaluationContext context)
    {
        if (context.Parameters.ValueKind != JsonValueKind.Object
            || !context.Parameters.TryGetProperty("state", out var value) || value.ValueKind != JsonValueKind.String)
            return null;
        string state = value.GetString()!;
        if (!LevelObjectContract.ScanStateNames.Contains(state))
            throw new RuntimeContractException("scan-state-unknown", state);
        return state;
    }

    private static string Text(JsonElement reading, string port, string id)
    {
        var value = ValueRowReading.Require(reading, port, id);
        if (value.ValueKind != JsonValueKind.String)
            throw new RuntimeContractException("reading-incomplete", id + "." + port);
        return value.GetString()!;
    }

    /// <summary>The game-bound half of the value row, handed in by the session that owns the level. A
    /// registration with no reader answers the row with a refusal instead of an empty value, which is what keeps
    /// a row that cannot be read from looking like a row that read nothing: the scan reader answers the state
    /// name and the fraction, so the whole reading of a native subject stays on the side that can see the
    /// subject.</summary>
    public Func<string, double, JsonElement>? ScanStateReader { get; set; }

    /// <summary>The world the native objects these facts name belong to: the same epoch the references carry,
    /// which is what the registration's entity resolver answers with.</summary>
    public long WorldEpoch => _kernel.WorldEpoch;

    /// <summary>Whether one level-object reference names something of this provider in the world this module
    /// holds. The kind namespace is this module's own and the epoch is the kernel's current one, so a reference
    /// of a level that no longer exists is refused rather than accepted as a same-key object of the new
    /// one.</summary>
    public bool IsCurrent(EntityReference reference) => reference.Id.StartsWith(EntityKind + ":", StringComparison.Ordinal)
        && reference.WorldEpoch == _kernel.WorldEpoch && reference.LifeEpoch == 1;

    /// <summary>Test-only observation point: the exact event this half hands to the kernel, before it is
    /// queued. Production wires nothing here — a fact's real observation is the dispatch a plan's own binding
    /// produces — and a focused case needs the payload without a plan standing behind every row.</summary>
    internal Action<RuntimeEvent>? FactObserver { get; set; }

    // ---- publication --------------------------------------------------------------------------------

    private EntityReference Scan(string uid) => new(EntityKind + ":" + LevelObjectContract.ScanCategory + "/" + uid,
        _kernel.WorldEpoch, 1);

    private EntityReference LevelObject(string key) => new(EntityKind + ":" + key, _kernel.WorldEpoch, 1);

    /// <summary>Publishes one fact of one subject. A repeated reading of one state has no new transition and
    /// therefore no new event id, which is what keeps a per-frame caller from flooding the kernel.</summary>
    private bool Publish(string binding, string fact, string subject, string stateKey, JsonElement outputs)
    {
        if (_disposed || !_registration.IsRegistered) return false;
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        string key = fact + ":" + subject;
        if (_states.TryGetValue(key, out var last) && last.Key == stateKey) return false;
        long transition = last == null ? 1 : last.Transition + 1;
        _states[key] = new State(stateKey, transition);
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built. The reading above is still remembered,
        // because a state observed while nobody listened is a state this module has already reported.
        if (_gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers) return false;
        var published = new RuntimeEvent(
            EntityKind + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture) + ":"
                + subject + ":" + transition.ToString(CultureInfo.InvariantCulture),
            binding, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick),
            "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture), outputs);
        FactObserver?.Invoke(published);
        var result = _registration.Publish(published);
        if (result.Status == "queued") { _publishedFacts++; return true; }
        if (result.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + result.Code, "level object fact rejected: " + result.Code);
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

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Level objects require the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        ClearWorld();
    }
}
