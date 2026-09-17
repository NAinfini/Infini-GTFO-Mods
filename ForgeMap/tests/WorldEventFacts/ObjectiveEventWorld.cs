using System;
using System.Collections.Generic;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.WorldEventFacts;

/// <summary>
/// One live registration for the objective-event cases: a kernel in its own world epoch with the two rows
/// registered under the contract's own declaration, and each row's real decision — the one `ObjectiveEventContract`
/// owns — wired as the handler body. Nothing here depends on the game, so every refusal and every result a case
/// asserts is the decision the native half would have taken before its one call to the level-event executor.
/// </summary>
internal sealed class ObjectiveEventWorld : IDisposable
{
    private static readonly RuntimeModule Declared = Definition();
    private readonly RuntimeModuleHandle _registration;

    private ObjectiveEventWorld(RuntimeKernel kernel, RuntimeModuleHandle registration)
    {
        Kernel = kernel;
        _registration = registration;
    }

    internal RuntimeKernel Kernel { get; }

    /// <summary>Starts one world with the contract's own rows. The kernel refuses an implemented binding whose
    /// handler the module does not supply, so both execute rows are registered with a body that runs the
    /// contract's own decision and answers its own result — exactly what `ObjectiveEventActions` does around the
    /// native call.</summary>
    internal static ObjectiveEventWorld Start(long worldEpoch = 1)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
        kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        var registration = kernel.RegisterModule(Declared, RuntimeLogLevel.Off);
        kernel.BeginWorld(worldEpoch);
        kernel.StartRuntime(static () => { });
        kernel.Advance(0, true);
        return new ObjectiveEventWorld(kernel, registration);
    }

    private static RuntimeModule Definition()
        => new(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new
            {
                providers = new[]
                {
                    new
                    {
                        id = ObjectiveEventContract.ProviderId, kind = "native", version = "1.0.0",
                        dependencies = Array.Empty<string>()
                    }
                },
                capabilities = ObjectiveEventContract.CapabilityRows(),
                bindings = ObjectiveEventContract.BindingRows()
            }).GetRawText(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
            {
                [ObjectiveEventContract.DisplayHandlerName] = context =>
                    ObjectiveEventContract.TryDisplay(context, out var request, out string? code)
                        ? CommandResult.Succeeded(ObjectiveEventContract.DisplayOutputs(request))
                        : ObjectiveEventContract.Refused(code!),
                [ObjectiveEventContract.ProgressHandlerName] = context =>
                    ObjectiveEventContract.TryProgress(context, out var request, out string? code)
                        ? CommandResult.Succeeded(ObjectiveEventContract.ProgressOutputs(request))
                        : ObjectiveEventContract.Refused(code!)
            },
            ObjectiveEventContract.Supports())
        {
            // The rows' own port sets, composed exactly as the production session composes them.
            Shapes = ObjectiveEventContract.Shapes()
        };

    /// <summary>One command context for a case to drive a handler with.</summary>
    internal CommandContext Context(object? inputs = null, object? parameters = null, bool isHost = true)
        => CommandContexts.For(Kernel, inputs, parameters, isHost);

    /// <summary>One request driven through the very handler table the registration was accepted with.</summary>
    internal CommandResult Display(CommandContext context)
        => Declared.Handlers[ObjectiveEventContract.DisplayHandlerName](context);

    internal CommandResult Progress(CommandContext context)
        => Declared.Handlers[ObjectiveEventContract.ProgressHandlerName](context);

    public void Dispose() => _registration.Dispose();
}
