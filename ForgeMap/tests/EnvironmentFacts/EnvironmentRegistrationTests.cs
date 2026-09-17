using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.NativeEnvironment;

/// <summary>
/// What a registration is checked against before any step runs. The cases here register the slice's own rows with
/// the real runtime: the registry validates every capability graph, resolves every handler shape against the
/// capability its binding implements, and refuses a registration that names a handler it does not supply. A row
/// whose ports and shape disagree therefore fails here rather than at dispatch.
/// </summary>
public sealed class EnvironmentRegistrationTests
{
    private static readonly string[] CommandHandlerNames =
    {
        EnvironmentContract.LightingHandler, EnvironmentContract.LightColorHandler,
        EnvironmentContract.FogHandler, EnvironmentContract.FogCycleHandler,
        EnvironmentContract.AudioHandler, EnvironmentContract.AudioStopHandler, EnvironmentContract.IntelHandler,
        EnvironmentContract.DialogueHandler,
        EnvironmentContract.NavMarkerHandler, EnvironmentContract.AnimationHandler,
        EnvironmentContract.PlayerVoiceHandler, HudContract.ValueHandler
    };

    private static string CapabilitiesJson()
    {
        var rows = new List<string>();
        foreach (var row in EnvironmentContract.Rows().Values) rows.Add(RuntimeJson.From(row).GetRawText());
        rows.Add(HudContract.ValueCapabilityJson);
        return "[" + string.Join(",", rows) + "]";
    }

    private static string RegistryJson()
    {
        var providers = new[]
        {
            new { id = ModuleDefinition.ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() }
        };
        var capabilities = JsonDocument.Parse(CapabilitiesJson()).RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        var bindings = JsonDocument.Parse(RuntimeJson.From(
            EnvironmentContract.Bindings().Append(HudContract.BindingRow()).ToArray()).GetRawText())
            .RootElement.EnumerateArray().Select(x => x.Clone()).ToArray();
        return RuntimeJson.From(new { providers, capabilities, bindings }).GetRawText();
    }

    private static RuntimeModule Module(EnvironmentWorld world)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [EnvironmentContract.LightingHandler] = world.Host.HandleLighting,
            [EnvironmentContract.LightColorHandler] = world.Host.HandleLightColor,
            [EnvironmentContract.FogHandler] = world.Host.HandleFog,
            [EnvironmentContract.FogCycleHandler] = world.Host.HandleFogCycle,
            [EnvironmentContract.NavMarkerHandler] = world.Host.HandleNavMarker,
            [EnvironmentContract.AnimationHandler] = world.Host.HandleAnimation,
            [EnvironmentContract.AudioHandler] = world.Presentation.HandleAudio,
            [EnvironmentContract.AudioStopHandler] = world.Presentation.HandleAudioStop,
            [EnvironmentContract.IntelHandler] = world.Presentation.HandleIntel,
            [EnvironmentContract.DialogueHandler] = world.Presentation.HandleDialogue,
            [EnvironmentContract.PlayerVoiceHandler] = world.Presentation.HandlePlayerVoice,
            [HudContract.ValueHandler] = world.Hud.HandleValue
        };
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        foreach (var pair in EnvironmentContract.Shapes()) shapes[pair.Key] = pair.Value;
        foreach (var pair in HudContract.Shapes()) shapes[pair.Key] = pair.Value;
        return new RuntimeModule(RuntimeKernel.ApiVersion, RegistryJson(), handlers,
            EnvironmentContract.Supports().Append(HudContract.Support()).ToArray())
        {
            Shapes = shapes,
            Evaluators = new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
            {
                [EnvironmentContract.EnvironmentStateHandler] = EnvironmentQuery.Evaluate,
                [EnvironmentContract.ZoneLightsHandler] = EnvironmentQuery.ZoneLights
            },
            PresentationSessions = new Dictionary<string, Func<IReadOnlyList<EntityReference>?, IReadOnlyList<string>?>>(StringComparer.Ordinal)
            {
                [ModuleDefinition.ProviderId] = PlayerSessions.SessionsOf
            }
        };
    }

    private static RuntimeKernel Kernel()
        => new(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));

    [Fact]
    public void TheSlicesRowsRegisterWithTheRuntime()
    {
        using var world = EnvironmentWorld.Start();

        // Every capability graph is validated, every shape is resolved against the row its handler answers, and
        // every implemented binding has to have its handler supplied: a throw here is one of those three.
        var handle = Kernel().RegisterModule(Module(world), RuntimeLogLevel.Error);

        Assert.NotNull(handle);
        handle.Dispose();
    }

    [Fact]
    public void EveryCommandRowCarriesAHandlerTheRegistrationSupplies()
    {
        using var world = EnvironmentWorld.Start();
        var module = Module(world);

        foreach (var name in CommandHandlerNames) Assert.True(module.Handlers.ContainsKey(name), name);
        // The two read-only rows: the environment state and the zone's own light count.
        Assert.Equal(2, module.Evaluators!.Count);
        Assert.Equal(14, module.Handlers.Count + module.Evaluators!.Count);
    }

    [Fact]
    public void EveryRowDeclaresTheProviderAsItsOwnerAndItsOwnNamespace()
    {
        foreach (var pair in EnvironmentContract.Rows())
        {
            var row = RuntimeJson.From(pair.Value);
            Assert.Equal(ModuleDefinition.ProviderId, row.GetProperty("owner").GetString());
            Assert.Equal(pair.Key, row.GetProperty("id").GetString());
        }
        Assert.Equal(ModuleDefinition.ProviderId,
            RuntimeJson.From(HudContract.CapabilityRow()).GetProperty("owner").GetString());
    }

    [Fact]
    public void TheHostedRowsRunOnTheHostAndThePresentedOnesOnTheRecipient()
    {
        foreach (var capability in new[]
        {
            EnvironmentContract.LightingCapability, EnvironmentContract.LightColorCapability,
            EnvironmentContract.FogCapability,
            EnvironmentContract.FogCycleCapability, EnvironmentContract.NavMarkerCapability,
            EnvironmentContract.AnimationCapability
        })
            Assert.Equal("host", Contexts.Graph(capability).GetProperty("execution").GetString());

        foreach (var capability in new[]
        {
            EnvironmentContract.AudioCapability, EnvironmentContract.AudioStopCapability,
            EnvironmentContract.IntelCapability,
            EnvironmentContract.DialogueCapability, EnvironmentContract.PlayerVoiceCapability,
            HudContract.ValueCapability
        })
            Assert.Equal("presentation", Contexts.Graph(capability).GetProperty("execution").GetString());
    }

    [Fact]
    public void EveryActionRowNamesANonOptionalRecipientPort()
    {
        var rows = new List<JsonElement>();
        foreach (var row in EnvironmentContract.Rows().Values) rows.Add(RuntimeJson.From(row));
        rows.Add(RuntimeJson.From(HudContract.CapabilityRow()));

        foreach (var row in rows)
        {
            if (row.GetProperty("kind").GetString() != "action") continue;
            var graph = row.GetProperty("graph");
            var recipients = graph.GetProperty("recipients");
            string input = recipients.GetProperty("input").GetString()!;
            var port = graph.GetProperty("inputs").EnumerateArray()
                .Single(x => x.GetProperty("id").GetString() == input);
            Assert.Equal(recipients.GetProperty("target").GetString(), port.GetProperty("type").GetString());
            // The contract requires a recipient that is really there, so the port is never optional.
            Assert.False(port.TryGetProperty("optional", out var optional) && optional.GetBoolean());
        }
    }

    [Fact]
    public void EveryBindingRowNamesItsCanonicalCapability()
    {
        var bindings = EnvironmentContract.Bindings().Append(HudContract.BindingRow())
            .Select(RuntimeJson.From).ToArray();
        var capabilities = EnvironmentContract.Rows().Keys
            .Append(HudContract.ValueCapability).ToHashSet(StringComparer.Ordinal);

        Assert.Equal(14, bindings.Length);
        foreach (var binding in bindings)
        {
            Assert.Contains(binding.GetProperty("capabilityId").GetString()!, capabilities);
            Assert.Equal("implemented", binding.GetProperty("status").GetString());
            Assert.Equal(ModuleDefinition.ProviderId, binding.GetProperty("providerId").GetString());
        }
    }

    [Fact]
    public void TheReadOnlyRowCarriesNoBindingShapeOfAnAction()
    {
        var role = EnvironmentContract.Bindings().Select(RuntimeJson.From)
            .Single(x => x.GetProperty("capabilityId").GetString() == EnvironmentContract.EnvironmentStateCapability);
        // The row is evaluated on demand and publishes nothing, so it registers in the one evaluator table
        // `evaluate` and `observe` share rather than in the command table.
        Assert.Equal("evaluate", role.GetProperty("role").GetString());
    }
}
