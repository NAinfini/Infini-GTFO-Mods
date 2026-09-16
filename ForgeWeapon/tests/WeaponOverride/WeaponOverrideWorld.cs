using System.Reflection;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponOverride;

/// <summary>
/// One case's ledger and its write-back port. The sink is a stand-in for the native applier: it records what a
/// real one would have written and can be told to refuse, which is what a case needs to show that a refused
/// request is not remembered. Nothing here touches a game type, so the whole fixture is cheap to build.
/// </summary>
internal sealed class OverrideWorld
{
    internal const long WorldEpoch = 11;

    internal sealed class Sink : IWeaponOverrideSink
    {
        internal readonly Dictionary<string, List<IReadOnlyList<WeaponOverrideField>>> Applications = new(StringComparer.Ordinal);
        internal readonly List<string> Restored = new();
        internal bool RefuseApply;
        internal string RefusalCode = "fixture-refused";
        internal int RestoreAllCalls;

        public bool Apply(EntityReference equipment, IReadOnlyList<WeaponOverrideField> fields, out string code)
        {
            if (RefuseApply) { code = RefusalCode; return false; }
            if (!Applications.TryGetValue(equipment.Id ?? "", out var writes))
                Applications[equipment.Id ?? ""] = writes = new List<IReadOnlyList<WeaponOverrideField>>();
            writes.Add(fields.ToArray());
            code = "";
            return true;
        }

        public bool Restore(EntityReference equipment, out string code)
        {
            Restored.Add(equipment.Id ?? "");
            code = "";
            return true;
        }

        /// <summary>Every instance this machine wrote, restored in the reverse order of its first write: a world
        /// boundary has to end up with nothing applied, including instances whose own last write was refused.</summary>
        public int RestoreAll()
        {
            RestoreAllCalls++;
            var restored = 0;
            foreach (var key in Applications.Keys.Reverse().ToArray())
                if (Restore(new EntityReference(key, WorldEpoch, 1), out _)) restored++;
            Applications.Clear();
            return restored;
        }
    }

    internal Sink WriteBack { get; } = new();
    internal WeaponOverrideLedger Ledger { get; }

    internal OverrideWorld()
    {
        Ledger = new WeaponOverrideLedger(WriteBack);
        Ledger.BeginWorld(WorldEpoch);
    }

    internal static EntityReference Equipment(string id = "gtfo.equipment:1", long life = 1)
        => new(id, WorldEpoch, life);

    internal static WeaponOverrideSource Source(string plan = "plan", string node = "node", long sequence = 0)
        => new(plan, node, sequence);

    internal static WeaponOverrideRequest Request(EntityReference equipment, WeaponOverrideSource source,
        long duration, params (string Name, double Value)[] fields)
        => new(equipment, fields.Select(field => new WeaponOverrideField(field.Name, field.Value)).ToArray(),
            source, duration);

    /// <summary>The values a real applier would have written for one instance, in write order.</summary>
    internal double? Written(EntityReference equipment, int write, string field)
    {
        if (!WriteBack.Applications.TryGetValue(equipment.Id ?? "", out var writes) || writes.Count <= write)
            return null;
        foreach (var value in writes[write])
            if (value.Name == field) return value.Value;
        return null;
    }

    /// <summary>A frame with exactly the ports a case names, in the shape the loader hands a handler: a JSON
    /// object of entities, numbers and handles.</summary>
    internal static JsonElement Frame(params (string Port, object? Value)[] inputs)
    {
        var ports = new Dictionary<string, object?>();
        foreach (var (port, value) in inputs) ports[port] = value;
        return RuntimeJson.From(ports);
    }

    /// <summary>One entity value as the request frame carries it, written as the reference object the frame's
    /// entity slots hold rather than as a bare string.</summary>
    internal static object Entity(EntityReference reference)
        => new { id = reference.Id, worldEpoch = reference.WorldEpoch, lifeEpoch = reference.LifeEpoch };

    /// <summary>One handle value as the frame carries it: kind and lifetime included, because the applier reads
    /// the handle's own identity rather than trusting the port it arrived on.</summary>
    internal static object Handle(string kind, string lifetime)
        => new { kind, lifetime, id = "fixture.handle", generation = 1 };

    /// <summary>
    /// One command context, built through the SDK's own internal constructor exactly as the kernel builds it: an
    /// origin event, the tick and the ids, then the node's own parameters and the request frame. The action rows
    /// are pure functions from this frame to a decision, so a case can drive them without a plan, a loader or a
    /// running kernel.
    /// </summary>
    internal static CommandContext Context(object? parameters, params (string Port, object? Value)[] inputs)
    {
        var origin = new RuntimeEvent("test.override.trigger", "test.override.binding", WorldEpoch, 0,
            "test.override.scope", RuntimeJson.EmptyObject);
        var ports = new Dictionary<string, object?>();
        foreach (var (port, value) in inputs) ports[port] = value;
        return (CommandContext)Activator.CreateInstance(typeof(CommandContext),
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.OptionalParamBinding, null,
            new object[]
            {
                origin, 7L, "test.override.command", "test.override.plan", "test.override.resource", "1",
                "test.override.node", RuntimeJson.From(parameters ?? new { }), RuntimeJson.From(ports), true
            }, null)!;
    }
}
