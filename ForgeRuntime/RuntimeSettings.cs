using System;
using BepInEx.Configuration;
using ForgeRuntime.Framework;

namespace ForgeRuntime;

/// <summary>Host-owned config keys. Plans are discovered under BepInEx/plugins (I-PACK D-009), not configured here.</summary>
internal static class RuntimeSettings
{
    internal static RuntimeMode Mode { get; private set; } = RuntimeMode.Off;
    internal static RuntimeLogLevel LogLevel { get; private set; } = RuntimeLogLevel.Off;
    internal static void Bind(ConfigFile config)
    {
        Mode = RuntimeMode.Off;
        LogLevel = RuntimeLogLevel.Off;
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
        // Raw text for the same reason as Mode; trace is deliberately not accepted here, only ForgeDevelopment elevation reaches it.
        var level = config.Bind("Logging", "Level", "error",
            "Forge Runtime records written to BepInEx/forge-logs/*.jsonl: off, error or info. error writes only when something goes wrong; info also writes normal records. Error and info records are mirrored to the console. Runtime.Mode Off starts no writer. Restart required.");
        LogLevel = level.Value.Trim().ToLowerInvariant() switch
        {
            "off" => RuntimeLogLevel.Off,
            "error" => RuntimeLogLevel.Error,
            "info" => RuntimeLogLevel.Info,
            _ => throw new InvalidOperationException("Unsupported Forge Logging.Level; use off, error or info. Native startup was not attempted.")
        };
    }
}
