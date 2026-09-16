using System;
using System.Globalization;
using System.Reflection;
using System.Text.Json;
using Il2CppInterop.Runtime;
using Il2CppInterop.Runtime.InteropTypes;
using UnityEngine;
using Object = UnityEngine.Object;

namespace ForgeDevelopment.Native;

/// <summary>
/// The IL2CPP half of the reader. It answers the three questions the pure reader cannot: whether a game object is
/// still alive, what a wrapper's identity is, and how to read a field the interop type does not project as a
/// property. It also writes the engine's own value types, whose data lives in private native fields.
/// </summary>
internal sealed class RecIl2CppProbe : IRecValueProbe
{
    internal static readonly RecIl2CppProbe Instance = new();

    private const uint Vector3Hash = 0x8C1A3F27;
    private const uint Vector2Hash = 0x3E9B7D14;
    private const uint Vector4Hash = 0x51D2C6A8;
    private const uint QuaternionHash = 0xA7349E5B;
    private const uint ColorHash = 0x1F86B3D0;
    private const uint Color32Hash = 0x6D20F4A9;
    private const uint RectHash = 0xB5C81E63;
    private const uint BoundsHash = 0x27A9D4F1;
    private const uint IntPtrHash = 0xE40B7C95;

    /// <summary>A destroyed Unity object still has a managed wrapper, and almost every member read on it throws. It
    /// is reported as destroyed instead, because "the object is gone" is the fact a later reader needs.</summary>
    public bool IsDestroyed(object value)
    {
        try
        {
            if (value is Object unity)
            {
                var pointer = unity.Pointer;
                return pointer == IntPtr.Zero || unity.WasCollected;
            }
            if (value is Il2CppObjectBase interop) return interop.WasCollected || interop.Pointer == IntPtr.Zero;
            return false;
        }
        catch (Exception) { return true; }
    }

    /// <summary>Unity's own instance id when the value is a Unity object, the native pointer otherwise. The pointer is
    /// what makes two records about the same native object comparable when no component id exists.</summary>
    public bool TryInstanceId(object value, out long id)
    {
        id = 0;
        try
        {
            if (value is Object unity)
            {
                id = unity.GetInstanceID();
                return true;
            }
            if (value is Il2CppObjectBase interop)
            {
                id = interop.Pointer.ToInt64();
                return true;
            }
        }
        catch (Exception)
        {
            // A destroyed object can still answer its pointer; if even that fails the managed hash code is used.
            if (value is Il2CppObjectBase interop) { id = interop.Pointer.ToInt64(); return true; }
        }
        return false;
    }

    /// <summary>Reads a native field by name when the managed type has no member for it: the interop class handle
    /// finds the field, and the field's value comes back boxed. It is how a private native field of an interop type
    /// is reachable without a generated property.</summary>
    public bool TryReadNativeMember(object target, string name, out object? value)
    {
        value = null;
        if (target is not Il2CppObjectBase interop) return false;
        try
        {
            var field = IL2CPP.GetIl2CppField(interop.ObjectClass, name);
            if (field == IntPtr.Zero) return false;
            if (interop.Pointer == IntPtr.Zero) return false;
            var boxed = IL2CPP.il2cpp_field_get_value_object(field, interop.Pointer);
            if (boxed == IntPtr.Zero) return true;
            value = new Il2CppSystem.Object(boxed);
            return true;
        }
        catch (Exception)
        {
            value = null;
            return false;
        }
    }

    public bool Handles(Type type)
    {
        var hash = Hash(type.FullName ?? type.Name);
        return hash is Vector3Hash or Vector2Hash or Vector4Hash or QuaternionHash or ColorHash or Color32Hash or RectHash or BoundsHash or IntPtrHash;
    }

    /// <summary>Writes the engine's value types member by member. The members are read through the same guarded reader
    /// as everything else, so a game build that renames one degrades to a reported error rather than a crash.</summary>
    public void Write(RecReflect.RecWriter writer, object value, int depth)
    {
        writer.Json.WriteStartObject();
        writer.Json.WriteString("$type", value.GetType().FullName ?? "");
        foreach (var member in Members(value.GetType()))
        {
            writer.Json.WritePropertyName(member);
            try { writer.WriteNamed(RecReflect.ReadMember(value, member), member, depth + 1); }
            catch (Exception error) { writer.Json.WriteString(member, "$error:" + error.GetType().Name); }
        }
        writer.Json.WriteEndObject();
    }

    private static readonly string[] Vector3Members = { "x", "y", "z" };
    private static readonly string[] Vector2Members = { "x", "y" };
    private static readonly string[] Vector4Members = { "x", "y", "z", "w" };
    private static readonly string[] QuaternionMembers = { "x", "y", "z", "w" };
    private static readonly string[] ColorMembers = { "r", "g", "b", "a" };
    private static readonly string[] RectMembers = { "x", "y", "width", "height" };
    private static readonly string[] BoundsMembers = { "center", "extents", "size", "min", "max" };
    private static readonly string[] IntPtrMembers = { "value" };

    private static string[] Members(Type type)
    {
        var hash = Hash(type.FullName ?? type.Name);
        if (hash == Vector3Hash) return Vector3Members;
        if (hash == Vector2Hash) return Vector2Members;
        if (hash == Vector4Hash) return Vector4Members;
        if (hash == QuaternionHash) return QuaternionMembers;
        if (hash == ColorHash || hash == Color32Hash) return ColorMembers;
        if (hash == RectHash) return RectMembers;
        if (hash == BoundsHash) return BoundsMembers;
        if (hash == IntPtrHash) return IntPtrMembers;
        return Array.Empty<string>();
    }

    /// <summary>FNV-1a over the type's name. It is computed rather than referenced so this file needs no compile-time
    /// knowledge of the engine's assemblies, and two builds with different type identities stay apart.</summary>
    private static uint Hash(string name)
    {
        var hash = 2166136261u;
        foreach (var c in name) { hash ^= c; hash *= 16777619u; }
        return hash;
    }
}

/// <summary>What the game-side reader needs: install the IL2CPP probe, read engine versions, and keep the camera
/// helpers the bookmark uses.</summary>
internal static class RecUnity
{
    internal static void Install() => RecReflect.Probe = RecIl2CppProbe.Instance;

    internal static string GameVersion => Safe(() => Application.version, "unavailable") ?? "unavailable";
    internal static string UnityVersion => Safe(() => Application.unityVersion, "unavailable") ?? "unavailable";

    private static T? Safe<T>(Func<T> read, T? fallback = default)
    {
        try { return read(); }
        catch (Exception) { return fallback; }
    }
}

/// <summary>
/// The recorder's hotkeys. They are polled from the authoring monitor's Update, which is the only place the plugin
/// already runs per frame; the entries are config keys because the keys themselves are configuration.
/// </summary>
internal static class RecHotkeys
{
    internal static void Poll()
    {
        if (!RecSession.Active) return;
        if (Pressed(Settings.RecorderBookmarkKey.Value)) RecSession.Bookmark(Settings.RecorderBookmarkLabel.Value);
        if (Pressed(Settings.RecorderSnapshotKey.Value)) CaptureRegistry.SnapshotAll("hotkey");
        if (Pressed(Settings.RecorderScreenshotKey.Value)) RecSession.Screenshot("hotkey");
        if (Pressed(Settings.RecorderPanelKey.Value)) ExperimentPanel.Toggle();
    }

    private static bool Pressed(KeyCode key)
    {
        try { return key != KeyCode.None && Input.GetKeyDown(key); }
        catch (Exception) { return false; }
    }
}

/// <summary>
/// The screen-corner notice. A session that reached its budget says so where the player is looking, not only in a
/// log they will never read; it is drawn by the authoring monitor and disappears on its own.
/// </summary>
internal static class RecNotice
{
    private static string _text = "";
    private static long _until;
    private static GUIStyle? _style;

    internal static void Show(string message)
    {
        _text = RecReflect.Truncate(message, 200);
        _until = Environment.TickCount64 + 6000;
    }

    internal static void OnGui()
    {
        if (Environment.TickCount64 > _until || _text.Length == 0) return;
        _style ??= new GUIStyle(GUI.skin.label) { fontSize = 14, alignment = TextAnchor.LowerLeft, wordWrap = false };
        _style.normal.textColor = Color.white;
        var rect = new Rect(12f, Screen.height - 48f, Screen.width - 24f, 40f);
        GUI.Label(rect, _text, _style);
    }
}
