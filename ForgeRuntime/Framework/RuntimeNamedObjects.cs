using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The plan header's level-object table: one row per object a behaviour names, `{ id, type }` with an optional
/// `initial` binding. A row is a declaration in the <c>named</c> scope, so "警报A" is one address in the one store
/// the kernel owns rather than a second registry beside it — a read of it is a variable read, a write of it is a
/// variable write, and the scope's lifecycle is the variable store's.
///
/// Two halves exist because a level object is one of exactly two things on the wire: an <c>entity</c> (a door, a
/// terminal, a fog preset) or a <c>handle</c> (a wave, an alarm). A handle's port has to declare its own kind and
/// lifetime, so the table's handle half is the concrete `effect`/`encounter` pair the alarm and wave rows already
/// use; the read/write rows expose both halves and an author wires the one its object is declared as.
/// </summary>
internal static class RuntimeNamedObjectContract
{
    internal const int MaximumObjects = 128;
    internal static readonly IReadOnlyList<string> Types = new[] { VariableValueTypes.Entity, VariableValueTypes.Handle };

    /// <summary>The plan's `objects[]`, as `named`-scope declarations. A duplicate name is refused here, and a name
    /// another loaded plan declares differently is refused by the store's own `variable-conflict` rule.</summary>
    internal static IReadOnlyList<VariableDeclaration> ParsePlanObjects(JsonElement plan)
    {
        if (!plan.TryGetProperty("objects", out _)) return Array.Empty<VariableDeclaration>();
        var rows = RuntimeJson.Rows(plan, "objects", MaximumObjects);
        var result = new List<VariableDeclaration>(rows.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            RuntimeJson.Shape(row, "id type", "initial");
            var name = RuntimeJson.Id(row, "id");
            RuntimeJson.Require(seen.Add(name), "duplicate-object", name);
            var type = RuntimeJson.Text(row, "type");
            RuntimeJson.Require(Types.Contains(type, StringComparer.Ordinal), "variable-type", name);
            var initial = row.TryGetProperty("initial", out var value) ? value : RuntimeJson.Parse("null");
            RuntimeVariableContract.Validate(initial, type, name);
            result.Add(new VariableDeclaration(name, VariableScopeKinds.Named, type, initial));
        }
        return result.AsReadOnly();
    }
}

/// <summary>
/// The interface a package that produces a level object writes through, and the one a package that consumes one
/// reads through. It is deliberately this small: a wave action ends with
/// <c>RuntimeNamedObjects.Bind(kernel, "警报A", waveHandle)</c> and the alarm's own stop action later reads the
/// same name back, which is the whole "名字 → 实体或句柄" agreement. Both calls are host-only, like every other
/// write the kernel owns; a client reads the replicated values instead of binding its own.
///
/// A name is declared by the plan header's `objects[]`, so a bind for a name no loaded plan declares is refused by
/// name (`variable-undeclared`) rather than creating an address nothing can read; a value whose kind does not match
/// the declaration is refused as `variable-value`.
/// </summary>
public static class RuntimeNamedObjects
{
    /// <summary>Stores one entity or handle under a level-object name. Returns whether the value changed, false
    /// when the name already held it.</summary>
    public static bool Bind(RuntimeKernel kernel, string name, JsonElement value)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        return kernel.BindNamedObject(name, value);
    }

    /// <summary>The value a name currently holds, or null when it was never bound (or was cleared).</summary>
    public static JsonElement? Read(RuntimeKernel kernel, string name)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        return kernel.ReadNamedObject(name);
    }

    /// <summary>Every declared object name of the loaded plans, ordinal sorted.</summary>
    public static IReadOnlyList<string> Names(RuntimeKernel kernel)
    {
        ArgumentNullException.ThrowIfNull(kernel);
        return kernel.NamedObjectNames();
    }
}
