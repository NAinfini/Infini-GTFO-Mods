using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeRuntime.Framework;

/// <summary>
/// The variable, flow-glue and cross-module message vocabulary, owned by the kernel the same way
/// <see cref="ControlContracts"/> owns the first-step control words. Every row here is a `control` step except the
/// receive trigger: the kernel walks controls itself, which is what lets these rows write a frame and route to more
/// than one exit, and it is also why none of them needs a provider handler.
///
/// The rows are:
/// <list type="bullet">
/// <item><c>forge.variable.store.read</c> / <c>.write</c> — read and write one declared variable (<c>g-var</c>),
/// and the same pair reads and writes a level's named object (<c>g-object</c>): a named object is a declaration in
/// the `named` scope, so the object table is the variable table and not a second one.</item>
/// <item><c>forge.control.flow.once</c> — run the rest of the region the first time this mount point reaches it
/// (<c>g-once</c>).</item>
/// <item><c>forge.control.flow.wait_event</c> — wait for a custom message or an engine event, with an optional
/// deadline, and leave by `received` or by `timeout` (<c>g-wait</c>).</item>
/// <item><c>forge.control.message.emit</c> — send a custom message another behaviour can receive (<c>g-message</c>).
/// </item>
/// <item><c>forge.trigger.status.event_received</c> — the entry point a behaviour hangs on to receive those
/// messages, and the row a `g-wait` names when it waits for one.</item>
/// </list>
///
/// Nothing here is a second value channel: `g-var`, `g-object`, `g-message` and `g-wait` all read and write the one
/// store the kernel owns, publish through the one event queue, and resume the one dispatch walk. `g-io` is not a
/// runtime row at all: a module is an editor-only construct that is flattened into the plan at export, so there is
/// no `forge.control.flow.subgraph`/`.return` to dispatch and none is registered here.
/// </summary>
public static class VariableContracts
{
    internal const string ProviderId = "forge.contract.variables";
    public const string ReadCapability = "forge.variable.store.read";
    public const string WriteCapability = "forge.variable.store.write";
    public const string OnceCapability = "forge.control.flow.once";
    public const string WaitCapability = "forge.control.flow.wait_event";
    public const string EmitCapability = "forge.control.message.emit";
    public const string MessageReceivedCapability = "forge.trigger.status.event_received";
    public const string NamedReadCapability = "forge.object.named.read";
    public const string NamedWriteCapability = "forge.object.named.write";
    public const string ReadBinding = ProviderId + ".binding.read";
    public const string WriteBinding = ProviderId + ".binding.write";
    public const string OnceBinding = ProviderId + ".binding.once";
    public const string WaitBinding = ProviderId + ".binding.wait";
    public const string EmitBinding = ProviderId + ".binding.emit";
    public const string NamedReadBinding = ProviderId + ".binding.named_read";
    public const string NamedWriteBinding = ProviderId + ".binding.named_write";
    /// <summary>The concrete handle contract a level object's handle half carries. `effect`/`encounter` is the pair
    /// the alarm and wave rows already declare for the handles this table exists to hold, so a handle read out of
    /// the table is accepted by the row that stops it.</summary>
    internal const string NamedHandleKind = "effect";
    internal const string NamedHandleLifetime = "encounter";
    /// <summary>The port a custom message's one value travels on, on both sides: the send step's input and the
    /// receive trigger's output. The envelope already has a field called `payload`, so a trigger port of that name
    /// would be the one port that says nothing about what it carries; `value` is the same word the send row uses.
    /// </summary>
    internal const string MessageValuePort = "value";
    /// <summary>The one binding a custom message is received through. A `g-wait` names it as its `target`, a
    /// trigger step binds it as its entry point, and an `emit` step's own `message` parameter carries the name the
    /// sender chose.</summary>
    public const string MessageReceivedBinding = ProviderId + ".binding.message_received";

    private static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "session", "logic" };
    internal static readonly string[] ValueTypePorts = VariableValueTypes.PortTypes.ToArray();

    /// <summary>The events a `g-wait` may additionally name, by the trigger binding that delivers them. These
    /// are the events the runtime already dispatches under a stable name, so waiting on one is a subscription to
    /// an existing row rather than a second event system; the custom-message row is named by
    /// <see cref="MessageReceivedBinding"/>, which is the one name that is not an engine event.
    ///
    /// An entry is the binding id the wait actually matches: the kernel looks a suspended wait up by
    /// <c>RuntimeEvent.BindingId</c>, so a capability id — whatever capability it names — is a name no
    /// registration publishes under and can never resume a wait. Ruling 148.2: the capability-shaped entries the
    /// table used to carry are removed rather than kept inert, and a row becomes waitable when its owner's row is
    /// named here by the binding id its provider registers (the level-event rows below are the ones wired so
    /// far).</summary>
    internal static readonly IReadOnlyList<string> WaitableEventBindings = new[]
    {
        MessageReceivedBinding,
        // Ruling 118.3: the five rows the level-event batch added, by the binding id ForgeMap registers them under.
        "forge.module.gtfo.map.binding.trigger_objective_won",
        "forge.module.gtfo.map.binding.trigger_session_expedition_started",
        "forge.module.gtfo.map.binding.trigger_session_checkpoint_restored",
        "forge.module.gtfo.map.binding.trigger_map_zone_entered",
        "forge.module.gtfo.map.binding.trigger_map_portal_warped"
    };

    internal static bool IsMessageEvent(string bindingId) => bindingId == MessageReceivedBinding;
    internal static bool IsWaitable(string bindingId) => WaitableEventBindings.Contains(bindingId, StringComparer.Ordinal);

    /// <summary>The kernel's own module. It is registered like any other built-in provider, so a plan pins these
    /// bindings by the same lock every other binding uses.</summary>
    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = new object[]
        {
            new { id = ReadCapability, owner = ProviderId, kind = "control", label = "读变量 / 读关卡对象",
                version = "1.0.0", parameters = new { description = "从一个作用域读变量或关卡具名对象；没有值时读声明的初值。" },
                graph = ReadGraph() },
            new { id = WriteCapability, owner = ProviderId, kind = "control", label = "写变量 / 写关卡对象",
                version = "1.0.0", parameters = new { description = "把一个值写进一个作用域的变量或关卡具名对象；只有主机提交。" },
                graph = WriteGraph() },
            new { id = OnceCapability, owner = ProviderId, kind = "control", label = "只触发一次",
                version = "1.0.0", parameters = new { description = "这个挂载点第一次走到时进入 first，之后每次都进入 later。" },
                graph = OnceGraph() },
            new { id = WaitCapability, owner = ProviderId, kind = "control", label = "等待消息或事件",
                version = "1.0.0", parameters = new { description = "等到指定消息或事件时走 received，超时走 timeout。" },
                graph = WaitGraph() },
            new { id = EmitCapability, owner = ProviderId, kind = "control", label = "发送消息",
                version = "1.0.0", parameters = new { description = "发一条自定义消息，别的行为可以接收或等待它。" },
                graph = EmitGraph() },
            new { id = MessageReceivedCapability, owner = ProviderId, kind = "trigger", label = "收到消息",
                version = "1.0.0", parameters = new { description = "接收到一条自定义消息时触发。" },
                graph = MessageGraph() },
            new { id = NamedReadCapability, owner = ProviderId, kind = "control", label = "读关卡具名对象",
                version = "1.0.0", parameters = new { description = "按名字读关卡对象（警报A、门、终端、雾预设）；没绑定过时两个值都为空。" },
                graph = NamedReadGraph() },
            new { id = NamedWriteCapability, owner = ProviderId, kind = "control", label = "写关卡具名对象",
                version = "1.0.0", parameters = new { description = "把实体或句柄按名字存进关卡对象表，例如把波次句柄存进“警报A”。" },
                graph = NamedWriteGraph() }
        },
        bindings = new object[]
        {
            Binding(ReadBinding, ReadCapability, "runtime.variable.read", "execute"),
            Binding(WriteBinding, WriteCapability, "runtime.variable.write", "execute"),
            Binding(OnceBinding, OnceCapability, "runtime.control.once", "execute"),
            Binding(WaitBinding, WaitCapability, "runtime.control.wait_event", "execute"),
            Binding(EmitBinding, EmitCapability, "runtime.message.emit", "execute"),
            Binding(MessageReceivedBinding, MessageReceivedCapability, "runtime.trigger.message_received", "observe"),
            Binding(NamedReadBinding, NamedReadCapability, "runtime.object.named_read", "execute"),
            Binding(NamedWriteBinding, NamedWriteCapability, "runtime.object.named_write", "execute")
        }
    }).GetRawText(),
    new Dictionary<string, CommandHandler>(),
    new[]
    {
        Support(ReadBinding), Support(WriteBinding), Support(OnceBinding), Support(WaitBinding),
        Support(EmitBinding), Support(MessageReceivedBinding),
        Support(NamedReadBinding), Support(NamedWriteBinding)
    });

    private static object Port(string id, string type, bool optional = false, bool nullable = false, string? valueTypeParameter = null,
        string? handleKind = null, string? lifetime = null)
    {
        var fields = new Dictionary<string, object>(StringComparer.Ordinal) { ["id"] = id, ["type"] = type };
        if (optional) fields["optional"] = true;
        if (nullable) fields["nullable"] = true;
        if (valueTypeParameter != null) fields["valueTypeParameter"] = valueTypeParameter;
        if (handleKind != null) fields["handleKind"] = handleKind;
        if (lifetime != null) fields["lifetime"] = lifetime;
        return fields;
    }

    /// <summary>The structural enum both value ports of a `g-var`/`g-message` node resolve through: the node
    /// declares the type it moves, so its frame slot carries the port type the author meant instead of one the
    /// kernel picked.</summary>
    private static object ValueTypeParameter() => new
    {
        id = "value_type", type = "enum", role = "structural", required = true, values = ValueTypePorts
    };

    private static object NameParameter() => new { id = "name", type = "string", role = "structural", required = true };

    private static object Binding(string id, string capabilityId, string handler, string role) => new
    {
        id, capabilityId, providerId = ProviderId, handler, role, status = "implemented",
        dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    private static BindingSupport Support(string bindingId) => new(bindingId, "implementation-only", Array.Empty<string>());

    private static object ReadGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = new object[] { Port("in", "execution"), Port("subject", "entity", optional: true, nullable: true), Port("slot", "integer", optional: true, nullable: true) },
        // The read's shape follows the class the declaration holds (ruling 158.5): a declared number, flag or text
        // always answers — a variable is declared with an initial value, so an address nothing has written yet
        // reads that value rather than "no value" (ruling 151.3) — while an entity is a world identity that starts
        // empty, so an entity read is a possibly-absent value a `present` step guards. `TypedPort` applies that
        // rule where the member resolves, which is the same place the website's `valueTypePort` applies it.
        outputs = new object[] { Port("next", "execution"), Port("value", "number", valueTypeParameter: "value_type") },
        parameters = new object[] { NameParameter(), ValueTypeParameter() }
    };

    private static object WriteGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = new object[]
        {
            Port("in", "execution"), Port("subject", "entity", optional: true, nullable: true),
            Port("slot", "integer", optional: true, nullable: true), Port("value", "number", optional: true, nullable: true, valueTypeParameter: "value_type")
        },
        // `previous` is the value the address held before this write — the declared initial value for an address
        // nothing has written yet, the same answer the read row gives — so a comparison behind this step can ask
        // whether the write crossed a threshold without a second read step and a second address resolution.
        outputs = new object[] { Port("next", "execution"), Port("written", "boolean"),
            Port("previous", "number", optional: true, nullable: true, valueTypeParameter: "value_type") },
        parameters = new object[] { NameParameter(), ValueTypeParameter() }
    };

    private static object OnceGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = new object[] { Port("in", "execution") },
        outputs = new object[] { Port("first", "execution"), Port("later", "execution") },
        parameters = Array.Empty<object>()
    };

    private static object WaitGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = new object[] { Port("in", "execution") },
        outputs = new object[]
        {
            Port("received", "execution"), Port("timeout", "execution"),
            Port("message", "string"), Port("payload", "number", nullable: true)
        },
        parameters = new object[]
        {
            // The wait's target is a binding id, which is a dotted name and therefore cannot be an enum member
            // (a member is a plain identifier): it is a structural string the dispatcher checks against the closed
            // waitable set, so an event outside the set is refused by name instead of waited on forever.
            new { id = "target", type = "string", role = "structural", required = true },
            new { id = "message", type = "string", role = "structural", required = false },
            new { id = "timeout", type = "integer", role = "structural", required = false }
        }
    };

    /// <summary>One custom message carries its name and one number on `value`, the same port the receive row
    /// publishes. The value is not a typed variable: the receive row resolves no parameter, so the one port both
    /// sides read is the one shape they can agree on without a structural parameter to resolve.</summary>
    private static object EmitGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = new object[] { Port("in", "execution"), Port(MessageValuePort, "number", optional: true, nullable: true) },
        outputs = new object[] { Port("next", "execution") },
        parameters = new object[] { new { id = "message", type = "string", role = "structural", required = true } }
    };

    /// <summary>The receive side: the message name the sender chose and the value the sender passed, both read from
    /// the event's own frame. A trigger declares exactly one execution output and resolves no parameter here, which
    /// is why the value's single port is declared rather than resolved from one.</summary>
    private static object MessageGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = Array.Empty<object>(),
        outputs = new object[] { Port("next", "execution"), Port("message", "string"), Port(MessageValuePort, "number", nullable: true) },
        parameters = Array.Empty<object>()
    };

    /// <summary>
    /// The level-object pair. Both rows name one object and move whichever half its declaration has: an entity for
    /// a door, terminal or fog preset, a handle for a wave or alarm. A name that was never bound reads as two nulls
    /// rather than as a refusal — "this object is not up yet" is a state a behaviour acts on, and a write is what
    /// binds it.
    /// </summary>
    private static object NamedReadGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = new object[] { Port("in", "execution") },
        outputs = new object[]
        {
            Port("next", "execution"), Port("value", "entity", nullable: true),
            Port("wave", "handle", nullable: true, handleKind: NamedHandleKind, lifetime: NamedHandleLifetime)
        },
        parameters = new object[] { NameParameter() }
    };

    private static object NamedWriteGraph() => new
    {
        domains = Domains,
        execution = "host",
        inputs = new object[]
        {
            Port("in", "execution"), Port("value", "entity", optional: true, nullable: true),
            Port("wave", "handle", optional: true, nullable: true, handleKind: NamedHandleKind, lifetime: NamedHandleLifetime)
        },
        outputs = new object[] { Port("next", "execution"), Port("written", "boolean") },
        parameters = new object[] { NameParameter() }
    };
}
