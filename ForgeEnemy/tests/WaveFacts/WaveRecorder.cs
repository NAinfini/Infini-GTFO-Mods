using ForgeRuntime.Framework;

/// <summary>The recording actions this suite drives plans with. Two shapes, because a forge action must declare a
/// recipient while the wave rows hand a plan very little: the wave shape's recipient is a `wave` resource the
/// fixture owns, so a plan can subscribe to any of the five facts and read the ports that do carry values; the
/// member shape takes the batch's entity collection, which only `wave_spawned` carries. Neither shape reads a port
/// through its declared handler shape: the whole dispatch context goes to the suite.</summary>
internal static class WaveRecorder
{
    internal const string ProviderId = "test.wave";
    internal const string WaveCapabilityId = "test.wave.action.record";
    internal const string WaveBindingId = "test.wave.binding.record";
    internal const string WaveHandlerName = "test.wave.record";
    internal const string MembersCapabilityId = "test.wave.action.members";
    internal const string MembersBindingId = "test.wave.binding.members";
    internal const string MembersHandlerName = "test.wave.members";
    internal const string Permission = "test.wave.record";
    /// <summary>The one wave resource this fixture owns; a plan names it as the compiled reference its action's
    /// recipient input carries.</summary>
    internal const string WaveResourceId = "test.wave.1";

    private const string Registry = """
    {"providers":[{"id":"test.wave","kind":"extension","version":"1.0.0","dependencies":[]}],
    "capabilities":[
    {"id":"test.wave.action.record","owner":"test.wave","kind":"action","label":"QA wave record","version":"1.0.0",
    "parameters":{},
    "graph":{"domains":["enemy"],"execution":"host",
    "inputs":[{"id":"in","type":"execution"},
    {"id":"wave_resource","type":"resource","resourceKind":"wave","schema":"forge.resource.wave"},
    {"id":"count","type":"integer","optional":true},
    {"id":"requested","type":"integer","optional":true},
    {"id":"spawned_count","type":"integer","optional":true}],
    "outputs":[{"id":"next","type":"execution"},
    {"id":"result","type":"result","schema":"test.wave.result.record",
    "fields":[{"id":"target","type":"entity"},{"id":"status","type":"enum","schema":"execution_outcome"},
    {"id":"committed","type":"enum","schema":"commit_state"},{"id":"code","type":"string"}]}],
    "parameters":[],
    "recipients":{"input":"wave_resource","target":"resource","cardinality":"one","requires":[],"result":"result"}}},
    {"id":"test.wave.action.members","owner":"test.wave","kind":"action","label":"QA wave members","version":"1.0.0",
    "parameters":{},
    "graph":{"domains":["enemy"],"execution":"host",
    "inputs":[{"id":"in","type":"execution"},
    {"id":"targets","type":"entity","cardinality":"many"}],
    "outputs":[{"id":"next","type":"execution"},
    {"id":"result","type":"result","schema":"test.wave.result.record",
    "fields":[{"id":"target","type":"entity"},{"id":"status","type":"enum","schema":"execution_outcome"},
    {"id":"committed","type":"enum","schema":"commit_state"},{"id":"code","type":"string"}]}],
    "parameters":[],
    "recipients":{"input":"targets","target":"entity","cardinality":"many","requires":[],"result":"result"}}}],
    "bindings":[
    {"id":"test.wave.binding.record","capabilityId":"test.wave.action.record","providerId":"test.wave",
    "handler":"test.wave.record","role":"execute","status":"implemented","dependencies":[],"requires":[]},
    {"id":"test.wave.binding.members","capabilityId":"test.wave.action.members","providerId":"test.wave",
    "handler":"test.wave.members","role":"execute","status":"implemented","dependencies":[],"requires":[]}]}
    """;

    /// <summary>The recording actions; every invocation succeeds after handing its context to the suite. The
    /// module also owns the one `wave` resource, because a compiled reference is only as good as the kind's
    /// registered owner.</summary>
    internal static RuntimeModule Module(Action<CommandContext> record) => new(RuntimeKernel.ApiVersion, Registry,
        new Dictionary<string, CommandHandler>
        {
            [WaveHandlerName] = context => { record(context); return CommandResult.Succeeded(RuntimeJson.EmptyObject); },
            [MembersHandlerName] = context => { record(context); return CommandResult.Succeeded(RuntimeJson.EmptyObject); }
        },
        new[]
        {
            new BindingSupport(WaveBindingId, "implementation-only", new[] { Permission }),
            new BindingSupport(MembersBindingId, "implementation-only", new[] { Permission })
        })
    {
        Shapes = new Dictionary<string, HandlerShape>
        {
            [WaveHandlerName] = new HandlerShape(), [MembersHandlerName] = new HandlerShape()
        },
        ResourceProviders = new Dictionary<string, RuntimeResourceProvider>
        {
            ["wave"] = RuntimeResourceProvider.Of(
                () => new[] { new ResourceRef("wave", WaveResourceId) },
                id => id == WaveResourceId ? new ResourceRef("wave", WaveResourceId) : null)
        }
    };
}
