using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

namespace ForgeDevelopment.Native;

/// <summary>
/// The data block export. It walks the loaded assemblies for every closed <c>GameDataBlockBase&lt;T&gt;</c> the game
/// has, asks each type for its own loaded blocks and records the identity every block carries. It does not hold a
/// list of block types: a block type another mod adds is exported because it answers the same static
/// <c>GetAllBlocks</c>, which is the whole point of a per-level export.
/// </summary>
internal static class CaptureDataBlocks
{
    private const string Where = "ForgeDevelopment.Native.CaptureDataBlocks";
    private static readonly List<(Type Type, MethodInfo? All, MethodInfo? Name, MethodInfo? PersistentId)> Resolved = new();
    private static bool _resolved;

    /// <summary>The block types this process has loaded. Resolved once, because the answer cannot change while the
    /// data manager stays loaded and the walk is not cheap.</summary>
    internal static IReadOnlyList<(Type Type, MethodInfo? All, MethodInfo? Name, MethodInfo? PersistentId)> Types()
    {
        if (_resolved) return Resolved;
        _resolved = true;
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var name = assembly.GetName().Name ?? "";
            if (name is "ForgeDevelopment.Native" or "0Harmony" or "BepInEx.Core" or "Il2CppInterop.Runtime"
                || name.StartsWith("System.", StringComparison.Ordinal) || name.StartsWith("Microsoft.", StringComparison.Ordinal)
                || name is "mscorlib" or "netstandard" or "Il2Cppmscorlib") continue;
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (Exception) { continue; }
            foreach (var type in types)
            {
                if (type.IsAbstract || type.IsGenericTypeDefinition) continue;
                var baseType = BaseBlockType(type);
                if (baseType == null) continue;
                Resolved.Add((type, Static(baseType, "GetAllBlocks"), Static(baseType, "get_name"), Static(baseType, "get_persistentID")));
            }
        }
        Resolved.Sort((left, right) => string.CompareOrdinal(left.Type.FullName, right.Type.FullName));
        return Resolved;
    }

    /// <summary>The blocks one type has loaded, as plain objects. A type whose table cannot be read answers null and
    /// the snapshot says so rather than failing.</summary>
    internal static IReadOnlyList<object>? Blocks(Type type)
    {
        var method = Static(type, "GetAllBlocks") ?? FindMethod(type, "GetAllBlocks");
        if (method == null) return null;
        try
        {
            var value = method.Invoke(null, null);
            if (value == null) return null;
            var list = new List<object>();
            foreach (var block in Enumerate(value)) if (block != null) list.Add(block);
            return list;
        }
        catch (Exception error) { Log("blocks:" + type.Name, error); return null; }
    }

    internal static string Name(object block) => Text(block, "get_name") ?? block.GetType().Name;

    internal static string PublicName(object block)
        => Text(block, "get_PublicName") ?? Text(block, "PublicName") ?? "";

    internal static string PersistentId(object block)
    {
        var value = Member(block, "get_persistentID") ?? Member(block, "persistentID");
        return value == null ? "" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? "";
    }

    private static IEnumerable<object?> Enumerate(object value)
    {
        if (value is System.Collections.IEnumerable enumerable && value is not string)
        {
            foreach (var item in enumerable) yield return item;
            yield break;
        }
        var type = value.GetType();
        var count = type.GetProperty("Count")?.GetValue(value) ?? type.GetProperty("Length")?.GetValue(value);
        var indexer = type.GetProperty("Item");
        if (count == null || indexer == null) yield break;
        var length = Convert.ToInt32(count, CultureInfo.InvariantCulture);
        for (var i = 0; i != length; i++) yield return indexer.GetValue(value, new object[] { i });
    }

    /// <summary>The closed <c>GameDataBlockBase&lt;T&gt;</c> a block type derives from, or null for anything else.
    /// The base is found by name so a block type a mod injected is treated exactly like a shipped one.</summary>
    private static Type? BaseBlockType(Type type)
    {
        for (var current = type.BaseType; current != null; current = current.BaseType)
        {
            if (current.IsGenericType && current.GetGenericTypeDefinition().FullName == "GameData.GameDataBlockBase`1")
                return current;
        }
        return null;
    }

    private static MethodInfo? Static(Type type, string name) => FindMethod(type, name);

    private static MethodInfo? FindMethod(Type type, string name)
        => type.GetMethod(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);

    private static object? Member(object block, string name)
    {
        var type = block.GetType();
        try
        {
            var property = type.GetProperty(name, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            return property?.GetValue(block);
        }
        catch (Exception) { return null; }
    }

    private static string? Text(object block, string name) => Member(block, name) as string;

    /// <summary>The data block snapshot: one row per loaded block type, then one row per block with the identity a
    /// reader matches against an authored resource. It is the export the task asks for once per level.</summary>
    internal static CaptureSnapshot Collect(string reason, long tick, int maxPerType)
    {
        var snapshot = new CaptureSnapshot(reason, "datablocks", tick);
        var section = snapshot.Section(CaptureSections.DataBlocks, "GameDataBlockBase<T>.GetAllBlocks()");
        foreach (var (type, _, _, _) in Types())
        {
            var blocks = Blocks(type);
            if (blocks == null) continue;
            var count = new CaptureRow("dataBlockType", "datablock/" + type.FullName, type.Name);
            count.Field("count", blocks.Count.ToString(CultureInfo.InvariantCulture));
            section.Rows.Add(count);
            for (var i = 0; i != blocks.Count && i < maxPerType; i++)
            {
                var block = blocks[i];
                if (block == null) continue;
                var row = new CaptureRow("dataBlock", "datablock/" + type.Name + "/" + PersistentId(block), Name(block));
                row.Group = type.Name;
                row.Field("type", type.Name);
                row.Field("persistentId", PersistentId(block));
                row.Field("publicName", PublicName(block));
                section.Rows.Add(row);
            }
            if (blocks.Count > maxPerType)
                snapshot.Notes.Add(type.Name + ": " + blocks.Count.ToString(CultureInfo.InvariantCulture) + " blocks, first "
                    + maxPerType.ToString(CultureInfo.InvariantCulture) + " recorded");
        }
        return snapshot;
    }

    private static void Log(string what, Exception error)
    {
        try
        {
            RecSession.Write("log", "capture_error", json =>
            {
                json.WriteString("where", Where + ":" + what);
                json.WriteString("error", error.GetType().Name);
                json.WriteString("message", RecReflect.Truncate(error.Message, 200));
            });
        }
        catch (Exception) { }
    }
}

