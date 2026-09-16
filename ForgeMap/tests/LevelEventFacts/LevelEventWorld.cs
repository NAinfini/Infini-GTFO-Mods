using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.LevelEventFacts;

/// <summary>
/// One live world for the cases below: a kernel in its own world epoch with the nine rows registered under the
/// contract's own declaration, and the module under test publishing through that registration. Nothing here
/// depends on the game: the level-event module is game-independent by design, so every port a fact carries is
/// asserted against the value a case put in rather than against a native read.
/// </summary>
internal sealed class LevelEventWorld : IDisposable
{
    private readonly RuntimeModuleHandle _registration;

    private LevelEventWorld(RuntimeKernel kernel, RuntimeModuleHandle registration, LevelEventModule module)
    {
        Kernel = kernel;
        _registration = registration;
        Module = module;
        Module.FactObserver = Published.Add;
    }

    internal RuntimeKernel Kernel { get; }
    internal LevelEventModule Module { get; }
    internal readonly List<RuntimeEvent> Published = new();

    /// <summary>Starts one world with the contract's own rows registered. The kernel refuses an implemented
    /// binding whose handler the module does not supply, so each of the three action rows is registered with a
    /// stand-in handler and the six observation rows carry the fact name their binding publishes under —
    /// exactly the table <c>MapPluginSession.Definition()</c> composes. The six trigger capability rows are the
    /// trigger contract's own declarations, so the contract registers beside this provider exactly as the host
    /// registers its built-in providers (ruling 148.3).</summary>
    internal static LevelEventWorld Start(long worldEpoch = 1)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.2.0", RuntimeKernel.ApiVersion, "20403457"));
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        var registration = kernel.RegisterModule(Definition(), RuntimeLogLevel.Off);
        kernel.BeginWorld(worldEpoch);
        kernel.StartRuntime(static () => { });
        kernel.Advance(0, true);
        return new LevelEventWorld(kernel, registration, new LevelEventModule(kernel, registration, _ => { }));
    }

    internal static RuntimeModule Definition()
        => new(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new
            {
                providers = new[]
                {
                    new { id = LevelEventContract.ProviderId, kind = "native", version = "0.1.0", dependencies = Array.Empty<string>() }
                },
                capabilities = LevelEventContract.CapabilityRows(),
                bindings = LevelEventContract.BindingRows()
            }).GetRawText(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
            {
                [LevelEventContract.ObjectiveTimerHandlerName] = _ => CommandResult.Succeeded(RuntimeJson.EmptyObject),
                [LevelEventContract.DimensionHandlerName] = _ => CommandResult.Succeeded(RuntimeJson.EmptyObject),
                [LevelEventContract.ExpeditionEndHandlerName] = _ => CommandResult.Succeeded(RuntimeJson.EmptyObject)
            },
            LevelEventContract.Supports())
        {
            // The rows' own port sets, composed exactly as the production session composes them.
            Shapes = LevelEventContract.Shapes()
        };

    /// <summary>The last fact one capability published, or null when it published none. A fact is looked up by the
    /// binding its capability owns, which is what a plan subscribes to.</summary>
    internal RuntimeEvent? Last(string capability)
    {
        string binding = LevelEventContract.Binding(capability);
        RuntimeEvent? last = null;
        foreach (var published in Published) if (published.BindingId == binding) last = published;
        return last;
    }

    /// <summary>How many facts one capability published. The kernel answers `no-consumer` for a binding no plan
    /// subscribes to, which is not a rejection — the fact was published and queued for nobody — so a case counts
    /// what the module handed over rather than what the kernel had a subscriber for.</summary>
    internal int Count(string capability)
    {
        string binding = LevelEventContract.Binding(capability);
        int count = 0;
        foreach (var published in Published) if (published.BindingId == binding) count++;
        return count;
    }

    /// <summary>Every fact the module published, in publication order.</summary>
    internal int Total => Published.Count;

    internal static string Capability(string fact)
        => LevelEventContract.Triggers.First(row => row.Fact == fact).Capability;

    /// <summary>One command context for a case to drive a handler with, over the given structural parameters. The
    /// kernel builds this type for a dispatched step and its constructor is assembly-internal, so a case that is
    /// not running the whole dispatch walk builds the same object through it.</summary>
    internal CommandContext Context(object? inputs, object? parameters, bool isHost = true)
        => CommandContexts.For(Kernel, inputs, parameters, isHost);

    public void Dispose()
    {
        Module.Dispose();
        _registration.Dispose();
    }
}

/// <summary>Builds the one command-context type a case needs through its own constructor, with the row's
/// structural enum parameters resolved the way the kernel resolves them at the handler boundary.</summary>
internal static class CommandContexts
{
    private static readonly ConstructorInfo Constructor = typeof(CommandContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[]
            {
                typeof(RuntimeEvent), typeof(long), typeof(string), typeof(string), typeof(string), typeof(string),
                typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(bool)
            }, null) ?? throw new InvalidOperationException("CommandContext's own constructor was not found.");

    private static readonly MethodInfo ResolveEnumParameters = typeof(RuntimeJson)
        .GetMethod("ResolveEnumParameters", BindingFlags.Static | BindingFlags.NonPublic)
        ?? throw new InvalidOperationException("RuntimeJson.ResolveEnumParameters was not found.");

    internal static CommandContext For(RuntimeKernel kernel, object? inputs, object? parameters, bool isHost)
    {
        var origin = new RuntimeEvent("test.event", "test.binding", kernel.WorldEpoch, 0, "test.scope", RuntimeJson.EmptyObject);
        return (CommandContext)Constructor.Invoke(new object?[]
        {
            origin, 0L, "test.command", "test.plan", "author.resource", "revision-1", "A_action",
            Resolve(parameters), Bag(inputs), isHost
        })!;
    }

    private static JsonElement Bag(object? value) => value == null ? RuntimeJson.EmptyObject : RuntimeJson.From(value);

    /// <summary>The kernel's own structural-enum resolution over the rows this family declares: a numeric
    /// parameter becomes the member name at its index, exactly as a dispatch hands it to a handler.</summary>
    private static JsonElement Resolve(object? parameters)
    {
        var bag = Bag(parameters);
        foreach (var row in LevelEventContract.ActionRows())
        {
            var graph = RuntimeJson.From(row).GetProperty("graph");
            foreach (var definition in graph.GetProperty("parameters").EnumerateArray())
            {
                if (definition.GetProperty("type").GetString() != "enum") continue;
                var name = definition.GetProperty("id").GetString()!;
                if (!bag.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) continue;
                if (!value.TryGetInt32(out int index) || index < 0 || index >= definition.GetProperty("values").GetArrayLength())
                    continue;
                return (JsonElement)ResolveEnumParameters.Invoke(null, new object[] { bag, row })!;
            }
        }
        return bag;
    }
}
