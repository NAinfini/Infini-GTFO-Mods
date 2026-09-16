using System;
using System.Buffers;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace ForgeDevelopment.Native;

/// <summary>
/// A bounded JSON sink. <see cref="System.Text.Json.Utf8JsonWriter"/> cannot be pointed at a cap, so every recorder
/// record is composed into one of these: the writer is handed the buffer's own memory and a write that would pass the
/// cap throws <see cref="RecValueTooLargeException"/>. That is what one over-long record costs — the session drops
/// that record and counts it, instead of letting a single object tree grow without bound.
/// </summary>
internal sealed class RecJsonBuffer : IBufferWriter<byte>
{
    private readonly byte[] _buffer;
    private int _written;

    internal RecJsonBuffer(int limit) { _buffer = new byte[Math.Max(256, limit)]; }
    internal ReadOnlySpan<byte> WrittenSpan => _buffer.AsSpan(0, _written);
    internal int Length => _written;
    internal void Reset() => _written = 0;

    public void Advance(int count)
    {
        if (count < 0 || _written + count > _buffer.Length) throw new RecValueTooLargeException(_buffer.Length);
        _written += count;
    }

    public Memory<byte> GetMemory(int sizeHint = 0)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (_written + Math.Max(sizeHint, 256) > _buffer.Length) throw new RecValueTooLargeException(_buffer.Length);
        return _buffer.AsMemory(_written);
    }

    public Span<byte> GetSpan(int sizeHint = 0)
    {
        if (sizeHint < 0) throw new ArgumentOutOfRangeException(nameof(sizeHint));
        if (_written + Math.Max(sizeHint, 256) > _buffer.Length) throw new RecValueTooLargeException(_buffer.Length);
        return _buffer.AsSpan(_written);
    }
}

/// <summary>Raised when one record would not fit the per-record budget. The session counts the record as dropped.</summary>
internal sealed class RecValueTooLargeException : Exception
{
    internal RecValueTooLargeException(int limit) : base("A recorded value exceeded the " + limit + " byte record budget.") { }
}

/// <summary>
/// Turns any IL2CPP or managed value into JSON. Nothing about a value is assumed safe: every member read runs inside
/// the call's own guard, a failed read writes <c>"$error:..."</c> in place of the member, and a value already on the
/// current path is written as <c>{"$ref":id}</c>. Depth, element count and wall-clock budget are capped, and the
/// budget is tested before each member, so one hostile object graph costs a bounded amount of frame time.
/// </summary>
internal static class RecReflect
{
    internal const int DefaultMaxDepth = 6;
    internal const int DefaultMaxElements = 64;
    internal const int DefaultBudgetMilliseconds = 20;

    /// <summary>Types the reader answers itself: enums are written as name plus number, and the game's own value types
    /// are read through <see cref="IRecValueProbe"/> because interop exposes them as private native fields.</summary>
    internal static IRecValueProbe Probe { get; set; } = RecInertProbe.Instance;

    internal static int MaxDepth { get; set; } = DefaultMaxDepth;
    internal static int MaxElements { get; set; } = DefaultMaxElements;
    internal static int BudgetMilliseconds { get; set; } = DefaultBudgetMilliseconds;

    /// <summary>Writes one value under a property name. <paramref name="name"/> must be a JSON property name; an
    /// array element or a member inside a value type is written through the writer itself instead.</summary>
    internal static void WriteValue(Utf8JsonWriter w, object? value, int depth)
    {
        ArgumentNullException.ThrowIfNull(w);
        new RecWriter(w).WriteValue(value, Math.Max(0, depth));
    }

    /// <summary>Reads a dotted member path such as <c>m_tagMarker.m_title.text</c>. A step is a field or a property,
    /// public or not, on an interop or managed object; a step that cannot be read answers null rather than throwing,
    /// so the caller can record the path it asked for.</summary>
    internal static object? ReadPath(object? root, string path)
    {
        if (root == null) return null;
        if (string.IsNullOrEmpty(path)) return root;
        var current = root;
        var start = 0;
        while (true)
        {
            var dot = path.IndexOf('.', start);
            var name = dot < 0 ? path[start..] : path[start..dot];
            if (name.Length != 0) current = ReadMember(current, name);
            if (current == null || dot < 0) return current;
            start = dot + 1;
        }
    }

    /// <summary>Reads several paths as one row of name/value pairs. A path that reads to nothing keeps its row with a
    /// null value, so a trace record never silently loses the field it was configured for.</summary>
    internal static (string Path, object? Value)[] ReadPaths(object? root, IReadOnlyList<string> paths)
    {
        var values = new (string, object?)[paths.Count];
        for (var i = 0; i < paths.Count; i++) values[i] = (paths[i], ReadPath(root, paths[i]));
        return values;
    }

    /// <summary>Type name plus a short identity: the engine instance id for a live game object, the managed hash code
    /// otherwise. This is what a trace record carries when the full value is not wanted.</summary>
    internal static string Describe(object? obj)
    {
        if (obj == null) return "null";
        if (obj is string text) return "string(" + text.Length.ToString(CultureInfo.InvariantCulture) + ")";
        var type = obj.GetType();
        if (type.IsPrimitive || type.IsEnum) return type.Name + "(" + Convert.ToString(obj, CultureInfo.InvariantCulture) + ")";
        var id = InstanceId(obj, out var native);
        var name = ReadMember(obj, "name") as string;
        return type.FullName + "#" + id.ToString(CultureInfo.InvariantCulture) + (native ? "" : "(managed)")
            + (string.IsNullOrEmpty(name) ? "" : " \"" + Truncate(name!, 64) + "\"");
    }

    internal static string Truncate(string text, int max) => text.Length <= max ? text : text[..max] + "…";

    /// <summary>The shortest identity available. A destroyed Unity object keeps its instance id, which is exactly what
    /// makes a trace of it readable after the fact.</summary>
    internal static long InstanceId(object obj, out bool native)
    {
        native = false;
        if (Probe.TryInstanceId(obj, out var id)) { native = true; return id; }
        return (obj.GetType().GetHashCode() * 1_000_003L) + obj.GetHashCode();
    }

    /// <summary>Reads one member by name: property first, then field, then the native field the interop type does not
    /// project. It answers null for a member that cannot be read, and never throws.</summary>
    internal static object? ReadMember(object target, string name)
    {
        var type = target.GetType();
        try
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (property != null && property.GetIndexParameters().Length == 0)
            {
                var getter = property.GetGetMethod(true);
                if (getter != null) return Invoke(getter, target);
            }
            var field = type.GetField(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.FlattenHierarchy);
            if (field != null) return field.GetValue(field.IsStatic ? null : target);
        }
        catch (Exception)
        {
            // A member the game itself refuses to read is not a recorder error; the caller records the null.
        }
        return Probe.TryReadNativeMember(target, name, out var value) ? value : null;
    }

    private static object? Invoke(MethodInfo method, object target, params object?[] arguments)
    {
        try { return method.Invoke(method.IsStatic ? null : target, arguments); }
        catch (TargetInvocationException error) { throw error.InnerException ?? error; }
    }

    /// <summary>True when the value carries no state beyond itself: nulls, primitives, strings, enums and the game's
    /// value types. RecTracer uses the same test to decide that a `changes` diff has something to compare.</summary>
    internal static bool IsScalar(object? value)
    {
        if (value == null) return true;
        var type = Nullable.GetUnderlyingType(value.GetType()) ?? value.GetType();
        return IsScalarOf(type) || Probe.Handles(type);
    }

    private static bool IsScalarOf(Type type)
        => type.IsPrimitive || type.IsEnum || type == typeof(string) || type == typeof(decimal)
            || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid);

    /// <summary>Cycles, depth, element caps and per-reader error guards live here. Every value on the current path is
    /// held in <see cref="_ids"/> until its own object ends, so a cycle answers the id it was first written under.</summary>
    internal sealed class RecWriter
    {
        private readonly Utf8JsonWriter _json;
        private readonly Dictionary<object, int> _ids = new(ReferenceComparer.Instance);
        private readonly long _deadline;
        private int _nextId = 1;

        internal RecWriter(Utf8JsonWriter json)
        {
            _json = json;
            _deadline = Environment.TickCount64 + BudgetMilliseconds;
        }

        /// <summary>The writer a probe composes into when it answers a value type itself.</summary>
        internal Utf8JsonWriter Json => _json;

        internal void WriteNamed(object? value, string name, int depth)
        {
            _json.WritePropertyName(name);
            WriteValue(value, depth);
        }

        /// <summary>A value type the probe owns: Unity's own Vector3, Color and Quaternion, and their IL2CPP
        /// wrappers. The probe writes the members it reads; everything the reader does still applies to them.</summary>
        internal void WriteValue(object? value, int depth)
        {
            if (value == null) { _json.WriteNullValue(); return; }
            var type = value.GetType();
            var underlying = Nullable.GetUnderlyingType(type);
            if (underlying != null) { WriteSimple(value, underlying); return; }
            if (type.IsEnum) { WriteEnum(value, type); return; }
            if (type.IsPrimitive || type == typeof(string) || type == typeof(decimal)
                || type == typeof(DateTime) || type == typeof(DateTimeOffset) || type == typeof(TimeSpan) || type == typeof(Guid))
            { WriteSimple(value, type); return; }
            if (Probe.Handles(type)) { Probe.Write(this, value, depth); return; }            if (value is IDictionary map) { WriteDictionary(map, depth); return; }
            if (value is IEnumerable sequence) { WriteSequence(sequence, depth); return; }
            WriteObject(value, depth);
        }

        private void WriteSimple(object value, Type type)
        {
            switch (value)
            {
                case bool b: _json.WriteBooleanValue(b); break;
                case string s: _json.WriteStringValue(s); break;
                case char c: _json.WriteStringValue(c.ToString()); break;
                // Numbers are written as numbers: a trace record that turned every id into a string would sort and
                // compare differently from the value it claims to record.
                case float f: WriteNumber(f); break;
                case double d: WriteNumber(d); break;
                case decimal m: _json.WriteNumberValue(m); break;
                case byte v: _json.WriteNumberValue(v); break;
                case sbyte v: _json.WriteNumberValue(v); break;
                case short v: _json.WriteNumberValue(v); break;
                case ushort v: _json.WriteNumberValue(v); break;
                case int v: _json.WriteNumberValue(v); break;
                case uint v: _json.WriteNumberValue(v); break;
                case long v: _json.WriteNumberValue(v); break;
                case ulong v: _json.WriteNumberValue(v); break;
                case DateTime dt: _json.WriteStringValue(dt.ToString("O", CultureInfo.InvariantCulture)); break;
                case DateTimeOffset dto: _json.WriteStringValue(dto.ToString("O", CultureInfo.InvariantCulture)); break;
                case TimeSpan ts: _json.WriteStringValue(ts.ToString()); break;
                case Guid g: _json.WriteStringValue(g.ToString()); break;
                default:
                    if (value is IFormattable formattable) _json.WriteStringValue(formattable.ToString(null, CultureInfo.InvariantCulture));
                    else _json.WriteStringValue(value.ToString() ?? "");
                    break;
            }
        }

        private void WriteNumber(double value)
        {
            if (double.IsNaN(value) || double.IsInfinity(value))
            {
                _json.WriteStringValue(value.ToString(CultureInfo.InvariantCulture));
                return;
            }
            _json.WriteNumberValue(value);
        }

        internal void WriteEnum(object value, Type type)
        {
            _json.WriteStartObject();
            _json.WriteString("name", Enum.GetName(type, value) ?? value.ToString());
            try { _json.WriteNumber("value", Convert.ToInt64(value, CultureInfo.InvariantCulture)); }
            catch (Exception) { _json.WriteString("value", value.ToString() ?? ""); }
            _json.WriteString("type", type.Name);
            _json.WriteEndObject();
        }

        private void WriteSequence(IEnumerable sequence, int depth)
        {
            if (!Enter(sequence, depth, out var id)) { WriteReference(id); return; }
            _json.WriteStartArray();
            var written = 0;
            var truncated = false;
            try
            {
                foreach (var item in sequence)
                {
                    if (written >= MaxElements || OverBudget()) { truncated = true; break; }
                    WriteValue(item, depth + 1);
                    written++;
                }
            }
            catch (Exception error) when (error is not RecValueTooLargeException)
            {
                WriteError(error);
            }
            finally { Exit(sequence); }
            _json.WriteEndArray();
            if (truncated) _json.WriteNumber("$truncatedAt", written);
        }

        private void WriteDictionary(IDictionary map, int depth)
        {
            if (!Enter(map, depth, out var id)) { WriteReference(id); return; }
            _json.WriteStartObject();
            var written = 0;
            var truncated = false;
            try
            {
                foreach (DictionaryEntry entry in map)
                {
                    if (written >= MaxElements || OverBudget()) { truncated = true; break; }
                    _json.WritePropertyName(Truncate(entry.Key?.ToString() ?? "null", 128));
                    WriteValue(entry.Value, depth + 1);
                    written++;
                }
            }
            catch (Exception error) when (error is not RecValueTooLargeException)
            {
                _json.WriteString("$error", error.GetType().Name + ": " + Truncate(error.Message, 200));
            }
            finally { Exit(map); }
            _json.WriteEndObject();
            if (truncated) _json.WriteNumber("$truncatedAt", written);
        }

        private void WriteObject(object value, int depth)
        {
            if (!Enter(value, depth, out var id)) { WriteReference(id); return; }
            _json.WriteStartObject();
            var type = value.GetType();
            _json.WriteString("$type", type.FullName);
            try
            {
                if (Probe.IsDestroyed(value)) _json.WriteBoolean("$destroyed", true);
                if (depth >= MaxDepth) { _json.WriteBoolean("$depthCapped", true); return; }
                var written = 0;
                foreach (var member in Members(type))
                {
                    if (written >= MaxElements || OverBudget()) { _json.WriteNumber("$truncatedAt", written); break; }
                    _json.WritePropertyName(member.Name);
                    try { WriteValue(ReadMemberOrNull(value, member), depth + 1); }
                    catch (RecValueTooLargeException) { throw; }
                    catch (Exception error) { WriteError(error); }
                    written++;
                }
            }
            catch (Exception error) when (error is not RecValueTooLargeException)
            {
                _json.WriteString("$error", error.GetType().Name + ": " + Truncate(error.Message, 200));
            }
            finally
            {
                Exit(value);
                _json.WriteEndObject();
            }
        }

        private static object? ReadMemberOrNull(object target, MemberInfo member)
        {
            switch (member)
            {
                case FieldInfo field: return field.GetValue(field.IsStatic ? null : target);
                case PropertyInfo property:
                    var getter = property.GetGetMethod(true);
                    return getter == null ? null : Invoke(getter, target);
                default: return null;
            }
        }

        /// <summary>The members worth reading: readable instance fields and indexer-free properties, declared and
        /// inherited. Interop assemblies mirror every native method and field as a <c>NativeMethodInfoPtr_*</c> or
        /// <c>NativeFieldInfoPtr_*</c> static pointer; those are addresses, not state, and are skipped by name.</summary>
        internal static IEnumerable<MemberInfo> Members(Type type)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var current = type; current != null && current != typeof(object); current = current.BaseType)
            {
                foreach (var field in current.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (field.IsStatic || IsInteropPointer(field.Name)) continue;
                    if (seen.Add(field.Name)) yield return field;
                }
                foreach (var property in current.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                {
                    if (property.GetIndexParameters().Length != 0 || property.GetGetMethod(true) == null) continue;
                    if (seen.Add(property.Name)) yield return property;
                }
            }
        }

        private static bool IsInteropPointer(string name)
            => name.StartsWith("NativeMethodInfoPtr_", StringComparison.Ordinal) || name.StartsWith("NativeFieldInfoPtr_", StringComparison.Ordinal);

        /// <summary>Registers a value on the current path. A repeated reference answers false with the id it was first
        /// written under, which is what turns a cycle into <c>{"$ref":id}</c> instead of a stack overflow.</summary>
        private bool Enter(object value, int depth, out int id)
        {
            if (_ids.TryGetValue(value, out id)) return false;
            id = _nextId++;
            _ids[value] = id;
            return true;
        }

        private void Exit(object value) => _ids.Remove(value);

        private void WriteReference(int id)
        {
            _json.WriteStartObject();
            _json.WriteNumber("$ref", id);
            _json.WriteEndObject();
        }

        private void WriteError(Exception error)
        {
            _json.WriteStartObject();
            _json.WriteString("$error", error.GetType().Name + ": " + Truncate(error.Message, 200));
            _json.WriteEndObject();
        }

        private bool OverBudget() => Environment.TickCount64 > _deadline;
    }

    private sealed class ReferenceComparer : IEqualityComparer<object>
    {
        internal static readonly ReferenceComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}

/// <summary>What the reader needs from the runtime it reads. The game build installs the IL2CPP implementation; a test
/// installs a managed double, so cycle, depth, element-cap and error handling are all exercised without Unity.</summary>
internal interface IRecValueProbe
{
    bool TryInstanceId(object value, out long id);
    bool IsDestroyed(object value);
    bool TryReadNativeMember(object target, string name, out object? value);
    /// <summary>True for the game's own value types, whose data lives in native fields rather than managed ones.
    /// The probe owns both the test and the write for them, so the pure reader stays free of Unity references.</summary>
    bool Handles(Type type);
    /// <summary>Writes one of the probe's own value types. Members that are not simple are handed back to the
    /// writer, so the same depth, element and budget caps apply inside them.</summary>
    void Write(RecReflect.RecWriter writer, object value, int depth);
}

/// <summary>The default: no engine, no interop. Reading an interop object under this probe reports only its managed
/// view, which is what a test process and an early startup both have.</summary>
internal sealed class RecInertProbe : IRecValueProbe
{
    internal static readonly RecInertProbe Instance = new();
    public bool TryInstanceId(object value, out long id) { id = 0; return false; }
    public bool IsDestroyed(object value) => false;
    public bool TryReadNativeMember(object target, string name, out object? value) { value = null; return false; }
    public bool Handles(Type type) => false;
    public void Write(RecReflect.RecWriter writer, object value, int depth) { }
}


