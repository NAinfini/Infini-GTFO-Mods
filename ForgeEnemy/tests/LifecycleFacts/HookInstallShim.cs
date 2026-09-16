using System;
using System.Collections.Generic;
using HarmonyLib;

namespace ForgeEnemy.Native;

/// <summary>The install table this suite states: the one hook family it compiles.
///
/// The package's own table composes every family the build declares, and the families it leaves out — the damage
/// window, the node list and the wave triggers — are not part of this suite's statement; `NativePlugin` states the
/// real table against the whole package. The two classes below are the production adapters, which the package's
/// own table lists inside `EnemyNativeHooks.Types`. `Plugin.Load` is never called here, so this table is only what
/// the entry point's install call compiles against.</summary>
internal static class EnemyHookInstall
{
    internal static readonly IReadOnlyList<Type>[] Families =
    {
        Array.AsReadOnly(new[] { typeof(EnemyDeathStarted), typeof(EnemyLimbBroken) })
    };

    internal static IEnumerable<Type> Declared
    {
        get { foreach (var family in Families) foreach (var type in family) yield return type; }
    }

    internal static void Install(Harmony harmony)
    {
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        foreach (var type in Declared) harmony.CreateClassProcessor(type).Patch();
    }
}
