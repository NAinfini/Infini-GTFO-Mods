using System;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;

namespace ForgeWeapon.Native;

/// <summary>Where a loadout policy's pinned bytes come from in a real install: the game assembly the game itself was
/// started from, the loader's own per-guid plugin locations, and the files under the BepInEx root. This is the one
/// place that knows those three, which is what keeps <see cref="LoadoutPolicyData"/> a function of a root directory
/// and an injected source — and so keeps its suite runnable with no profile and no game.</summary>
internal static class LoadoutPolicyWiring
{
    /// <summary>The production pin source. The game assembly is read from the install the loader is running in, not
    /// from a path a policy file names, and a GUID this install has not installed answers null rather than a
    /// guessed location.</summary>
    internal static LoadoutPinSource PinSource() => new(
        Path.Combine(Paths.GameRootPath, GameAssemblyFileName),
        guid => IL2CPPChainloader.Instance.Plugins.TryGetValue(guid, out var plugin) ? plugin.Location : null,
        LoadoutPolicyData.ReadOrNull);

    /// <summary>The game's own assembly file name, relative to the game root `BepInEx.Paths.GameRootPath` names.</summary>
    private const string GameAssemblyFileName = "GameAssembly.dll";
}
