using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace ForgeRuntime.Framework;

public sealed record RuntimeIdentity(string Id, string Version, string ApiVersion, string GameBuild);

public sealed record RuntimeLimits
{
    public int MaxEntrypoints { get; init; } = 32;
    public int MaxStepsPerEntrypoint { get; init; } = 128;
    public int MaxTotalSteps { get; init; } = 512;
    public int MaxEventsPerTick { get; init; } = 128;
    public int MaxCommandsPerTick { get; init; } = 512;
    public int MaxQueuedEvents { get; init; } = 1024;
    public int MaxCausalDepth { get; init; } = 16;
    /// <summary>Value slots one step's frame may span (its inputs and its result row together), counting one slot
    /// per value the step reads, an event's row index included. A collection port reserves its whole declared
    /// element width on top of this budget instead, and a handle, a resource and a result row are charged to the
    /// plan's frame bytes rather than to this number at all. A plan that needs more is refused with
    /// `frame-slot-budget`. Defaults are estimates pending measurement against the real plans: see the frame
    /// design's open item on high-water slot counts.</summary>
    public int MaxValueSlotsPerStep { get; init; } = 64;
    /// <summary>Bytes the frames of one plan may occupy: value slots at 24 bytes plus the plan's constant
    /// string bytes. A plan that needs more is refused with `frame-bytes-budget`. Default pending measurement.</summary>
    public int MaxDispatchFrameBytes { get; init; } = 4 * 1024 * 1024;
    /// <summary>Rounds one control step may run in a single dispatch (`repeat`, `for_each`, an `interval`
    /// schedule's pulse count). A plan that asks for more is refused with `iteration-budget`.</summary>
    public int MaxControlIterations { get; init; } = 1024;
    /// <summary>Steps one activation may execute. An activation is one dispatch, one loop round or one schedule
    /// resumption; exceeding this is refused with `dispatch-step-budget`.</summary>
    public int MaxStepExecutionsPerDispatch { get; init; } = 2048;
    /// <summary>Attachments one plan may declare, against `attachment-budget`.</summary>
    public int MaxAttachmentsPerPlan { get; init; } = 256;
    /// <summary>Live handles one plan may hold at once, against `handle-budget`.</summary>
    public int MaxLiveHandles { get; init; } = 1024;
}

/// <summary>
/// The three answers one reference can get from the session's presence read: it is current, the kernel proved it
/// is not, or the observation could not be made at all. Only `forge.condition.predicate.exists` reads through that
/// read, because only that contract treats "proven gone" as `false`; every other consumer of a reference refuses a
/// stale identity exactly like an unreadable one.
/// </summary>
public enum EntityPresence { Current, Absent, Unknown }

/// <summary>
/// The budgeted world read an `evaluate` handler of a `query` step is given. It is the only way a step reaches
/// the world, it is unavailable to a `pure` step, and every refusal is reported with a code instead of an empty
/// answer, so a caller can never mistake "exhausted" for "nothing there". The session itself owns no counters:
/// the kernel's single query budget does.
/// </summary>
public readonly struct RuntimeQuerySession
{
    private readonly RuntimeKernel? kernel;
    private readonly bool available;
    internal RuntimeQuerySession(RuntimeKernel kernel, string nodeId, bool available)
    { this.kernel = kernel; NodeId = nodeId; this.available = available; }
    /// <summary>False inside a `pure` step, whose contract forbids world access.</summary>
    public bool Available => available && kernel != null;
    public string NodeId { get; }
    /// <summary>The world this session reads, as the kernel's own epoch. A provider that resolves a reference to a
    /// native instance of a level needs it for the same reason every other read does: an instance of another world
    /// is not the instance the reference names. Zero when no kernel is behind the session, which is also when no
    /// read succeeds.</summary>
    public long WorldEpoch => kernel?.WorldEpoch ?? 0;
    /// <summary>Reads one entity's current observation through the kernel's query budget. The returned code is
    /// the kernel's own reason (`entity-observer-unavailable`, `entity-query-tick-budget`, a stale identity) or
    /// `query-authority` when this step may not read the world at all.</summary>
    public bool TrySnapshot(EntityReference entity, out RuntimeEntitySnapshot? snapshot, out string code)
    {
        snapshot = null;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var result = kernel!.InspectEntities(new[] { entity });
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        var item = result.Items.Count == 1 ? result.Items[0] : null;
        if (item?.Snapshot == null) { code = item?.Code ?? result.Code; kernel.QueryRefused(code); return false; }
        snapshot = item.Snapshot; code = item.Code; return true;
    }
    /// <summary>
    /// Reads a whole explicit candidate set in one budgeted call, for a selector that ranks or filters the set it
    /// was handed instead of answering about a single reference. This is <see cref="TrySnapshot"/> over many
    /// references: the same kernel read, the same refusal code recorded through <see cref="RuntimeKernel.QueryRefused"/>,
    /// and a partial answer is still a refusal — a selector that ranked half a candidate set would silently drop the
    /// targets it never saw, so the code the kernel answered with is reported and the caller rejects the step. The
    /// caller decides what a partial result means for its own contract; nothing here filters, truncates or reorders.
    /// </summary>
    public bool TrySnapshots(IReadOnlyList<EntityReference> references, out RuntimeEntityQueryResult result, out string code)
    {
        result = null!;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var query = kernel!.InspectEntities(references);
        if (query.Status == "rejected") { code = query.Code; kernel.QueryRefused(code); return false; }
        result = query; code = query.Code; return true;
    }
    /// <summary>
    /// Answers whether a reference still points at something real, in three states instead of two. A live
    /// snapshot is <see cref="EntityPresence.Current"/>; a world that has ended or a life the owning resolver no
    /// longer knows is <see cref="EntityPresence.Absent"/> — the proof the reference is gone, reported as an
    /// answer rather than a refusal, because `forge.condition.predicate.exists` is defined as `false` there; and
    /// an observation the kernel could not make is <see cref="EntityPresence.Unknown"/>, recorded as a refusal
    /// exactly like <see cref="TrySnapshot"/>'s, so the step is rejected and never handed a false that looks
    /// observed. The read is charged to the same per-tick query budget as every other read.
    /// </summary>
    public EntityPresence TryPresence(EntityReference entity, out string code)
    {
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return EntityPresence.Unknown; }
        var result = kernel!.InspectEntities(new[] { entity });
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return EntityPresence.Unknown; }
        var item = result.Items.Count == 1 ? result.Items[0] : null;
        if (item?.Snapshot != null) { code = item.Code; return EntityPresence.Current; }
        code = item?.Code ?? result.Code;
        // Two codes mean the kernel proved the reference is not current: the world epoch has moved on, or the
        // resolver that owns the kind says the instance is gone. Every other failure — no observer for the kind, a
        // callback that threw, an observation that came back null or about another life — is a read that could not
        // be made, and stays a refusal.
        if (code is "stale-world" or "stale-entity") return EntityPresence.Absent;
        kernel.QueryRefused(code);
        return EntityPresence.Unknown;
    }
    /// <summary>
    /// Enumerates every entity of one kind the provider that owns that kind currently tracks. This is the only way
    /// a step learns about entities it was not handed: there is no world scan behind it, no truncation when the
    /// answer is longer than the budget (the call is refused instead), and a kind no registered provider exposes
    /// is refused with `entity-candidates-unavailable` rather than answered with an empty list. The read is charged
    /// to the same per-tick query budget as <see cref="TrySnapshot"/>, and the returned code is either the
    /// provider's own refusal or `query-budget` once the budget is spent.
    /// </summary>
    public bool TryCandidates(string kind, out IReadOnlyList<EntityReference> candidates, out string code)
    {
        candidates = Array.Empty<EntityReference>();
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var result = kernel!.EnumerateEntityCandidates(kind);
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        candidates = Array.AsReadOnly(result.Items.Select(item => item.Reference).ToArray());
        code = result.Code; return true;
    }
    /// <summary>
    /// Reads the zone one entity stands in, from the provider that owns the entity's kind — the region half of
    /// <see cref="TrySnapshot"/>, which carries no zone of its own. An entity the provider answered about is a
    /// successful read even when no zone came back: "this entity stands outside every zone" is a fact about the
    /// world, and a selector that read it as a failure would drop the entity instead of excluding it from the zone
    /// it named. A read that could not be made — a kind whose owner registered no responder, an entity the owner
    /// cannot place right now, a responder that failed — is refused with the kernel's own code or the provider's,
    /// so an unreadable entity is never answered as one that stands nowhere. The read is charged to the same
    /// per-tick query budget as every other read.
    /// </summary>
    public bool TryZone(EntityReference entity, out EntityReference? zone, out string code)
    {
        zone = null;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var answer = kernel!.ZoneOfEntity(entity);
        zone = answer.Zone; code = answer.Code;
        if (answer.Answered) return true;
        kernel.QueryRefused(code);
        return false;
    }
    /// <summary>
    /// Enumerates every resource of one kind the provider that owns that kind currently holds. This is the resource
    /// half of <see cref="TryCandidates"/> and follows the same rules: no world scan behind it, a kind no provider
    /// owns refused with `resource-unavailable` instead of answered with an empty list, and the read charged to the
    /// same per-tick query budget. A provider that cannot answer for something it listed is a refusal too, reported
    /// with the code it refused by.
    /// </summary>
    public bool TryResources(string kind, out IReadOnlyList<ResourceRef> resources, out string code)
    {
        resources = Array.Empty<ResourceRef>();
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var result = kernel!.EnumerateResources(kind);
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        resources = result.References;
        code = result.Code; return true;
    }
    /// <summary>Reads one named resource through the owner of its kind. The id is resolved by the provider that
    /// owns the kind and by nobody else; an id it does not have — and a kind no provider owns at all — is
    /// `resource-unavailable`, never a null handed back as if the world had answered.</summary>
    public bool TryResource(string kind, string resourceId, out ResourceRef? reference, out string code)
    {
        reference = null;
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        var result = kernel!.ResolveResources(kind, new[] { resourceId });
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        reference = result.References[0];
        code = result.Code; return true;
    }
    /// <summary>
    /// Resolves an explicit set of references in one budgeted call, for a selector that ranks or filters resources
    /// it was handed instead of asking about one. Every reference must still be there: a partial answer is a
    /// refusal, reported with its code, because a list that quietly dropped what had gone away would be read as a
    /// world that never held it.
    /// </summary>
    public bool TryResourceRefs(IReadOnlyList<ResourceRef> references, out IReadOnlyList<ResourceRef> resolved, out string code)
    {
        resolved = Array.Empty<ResourceRef>();
        if (!Available) { code = RuntimeAbiCodes.QueryAuthority; kernel?.QueryRefused(code); return false; }
        ArgumentNullException.ThrowIfNull(references);
        var result = kernel!.ResolveResourceRefs(references);
        if (result.Status == "rejected") { code = result.Code; kernel.QueryRefused(code); return false; }
        resolved = result.References;
        code = result.Code; return true;
    }
}

/// <summary>Registration-time mount matcher: given one `attachments[]` target and the entity an event is about,
/// answer whether the plan's behaviour applies to that entity. A provider registers one per attachment kind it
/// owns; the kernel calls it before dispatching a trigger, never after a command has started.</summary>
public delegate bool AttachmentMatcher(string? category, string reference, EntityReference subject);

/// <summary>Registration-time mount matcher for a kind whose target is not an entity: given one `attachments[]`
/// target, answer whether the behaviour applies in this world. The kernel asks it once per dispatch, before any
/// subject is read, so it judges the events that carry no subject at all (a world, timer or pulse event) exactly
/// like the ones that do. A provider registers one per such kind it owns.</summary>
public delegate bool AttachmentScopeMatcher(string? category, string reference);

/// <summary>
/// One attachment kind's matcher, as the provider that owns the kind registers it. A kind whose target is the
/// event's own subject registers <see cref="BySubject"/>; a kind whose target names no entity registers
/// <see cref="ByScope"/> instead, and the kernel then never looks for a subject to hand it. Exactly one of the
/// two is set, which the factories are the only way to build: a kind that declared both would leave the kernel
/// guessing which question to ask, and one that declared neither could never match anything.
/// </summary>
public sealed record AttachmentMatcherRegistration
{
    private AttachmentMatcherRegistration(AttachmentMatcher? subject, AttachmentScopeMatcher? scope)
    { Subject = subject; Scope = scope; }

    /// <summary>The matcher of a kind that is matched against the event's own subjects.</summary>
    public static AttachmentMatcherRegistration BySubject(AttachmentMatcher matcher)
        => new(matcher ?? throw new ArgumentNullException(nameof(matcher)), null);

    /// <summary>The matcher of a kind that is judged from the mount target alone, with no event subject.</summary>
    public static AttachmentMatcherRegistration ByScope(AttachmentScopeMatcher matcher)
        => new(null, matcher ?? throw new ArgumentNullException(nameof(matcher)));

    /// <summary>Set only for a subject-matched kind; the kernel asks a subject registration this and never
    /// <see cref="Scope"/>.</summary>
    public AttachmentMatcher? Subject { get; }
    /// <summary>Set only for a subject-free kind; the kernel asks a scope registration this once per dispatch,
    /// including the dispatches of events that carry no subject.</summary>
    public AttachmentScopeMatcher? Scope { get; }
}

public sealed record EntityReference(string Id, long WorldEpoch, long LifeEpoch);
public sealed record BindingSupport(string BindingId, string Verification, IReadOnlyList<string> RequiredPermissions);
public delegate CommandResult CommandHandler(CommandContext context);

/// <summary>The read-only boundary for an `evaluate` binding. Deliberately narrower than
/// <see cref="CommandContext"/> — no world, causal or authority information — because a node
/// (selector/condition/modifier) can be re-evaluated on demand and must stay side-effect free. The world itself is
/// only ever reached through <see cref="Query"/>; <see cref="Actors"/> and <see cref="Relations"/> are the frozen
/// context of the trigger event being dispatched, delivered whole instead of being looked up again.</summary>
public sealed class EvaluationContext
{
    internal EvaluationContext(string nodeId, JsonElement parameters, JsonElement inputs, RuntimeQuerySession query,
        RuntimeActorContext actors, RuntimeFactionRelations relations)
    {
        NodeId = nodeId; Parameters = parameters; Inputs = inputs; Query = query;
        Actors = actors; Relations = relations;
    }
    public string NodeId { get; }
    public JsonElement Parameters { get; }
    public JsonElement Inputs { get; }
    /// <summary>The budgeted world read of a `query` step; unavailable — every read refused with
    /// `query-authority` — inside a `pure` step, whose contract keeps it out of the world entirely.</summary>
    public RuntimeQuerySession Query { get; }
    /// <summary>The explicit actor roles the dispatched trigger event carries, read through the one
    /// <see cref="RuntimeActorRoles"/> table. A role the event does not carry is absent; a contract that does not
    /// allow that absence is refused with `actor-missing` before the handler runs.</summary>
    public RuntimeActorContext Actors { get; }
    /// <summary>The bounded directed faction relations the world currently exposes. Reading a relation over an
    /// actor the event does not carry is refused by name — no role is inferred from another, and no rule is
    /// invented for a faction pair the world says nothing about.</summary>
    public RuntimeFactionRelations Relations { get; }
}

/// <summary>Returns an object keyed by output port id; the kernel validates every port of the resolved contract
/// against the returned frame before a consumer reads it.</summary>
public delegate JsonElement EvaluatorHandler(EvaluationContext context);

/// <summary>
/// One handler's own port set, declared next to the handler and resolved once at registration against the
/// capability its binding implements. Resolution turns each declared name into the integer the frame path uses —
/// an input's offset inside the request frame, an output's position in the capability's output list, a parameter's
/// declaration index — so nothing reads a value by name. The capability graph is the other half of the same
/// contract: a name only one half knows is a registration error, never a silent misread.
/// </summary>
public sealed class HandlerShape
{
    private readonly List<string> inputs = new();
    private readonly List<string> outputs = new();
    private readonly List<string> parameters = new();
    private Dictionary<string, int> resolvedInputs = new(StringComparer.Ordinal);
    private Dictionary<string, int> resolvedOutputs = new(StringComparer.Ordinal);
    private Dictionary<string, int> resolvedParameters = new(StringComparer.Ordinal);
    private bool resolved;

    /// <summary>The value inputs the handler reads, in the order it names them; an `execution` input is not one.</summary>
    public HandlerShape Inputs(params string[] portIds) => Declare(inputs, portIds, "input");

    /// <summary>The capability outputs the handler writes or reads through its result.</summary>
    public HandlerShape Outputs(params string[] portIds) => Declare(outputs, portIds, "output");

    /// <summary>The parameters the handler reads, whether the plan compiled them as constants or promoted them.</summary>
    public HandlerShape Parameters(params string[] parameterIds) => Declare(parameters, parameterIds, "parameter");

    public IReadOnlyList<string> InputPorts => inputs;
    public IReadOnlyList<string> OutputPorts => outputs;
    public IReadOnlyList<string> ParameterIds => parameters;

    /// <summary>The request-frame offset of a declared input: every value port before it in the resolved contract
    /// occupies its own reserved width, so the offset is fixed by the contract, never by the plan's wiring.</summary>
    public int Input(string portId) => Resolved(resolvedInputs, portId, "input");
    /// <summary>The declared output's position in the capability's output list. A `result` output's row fields are
    /// declared on the port itself (I-CATALOG) and are not addressed here: this is the port's position, not a slot
    /// inside a row.</summary>
    public int Output(string portId) => Resolved(resolvedOutputs, portId, "output");
    /// <summary>The declared parameter's index in the capability's parameter list; the plan's own parameter
    /// descriptor says whether that parameter is a compiled constant or a promoted input.</summary>
    public int Parameter(string parameterId) => Resolved(resolvedParameters, parameterId, "parameter");

    private HandlerShape Declare(List<string> declared, string[] ids, string kind)
    {
        RuntimeJson.Require(!resolved, "shape-port", "A shape is declared before it is resolved.");
        foreach (var id in ids ?? Array.Empty<string>())
        {
            RuntimeJson.Require(id != null && !declared.Contains(id), "shape-port", kind + " " + id);
            declared.Add(RuntimeJson.Text(id!));
        }
        return this;
    }

    private static int Resolved(Dictionary<string, int> table, string id, string kind)
    {
        if (id == null || !table.TryGetValue(id, out var value)) throw new RuntimeContractException("shape-port", kind + " " + id);
        return value;
    }

    /// <summary>
    /// Resolves every declared name against one capability graph, once per binding, at registration. A capability
    /// whose ports are expanded per plan (`variadic`, `portGroups`) has no single registration-time layout, so a
    /// shape that names a port there is refused rather than handed a shifting offset; a shape that names nothing
    /// declares no address and resolves against any graph.
    /// </summary>
    internal void Resolve(JsonElement graph)
    {
        // A shape that names nothing needs no layout, so it describes any graph — including one whose ports are
        // expanded per plan and therefore have no registration-time address at all.
        if (inputs.Count + outputs.Count + parameters.Count == 0) { resolved = true; return; }
        RuntimeJson.Require(!graph.TryGetProperty("variadic", out _) && !graph.TryGetProperty("portGroups", out _),
            "shape-port", "A capability that expands its ports per plan has no fixed shape.");
        var nextInputs = new Dictionary<string, int>(StringComparer.Ordinal);
        var offset = 0;
        foreach (var port in RuntimeJson.Rows(graph, "inputs"))
        {
            var (_, width) = RuntimeGraphContracts.FramePort(port);
            var id = RuntimeJson.Text(port, "id");
            if (inputs.Contains(id))
            {
                RuntimeJson.Require(width > 0, "shape-port", "An execution input carries no value: " + id);
                nextInputs[id] = offset;
            }
            offset += width;
        }
        var nextOutputs = Index(RuntimeJson.Rows(graph, "outputs"));
        var nextParameters = Index(RuntimeJson.Rows(graph, "parameters"));
        foreach (var id in inputs) RuntimeJson.Require(nextInputs.ContainsKey(id), "shape-port", "input " + id);
        foreach (var id in outputs) RuntimeJson.Require(nextOutputs.ContainsKey(id), "shape-port", "output " + id);
        foreach (var id in parameters) RuntimeJson.Require(nextParameters.ContainsKey(id), "shape-port", "parameter " + id);
        // One instance may be shared by two bindings; that is only sound while both capabilities place the same
        // declared names at the same addresses, so a second, different resolution is refused rather than applied.
        RuntimeJson.Require(!resolved || Same(resolvedInputs, nextInputs) && Same(resolvedOutputs, nextOutputs)
            && Same(resolvedParameters, nextParameters), "shape-port", "One shape cannot describe two layouts.");
        resolvedInputs = nextInputs; resolvedOutputs = nextOutputs; resolvedParameters = nextParameters; resolved = true;
    }

    private static Dictionary<string, int> Index(JsonElement[] ports)
    {
        var table = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < ports.Length; index++) table[RuntimeJson.Text(ports[index], "id")] = index;
        return table;
    }

    private static bool Same(Dictionary<string, int> a, Dictionary<string, int> b)
        => a.Count == b.Count && a.All(x => b.TryGetValue(x.Key, out var value) && value == x.Value);
}

/// <summary>RegistryJson is the same ForgeRegistry seed consumed by the website; handlers do not define alternative node semantics.</summary>
public sealed record RuntimeModule(string ApiVersion, string RegistryJson,
    IReadOnlyDictionary<string, CommandHandler> Handlers, IReadOnlyList<BindingSupport> BindingSupport,
    IReadOnlyDictionary<string, Func<EntityReference, bool>>? EntityResolvers = null)
{
    /// <summary>Each handler's own port set, keyed by handler name — command handlers and evaluators alike. The
    /// registry resolves every declared shape once and rejects a supplied handler without one (`missing-shape`), the
    /// same exact-set rule the handler and evaluator tables already follow.</summary>
    public IReadOnlyDictionary<string, HandlerShape> Shapes { get; init; } = EmptyShapes;
    private static readonly IReadOnlyDictionary<string, HandlerShape> EmptyShapes = new Dictionary<string, HandlerShape>();
    /// <summary>Optional read-only targeting snapshots, owned by the same registered entity namespace.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, RuntimeEntitySnapshot?>>? EntityObservers { get; init; }
    /// <summary>Optional native-instance lookups, owned by the same registered entity namespace. The input is whatever
    /// native object the owning provider documents; any other object must return null rather than guess.</summary>
    public IReadOnlyDictionary<string, Func<object, EntityReference?>>? EntityInstanceResolvers { get; init; }
    /// <summary>`evaluate` binding handlers, keyed by handler name. Most modules register none; this
    /// is a normal empty default, not a compatibility shim.</summary>
    public IReadOnlyDictionary<string, EvaluatorHandler> Evaluators { get; init; } = EmptyEvaluators;
    private static readonly IReadOnlyDictionary<string, EvaluatorHandler> EmptyEvaluators = new Dictionary<string, EvaluatorHandler>();
    /// <summary>The candidate entities this module's own entity kinds expose to a `query` step, keyed by the kind
    /// its resolver already owns (`gtfo.enemy`). Discovery belongs to the provider that tracks the instances, so
    /// the kernel never scans a world: it hands the provider's own list back under the query budget. One kind has
    /// exactly one candidate source, and the source answers only with references of its own kind.</summary>
    public IReadOnlyDictionary<string, Func<IReadOnlyList<EntityReference>>>? EntityCandidates { get; init; }
    /// <summary>The zone one entity of a kind is in, keyed by the entity kind whose resolver this provider already
    /// owns (`gtfo.player` for the package that tracks players, `gtfo.enemy` for the one that tracks enemies). It
    /// is the region half of what a candidate source answers for a kind: the owner of a kind is the only package
    /// that can resolve its references to the native instance the level places, so it is also the only one that can
    /// say which zone that instance stands in. The answer is a zone reference, or null when this provider cannot
    /// place the entity it owns right now — unknown, never "no zone", which is why a reader refuses instead of
    /// treating the entity as outside every zone. Zone identity itself is the level's own coordinates, formatted by
    /// <see cref="RuntimeZones.Reference"/>.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, EntityReference?>>? EntityZones { get; init; }
    /// <summary>Attachment matchers this module owns, keyed by attachment kind (`map-object`, `gear-block`,
    /// `enemy-type`, `level`). A kind has exactly one owner, and each registration says whether its kind is
    /// matched against the event's own subjects or judged from the mount target alone.</summary>
    public IReadOnlyDictionary<string, AttachmentMatcherRegistration>? AttachmentMatchers { get; init; }
    /// <summary>The resource kinds this module owns, keyed by the kind's own name in the shared resource-kind
    /// table. Discovery follows ownership the same way entity candidates do: one kind has exactly one owner, the
    /// owner answers only with references of its own kind, and a kind nobody registered leaves every read of it
    /// refused rather than empty.</summary>
    public IReadOnlyDictionary<string, RuntimeResourceProvider>? ResourceProviders { get; init; }
    /// <summary>Native-instance lookups that are asked about objects rather than about a kind the caller already
    /// knows: each is handed one native object and answers the reference it currently owns, or null when this
    /// provider does not recognize it. They are asked in registration order, and the first non-null answer wins,
    /// which is what lets three packages that each know their own native types share one entry point
    /// (<see cref="RuntimeKernel.ResolveEntity(object)"/>) without knowing about each other. An object nobody
    /// recognizes is null — the same "not current" answer an absent optional package gives — never an exception.
    /// </summary>
    public IReadOnlyList<ObjectEntityResolver>? ObjectEntityResolvers { get; init; }
    /// <summary>The sessions one `presentation` step of this module's own bindings is addressed to, keyed by the
    /// module's provider id. Presentation writes a screen, so the set is a routing decision only the side that
    /// knows its session and its entities can make: the provider is handed the entity references the step's own
    /// `recipients` port carries and answers the sessions they resolve to. Null — or a step it does not answer for
    /// — is refused with `presentation-recipient` rather than sent to an implicit everyone, which is what keeps one
    /// player's value frame off every other player's machine.</summary>
    public IReadOnlyDictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>? PresentationSessions { get; init; }
    /// <summary>The `owner` tier's own routing: which player session holds the equipment one `owner` step of this
    /// module's own bindings is about, keyed by the capability the step implements. A `presentation` step writes a
    /// screen, so its recipient list is answerable before the step runs; an `owner` step writes one instance's
    /// state on the machine that holds it, so the question is about the step's own subject and the provider answers
    /// it from that subject's owner. A null answer means this build cannot name the holder, and the dispatch
    /// refuses the step with `owner-holder` rather than sending it to an implicit everyone.</summary>
    public IReadOnlyDictionary<string, Func<EntityReference, string?>>? OwnerSessions { get; init; }
}

/// <summary>One event a provider publishes: its own payload ports and nothing about an entity behind them. Who the
/// event is about is a payload port like any other value — `self` is the entity the plan's own mount matched at
/// dispatch — so there is no second, event-level subject channel to keep in step with it.</summary>
public sealed record RuntimeEvent(string EventId, string BindingId, long WorldEpoch, long SimulationTick,
    string ScopeId, JsonElement Outputs,
    string? CauseId = null, string? RootEventId = null, int CausalDepth = 0);

public sealed record RuntimeFact(string BindingId, JsonElement Outputs);

public sealed class CommandContext
{
    internal CommandContext(RuntimeEvent origin, long tick, string commandId, string planId, string resourceId,
        string resourceRevision, string nodeId, JsonElement parameters, JsonElement inputs, bool isHost)
    {
        EventId = origin.EventId; CauseId = origin.CauseId; RootEventId = origin.RootEventId ?? origin.EventId;
        WorldEpoch = origin.WorldEpoch; SimulationTick = tick; ScheduledTick = origin.SimulationTick;
        ScopeId = origin.ScopeId; CausalDepth = origin.CausalDepth;
        CommandId = commandId; PlanId = planId; ResourceId = resourceId; ResourceRevision = resourceRevision;
        NodeId = nodeId; Parameters = parameters; Inputs = inputs; IsHost = isHost;
    }
    public string EventId { get; }
    public string? CauseId { get; }
    public string RootEventId { get; }
    public long WorldEpoch { get; }
    public long SimulationTick { get; }
    public long ScheduledTick { get; }
    public string ScopeId { get; }
    public int CausalDepth { get; }
    public string CommandId { get; }
    public string PlanId { get; }
    public string ResourceId { get; }
    public string ResourceRevision { get; }
    public string NodeId { get; }
    /// <summary>Whether the advance that dispatched this command runs the authoritative world: the fact
    /// <see cref="RuntimeKernel.Advance"/> was called with, recorded at construction. A handler that writes game
    /// state asks this before writing; a presentation handler on a replica reads false and presents only.</summary>
    public bool IsHost { get; }
    public JsonElement Parameters { get; }
    public JsonElement Inputs { get; }
    public EntityReference GetEntityInput(string input) => RuntimeJson.Entity(Inputs.GetProperty(input));
}

public static class CommandStatuses
{
    public const string Succeeded = "succeeded";
    public const string Partial = "partial";
    public const string Rejected = "rejected";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";
    public const string Expired = "expired";
}

public static class CommitStates
{
    public const string None = "none";
    public const string Confirmed = "confirmed";
    public const string Unknown = "unknown";
}

public sealed class CommandResult
{
    public const int MaximumFacts = 128;
    public const int MaximumDetailBytes = 64 * 1024;
    private const int MaximumResultBytes = 256 * 1024;

    private CommandResult(string status, string commitState, string code, string detail, JsonElement outputs, IReadOnlyList<RuntimeFact> facts)
    {
        Status = status;
        CommitState = commitState;
        Code = code;
        Detail = detail;
        Outputs = outputs;
        Facts = facts;
    }

    public string Status { get; }
    public string CommitState { get; }
    public string Code { get; }
    public string Detail { get; }
    public JsonElement Outputs { get; }
    public IReadOnlyList<RuntimeFact> Facts { get; }

    public static CommandResult Succeeded(JsonElement outputs, params RuntimeFact[] facts)
        => Create(CommandStatuses.Succeeded, CommitStates.Confirmed, "committed", "", outputs, facts);

    public static CommandResult Partial(JsonElement outputs, string commitState, params RuntimeFact[] facts)
        => Create(CommandStatuses.Partial, commitState, "partial", "", outputs, facts);

    public static CommandResult Partial(JsonElement outputs, params RuntimeFact[] facts)
        => Partial(outputs, CommitStates.Confirmed, facts);

    public static CommandResult Rejected(string code, string detail = "")
        => Create(CommandStatuses.Rejected, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Failed(string code, string detail = "")
        => Create(CommandStatuses.Failed, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult FailedUnknown(string code, string detail = "", params RuntimeFact[] facts)
        => Create(CommandStatuses.Failed, CommitStates.Unknown, code, detail, RuntimeJson.EmptyObject, facts);

    public static CommandResult FailedUnknown(JsonElement outputs, string code, string detail = "", params RuntimeFact[] facts)
        => Create(CommandStatuses.Failed, CommitStates.Unknown, code, detail, outputs, facts);

    public static CommandResult Cancelled(string code, string detail = "")
        => Create(CommandStatuses.Cancelled, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Expired(string code, string detail = "")
        => Create(CommandStatuses.Expired, CommitStates.None, code, detail, RuntimeJson.EmptyObject);

    public static CommandResult Create(string status, string commitState, string code, string detail,
        JsonElement outputs, params RuntimeFact[] facts)
    {
        var safeStatus = RuntimeJson.Text(status);
        var safeCommitState = RuntimeJson.Text(commitState);
        var safeCode = RuntimeJson.Text(code);
        var safeDetail = ValidatedDetail(detail);
        var safeOutputs = SnapshotOutputs(outputs);
        var safeFacts = SnapshotFacts(safeOutputs, facts);
        return new CommandResult(safeStatus, safeCommitState, safeCode, safeDetail, safeOutputs, safeFacts);
    }

    private static string ValidatedDetail(string? detail)
    {
        detail ??= "";
        RuntimeJson.Require(Encoding.UTF8.GetByteCount(detail) <= MaximumDetailBytes, "result-detail-budget", "Command detail exceeds 64 KiB.");
        return detail;
    }

    private static JsonElement SnapshotOutputs(JsonElement outputs)
    {
        RuntimeJson.Require(outputs.ValueKind != JsonValueKind.Undefined, "invalid-result-output", "Command outputs must be a JSON value.");
        var text = outputs.GetRawText();
        RuntimeJson.Require(Encoding.UTF8.GetByteCount(text) <= RuntimeKernel.MaximumEventPayloadBytes, "result-payload-budget", "Command outputs exceed 64 KiB.");
        return RuntimeJson.Parse(text);
    }

    private static IReadOnlyList<RuntimeFact> SnapshotFacts(JsonElement outputs, RuntimeFact[] facts)
    {
        if (facts == null) throw new RuntimeContractException("invalid-result-facts", "Fact collection cannot be null.");
        RuntimeJson.Require(facts.Length <= MaximumFacts, "fact-budget", "A command can publish at most 128 committed facts.");
        var snapshot = new List<RuntimeFact>(facts.Length);
        long bytes = Encoding.UTF8.GetByteCount(outputs.GetRawText());
        foreach (var fact in facts)
        {
            if (fact == null) throw new RuntimeContractException("invalid-result-fact", "Committed fact cannot be null.");
            var bindingId = RuntimeJson.Text(fact.BindingId);
            RuntimeJson.Require(fact.Outputs.ValueKind != JsonValueKind.Undefined, "invalid-fact-output", "Committed fact outputs must be a JSON value.");
            var text = fact.Outputs.GetRawText();
            var size = Encoding.UTF8.GetByteCount(text);
            RuntimeJson.Require(size <= RuntimeKernel.MaximumEventPayloadBytes, "result-fact-payload-budget", "Committed fact exceeds 64 KiB.");
            bytes += size;
            RuntimeJson.Require(bytes <= MaximumResultBytes, "result-facts-budget", "Committed facts exceed the 256 KiB result budget.");
            snapshot.Add(new RuntimeFact(bindingId, RuntimeJson.Parse(text)));
        }
        return snapshot.AsReadOnly();
    }

    internal static string TruncateCode(string? code)
    {
        if (string.IsNullOrWhiteSpace(code)) return "unknown";
        var text = code!.Trim();
        if (text.Length > 256) text = text[..256];
        foreach (var c in text) if (c <= 31 || c == 127) return "invalid-code";
        return text;
    }

    internal static string TruncateDetail(string? detail)
    {
        if (string.IsNullOrEmpty(detail)) return "";
        return detail!.Length <= 4096 ? detail : detail[..4096];
    }
}

internal static class CommandResultRules
{
    internal static bool TryValidate(CommandResult result, out string violation)
    {
        violation = "";
        if (result.Status is not (CommandStatuses.Succeeded or CommandStatuses.Partial or CommandStatuses.Rejected
            or CommandStatuses.Failed or CommandStatuses.Cancelled or CommandStatuses.Expired))
        {
            violation = "unknown-status";
            return false;
        }

        switch (result.Status)
        {
            case CommandStatuses.Succeeded:
                if (result.CommitState != CommitStates.Confirmed) { violation = "succeeded-requires-confirmed"; return false; }
                return true;
            case CommandStatuses.Partial:
                // A partial result may report an unknown commit — it branches on whether any row committed at all,
                // not on fact count — and it may report `none`, which is what a `presentation` handler reports:
                // presenting is work the handler really did, and none of it commits world state.
                if (result.CommitState is not (CommitStates.Confirmed or CommitStates.Unknown or CommitStates.None))
                { violation = "partial-commit-state"; return false; }
                return true;
            case CommandStatuses.Rejected:
            case CommandStatuses.Cancelled:
            case CommandStatuses.Expired:
                if (result.CommitState != CommitStates.None) { violation = "terminal-status-requires-none"; return false; }
                if (result.Facts.Count != 0) { violation = "terminal-status-cannot-carry-facts"; return false; }
                return true;
            case CommandStatuses.Failed:
                if (result.CommitState is not (CommitStates.None or CommitStates.Unknown)) { violation = "failed-commit-state"; return false; }
                if (result.CommitState == CommitStates.None && result.Facts.Count != 0) { violation = "failed-none-cannot-carry-facts"; return false; }
                return true;
            default:
                violation = "unknown-status";
                return false;
        }
    }
}

public sealed record DispatchResult(string Status, string Code, string EventId);
/// <summary>
/// One `presentation` step the host walked, ready to be executed on each recipient. Presentation writes game
/// visuals, not world state, so the host decides when the step runs and who runs it, and the step's own inputs
/// travel resolved: a recipient executes the same handler on inputs the host already validated, without
/// re-evaluating a step, re-matching a mount or committing anything. <see cref="Recipients"/> is a non-empty set
/// of player sessions; an empty one is refused at dispatch with `presentation-recipient` rather than accepted as
/// a broadcast to nobody.
/// </summary>
public sealed record PresentationOutput(string Provider, string PlanId, string NodeId, string CommandId,
    string BindingId, int StepIndex, IReadOnlyList<string> Recipients, string EventId, string ScopeId,
    long WorldEpoch, long SimulationTick, JsonElement Inputs);
/// <summary>
/// One `owner` step the host walked, ready to be executed on the one machine that holds the thing it changes.
/// An owner write is a real world write whose correct machine is not the host — a weapon's magazine belongs to
/// the inventory that holds it — so the host decides the timing, the inputs and the plan step, and the holder
/// executes the same handler on the same inputs. <see cref="Holder"/> is exactly one player session, or the
/// dispatch would have refused the step with `owner-holder`; there is no broadcast form of this tier.
/// </summary>
public sealed record OwnerOutput(string Provider, string PlanId, string NodeId, string CommandId,
    string BindingId, int StepIndex, string Holder, string EventId, string ScopeId,
    long WorldEpoch, long SimulationTick, JsonElement Inputs);
public sealed record CommandReceipt(string CommandId, string EventId, string? CauseId, string RootEventId,
    string PlanId, string ResourceId, string ResourceRevision, string NodeId, string BindingId,
    long WorldEpoch, long SimulationTick, CommandResult Result);
public sealed record EventReceipt(string EventId, string Status, string Code);
public sealed record TickResult(int EventsProcessed, int CommandsExecuted, int DeferredEvents,
    IReadOnlyList<CommandReceipt> Commands, IReadOnlyList<EventReceipt> Events)
{
    public IReadOnlyList<ScheduleReceipt> Schedules { get; init; } = Array.Empty<ScheduleReceipt>();
    public IReadOnlyList<StateLeaseReceipt> StateLeases { get; init; } = Array.Empty<StateLeaseReceipt>();
    /// <summary>The `presentation` steps this advance decided to present, each addressed to the player sessions
    /// the runtime's own recipient resolver named. Empty on a replica, which presents what the host sends rather
    /// than what its own walk decides.</summary>
    public IReadOnlyList<PresentationOutput> Presentations { get; init; } = Array.Empty<PresentationOutput>();
    /// <summary>The `owner` steps this advance decided to dispatch, each addressed to the one player session that
    /// holds the equipment the step names. Empty on a replica, which executes what the host sends rather than what
    /// its own walk decides.</summary>
    public IReadOnlyList<OwnerOutput> OwnerCommands { get; init; } = Array.Empty<OwnerOutput>();
}

public sealed class RuntimeContractException : Exception
{
    public RuntimeContractException(string code, string message) : base(message) { Code = code; }
    public string Code { get; }
}

