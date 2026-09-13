using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace ForgeRuntime.GameBindings;

internal static class PluginPatchSelection
{
    internal static bool Includes(RuntimeMode mode, string? typeNamespace) => mode switch
    {
        RuntimeMode.Off => false,
        RuntimeMode.Play => typeNamespace == typeof(PluginPatchSelection).Namespace,
        RuntimeMode.Authoring => true,
        _ => throw new ArgumentOutOfRangeException(nameof(mode))
    };
    internal static IEnumerable<Type> Types(Assembly assembly, RuntimeMode mode)
    {
        foreach (var type in assembly.GetTypes())
            if (Includes(mode, type.Namespace) && type.IsDefined(typeof(HarmonyPatch), false)) yield return type;
    }
}
