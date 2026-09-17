using System;
using System.Globalization;
using System.Reflection;

namespace ForgeWeapon.Native;

/// <summary>
/// The three Unity value types and the one vector call this package's own projectile launch needs, reached
/// through the game's own interop assemblies by reflection rather than by a compile-time reference.
///
/// The reason is not the engine: it is that the launch body has to be compilable against the plain Unity surface
/// this repository's test doubles provide, and a game-free build carries no `UnityEngine.CoreModule` at all. A
/// reflection handle answers the same members that reference would — the struct fields, the `Cross` call and the
/// spawn method itself — so the launch row is exercised under the doubles by the same code the game runs, and a
/// build that renamed one of those members refuses the launch by name instead of failing to load the package.
///
/// Every member here is resolved once, at construction, from the assemblies the running process already has
/// loaded: nothing is looked up by name per call, and an unresolved member is a null handle rather than a throw
/// at the call site.
/// </summary>
internal sealed class UnityValueBridge
{
    private readonly Type? _vector;
    private readonly Type? _quaternion;
    private readonly MethodInfo? _cross;

    private UnityValueBridge(Type? vector, Type? quaternion, MethodInfo? cross)
    {
        _vector = vector; _quaternion = quaternion; _cross = cross;
    }

    /// <summary>The bridge of this process, or null while none could be built: the vector type is the one type
    /// every other member hangs off, so a build without it has no launch at all.</summary>
    internal static UnityValueBridge? Resolve()
    {
        var vector = Find("UnityEngine.Vector3");
        if (vector == null) return null;
        return new UnityValueBridge(vector, Find("UnityEngine.Quaternion"), FindMethod(vector, "Cross", 2));
    }

    /// <summary>Whether the two value types and the one vector call this package reads are all here.</summary>
    internal bool Complete => _vector != null && _quaternion != null && _cross != null;

    /// <summary>One vector from three components, or null when the type is not here.</summary>
    internal object? Vector(double x, double y, double z)
    {
        if (_vector == null) return null;
        var value = Activator.CreateInstance(_vector);
        if (!Set(value, "x", x) || !Set(value, "y", y) || !Set(value, "z", z)) return null;
        return value;
    }

    /// <summary>The three components of one vector, or false when any of them does not read.</summary>
    internal bool Components(object value, out double x, out double y, out double z)
    {
        x = y = z = 0d;
        return Read(value, "x", out x) && Read(value, "y", out y) && Read(value, "z", out z);
    }

    /// <summary>The cross product of two vectors, or null when the call is not here.</summary>
    internal object? Cross(object left, object right)
        => _cross == null ? null : _cross.Invoke(null, new[] { left, right });

    /// <summary>One rotation from its four components, or null when the type is not here.</summary>
    internal object? Quaternion(double x, double y, double z, double w)
    {
        if (_quaternion == null) return null;
        var value = Activator.CreateInstance(_quaternion);
        if (!Set(value, "x", x) || !Set(value, "y", y) || !Set(value, "z", z) || !Set(value, "w", w)) return null;
        return value;
    }

    /// <summary>The three components and the fourth of one rotation, for the build where the scalar is named
    /// `w` and for the one where it is named `m_W`.</summary>
    internal bool QuaternionComponents(object value, out double x, out double y, out double z, out double w)
    {
        x = y = z = w = 0d;
        return Read(value, "x", out x) && Read(value, "y", out y) && Read(value, "z", out z)
            && (Read(value, "w", out w) || Read(value, "m_W", out w));
    }

    private static bool Set(object? value, string member, double number)
    {
        if (value == null) return false;
        var type = value.GetType();
        var field = type.GetField(member, BindingFlags.Public | BindingFlags.Instance);
        if (field != null)
        {
            if (!Convertible(field.FieldType, number, out var stored)) return false;
            field.SetValue(value, stored);
            return true;
        }
        var property = type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance);
        if (property == null || !property.CanWrite) return false;
        if (!Convertible(property.PropertyType, number, out var assigned)) return false;
        property.SetValue(value, assigned);
        return true;
    }

    private static bool Read(object? value, string member, out double number)
    {
        number = 0d;
        if (value == null) return false;
        var type = value.GetType();
        if (type.GetField(member, BindingFlags.Public | BindingFlags.Instance) is { } field)
            return Number(field.GetValue(value), out number);
        if (type.GetProperty(member, BindingFlags.Public | BindingFlags.Instance) is { CanRead: true } property)
            return Number(property.GetValue(value), out number);
        return false;
    }

    private static bool Convertible(Type target, double number, out object? stored)
    {
        stored = null;
        try
        {
            stored = Convert.ChangeType(number, target, CultureInfo.InvariantCulture);
            return true;
        }
        catch (Exception) { return false; }
    }

    private static bool Number(object? value, out double number)
    {
        number = 0d;
        if (value == null) return false;
        try
        {
            number = Convert.ToDouble(value, CultureInfo.InvariantCulture);
            return double.IsFinite(number);
        }
        catch (Exception) { return false; }
    }

    /// <summary>One type of the engine, found by its own name. The search walks the assemblies the process has
    /// already loaded rather than loading anything: a machine with no Unity has none of them, which is exactly the
    /// answer a game-free build should give.</summary>
    private static Type? Find(string name)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            var candidate = assembly.GetType(name, throwOnError: false);
            if (candidate != null) return candidate;
        }
        return null;
    }

    private static MethodInfo? FindMethod(Type owner, string name, int parameters)
    {
        foreach (var candidate in owner.GetMethods(BindingFlags.Public | BindingFlags.Static))
            if (string.Equals(candidate.Name, name, StringComparison.Ordinal)
                && candidate.GetParameters().Length == parameters) return candidate;
        return null;
    }
}
