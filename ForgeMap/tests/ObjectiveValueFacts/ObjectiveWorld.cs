using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using ForgeMap;
using ForgeMap.Native;
using ForgeRuntime.Framework;

namespace ForgeMap.Tests.ObjectiveValueFacts;

/// <summary>
/// One live world for the cases below: a kernel in its own world epoch, the `v-obj` row registered under the
/// contract's own declaration, and the production reader over the double objective machine. A case fills the
/// machine and asserts what the production read answers for the state it wrote.
/// </summary>
internal sealed class ObjectiveWorld : IDisposable
{
    private readonly RuntimeModuleHandle _registration;

    private ObjectiveWorld(RuntimeKernel kernel, RuntimeModuleHandle registration)
    {
        Kernel = kernel;
        _registration = registration;
    }

    internal RuntimeKernel Kernel { get; }

    /// <summary>Starts one world with the row registered. The registry refuses an implemented binding whose
    /// handler the module does not supply, so the row carries the contract's own evaluator over the production
    /// reader and the contract's own shape table.</summary>
    internal static ObjectiveWorld Start(long worldEpoch = 1)
    {
        var kernel = new RuntimeKernel(new RuntimeIdentity("forge.runtime", "1.0.0", RuntimeKernel.ApiVersion, "20403457"));
        var registration = kernel.RegisterModule(Definition(), RuntimeLogLevel.Off);
        kernel.BeginWorld(worldEpoch);
        kernel.StartRuntime(static () => { });
        kernel.Advance(0, true);
        return new ObjectiveWorld(kernel, registration);
    }

    /// <summary>The definition a registration declares: the contract's capability and binding rows, the module's
    /// own evaluator table over the production reader, and the one shape the row declares.</summary>
    internal static RuntimeModule Definition()
        => new(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new
            {
                providers = new[]
                {
                    new
                    {
                        id = LevelObjectiveValueContract.ProviderId, kind = "native", version = "1.0.0",
                        dependencies = Array.Empty<string>()
                    }
                },
                capabilities = new[] { Capability },
                bindings = new[] { Binding }
            }).GetRawText(),
            new Dictionary<string, CommandHandler>(StringComparer.Ordinal),
            new[] { LevelObjectiveValueContract.Support() })
        {
            Shapes = LevelObjectiveValueContract.Shapes(),
            Evaluators = LevelObjectiveValueContract.Evaluators(
                new LevelObjectiveValueContract.LayerReader(LevelObjectiveValueReader.Read))
        };

    /// <summary>The capability row as a registration parses it: the contract's own text, not a copy.</summary>
    internal static JsonElement Capability => RuntimeJson.Parse(LevelObjectiveValueContract.CapabilityRowJson);

    /// <summary>The binding row as a registration parses it.</summary>
    internal static JsonElement Binding => RuntimeJson.Parse(LevelObjectiveValueContract.BindingRowJson);

    /// <summary>The evaluation context the kernel builds for a real query step: the row's own input bag and no
    /// session, actors or relations, because this row reads none of the three.</summary>
    internal static EvaluationContext Context(object inputs)
        => (EvaluationContext)EvaluationConstructor.Invoke(new object?[]
        {
            "A_row", RuntimeJson.EmptyObject, RuntimeJson.From(inputs), null!, null!, null!
        })!;

    private static readonly ConstructorInfo EvaluationConstructor = typeof(EvaluationContext)
        .GetConstructor(BindingFlags.Instance | BindingFlags.NonPublic, null, new[]
        {
            typeof(string), typeof(JsonElement), typeof(JsonElement), typeof(RuntimeQuerySession),
            typeof(RuntimeActorContext), typeof(RuntimeFactionRelations)
        }, null) ?? throw new InvalidOperationException("EvaluationContext's own constructor was not found.");

    public void Dispose() => _registration.Dispose();
}

/// <summary>The double machine's fixture: one state, the layers that have data and the objective instance each
/// layer's chain resolves to. Every case builds one, so no case inherits another's tables.</summary>
internal sealed class Machine : IDisposable
{
    internal Machine()
    {
        WardenObjectiveManager.CurrentState = new pWardenObjectiveState();
        WardenObjectiveManager.LayersWithData.Clear();
        WardenObjectiveManager.Objectives.Clear();
    }

    internal pWardenObjectiveState State => WardenObjectiveManager.CurrentState!;

    /// <summary>Gives one layer objective data and an objective instance of the given type, which is what the
    /// production read resolves the kind through.</summary>
    internal Machine Layer(string layer, eWardenObjectiveType kind)
    {
        var type = layer switch
        {
            "main" => LevelGeneration.LG_LayerType.MainLayer,
            "secondary" => LevelGeneration.LG_LayerType.SecondaryLayer,
            _ => LevelGeneration.LG_LayerType.ThirdLayer
        };
        WardenObjectiveManager.LayersWithData.Add(type);
        WardenObjectiveManager.Objectives[(type, State.main_chainIndex)] =
            new WardenObjective { ObjectiveType = kind };
        return this;
    }

    public void Dispose()
    {
        WardenObjectiveManager.CurrentState = null;
        WardenObjectiveManager.LayersWithData.Clear();
        WardenObjectiveManager.Objectives.Clear();
    }
}

