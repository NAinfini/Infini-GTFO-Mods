using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

/// <summary>
/// The scope a variable lives in, as the plan header declares it and as a step names it at runtime. Seven kinds
/// exist and each one has exactly one lifecycle: <c>level</c> is restored on checkpoint reload, <c>named</c> holds
/// one entry per level-object name, <c>player</c> survives a down but hands a late joiner its initial value,
/// <c>weapon</c> is one entry per player-and-slot pair, <c>object</c> is cleared when the object is retired (a
/// deployed item returning to its owner) and restored otherwise, <c>enemy</c> disappears with the enemy, and
/// <c>module</c> is one entry per mount point.
/// </summary>
public static class VariableScopeKinds
{
    public const string Level = "level";
    public const string Named = "named";
    public const string Player = "player";
    public const string Weapon = "weapon";
    public const string Object = "object";
    public const string Enemy = "enemy";
    public const string Module = "module";
    public static readonly IReadOnlyList<string> All = new[] { Level, Named, Player, Weapon, Object, Enemy, Module };
}

/// <summary>The declared value types. The wire spelling is the type's own name: a variable's declared type is the
/// only thing that says how its payload is read, so the payload itself never carries a tag.
///
/// <see cref="PortType"/> is the translation into the wire port vocabulary a plan's frame is built from: a
/// `g-var` node declares its own value port through the step's `value_type` structural parameter, and the port type
/// a variable compiles to has to be the same one its declared type reads as. The two tables are one rule in two
/// spellings, which is why the mapping lives here rather than in either the loader or the dispatcher.</summary>
public static class VariableValueTypes
{
    public const string Number = "number";
    public const string Flag = "flag";
    public const string Text = "text";
    public const string Entity = "entity";
    public const string Handle = "handle";
    public static readonly IReadOnlyList<string> All = new[] { Number, Flag, Text, Entity, Handle };
    /// <summary>The port types a variable node may compile to, in declaration order. A `g-var`/`g-message` node's
    /// `value_type` parameter offers exactly these members. <see cref="Handle"/> is deliberately not among them: a
    /// handle port has to name its own kind and lifetime in the contract, which one structural enum cannot vary, so
    /// a handle lives in the level-object table, whose read/write rows declare the concrete `effect`/`encounter`
    /// port the wave and alarm handles already use.</summary>
    public static readonly IReadOnlyList<string> PortTypes = new[] { "number", "boolean", "string", "entity" };

    internal static string PortType(string valueType) => valueType switch
    {
        Number => "number", Flag => "boolean", Text => "string", Entity => "entity", Handle => "handle",
        _ => throw new RuntimeContractException("variable-type", valueType)
    };

    internal static bool IsPortTypeOf(string portType, string valueType) => portType == PortType(valueType);
}

/// <summary>
/// One variable declaration from the plan header. <see cref="Name"/> is the author's own id and is the only name a
/// step uses; <see cref="Scope"/> is a member of <see cref="VariableScopeKinds"/>; <see cref="Type"/> is a member of
/// <see cref="VariableValueTypes"/>; and <see cref="Initial"/> is the value a fresh entry gets — validated against
/// the declared type at load, so a write can never put a value of the wrong type into the entry.
/// </summary>
public sealed record VariableDeclaration(string Name, string Scope, string Type, JsonElement Initial);

/// <summary>
/// One variable's address at runtime: which declaration it is, which scope kind it lives in, and the subject that
/// scope instance belongs to. <see cref="Subject"/> is the level-object name for <c>named</c>, the entity id for
/// <c>player</c>/<c>enemy</c>/<c>object</c>, empty for <c>level</c>, and either the mount point's own text or the
/// entity id for <c>module</c>; <see cref="Slot"/> is the weapon slot for <c>weapon</c> and -1 everywhere else.
/// The pairs that belong together are fixed by the scope kind, so a scope with a subject it cannot use is refused
/// instead of quietly keyed under something else.
/// </summary>
public sealed record RuntimeVariableScope(string Kind, string Subject, int Slot)
{
    public static RuntimeVariableScope Level() => new(VariableScopeKinds.Level, "", -1);
    public static RuntimeVariableScope Named(string name) => new(VariableScopeKinds.Named, name, -1);
    public static RuntimeVariableScope Player(string entityId) => new(VariableScopeKinds.Player, entityId, -1);
    public static RuntimeVariableScope Weapon(string entityId, int slot) => new(VariableScopeKinds.Weapon, entityId, slot);
    public static RuntimeVariableScope Object(string entityId) => new(VariableScopeKinds.Object, entityId, -1);
    public static RuntimeVariableScope Enemy(string entityId) => new(VariableScopeKinds.Enemy, entityId, -1);
    public static RuntimeVariableScope Module(string mount) => new(VariableScopeKinds.Module, mount, -1);
}

/// <summary>
/// One stored variable value: the scope it lives in, its declaration, its own address, and the payload. The
/// payload travels as JSON because a variable is read by a later step's port, and the port type is what decodes it;
/// <see cref="RuntimeVariableStore"/> is the only writer, so the payload's shape always agrees with the declared
/// type.
/// </summary>
public sealed record RuntimeVariableEntry(string Name, string ScopeKind, string Subject, int Slot, string Type, JsonElement Value)
{
    internal string Key => KeyOf(Name, ScopeKind, Subject, Slot);
    internal static string KeyOf(string name, string kind, string subject, int slot)
        => name + "\u0000" + kind + "\u0000" + subject + "\u0000" + slot.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// One replicated view of the host's variable state: every entry the host holds, in deterministic order. It is the
/// whole store rather than a stream of deltas, so a late joiner applies one snapshot and a client that missed a
/// message applies the next one without a gap to fill; an entry the snapshot omits is gone, which is how a cleared
/// object or a dead enemy propagates. A delta is the same shape with only the touched entries, which is what a
/// steady-state tick sends.
/// </summary>
public sealed record RuntimeVariableSnapshot(long WorldEpoch, IReadOnlyList<RuntimeVariableEntry> Entries)
{
    public static RuntimeVariableSnapshot Empty(long worldEpoch) => new(worldEpoch, Array.Empty<RuntimeVariableEntry>());

    internal static string Encode(long worldEpoch, IReadOnlyList<RuntimeVariableEntry> entries)
        => RuntimeJson.StableText(RuntimeJson.From(new
        {
            worldEpoch,
            entries = entries.Select(entry => new { name = entry.Name, scope = entry.ScopeKind, subject = entry.Subject, slot = entry.Slot, type = entry.Type, value = entry.Value }).ToArray()
        }));

    /// <summary>Reads one snapshot back. The world epoch it carries is the host's, so a client can tell a snapshot
    /// of the world it is in from one that predates the current one.</summary>
    public static RuntimeVariableSnapshot Decode(string json)
    {
        var value = RuntimeJson.Parse(json);
        RuntimeJson.Shape(value, "worldEpoch entries");
        var world = RuntimeJson.Integer(value.GetProperty("worldEpoch"));
        var entries = new List<RuntimeVariableEntry>();
        foreach (var row in RuntimeJson.Rows(value, "entries"))
        {
            RuntimeJson.Shape(row, "name scope subject slot type value");
            var subject = row.GetProperty("subject").GetString() ?? "";
            entries.Add(new RuntimeVariableEntry(RuntimeJson.Text(row, "name"), RuntimeJson.Text(row, "scope"),
                subject, (int)RuntimeJson.Integer(row.GetProperty("slot"), -1), RuntimeJson.Text(row, "type"), row.GetProperty("value")));
        }
        return new RuntimeVariableSnapshot(world, entries.AsReadOnly());
    }
}

/// <summary>
/// The variable store: one table of values keyed by declaration, scope kind, subject and slot, with the scope
/// lifecycle rules applied by the kernel. It owns no world state and never asks a provider a question — what a
/// scope's subject is comes from the step or from the engine event the kernel already dispatched.
///
/// A value only changes through <see cref="Set"/> or <see cref="ClearScope"/>, and both record the key in the tick's
/// touch set, so the host can send exactly what changed without comparing the whole store.
/// </summary>
internal sealed class RuntimeVariableStore
{
    private readonly Dictionary<string, RuntimeVariableEntry> entries = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Dictionary<string, VariableDeclaration>> declarations = new(StringComparer.Ordinal);
    private readonly HashSet<string> touched = new(StringComparer.Ordinal);
    private readonly HashSet<string> once = new(StringComparer.Ordinal);

    internal int Count => entries.Count;

    /// <summary>The declarations of one plan, by name. Each plan header declares its own set and a variable is read
    /// or written by the plan that declared it, so the sets are kept apart rather than merged into one namespace two
    /// plans could collide in. A step whose plan declared nothing reads no variable at all.</summary>
    internal IReadOnlyDictionary<string, VariableDeclaration> Declarations(string planId)
        => declarations.TryGetValue(planId, out var rows) ? rows : Empty;

    private static readonly IReadOnlyDictionary<string, VariableDeclaration> Empty =
        new Dictionary<string, VariableDeclaration>(StringComparer.Ordinal);

    /// <summary>
    /// Replaces one plan's whole set, which is what a load or a reload of that plan does. The values are untouched:
    /// a reload restores the plan's behaviour, and the checkpoint, not the file, owns the values.
    ///
    /// A variable name is one address in the world, so two loaded plans may share it only by declaring it
    /// identically (same scope, type and initial value); a second declaration that disagrees is refused by name
    /// instead of silently re-keying a value the first plan is still reading. The refusal is the loader's, which is
    /// why a conflicting plan is rejected whole.
    /// </summary>
    internal void Declare(string planId, IReadOnlyList<VariableDeclaration> rows)
    {
        var table = new Dictionary<string, VariableDeclaration>(StringComparer.Ordinal);
        foreach (var row in rows) table[row.Name] = row;
        foreach (var row in table.Values)
            foreach (var existing in declarations)
            {
                if (existing.Key == planId || !existing.Value.TryGetValue(row.Name, out var other)) continue;
                RuntimeJson.Require(other.Scope == row.Scope && other.Type == row.Type && Same(other.Initial, row.Initial),
                    "variable-conflict", row.Name);
            }
        if (table.Count == 0) declarations.Remove(planId); else declarations[planId] = table;
    }

    /// <summary>Every loaded plan's declarations, merged by name. Names are unique per world (see
    /// <see cref="Declare"/>), so this is the one table a step, a checkpoint restore and a named-object write all
    /// resolve a variable through.</summary>
    internal IEnumerable<VariableDeclaration> Declarations()
        => declarations.Values.SelectMany(table => table.Values).OrderBy(row => row.Name, StringComparer.Ordinal);

    /// <summary>The declaration a name resolves to in this world, or null when no loaded plan declares it.</summary>
    internal VariableDeclaration? Declared(string name)
    {
        VariableDeclaration? found = null;
        foreach (var entry in declarations.OrderBy(x => x.Key, StringComparer.Ordinal))
            if (entry.Value.TryGetValue(name, out var row)) { if (found != null) return found; found = row; }
        return found;
    }

    /// <summary>Forgets a plan's declarations, together with the latches keyed by that plan's own nodes. Its values
    /// stay: a variable whose plan unloaded is still a value of the world, and a plan loaded back under the same id
    /// reads what the world holds.</summary>
    internal void ForgetPlan(string planId)
    {
        declarations.Remove(planId);
        foreach (var key in once.Where(key => key.StartsWith(planId + ":", StringComparison.Ordinal)).ToArray()) once.Remove(key);
    }

    internal VariableDeclaration? Declared(string planId, string name)
        => declarations.TryGetValue(planId, out var rows) && rows.TryGetValue(name, out var row) ? row : null;

    /// <summary>Every entry of the current world, ordered so two stores holding the same values encode the same
    /// bytes. Deterministic order is what makes the snapshot comparable across peers and across a checkpoint.</summary>
    internal IReadOnlyList<RuntimeVariableEntry> Snapshot()
        => entries.Values.OrderBy(entry => entry.Name, StringComparer.Ordinal)
            .ThenBy(entry => entry.ScopeKind, StringComparer.Ordinal)
            .ThenBy(entry => entry.Subject, StringComparer.Ordinal)
            .ThenBy(entry => entry.Slot)
            .ToArray();

    /// <summary>The entries this tick wrote or cleared, for the delta a client applies. Reading the touch set does
    /// not clear it: one host advance may be handed to several recipients, and the delta belongs to the tick.</summary>
    internal IReadOnlyList<RuntimeVariableEntry> Touched()
        => Snapshot().Where(entry => touched.Contains(entry.Key)).ToArray();

    internal void BeginTick() => touched.Clear();

    /// <summary>Whether one address currently holds a value. A read of an address that holds none is answered from
    /// the declaration's initial value, which is why the store keeps the declaration and not just the value.</summary>
    internal bool Has(string name, RuntimeVariableScope scope)
        => entries.ContainsKey(RuntimeVariableEntry.KeyOf(name, scope.Kind, scope.Subject, scope.Slot));

    /// <summary>The value at one address, or the declaration's initial value when the address holds none yet. The
    /// caller has already checked the declaration exists, so the initial value it hands back is the declared one and
    /// never a guess.</summary>
    internal JsonElement Read(VariableDeclaration declaration, RuntimeVariableScope scope)
    {
        var key = RuntimeVariableEntry.KeyOf(declaration.Name, scope.Kind, scope.Subject, scope.Slot);
        return entries.TryGetValue(key, out var entry) ? entry.Value : declaration.Initial;
    }

    /// <summary>
    /// Writes one address, returning whether the value actually changed. Writing the value an address already holds
    /// is not a change, so it is neither touched nor replicated: that is what keeps a per-tick counter from
    /// producing an identical delta every tick.
    /// </summary>
    internal bool Set(VariableDeclaration declaration, RuntimeVariableScope scope, JsonElement value)
    {
        var key = RuntimeVariableEntry.KeyOf(declaration.Name, scope.Kind, scope.Subject, scope.Slot);
        if (entries.TryGetValue(key, out var existing) && Same(existing.Value, value)) return false;
        entries[key] = new RuntimeVariableEntry(declaration.Name, scope.Kind, scope.Subject, scope.Slot, declaration.Type, value.Clone());
        touched.Add(key);
        return true;
    }

    /// <summary>Clears one address. Clearing an address that holds nothing is a no-op rather than a touch, so a
    /// deployment returning twice does not replicate twice.</summary>
    internal bool Clear(string name, RuntimeVariableScope scope)
    {
        var key = RuntimeVariableEntry.KeyOf(name, scope.Kind, scope.Subject, scope.Slot);
        if (!entries.Remove(key)) return false;
        touched.Add(key);
        return true;
    }

    /// <summary>
    /// Clears every entry of one scope kind, or of one subject inside one kind, and reports how many entries went
    /// away. The cleared keys are touched because a client has to learn that they are gone; a snapshot omitting them
    /// is the same answer, and both are delivered.
    /// </summary>
    internal int ClearScope(string kind, string? subject = null)
    {
        var keys = entries.Where(x => x.Value.ScopeKind == kind && (subject == null || x.Value.Subject == subject)).Select(x => x.Key).ToArray();
        foreach (var key in keys) { entries.Remove(key); touched.Add(key); }
        return keys.Length;
    }

    /// <summary>Clears every entry of one scope kind whose subject starts with a prefix. A module variable is keyed
    /// by its mount point, which begins with the plan that owns it, so a plan that unloads takes exactly its own
    /// module values and nothing another plan holds.</summary>
    internal int ClearScopePrefix(string kind, string prefix)
    {
        var keys = entries.Where(x => x.Value.ScopeKind == kind && x.Value.Subject.StartsWith(prefix, StringComparison.Ordinal)).Select(x => x.Key).ToArray();
        foreach (var key in keys) { entries.Remove(key); touched.Add(key); }
        return keys.Length;
    }

    /// <summary>Drops every value and every latch without touching the declarations: a world ended, and a value from
    /// the previous world names entities that no longer exist. The touch set is left holding the keys so the removal
    /// is what the next delta carries.</summary>
    internal void ClearValues()
    {
        foreach (var key in entries.Keys) touched.Add(key);
        entries.Clear();
        foreach (var key in once) touched.Add("once\u0000" + key);
        once.Clear();
    }

    /// <summary>The latch <c>once</c> keeps: a `(plan, entrypoint, step)` triple is one mount point's own guard, and
    /// the entrypoint is part of the key because one plan may mount a behaviour on several entrypoints. The method
    /// is the whole check-and-set in one call, so two dispatches of the same latched step in one tick cannot both
    /// see it unset.
    /// </summary>
    internal bool ClaimOnce(string key)
    {
        if (!once.Add(key)) return false;
        touched.Add("once\u0000" + key);
        return true;
    }

    /// <summary>The `once` latches of the whole world, in one stable order: the checkpoint writer carries them and
    /// nothing else reads them.</summary>
    internal IReadOnlyList<string> Latches => once.OrderBy(key => key, StringComparer.Ordinal).ToArray();

    /// <summary>The latches and values one checkpoint has to carry. A checkpoint restores the same world, so the
    /// same world epoch comes back with it and the snapshot's own epoch is what proves the entries belong to it.
    /// <paramref name="gates"/> is the trigger gate table, already serialized by the store that owns it: the two
    /// tables are written as properties of one checkpoint but neither is a variable of the other's store.</summary>
    internal string Save(long worldEpoch, string gates) => RuntimeJson.StableText(RuntimeJson.From(new
    {
        worldEpoch,
        entries = Snapshot().Select(entry => new { name = entry.Name, scope = entry.ScopeKind, subject = entry.Subject, slot = entry.Slot, type = entry.Type, value = entry.Value }).ToArray(),
        once = Latches.ToArray(),
        gates = RuntimeJson.Parse(gates)
    }));

    /// <summary>
    /// Restores a checkpoint over the current values. A restore replaces the whole table rather than merging: the
    /// checkpoint is the authoritative state of the world it was taken in, and an entry it does not name is one the
    /// world did not have. The declarations are untouched — they come from the plan, which is reloaded beside this.
    ///
    /// An entry is restored only where the loaded plans declare it, and only where the declaration's own type and
    /// scope agree with the entry: a checkpoint names values, and a value whose address no longer exists (or exists
    /// as another kind) is not restored into an address a step would read as something else.
    /// </summary>
    internal void Restore(string json)
    {
        var value = RuntimeJson.Parse(json);
        RuntimeJson.Shape(value, "worldEpoch entries once", "gates");
        var restored = new Dictionary<string, RuntimeVariableEntry>(StringComparer.Ordinal);
        foreach (var row in RuntimeJson.Rows(value, "entries"))
        {
            RuntimeJson.Shape(row, "name scope subject slot type value");
            var name = RuntimeJson.Text(row, "name"); var kind = RuntimeJson.Text(row, "scope");
            RuntimeJson.Require(VariableScopeKinds.All.Contains(kind), "variable-scope", kind);
            var subject = row.GetProperty("subject").GetString() ?? "";
            var slot = (int)RuntimeJson.Integer(row.GetProperty("slot"), -1);
            var type = RuntimeJson.Text(row, "type");
            RuntimeJson.Require(VariableValueTypes.All.Contains(type), "variable-type", name);
            var declaration = Declared(name);
            if (declaration == null || declaration.Type != type || declaration.Scope != kind) continue;
            var entry = new RuntimeVariableEntry(name, kind, subject, slot, type, row.GetProperty("value").Clone());
            restored[entry.Key] = entry;
        }
        foreach (var key in entries.Keys) touched.Add(key);
        entries.Clear();
        foreach (var entry in restored) { entries[entry.Key] = entry.Value; touched.Add(entry.Key); }
        once.Clear();
        foreach (var key in RuntimeJson.Strings(value.GetProperty("once"))) once.Add(key);
    }

    private static bool Same(JsonElement left, JsonElement right) => RuntimeJson.StableText(left) == RuntimeJson.StableText(right);
}

/// <summary>
/// The variable declarations and the value checks behind them. Both the plan header and a `g-var` write go through
/// these, so "a variable is a declared name of a declared type" is one rule with one implementation rather than a
/// loader check and a runtime check that can drift apart.
/// </summary>
internal static class RuntimeVariableContract
{
    internal const int MaximumDeclarations = 512;
    internal const int MaximumEntries = 4096;
    internal const int MaximumTextBytes = 512;

    /// <summary>Reads the plan header's `variables[]`, rejecting the first declaration that is not a name, a scope,
    /// a type and an initial value of that type.</summary>
    internal static IReadOnlyList<VariableDeclaration> ParsePlanVariables(JsonElement plan)
    {
        var rows = plan.TryGetProperty("variables", out var declared) ? RuntimeJson.Rows(plan, "variables", MaximumDeclarations) : Array.Empty<JsonElement>();
        var result = new List<VariableDeclaration>(rows.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in rows)
        {
            RuntimeJson.Shape(row, "id scope type initial");
            var name = RuntimeJson.Id(row, "id");
            RuntimeJson.Require(seen.Add(name), "duplicate-variable", name);
            var scope = RuntimeJson.Text(row, "scope");
            RuntimeJson.Require(VariableScopeKinds.All.Contains(scope), "variable-scope", name);
            // A named variable is a level object, and the plan header declares those in `objects[]`: one spelling
            // per concept, so the section an author wrote a row in is the scope it has.
            RuntimeJson.Require(scope != VariableScopeKinds.Named, "variable-scope", name);
            var type = RuntimeJson.Text(row, "type");
            RuntimeJson.Require(VariableValueTypes.All.Contains(type), "variable-type", name);
            var initial = row.GetProperty("initial");
            Validate(initial, type, name);
            result.Add(new VariableDeclaration(name, scope, type, initial.Clone()));
        }
        return result.AsReadOnly();
    }

    /// <summary>Checks one payload against a declared type. The type is the whole contract: a number variable holds
    /// a finite number, a flag holds a boolean, text holds a bounded string, and an entity or handle holds the
    /// identity object of its own kind. Null is allowed for entity and handle only, because "this variable holds no
    /// entity" is a fact a behaviour can act on and needs no second representation.</summary>
    internal static void Validate(JsonElement value, string type, string name)
    {
        if (value.ValueKind == JsonValueKind.Null && type is VariableValueTypes.Entity or VariableValueTypes.Handle) return;
        switch (type)
        {
            case VariableValueTypes.Number:
                RuntimeJson.Require(value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number),
                    "variable-value", name);
                return;
            case VariableValueTypes.Flag:
                RuntimeJson.Require(value.ValueKind is JsonValueKind.True or JsonValueKind.False, "variable-value", name);
                return;
            case VariableValueTypes.Text:
                var text = value.ValueKind == JsonValueKind.String ? value.GetString()! : null;
                RuntimeJson.Require(text != null && System.Text.Encoding.UTF8.GetByteCount(text) <= MaximumTextBytes, "variable-value", name);
                foreach (var character in text!) RuntimeJson.Require(character > 31 && character != 127, "variable-value", name);
                return;
            case VariableValueTypes.Entity:
                RuntimeJson.Shape(value, "id worldEpoch lifeEpoch");
                RuntimeJson.Text(value, "id"); RuntimeJson.Integer(value.GetProperty("worldEpoch")); RuntimeJson.Integer(value.GetProperty("lifeEpoch"));
                return;
            case VariableValueTypes.Handle:
                RuntimeJson.Shape(value, "worldEpoch lifeEpoch local provider");
                RuntimeJson.Integer(value.GetProperty("worldEpoch")); RuntimeJson.Integer(value.GetProperty("lifeEpoch"));
                RuntimeJson.Integer(value.GetProperty("local")); RuntimeJson.Integer(value.GetProperty("provider"));
                return;
            default:
                throw new RuntimeContractException("variable-type", name);
        }
    }
}
