using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.WorldEventFacts;

/// <summary>
/// One live registration for the cases below: a kernel in its own world epoch with the three world-event rows
/// registered under the contract's own declaration, and the condition action's real decision — the one
/// `WorldEventContract` owns — wired as the handler body. Nothing here depends on the game: the contract is
/// game-independent by design, so every refusal and every result a case asserts is the decision the native half
/// would have taken, without a native call.
/// </summary>
internal sealed class WorldEventWorld : IDisposable
{
    private static readonly RuntimeModule Declared = Definition();
    private readonly RuntimeModuleHandle _registration;

    private WorldEventWorld(RuntimeKernel kernel, RuntimeModuleHandle registration)
    {
        Kernel = kernel;
        _registration = registration;
    }

    internal RuntimeKernel Kernel { get; }

    /// <summary>Starts one world with the contract's own rows. The kernel refuses an implemented binding whose
    /// handler the module does not supply, so the one execute row is registered with a body that runs the
    /// contract's own decision and answers its own result — exactly what `WorldEventFacts.Condition` does around
    /// the native call. The two trigger rows are `observe` bindings, so they need no body at all; the capability
    /// rows the control and trigger contracts declare register beside this provider the way the host registers its
    /// built-in providers.</summary>
    internal static WorldEventWorld Start(long worldEpoch = 1)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        var registration = kernel.RegisterModule(Declared, RuntimeLogLevel.Off);
        kernel.BeginWorld(worldEpoch);
        kernel.StartRuntime(static () => { });
        kernel.Advance(0, true);
        return new WorldEventWorld(kernel, registration);
    }

    private static RuntimeModule Definition()
        => new(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new
            {
                providers = new[]
                {
                    new
                    {
                        id = WorldEventContract.ProviderId, kind = "native", version = "1.0.0",
                        dependencies = Array.Empty<string>()
                    }
                },
                capabilities = WorldEventContract.CapabilityRows(),
                bindings = WorldEventContract.BindingRows()
            }).GetRawText(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
            {
                // The body is the contract's own decision and its own result: this is the game-independent half of
                // the native handler, so a case that drives it drives everything except the native call.
                [WorldEventContract.WorldEventConditionHandlerName] = context =>
                    WorldEventContract.TryCondition(context, out var request, out string? code)
                        ? CommandResult.Succeeded(WorldEventContract.Outputs(request))
                        : WorldEventContract.Refused(code!)
            },
            WorldEventContract.Supports())
        {
            // The rows' own port sets, composed exactly as the production session composes them.
            Shapes = WorldEventContract.Shapes()
        };

    /// <summary>One command context for a case to drive the handler with. The kernel builds this type for a
    /// dispatched step and its constructor is assembly-internal, so a case that is not running the whole dispatch
    /// walk builds the same object through it.</summary>
    internal CommandContext Context(object? inputs = null, object? parameters = null, bool isHost = true)
        => CommandContexts.For(Kernel, inputs, parameters, isHost);

    /// <summary>The condition action's own result for one request, driven through the very handler table the
    /// registration was accepted with.</summary>
    internal CommandResult Run(CommandContext context)
        => Declared.Handlers[WorldEventContract.WorldEventConditionHandlerName](context);

    public void Dispose() => _registration.Dispose();
}

/// <summary>Builds the one command-context type a case needs through its own constructor.</summary>
internal static class CommandContexts
{
    private static readonly ConstructorInfo Constructor = typeof(CommandContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null,
            new[]
            {
                typeof(RuntimeEvent), typeof(long), typeof(string), typeof(string), typeof(string), typeof(string),
                typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(bool)
            }, null) ?? throw new InvalidOperationException("CommandContext's own constructor was not found.");

    internal static CommandContext For(RuntimeKernel kernel, object? inputs, object? parameters, bool isHost)
    {
        var origin = new RuntimeEvent("test.event", "test.binding", kernel.WorldEpoch, 0, "test.scope",
            RuntimeJson.EmptyObject);
        return (CommandContext)Constructor.Invoke(new object?[]
        {
            origin, 0L, "test.command", "test.plan", "author.resource", "revision-1", "A_action",
            Bag(parameters), Bag(inputs), isHost
        })!;
    }

    private static JsonElement Bag(object? value) => value == null ? RuntimeJson.EmptyObject : RuntimeJson.From(value);
}
