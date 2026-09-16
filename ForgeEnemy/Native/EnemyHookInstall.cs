using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ForgeEnemy.Native;

/// <summary>The one table of Harmony classes this package installs. Each hook family declares its own `Types`
/// array next to the hooks themselves; this file is where those declarations become the install list, so a family
/// that is declared but never installed cannot happen by omission — the install loop only ever sees this table,
/// and `ForgeEnemy/tests/HookInstall` pins that every `[HarmonyPatch]` class in the built assembly is in it.
///
/// The order is the order the families were written in and is not otherwise meaningful: Harmony classes patch
/// independently of one another.</summary>
internal static class EnemyHookInstall
{
    /// <summary>Every declared hook family, in install order.</summary>
    internal static readonly IReadOnlyList<Type>[] Families =
    {
        EnemyNativeHooks.Types, EnemyNodeHooks.Types, EnemyWaveHooks.Types
    };

    /// <summary>Every class the package installs, flattened in the same order.</summary>
    internal static IEnumerable<Type> Declared
    {
        get { foreach (var family in Families) foreach (var type in family) yield return type; }
    }

    /// <summary>Installs every declared hook class through one Harmony instance. Called once, from the plugin's
    /// load path; nothing else in the package patches.</summary>
    internal static void Install(Harmony harmony)
    {
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        foreach (var type in Declared) harmony.CreateClassProcessor(type).Patch();
    }
}
