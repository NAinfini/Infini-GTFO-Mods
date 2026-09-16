using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The Map provider: player entity identity, the read-only player observer and the
/// `forge.selector.target.players` query binding that answers with the player entity set. The native
/// implementation of the binding lives in the game-bound Map native assembly; this declaration is what the
/// runtime registry, the manifest and the website compare against.</summary>
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.map";
    public const string Version = "0.1.0";

    /// <summary>The declaration of this assembly's own surface alone: every capability it declares with the one
    /// binding that implements it. A registration that carries no native half declares exactly this, and it is
    /// the whole declaration the game-independent contract tests compare against.</summary>
    public static RuntimeModule Create() => Create(Array.Empty<object>(), Array.Empty<object>(), Array.Empty<BindingSupport>());

    /// <summary>The shapes this declaration's own rows resolve through, which is the one table a registration
    /// extends with its native handlers' shapes instead of replacing. It is the same table
    /// <see cref="Create(IReadOnlyList{object}, IReadOnlyList{object}, IReadOnlyList{BindingSupport})"/> installs,
    /// exposed so a caller composing a definition does not have to build a second module to read it.
    ///
    /// It carries the two selectors and nothing else: every other row this declaration binds is composed by the
    /// registration that implements it — the map-object and level-object families add their own shapes through
    /// their contracts' `Shapes()` tables, and a registration that repeated one here would add the same key
    /// twice.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> FormShapes { get; } =
        new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [PlayerSelectorContract.HandlerName] = PlayerSelectorContract.Shape,
            [ZoneSelectorContract.HandlerName] = ZoneSelectorContract.Shape
        };

    /// <summary>The one Map provider declaration. This assembly's own selector row and the three map-object
    /// observations are the declared surface; everything the native half answers for is named by the caller,
    /// because a capability row and its binding are one fact and a registration that declares a binding without
    /// its row — or a row nothing implements — is exactly what the runtime refuses.
    ///
    /// <paramref name="bindings"/> are this provider's own binding rows whose handler a half outside this
    /// assembly supplies, <paramref name="capabilities"/> their capability rows and <paramref name="support"/>
    /// their registration rows, all three in the same order. They are the contracts' own rows
    /// (<see cref="TerminalObjectContract"/>, <see cref="ObjectiveActionContract"/>, <see cref="PlayerHealthContract"/>),
    /// not a second copy of a shape. A registration that implements them declares them; one that cannot must not,
    /// because the runtime refuses an implemented binding whose handler the registration does not supply.</summary>
    public static RuntimeModule Create(IReadOnlyList<object> bindings, IReadOnlyList<object> capabilities,
        IReadOnlyList<BindingSupport> support) => new(RuntimeKernel.ApiVersion,
        RuntimeJson.From(new
        {
            providers = new[] { new { id = ProviderId, kind = "native", version = Version, dependencies = Array.Empty<string>() } },
            capabilities = new object[]
            {
                new {
                    id = PlayerSelectorContract.CapabilityId,
                    owner = ProviderId,
                    kind = "selector",
                    label = "按队伍与状态选择玩家",
                    version = "1.0.0",
                    parameters = new { description = "按队伍和状态挑玩家。" },
                    graph = new {
                        domains = new[] { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic" },
                        execution = "query",
                        inputs = Array.Empty<object>(),
                        outputs = new object[] { new { id = "targets", type = "entity", cardinality = "many" } },
                        parameters = new object[]
                        {
                            new { id = "relation", type = "enum", role = "structural", required = true, set = "recipient_relation" },
                            new { id = "empty", type = "enum", role = "structural", required = true, set = "empty_policy" }
                        }
                    }
                },
                // The zone selector's row is its contract's own JSON, parsed here rather than restated as an
                // object: the catalog comparison, the manifest and the registry all read one spelling of the shape.
                RuntimeJson.Parse(ZoneSelectorContract.CapabilityRowJson),
                // The three map-object observations below are published by this provider's own native callbacks,
                // but the shape the catalog carries is not the shape this provider publishes: `door_state` leaves
                // its phase port out when the status is not a transition, and the two terminal rows leave the
                // actor port out when the native callback carries no player. They keep their own declaration until
                // the catalog says which shape those ports really have; every row whose shape the runtime's
                // `TriggerContracts` already owns is bound here without a second copy.
                MapObjectContract.Row(MapObjectContract.DoorStateCapability, "门状态阶段变化", "门的开关状态变了。",
                    MapObjectContract.Port("next", "execution"), MapObjectContract.Port("door", "entity"),
                    MapObjectContract.Port("state", "string"),
                    new { id = "phase", type = "enum", schema = "interaction_phase", optional = true }),
                MapObjectContract.Row(MapObjectContract.TerminalCommandCapability, "终端命令被接受", "终端接受了一条命令。",
                    MapObjectContract.Port("next", "execution"), MapObjectContract.Port("terminal", "entity"),
                    MapObjectContract.Optional("actor", "entity"), MapObjectContract.Port("command", "string")),
                MapObjectContract.Row(MapObjectContract.TerminalSessionCapability, "玩家登上 / 离开终端", "玩家进入或退出终端。",
                    MapObjectContract.Port("next", "execution"), MapObjectContract.Port("terminal", "entity"),
                    MapObjectContract.Optional("actor", "entity"), MapObjectContract.Port("active", "boolean")),
                // The level-object rows: their capability JSON is `LevelObjectContract`'s own, parsed here rather
                // than restated, so the declaration, the catalog and the binding stay one fact. The native
                // registration adds no second copy of them. The generator rows are the map-object category's own
                // (ruling 148.4) and `GeneratorContract` declares them the same way.
                RuntimeJson.Parse(LevelObjectContract.ScanStartedCapabilityJson),
                RuntimeJson.Parse(LevelObjectContract.ScanProgressCapabilityJson),
                RuntimeJson.Parse(LevelObjectContract.ScanCompletedCapabilityJson),
                RuntimeJson.Parse(LevelObjectContract.ContainerStateCapabilityJson),
                RuntimeJson.Parse(LevelObjectContract.ItemPickupCapabilityJson),
                RuntimeJson.Parse(LevelObjectContract.ScanStateCapabilityJson),
                RuntimeJson.Parse(GeneratorContract.GeneratorCellCapabilityJson),
                RuntimeJson.Parse(GeneratorContract.GeneratorClusterCapabilityJson),
                RuntimeJson.Parse(GeneratorContract.GeneratorStateCapabilityJson)
            }.Concat(capabilities).ToArray(),
            bindings = new object[]
            {
                new {
                    id = PlayerSelectorContract.BindingId,
                    capabilityId = PlayerSelectorContract.CapabilityId,
                    providerId = ProviderId,
                    handler = PlayerSelectorContract.HandlerName,
                    role = "observe",
                    status = "implemented",
                    dependencies = Array.Empty<string>(),
                    requires = Array.Empty<string>()
                },
                RuntimeJson.Parse(ZoneSelectorContract.BindingRowJson),
                MapObjectContract.BindingRow(MapObjectContract.DoorStateCapability, MapObjectModule.EntityKind + ".door_state"),
                MapObjectContract.BindingRow(MapObjectContract.LockStateCapability, MapObjectModule.EntityKind + ".lock_state"),
                MapObjectContract.BindingRow(MapObjectContract.TerminalCommandCapability, MapObjectModule.EntityKind + ".terminal_command"),
                MapObjectContract.BindingRow(MapObjectContract.TerminalResultCapability, MapObjectModule.EntityKind + ".terminal_result"),
                MapObjectContract.BindingRow(MapObjectContract.TerminalSessionCapability, MapObjectModule.EntityKind + ".terminal_session"),
                ExpeditionContract.BindingRow()
            }.Concat(LevelObjectContract.BindingRows()).Concat(GeneratorContract.BindingRows()).Concat(bindings).ToArray()
        }).GetRawText(),
        new Dictionary<string, CommandHandler>(),
        new[]
        {
            new BindingSupport(PlayerSelectorContract.BindingId, "implementation-only", Array.Empty<string>()),
            // The zone selector reads the level's own zone table, which is this provider's to read and needs no
            // permission: it writes nothing and the anchor it is asked about is a reference the caller already holds.
            new BindingSupport(ZoneSelectorContract.BindingId, "implementation-only", Array.Empty<string>()),
            new BindingSupport(MapObjectContract.Binding(MapObjectContract.DoorStateCapability), "implementation-only", new[] { MapObjectContract.MapObjectReadPermission }),
            new BindingSupport(MapObjectContract.Binding(MapObjectContract.LockStateCapability), "implementation-only", new[] { MapObjectContract.MapObjectReadPermission }),
            new BindingSupport(MapObjectContract.Binding(MapObjectContract.TerminalCommandCapability), "implementation-only", new[] { MapObjectContract.MapObjectReadPermission }),
            new BindingSupport(MapObjectContract.Binding(MapObjectContract.TerminalResultCapability), "implementation-only", new[] { MapObjectContract.MapObjectReadPermission }),
            new BindingSupport(MapObjectContract.Binding(MapObjectContract.TerminalSessionCapability), "implementation-only", new[] { MapObjectContract.MapObjectReadPermission }),
            // An expedition end is read from the game's own end state and publishes no map-object state, so the
            // binding carries no permission: nothing here reads or writes an object the plan would have to own.
            new BindingSupport(ExpeditionContract.EndedBinding, "implementation-only", Array.Empty<string>())
        }.Concat(LevelObjectContract.Support()).Concat(GeneratorContract.Support()).Concat(support).ToArray())
    {
        // The selector's shape is part of this declaration: a registration built from it resolves the handler's
        // ports here, and a caller that supplies the native evaluator adds only that, never a second layout.
        Shapes = FormShapes
    };
}
