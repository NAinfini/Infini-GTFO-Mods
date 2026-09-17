using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The one Map provider registration: every capability, binding and support row the single Map
/// registration declares, composed from the contracts that own them, and the one shape table those rows resolve
/// through.
///
/// Two halves register it and neither one composes it: the game-bound session, which supplies the native bodies
/// the rows name, and the release export, which supplies a refusing stand-in for every body it cannot compile.
/// Composing the rows here is what keeps one description of the registry — a row a contract adds reaches the game
/// and the manifest together, and a registration that carries a native half declares the same set as one that
/// carries none, because both read this set instead of assembling their own.</summary>
public static class ModuleRegistration
{
    /// <summary>The capability rows of the families this provider answers with a native half: the environment's
    /// presentation and two value rows, the HUD value row, the five alarm/scan/wave execute rows, the `v-obj`
    /// value row, the door value row, the level-event family's rows and the two trigger rows. Each is its own
    /// contract's JSON, parsed here rather than restated, so the declaration, the catalog and the binding stay one
    /// fact.</summary>
    private static readonly object[] NativeCapabilities = new object[]
    {
        RuntimeJson.Parse(EnvironmentContract.LightingCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.LightColorCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.FogCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.FogCycleCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.AudioCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.AudioStopCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.IntelCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.DialogueCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.NavMarkerCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.AnimationCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.PlayerVoiceCapabilityJson),
        // The two environment value rows: their bindings in `EnvironmentContract.Bindings` are `evaluate` ones, so
        // each row is declared here exactly as the ten action rows' are.
        RuntimeJson.Parse(EnvironmentContract.EnvironmentStateCapabilityJson),
        RuntimeJson.Parse(EnvironmentContract.ZoneLightsCapabilityJson),
        RuntimeJson.Parse(HudContract.ValueCapabilityJson),
        // The three scan/wave execute rows and their resource kinds (ruling 129.4; the two alarm rows were
        // deleted by a later ruling — an alarm is the chained puzzle `scan_start` activates).
        RuntimeJson.Parse(AlarmWaveContract.ScanStartCapabilityJson),
        RuntimeJson.Parse(AlarmWaveContract.WaveStartCapabilityJson),
        RuntimeJson.Parse(AlarmWaveContract.WaveStopCapabilityJson),
        // The `v-obj` value row, and the level-event family's own rows: its eight trigger bindings name canonical
        // capability rows the runtime trigger contract declares (registered as a host built-in), the three action
        // rows are this contract's own spelling of the catalog's graph, and the two spatial triggers are their own
        // contract's.
        RuntimeJson.Parse(LevelObjectiveValueContract.CapabilityRowJson),
        // The door value row: its binding is an `evaluate` one too, and a row nothing declares is refused.
        RuntimeJson.Parse(DoorQueryContract.CapabilityRowJson)
    }
        .Concat(LevelEventContract.CapabilityRows())
        .Concat(TriggerZoneContract.CapabilityRows())
        .ToArray();

    /// <summary>Every capability row of the registration, in the order the contracts declare them: the terminal
    /// and objective action rows first — their handlers read the player and the level the same registration
    /// answers for — then the native families, then the player value rows and the one control row the player
    /// command family adds.</summary>
    public static IReadOnlyList<object> Capabilities { get; } = TerminalObjectContract.Rows()
        .Concat(ObjectiveActionContract.CapabilityRows())
        .Concat(DoorTerminalActionContract.Rows())
        .Concat(NativeCapabilities)
        // The two native families whose bodies this provider answers but whose contract text another owner wrote:
        // the two player actions and the three door actions. The two sourced-modifier rows are not here: their
        // owner is `forge.contract.combat`, a row may only be declared by the provider that owns it, and the
        // combat contract does not declare them yet — the kernel refuses such a row with `capability-owner`, and
        // refuses a binding nothing declares with `missing-capability`. Their text stays in
        // `AgentModifierContract` for the host insertion, and this provider's binding for them is added back in
        // the same change that declares them (see that file's own note).
        .Concat(PlayerActionContract.Rows())
        .Concat(DoorActionContract.Rows())
        .Concat(new object[] { MovementProfileContract.Row() })
        .Concat(PlayerStateContract.ValueRows())
        .Concat(new object[] { PlayerCommandContract.DownRow() })
        .ToArray();

    /// <summary>Every binding row, in the same order as the capabilities above: one row per handler this provider
    /// answers, each naming the contract's own handler name.</summary>
    public static IReadOnlyList<object> Bindings { get; } = new object[] { PlayerHealthContract.Row() }
        .Concat(TerminalObjectContract.Bindings())
        .Concat(ObjectiveActionContract.Bindings())
        .Concat(DoorTerminalEventContract.Bindings())
        .Concat(DoorTerminalActionContract.Bindings())
        .Concat(PlayerActionContract.Bindings())
        .Concat(DoorActionContract.Bindings())
        .Concat(new object[] { MovementProfileContract.BindingRow() })
        .Concat(new object[] { RuntimeJson.Parse(DoorQueryContract.BindingRowJson) })
        .Concat(EnvironmentContract.Bindings())
        .Concat(new object[] { HudContract.BindingRow() })
        .Concat(PlayerStateContract.Rows())
        .Concat(PlayerEventContract.Rows())
        .Concat(PlayerLifeContract.Rows())
        .Concat(PlayerStateContract.ValueBindings())
        .Concat(PlayerCommandContract.Rows())
        .Concat(AlarmWaveContract.Bindings())
        .Concat(LevelEventContract.BindingRows())
        .Concat(TriggerZoneContract.BindingRows())
        .Concat(new object[] { RuntimeJson.Parse(LevelObjectiveValueContract.BindingRowJson) })
        .ToArray();

    /// <summary>One support row per implemented binding, each carrying the permission its own contract declares.
    /// The kernel refuses a registration whose implemented binding has no support row, so these travel with the
    /// rows above and not with the half that answers them.</summary>
    public static IReadOnlyList<BindingSupport> Support { get; } = new[] { PlayerHealthContract.Support() }
        .Concat(TerminalObjectContract.Supports())
        .Concat(ObjectiveActionContract.Supports())
        .Concat(DoorTerminalEventContract.Supports())
        .Concat(DoorTerminalActionContract.Supports())
        .Concat(PlayerActionContract.Support())
        .Concat(DoorActionContract.Supports())
        .Concat(new[] { MovementProfileContract.Support() })
        .Concat(new[] { new BindingSupport(DoorQueryContract.BindingId, "implementation-only", new[] { MapObjectContract.MapObjectReadPermission }) })
        .Concat(EnvironmentContract.Supports())
        .Concat(new[] { HudContract.Support() })
        .Concat(PlayerStateContract.Support())
        .Concat(PlayerEventContract.Support())
        .Concat(PlayerLifeContract.Support())
        .Concat(PlayerStateContract.ValueSupport())
        .Concat(PlayerCommandContract.Support())
        .Concat(AlarmWaveContract.Support())
        .Concat(LevelEventContract.Supports())
        .Concat(TriggerZoneContract.Supports())
        .Concat(new[] { LevelObjectiveValueContract.Support() })
        .ToArray();

    /// <summary>The one shape table the registration carries: this declaration's own entries — the two selectors
    /// and the map-object rows — plus every native handler's shape, each contract's own. Composing them here
    /// rather than restating a port is what keeps one layout per handler.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes { get; } = ComposeShapes();

    private static Dictionary<string, HandlerShape> ComposeShapes()
    {
        var shapes = new Dictionary<string, HandlerShape>(ModuleDefinition.FormShapes, StringComparer.Ordinal)
        {
            [PlayerHealthContract.HandlerName] = PlayerHealthContract.Shape,
            [TerminalObjectContract.CommandHandlerName] = TerminalObjectContract.CommandShape,
            [TerminalObjectContract.VisibilityHandlerName] = TerminalObjectContract.VisibilityShape,
            [TerminalObjectContract.OutputHandlerName] = TerminalObjectContract.OutputShape,
            [ObjectiveActionContract.StateHandlerName] = ObjectiveActionContract.StateShape,
            [ObjectiveActionContract.PhaseHandlerName] = ObjectiveActionContract.PhaseShape,
            [ObjectiveActionContract.ExtractionHandlerName] = ObjectiveActionContract.ExtractionShape,
            [DoorTerminalActionContract.LockHandlerName] = DoorTerminalActionContract.LockShape,
            [DoorTerminalActionContract.UnlockHandlerName] = DoorTerminalActionContract.UnlockShape,
            [DoorQueryContract.HandlerName] = DoorQueryContract.Shape,
            [EnvironmentContract.EnvironmentStateHandler] = EnvironmentContract.EnvironmentStateShape,
            [LevelObjectContract.ScanStateHandler] = LevelObjectContract.ScanStateShape,
            [GeneratorContract.GeneratorStateHandler] = GeneratorContract.GeneratorStateShape
        };
        foreach (var (name, shape) in EnvironmentContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in PlayerActionContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in DoorActionContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in MovementProfileContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in HudContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in PlayerCommandContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in PlayerStateContract.ValueShapes()) shapes[name] = shape;
        foreach (var (name, shape) in AlarmWaveContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in LevelEventContract.Shapes()) shapes[name] = shape;
        foreach (var (name, shape) in LevelObjectiveValueContract.Shapes()) shapes[name] = shape;
        return shapes;
    }

    /// <summary>The declared rows with no body: every name below is read back from this, which is why the two
    /// tables are derived and never listed a second time.</summary>
    private static readonly RuntimeModule Declaration = ModuleDefinition.Create(Bindings, Capabilities, Support);

    /// <summary>The handler names the registration needs a body for: one per implemented `execute` binding whose
    /// capability is an action. Read back from the declaration by the same rule the registry wires a binding
    /// with, so a row a contract adds is a body both registering halves supply.</summary>
    public static IReadOnlyList<string> CommandHandlers { get; } = Required((kind, role) =>
        role == "execute" && kind == "action");

    /// <summary>The evaluator names it needs one for: one per implemented row read on demand — an `evaluate`
    /// binding, or an `observe` one whose capability is a selector, a condition or a state. A trigger row is
    /// published and a control step is walked by the kernel, so neither carries one.</summary>
    public static IReadOnlyList<string> EvaluatorHandlers { get; } = Required((kind, role) =>
        role == "evaluate" || role == "observe" && kind is "selector" or "condition" or "state");

    /// <summary>The row-wiring rule above, applied to the declaration's own JSON. It is the registry's own rule,
    /// read from the rows rather than restated as a list of names: a row a contract adds needs a body of the same
    /// kind as the rows already there, and one a contract drops stops being asked for.</summary>
    private static IReadOnlyList<string> Required(Func<string, string, bool> wanted)
    {
        using var declared = JsonDocument.Parse(Declaration.RegistryJson);
        var kinds = DeclaredKinds();
        var names = new List<string>();
        foreach (var row in declared.RootElement.GetProperty("bindings").EnumerateArray())
        {
            if (row.GetProperty("status").GetString() != "implemented") continue;
            var kind = kinds[row.GetProperty("capabilityId").GetString()!];
            var role = row.GetProperty("role").GetString()!;
            if (!wanted(kind, role)) continue;
            var handler = row.GetProperty("handler").GetString()!;
            if (!names.Contains(handler, StringComparer.Ordinal)) names.Add(handler);
        }
        return names;
    }

    /// <summary>Every capability kind a Map binding can name: this declaration's own rows and the four contract
    /// providers the host registers before any package. A binding whose capability is not among them would be one
    /// no registration declares, which the kernel refuses on its own.</summary>
    private static Dictionary<string, string> DeclaredKinds()
    {
        var kinds = new Dictionary<string, string>(StringComparer.Ordinal);
        var declared = new[]
        {
            Declaration, CombatContracts.Module(), ControlContracts.Module(), TriggerContracts.Module(),
            ObservationContracts.Module()
        };
        foreach (var module in declared)
        {
            using var rows = JsonDocument.Parse(module.RegistryJson);
            foreach (var row in rows.RootElement.GetProperty("capabilities").EnumerateArray())
                kinds[row.GetProperty("id").GetString()!] = row.GetProperty("kind").GetString()!;
        }
        return kinds;
    }

    /// <summary>The registration both halves build: this declaration's rows and shapes, with the bodies the
    /// registering half supplies. Every name <see cref="CommandHandlers"/> and <see cref="EvaluatorHandlers"/>
    /// report has to be answered, and no name outside them may be: the registry checks both directions.</summary>
    public static RuntimeModule Create(IReadOnlyDictionary<string, CommandHandler> handlers,
        IReadOnlyDictionary<string, EvaluatorHandler> evaluators) => Declaration with
    {
        Handlers = handlers,
        Shapes = Shapes,
        Evaluators = evaluators
    };
}
