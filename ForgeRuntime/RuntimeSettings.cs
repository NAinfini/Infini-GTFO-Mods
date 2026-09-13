using System;
using BepInEx.Configuration;

namespace ForgeRuntime;

/// <summary>Host-owned config keys; Development owns no plan path or host permission grants.</summary>
internal static class RuntimeSettings
{
    internal static RuntimeMode Mode { get; private set; } = RuntimeMode.Off;
    internal static ConfigEntry<string> PlanPath = null!;
    internal static ConfigEntry<string> AllowedPermissions = null!;
    internal static void Bind(ConfigFile config)
    {
        Mode = RuntimeMode.Off;
        // Bind raw text so ConfigFile cannot silently replace a malformed enum with Authoring.
        var mode = config.Bind("Runtime", "Mode", "Authoring",
            "Play = framework and game bindings without diagnostic collectors; Authoring = Play plus authoring diagnostics; Off = no components or hooks. Restart required. No mode enables a plan automatically.");
        Mode = mode.Value.Trim().ToLowerInvariant() switch
        {
            "off" or "0" => RuntimeMode.Off,
            "authoring" or "1" => RuntimeMode.Authoring,
            "play" or "2" => RuntimeMode.Play,
            _ => throw new InvalidOperationException("Unsupported Forge Runtime.Mode; use Off, Play or Authoring. Native startup was not attempted.")
        };
        PlanPath = config.Bind("Framework", "PlanPath", "", "Optional offline development plan path relative to BepInEx, maximum 4 MiB. Empty disables automatic plan loading. Restart required.");
        AllowedPermissions = config.Bind("Framework", "AllowedPermissions", "", "Explicit comma-separated grants. Example: gtfo.enemy.health.read,gtfo.enemy.health.write. The plan cannot authorize itself; empty grants nothing.");
    }
}
