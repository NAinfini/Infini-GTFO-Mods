using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeDevelopment.Native;

/// <summary>
/// The authoring-only diagnostic provider. It declares the seven `forge.action.diagnostic.*` rows the catalog
/// carries and registers the four this build can honestly keep the promise of. The other three stay unregistered
/// rather than being bound to a handler that could only refuse:
/// <list type="bullet">
/// <item><description>`trace`, `assert`, `inspect` and `metric` are implemented at the `presentation` tier the
/// catalog gives them, in <see cref="DiagnosticHandlers"/> over the ledger in
/// <see cref="DiagnosticSessions"/>.</description></item>
/// <item><description>`breakpoint` is not registered: its `pause_policy` offers a `pause` this kernel has no
/// state for, and the design's own ruling is that it stays out until a real pause exists. A plan that pins it
/// fails to resolve a binding at load, which is the truthful answer, instead of being handed a breakpoint that
/// only writes a log line.</description></item>
/// <item><description>`cost_estimate` is not registered either, and this is the one place this provider declines a
/// row the design called cheap. The row's subject is a `behavior_graph` resource and its own `effectRequirements`
/// name `costs`, `budgets` and `scenario`; the runtime hands a handler the resource's `{resourceKind, resourceId}`
/// only — the compiled plan and its IR stay inside the kernel — so a handler here could produce a number about
/// something other than the graph it was asked about. The row's own design note ("未知handler成本不能填零宣称
/// 安全") rules that out, so it waits for a kernel-side plan/graph read instead of estimating in the dark. The
/// ledger it would have used is already here.</description></item>
/// <item><description>`spawn_preview` is not registered: its recipient is a `room` resource, ruling 85 deleted the
/// `room` resource kind, and the `zone`-based shape plus the spawn solver it needs are not in this runtime yet. It
/// waits rather than previewing against a resource nobody owns.</description></item>
/// </list>
///
/// The author gate is the one the package already has, checked before anything is registered: the module is only
/// created on the `RuntimeMode.Authoring` branch of <c>Plugin.Load</c>, and a caller that asks for registration
/// from any other mode is answered with <see cref="DiagnosticCodes.Disabled"/> instead of silently growing these
/// endpoints on a player machine.
/// </summary>
internal static class DevelopmentModule
{
    /// <summary>The provider id every row below is owned by: the package's own id, so a manifest reader can tell
    /// which assembly would have to be present for a row to resolve.</summary>
    internal const string ProviderId = "forge.module.forge.development";

    internal const string TraceCapability = "forge.action.diagnostic.trace";
    internal const string AssertCapability = "forge.action.diagnostic.assert";
    internal const string BreakpointCapability = "forge.action.diagnostic.breakpoint";
    internal const string InspectCapability = "forge.action.diagnostic.inspect";
    internal const string MetricCapability = "forge.action.diagnostic.metric";
    internal const string SpawnPreviewCapability = "forge.action.diagnostic.spawn_preview";
    internal const string CostEstimateCapability = "forge.action.diagnostic.cost_estimate";

    internal const string TraceHandlerName = "forge.development.trace";
    internal const string AssertHandlerName = "forge.development.assert";
    internal const string InspectHandlerName = "forge.development.inspect";
    internal const string MetricHandlerName = "forge.development.metric";

    /// <summary>The one permission each row's recipient contract names, spelled as the catalog spells it.</summary>
    internal const string TracePermission = "diagnostic.trace";
    internal const string AssertPermission = "diagnostic.assert";
    internal const string InspectPermission = "diagnostic.inspect";
    internal const string MetricPermission = "diagnostic.metric";

    /// <summary>The domains the catalog gives every row in this family.</summary>
    internal static readonly string[] Domains = { "logic", "editor" };

    /// <summary>The one handler shape each registered row's port layout resolves against. They are declared once
    /// here so the capability row and the handler cannot describe two different layouts.</summary>
    internal static readonly HandlerShape TraceShape = new HandlerShape()
        .Inputs("session", "graph", "sample_rate").Outputs("result").Parameters();
    internal static readonly HandlerShape AssertShape = new HandlerShape()
        .Inputs("session", "condition", "message").Outputs("result").Parameters("severity", "policy");
    internal static readonly HandlerShape InspectShape = new HandlerShape()
        .Inputs("targets", "fields", "budget").Outputs("result", "snapshot").Parameters();
    internal static readonly HandlerShape MetricShape = new HandlerShape()
        .Inputs("session", "value", "window", "sample_rate").Outputs("result").Parameters();

    /// <summary>Where a record goes. The production wiring points this at the package's existing diagnostic
    /// pipeline; a test points it at a list. It is a delegate rather than a direct call so the decision — what to
    /// record, and whether a budget allowed it — stays in <see cref="DiagnosticHandlers"/> while the writing stays
    /// with the report that already owns the file, the classifier and the queue.</summary>
    internal static Action<DiagnosticRecord> Sink { get; set; } = _ => { };

    /// <summary>Records a diagnostic row could not hand to the pipeline because no report was open. It is a count
    /// and never a silent drop: a session whose value here is non-zero produced diagnostics nobody can read.
    /// <see cref="Record"/> is the only writer, and the host wiring replaces <see cref="Sink"/> at load.</summary>
    internal static long SinkDrops { get; private set; }

    /// <summary>The session ledger of the live registration, or null while nothing is registered.</summary>
    internal static DiagnosticSessions? Sessions { get; private set; }

    /// <summary>The kernel handle the module registered under, or null. The provider is a singleton because there
    /// is exactly one authoring runtime per process and these rows are global endpoints, not per-plan ones.</summary>
    internal static RuntimeModuleHandle? Registration { get; private set; }

    internal static bool IsRegistered => Registration != null;

    /// <summary>The kernel this module registered into, or null. It is held because the handlers are built from it
    /// and a rebuild of the handler table has to name them again; nothing else reads it.</summary>
    internal static RuntimeKernel? Kernel { get; private set; }

    /// <summary>The kernel lifecycle subscription the registration opened, so the world boundary reaches the ledger
    /// without a second hook in the plugin. It is disposed with the registration.</summary>
    private static RuntimeLifecycleSubscription? Lifecycle { get; set; }

    /// <summary>Drops the module's own view of its registration. The kernel's registration is released by disposing
    /// the handle; this only forgets the handle so a later <see cref="Register"/> is not answered as a duplicate.
    /// It exists because a process has one authoring runtime and a test process runs many: nothing in the game
    /// calls it.</summary>
    internal static void Forget()
    {
        Lifecycle?.Dispose();
        Lifecycle = null;
        Registration = null;
        Sessions = null;
        Kernel = null;
        SinkDrops = 0;
    }

    /// <summary>
    /// Registers the capability rows and the handlers that implement them. Returns false with a code when the
    /// caller is not the authoring runtime; a second call on a live registration is the state the caller asked
    /// for. `enabled` is the author gate and defaults to on, because the plugin calls this only on its own
    /// Authoring branch — a caller that wants the refusal path says so explicitly rather than relying on a mode
    /// read this package cannot make for itself.
    /// </summary>
    internal static bool Register(RuntimeKernel kernel, out string code, bool enabled = true,
        Action<DiagnosticRecord>? sink = null)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        if (Registration != null) { code = "diagnostic-already-registered"; return true; }
        if (!enabled) { code = DiagnosticCodes.Disabled; return false; }
        if (sink != null) Sink = sink;
        Kernel = kernel;
        var handlers = new DiagnosticHandlers(kernel, () => Sessions, Record);
        RuntimeModuleHandle registration;
        try
        {
            registration = kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
                Handlers(handlers), Supports(), null)
            {
                Shapes = Shapes()
            }, RuntimeLogLevel.Info);
        }
        catch (RuntimeContractException error)
        {
            // A rejected registration leaves this provider exactly as it was: no handle, no session and no
            // half-registered row, so a caller can fix the shape and try again on the same kernel.
            Kernel = null;
            code = error.Code;
            return false;
        }
        Registration = registration;
        var ledger = new DiagnosticSessions(registration, () => kernel.CurrentTick);
        try { Lifecycle = registration.ObserveLifecycle(OnLifecycle); }
        catch
        {
            registration.Dispose();
            Registration = null;
            Kernel = null;
            throw;
        }
        Sessions = ledger;
        code = "diagnostic-registered";
        return true;
    }

    /// <summary>
    /// The world boundary, taken from the kernel's own lifecycle rather than from a game hook: a new world drops
    /// the previous session and opens this world's, and a runtime that failed or stopped drops it without opening
    /// another. Nothing else in this module reacts to a lifecycle event — a tick advance is not a diagnostic.
    /// </summary>
    private static void OnLifecycle(RuntimeLifecycleEvent value)
    {
        var ledger = Sessions;
        if (ledger == null) return;
        if (value.Kind == RuntimeLifecycleKind.WorldChanged
            || value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped)
            ledger.BeginWorld();
        else if (value.Kind == RuntimeLifecycleKind.Snapshot && !ledger.HasSession
            && value.Current.StartupState == RuntimeStartupState.Ready && value.Current.WorldEpoch > 0)
            ledger.TryBegin(1, out _, out _);
    }

    /// <summary>The handler table of the live registration, built again over the ledger the registration owns. The
    /// handlers are closures over state and hold nothing the table itself owns, so this is the same table the
    /// kernel was handed; it exists so a caller can invoke one row's handler directly, which is how the focused
    /// tests reach a row without a compiled plan behind it.</summary>
    internal static Dictionary<string, CommandHandler> HandlersForTest()
        => Kernel == null
            ? throw new InvalidOperationException("The module is not registered.")
            : Handlers(new DiagnosticHandlers(Kernel, () => Sessions, Record));

    /// <summary>The four registered rows, in the order <see cref="CapabilityRows"/> declares them.</summary>
    private static Dictionary<string, CommandHandler> Handlers(DiagnosticHandlers handlers) => new(StringComparer.Ordinal)
    {
        [TraceHandlerName] = handlers.Trace,
        [AssertHandlerName] = handlers.Assert,
        [InspectHandlerName] = handlers.Inspect,
        [MetricHandlerName] = handlers.Metric
    };

    private static Dictionary<string, HandlerShape> Shapes() => new(StringComparer.Ordinal)
    {
        [TraceHandlerName] = TraceShape,
        [AssertHandlerName] = AssertShape,
        [InspectHandlerName] = InspectShape,
        [MetricHandlerName] = MetricShape
    };

    /// <summary>One binding's registration support: the one permission the catalog's recipient contract names for
    /// the row, and `implementation-only` because these rows are observed by the native handler and by nothing
    /// else.</summary>
    private static BindingSupport[] Supports() => new[]
    {
        new BindingSupport(Binding(TraceCapability), "implementation-only", new[] { TracePermission }),
        new BindingSupport(Binding(AssertCapability), "implementation-only", new[] { AssertPermission }),
        new BindingSupport(Binding(InspectCapability), "implementation-only", new[] { InspectPermission }),
        new BindingSupport(Binding(MetricCapability), "implementation-only", new[] { MetricPermission })
    };

    /// <summary>This provider's own binding id for a capability, so the counterpart of a row is readable from
    /// either side.</summary>
    internal static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    // ---- registry rows ---------------------------------------------------------------------------------

    /// <summary>
    /// The registry seed: the provider, the four capability rows and their bindings. The three unregistered rows
    /// are deliberately absent — a row with no binding fails plan resolution at load, which is the answer this
    /// build means to give.
    /// </summary>
    internal static string Registry() => RuntimeJson.From(new
    {
        providers = new[] { new { id = ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = CapabilityRows(),
        bindings = BindingRows()
    }).GetRawText();

    /// <summary>The four capability rows this provider owns, in the catalog's own spelling, port for port.</summary>
    internal static object[] CapabilityRows() => new object[] { TraceRow(), AssertRow(), InspectRow(), MetricRow() };

    /// <summary>`forge.action.diagnostic.trace`: the step's execution, a diagnostic session, the behavior graph
    /// being traced and the sampling rate, answering with the plan's next step and the row's result.</summary>
    internal static object TraceRow() => Capability(TraceCapability, "有预算地记录图执行轨迹", "有预算地记录图的执行轨迹。",
        new object[]
        {
            Execution("in"), SessionHandle(), BehaviorGraph("graph"), Integer("sample_rate")
        },
        TraceOutputs(), Array.Empty<object>(), Recipients("session", "handle", TracePermission));

    /// <summary>`forge.action.diagnostic.assert`: the invariant as a boolean, the message a failure reports, and
    /// the two structural policies. `policy` is narrowed to the two members this build implements — `report` and
    /// `record` — because a member that would mean "halt" has no kernel state behind it and is refused by name.</summary>
    internal static object AssertRow() => Capability(AssertCapability, "校验声明的不变量并报告", "检查一条你声明的不变量并报告。",
        new object[]
        {
            Execution("in"), SessionHandle(), Boolean("condition"), String("message")
        },
        AssertOutputs(),
        new object[] { Structural("severity", new[] { "info", "warning", "error" }), Structural("policy", new[] { "report", "record" }) },
        Recipients("session", "handle", AssertPermission));

    /// <summary>`forge.action.diagnostic.inspect`: the entities to read, the field names to project and the call's
    /// budget, answering with the row's result and the snapshot text. The row's recipient is the target collection
    /// itself, which is how the catalog declares it.</summary>
    internal static object InspectRow() => Capability(InspectCapability, "导出受限状态快照", "导出一份受限的状态快照。",
        new object[]
        {
            Execution("in"), Many("targets", "entity"), String("fields"), Integer("budget")
        },
        InspectOutputs(), Array.Empty<object>(), Recipients("targets", "entity", InspectPermission, "many"));

    /// <summary>`forge.action.diagnostic.metric`: the session, the sample, the window the aggregate covers and the
    /// rate it is taken at.</summary>
    internal static object MetricRow() => Capability(MetricCapability, "记录聚合性能和数量指标", "记录一个聚合的性能或数量指标。",
        new object[]
        {
            Execution("in"), SessionHandle(), Number("value"), Ticks("window"), Integer("sample_rate")
        },
        MetricOutputs(), Array.Empty<object>(), Recipients("session", "handle", MetricPermission));

    /// <summary>The binding rows, one per registered capability, in the same order.</summary>
    internal static object[] BindingRows() => new object[]
    {
        BindingRow(TraceCapability, TraceHandlerName),
        BindingRow(AssertCapability, AssertHandlerName),
        BindingRow(InspectCapability, InspectHandlerName),
        BindingRow(MetricCapability, MetricHandlerName)
    };

    /// <summary>One execute binding row: this provider's own id, the canonical capability, the handler this
    /// assembly supplies, and no dependencies — the closure of a plan that pins it is the row itself.</summary>
    internal static object BindingRow(string capability, string handler) => new
    {
        id = Binding(capability),
        capabilityId = capability,
        providerId = ProviderId,
        handler,
        role = "execute",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    // ---- port and row vocabulary ------------------------------------------------------------------------

    private static object Capability(string id, string label, string description, object[] inputs, object[] outputs,
        object[] parameters, object recipients) => new
    {
        id,
        owner = ProviderId,
        kind = "action",
        label,
        version = "1.0.0",
        parameters = new { description },
        graph = new
        {
            domains = Domains,
            execution = "presentation",
            inputs,
            outputs,
            parameters,
            recipients
        }
    };

    /// <summary>The recipient contract every row here carries: the input the plan wired in, the kind of target
    /// that input addresses, its cardinality, the catalog's own requirement name and the row's result port.</summary>
    private static object Recipients(string input, string target, string requirement, string cardinality = "one")
        => new { input, target, cardinality, requires = new[] { requirement }, result = "result" };

    /// <summary>The session's own handle. Its kind is `request` because that is the kind the runtime's handle API
    /// casts for a provider — the catalog's older `subscription` spelling is a catalog-side reshape this provider
    /// does not get to make for itself.</summary>
    private static object SessionHandle() => new { id = "session", type = "handle", handleKind = "request", lifetime = "session" };

    private static object BehaviorGraph(string id) => new { id, type = "resource", resourceKind = "behavior_graph", schema = "forge.resource.behavior_graph" };
    private static object Execution(string id) => new { id, type = "execution" };
    private static object Integer(string id) => new { id, type = "integer" };
    private static object Number(string id) => new { id, type = "number" };
    private static object String(string id) => new { id, type = "string" };
    private static object Boolean(string id) => new { id, type = "boolean" };
    private static object Ticks(string id) => new { id, type = "integer", unit = "tick" };
    private static object Many(string id, string type) => new { id, type, cardinality = "many" };

    /// <summary>A structural enum parameter declared inline: the member list is this row's own, so the compiled
    /// value of a member is its position in this list and the list order is the wire order.</summary>
    private static object Structural(string id, string[] values)
        => new { id, type = "enum", role = "structural", required = true, values };

    /// <summary>The four fixed result columns, in the framework's own order.</summary>
    private static object[] FixedFields() => new object[]
    {
        Field("target", "entity"), EnumField("status", "execution_outcome"),
        EnumField("committed", "commit_state"), Field("code", "string")
    };

    private static object Field(string id, string type) => new { id, type };
    private static object UnitField(string id, string type, string unit) => new { id, type, unit };
    private static object EnumField(string id, string schema) => new { id, type = "enum", schema };

    private static object[] TraceOutputs() => new object[]
    {
        Execution("next"),
        Result("forge.result.diagnostic.trace", UnitField("sample_rate", "integer", "tick"))
    };

    private static object[] AssertOutputs() => new object[]
    {
        Execution("next"),
        Result("forge.result.diagnostic.assert")
    };

    private static object[] InspectOutputs() => new object[]
    {
        Execution("next"),
        Result("forge.result.diagnostic.inspect", Field("budget", "integer"), Field("target_count", "integer")),
        String("snapshot")
    };

    private static object[] MetricOutputs() => new object[]
    {
        Execution("next"),
        Result("forge.result.diagnostic.metric", Field("value", "number"))
    };

    private static object Result(string schema, params object[] extra)
    {
        var fields = new List<object>(FixedFields());
        fields.AddRange(extra);
        return new { id = "result", type = "result", schema, fields = fields.ToArray() };
    }

    /// <summary>The one place a record reaches the pipeline. A missing report is counted, and the count is
    /// readable through <see cref="SinkDrops"/> so a session with unreadable diagnostics says so.</summary>
    private static void Record(DiagnosticRecord record)
    {
        var sink = Sink;
        if (sink == null) { SinkDrops++; return; }
        sink(record);
    }
}
