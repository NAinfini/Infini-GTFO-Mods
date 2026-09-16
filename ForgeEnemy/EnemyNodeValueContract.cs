using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The enemy-domain read rows of the node list's `values` section: `v-e-health`, `v-e-alive`,
/// `v-e-type`, `v-e-sleep`, `v-e-where` and `v-e-tagged` — the six facts a plan reads out of one enemy without
/// changing anything.
///
/// They are `state`-kind rows at the `query` tier, which is the pair the framework's own value rows use: `state`
/// is the value row's own kind — the one an on-demand read about the world is registered under, and the one the
/// registration accepts an evaluator for (`RuntimeRegistry.WithModule`) — and every row here declares an entity
/// input, so `query` is what the tier rule requires rather than a choice. The ids
/// follow the node list's own `forge.<…>.<domain>.<snake_name>` grammar with `query` as the middle segment, the
/// spelling ruling 92 fixed for the values section; the two packages that grow value rows (enemy and player) use
/// it identically.
///
/// Every row takes one entity input named `enemy`, the node list's own port name. What each row answers, and the
/// native fact behind it:
/// <list type="bullet">
/// <item>`health`: the enemy's current and maximum health, read from its own damage receiver through the shared
/// entity snapshot (`RuntimeEntitySnapshot.Health` / `.HealthMaximum`).</item>
/// <item>`alive`: whether the enemy is alive. Read from the snapshot's life state, which is the one place the
/// provider publishes it.</item>
/// <item>`type`: the official `EnemyDataBlock.persistentID` the agent was built from, as decimal text — the same
/// spelling the `enemy-type` mount matcher uses (`Native/Observation/EnemyTypeReader.cs`).</item>
/// <item>`sleeping`: whether the enemy's behaviour state is one of the game's two hibernation states, read from
/// the snapshot's `aiState` through the same table `forge.trigger.enemy.awakened` publishes from.</item>
/// <item>`where`: the enemy's world position (the snapshot's own vector, in metres) and the zone it stands in as
/// the `forge.resource.zone` reference the framework's zone identity is written from.</item>
/// <item>`tagged`: the enemy's own BioTracker tag flag and the seconds of tag time it still has
/// (`EnemyAgent.IsTagged` / `EnemyAgent.EnemyTaggedTimer`).</item>
/// </list>
///
/// A read that cannot be made is the step's own refusal with a code, never a zero, an empty string or a false:
/// a snapshot without health means this provider publishes no health for that life, and a row that answered a
/// default would be indistinguishable from an observed default. Two rows answer a port the game genuinely
/// answered with nobody: `where`'s `zone` is null when the enemy stands outside every zone, which is an answer
/// and not a missing read — a read that could not be made refuses before the port is built.</summary>
public static class EnemyNodeValueContract
{
    /// <summary>The provider every row here belongs to.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>The one entity kind every row is about. A fact about another kind is not this family's.</summary>
    public const string EntityKind = "gtfo.enemy";

    /// <summary>The entity input every row takes.</summary>
    public const string EnemyPort = "enemy";

    /// <summary>`v-e-health`: current and maximum health.</summary>
    public const string HealthCapability = "forge.query.enemy.health";
    public const string HealthHandler = "gtfo.enemy.value.health";

    /// <summary>`v-e-alive`: whether the enemy is alive.</summary>
    public const string AliveCapability = "forge.query.enemy.alive";
    public const string AliveHandler = "gtfo.enemy.value.alive";

    /// <summary>`v-e-type`: the official enemy type id.</summary>
    public const string TypeCapability = "forge.query.enemy.type";
    public const string TypeHandler = "gtfo.enemy.value.type";

    /// <summary>`v-e-sleep`: whether the enemy is hibernating.</summary>
    public const string SleepingCapability = "forge.query.enemy.sleeping";
    public const string SleepingHandler = "gtfo.enemy.value.sleeping";

    /// <summary>`v-e-where`: position and the zone it stands in.</summary>
    public const string WhereCapability = "forge.query.enemy.where";
    public const string WhereHandler = "gtfo.enemy.value.where";

    /// <summary>`v-e-tagged`: the tag flag and the tag time left.</summary>
    public const string TaggedCapability = "forge.query.enemy.tagged";
    public const string TaggedHandler = "gtfo.enemy.value.tagged";

    /// <summary>The binding a row is registered under: the capability's own suffix under this provider, the same
    /// rule the player value family uses, so either side can name the counterpart of a row.</summary>
    public static string Binding(string capabilityId) => ProviderId + ".binding." + capabilityId["forge.".Length..];

    /// <summary>Every capability id this contract names, in the same order as <see cref="ValueRows"/>.</summary>
    public static readonly string[] CapabilityIds =
    {
        HealthCapability, AliveCapability, TypeCapability, SleepingCapability, WhereCapability, TaggedCapability
    };

    /// <summary>Every handler name this contract declares, in the same order as
    /// <see cref="CapabilityIds"/>. A `query` row is reached through the evaluator table, and the handler name is
    /// also its shape key, so a registration cannot supply an evaluator whose ports were never declared.</summary>
    public static readonly string[] HandlerNames =
    {
        HealthHandler, AliveHandler, TypeHandler, SleepingHandler, WhereHandler, TaggedHandler
    };

    /// <summary>Every binding id this contract declares, in the same order.</summary>
    public static string[] BindingIds()
    {
        var ids = new string[CapabilityIds.Length];
        for (int i = 0; i < ids.Length; i++) ids[i] = Binding(CapabilityIds[i]);
        return ids;
    }

    /// <summary>The six read-only rows, each a complete catalog entry: the id, the provider that owns it, the
    /// kind and tier the framework's rules require, and the ports the row really answers. A value row answers
    /// values only — no `next`, no `result`, no handle, and no write.</summary>
    public static IReadOnlyList<object> ValueRows() => Array.AsReadOnly(new[]
    {
        ValueRow(HealthCapability, "敌人生命值", "读一个敌人当前与最大的生命值。",
            "Reads one enemy's current and maximum health without changing anything.",
            new object[]
            {
                new { id = "value", type = "number", unit = "hp" },
                new { id = "maximum", type = "number", unit = "hp" }
            }),
        ValueRow(AliveCapability, "敌人是否存活", "读一个敌人是否还活着。",
            "Reads whether one enemy is still alive.",
            new object[] { new { id = "value", type = "boolean" } }),
        ValueRow(TypeCapability, "敌人类型", "读一个敌人的类型标识。",
            "Reads one enemy's official type id (EnemyDataBlock.persistentID) as decimal text.",
            new object[] { new { id = "value", type = "string" } }),
        ValueRow(SleepingCapability, "敌人是否休眠", "读一个敌人是否处于休眠。",
            "Reads whether one enemy is still hibernating.",
            new object[] { new { id = "value", type = "boolean" } }),
        ValueRow(WhereCapability, "敌人位置与所在区域", "读一个敌人的位置和它所在的区域。",
            "Reads one enemy's world position and the zone it stands in.",
            new object[]
            {
                new { id = "position", type = "vector3", unit = "m" },
                new { id = "zone", type = "resource", resourceKind = RuntimeZones.ResourceKind,
                    schema = "forge.resource." + RuntimeZones.ResourceKind, nullable = true }
            }),
        ValueRow(TaggedCapability, "敌人是否被标记", "读一个敌人是否被生物追踪器标记，以及标记还剩多久。",
            "Reads whether one enemy still carries a BioTracker tag, and how many seconds of it are left.",
            new object[]
            {
                new { id = "value", type = "boolean" },
                new { id = "remaining", type = "number", unit = "s" }
            })
    });

    /// <summary>The six bindings, one per row, paired with <see cref="ValueRows"/> by position. The role is
    /// `observe` for the reason the player value family uses it: an observation evaluated on demand registers
    /// through the evaluator table, and a `query` row is evaluated on demand by definition.</summary>
    public static IReadOnlyList<object> ValueBindings() => Array.AsReadOnly(new object[]
    {
        ValueBinding(HealthCapability, HealthHandler),
        ValueBinding(AliveCapability, AliveHandler),
        ValueBinding(TypeCapability, TypeHandler),
        ValueBinding(SleepingCapability, SleepingHandler),
        ValueBinding(WhereCapability, WhereHandler),
        ValueBinding(TaggedCapability, TaggedHandler)
    });

    /// <summary>The six registration support rows, paired with <see cref="ValueBindings"/> by position. A value
    /// read owns no object and writes nothing, so no row carries a permission.</summary>
    public static IReadOnlyList<BindingSupport> ValueSupport() => Array.AsReadOnly(new[]
    {
        ValueSupport(HealthCapability), ValueSupport(AliveCapability), ValueSupport(TypeCapability),
        ValueSupport(SleepingCapability), ValueSupport(WhereCapability), ValueSupport(TaggedCapability)
    });

    /// <summary>The one shape of each value handler: the enemy it reads and the ports it answers with. Declared
    /// here so the native read layer cannot describe a second layout.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> ValueShapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [HealthHandler] = new HandlerShape().Inputs(EnemyPort).Outputs("value", "maximum"),
        [AliveHandler] = new HandlerShape().Inputs(EnemyPort).Outputs("value"),
        [TypeHandler] = new HandlerShape().Inputs(EnemyPort).Outputs("value"),
        [SleepingHandler] = new HandlerShape().Inputs(EnemyPort).Outputs("value"),
        [WhereHandler] = new HandlerShape().Inputs(EnemyPort).Outputs("position", "zone"),
        [TaggedHandler] = new HandlerShape().Inputs(EnemyPort).Outputs("value", "remaining")
    };

    private static object ValueRow(string id, string label, string description, string descriptionEn, object[] outputs) => new
    {
        id,
        owner = ProviderId,
        kind = "state",
        label,
        version = "1.0.0",
        parameters = new { description, descriptionEn },
        graph = new
        {
            domains = new[] {
              "map",
              "enemy",
              "logic"
            },
            execution = "query",
            inputs = new object[] { new { id = EnemyPort, type = "entity" } },
            outputs,
            parameters = Array.Empty<object>()
        }
    };

    private static object ValueBinding(string capabilityId, string handler) => new
    {
        id = Binding(capabilityId),
        capabilityId,
        providerId = ProviderId,
        handler,
        role = "observe",
        status = "implemented",
        dependencies = Array.Empty<string>(),
        requires = Array.Empty<string>()
    };

    private static BindingSupport ValueSupport(string capabilityId)
        => new(Binding(capabilityId), "implementation-only", Array.Empty<string>());
}
