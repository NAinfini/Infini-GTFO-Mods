using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The four wave facts that belong to this package (ruling: the wave facts are ForgeEnemy's, the map
/// side owns placement and scheduling data). It sits in the game-independent Enemy assembly because the runtime
/// module, the manifest and the website read it there; the native observation is <c>EnemyWaveFacts</c> in the
/// native assembly, which publishes through these row ids.
///
/// The rows themselves are `forge.trigger.*` rows, so they are declared once by the runtime's trigger contract
/// (`ForgeRuntime/Framework/TriggerContracts.cs`): `forge.trigger.objective.{wave_started,wave_spawned,
/// wave_exhausted,wave_cleared}`, with the catalog's ports. This file carries the binding half only.
///
/// One documented difference from the catalog lives in that declaration: the `wave` output — the runtime
/// wave instance — is declared optional and is never filled, because this runtime mints no provider-owned handle
/// (see <c>ForgeEnemy/evidence/enemy-wave-facts.json</c>). A required port the provider cannot fill would make
/// every publish fail the kernel's own event shape check, which is a dead row rather than an honest one; an
/// always-null placeholder is refused outright by the same ruling that allows the absent form. The wave instance
/// identity therefore travels in the event's own scope id, `gtfo.wave:&lt;worldEpoch&gt;:&lt;EventID&gt;`, exactly
/// as the attack instance does. The other ports are published with real native values.</summary>
public static class EnemyWaveContract
{
    public const string WaveStartedCapability = "forge.trigger.objective.wave_started";
    public const string WaveSpawnedCapability = "forge.trigger.objective.wave_spawned";
    public const string WaveExhaustedCapability = "forge.trigger.objective.wave_exhausted";
    public const string WaveClearedCapability = "forge.trigger.objective.wave_cleared";

    /// <summary>A binding id is this provider's own name for the row and the capability id is the canonical name
    /// it carries; the two namespaces are separate maps in the kernel registry, so the two never share a
    /// spelling.</summary>
    public const string WaveStartedBinding = ModuleDefinition.ProviderId + ".binding.wave_started";
    public const string WaveSpawnedBinding = ModuleDefinition.ProviderId + ".binding.wave_spawned";
    public const string WaveExhaustedBinding = ModuleDefinition.ProviderId + ".binding.wave_exhausted";
    public const string WaveClearedBinding = ModuleDefinition.ProviderId + ".binding.wave_cleared";

    /// <summary>The handler names the `observe` rows carry. A trigger event arrives as one whole frame, so no
    /// handler and no shape is resolved for these bindings; the names are the rows' own discriminators.</summary>
    public const string WaveStartedHandler = "gtfo.enemy.wave_started";
    public const string WaveSpawnedHandler = "gtfo.enemy.wave_spawned";
    public const string WaveExhaustedHandler = "gtfo.enemy.wave_exhausted";
    public const string WaveClearedHandler = "gtfo.enemy.wave_cleared";

    /// <summary>Every binding this family declares, in the order the registry rows list them. A native hook
    /// returns before reading the game while none of them has a plan.</summary>
    public static readonly string[] Bindings =
    {
        WaveStartedBinding, WaveSpawnedBinding, WaveExhaustedBinding, WaveClearedBinding
    };

    /// <summary>Every permission this family reads the wave side of the game through. The facts are derived from
    /// the native wave state machine, the Mastermind's own group registry and the enemy module's life table, so
    /// the read side is one permission for the whole family.</summary>
    public static IReadOnlyList<BindingSupport> Support() => Array.AsReadOnly(new[]
    {
        new BindingSupport(WaveStartedBinding, "implementation-only", new[] { "gtfo.enemy.waves.read" }),
        new BindingSupport(WaveSpawnedBinding, "implementation-only", new[] { "gtfo.enemy.waves.read" }),
        new BindingSupport(WaveExhaustedBinding, "implementation-only", new[] { "gtfo.enemy.waves.read" }),
        new BindingSupport(WaveClearedBinding, "implementation-only", new[] { "gtfo.enemy.waves.read" })
    });

    /// <summary>The four binding rows the same declaration registers, one per row above.</summary>
    public const string BindingRowsJson = """
    {
      "id": "forge.module.gtfo.enemy.binding.wave_started",
      "capabilityId": "forge.trigger.objective.wave_started",
      "providerId": "forge.module.gtfo.enemy",
      "handler": "gtfo.enemy.wave_started",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    },
    {
      "id": "forge.module.gtfo.enemy.binding.wave_spawned",
      "capabilityId": "forge.trigger.objective.wave_spawned",
      "providerId": "forge.module.gtfo.enemy",
      "handler": "gtfo.enemy.wave_spawned",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    },
    {
      "id": "forge.module.gtfo.enemy.binding.wave_exhausted",
      "capabilityId": "forge.trigger.objective.wave_exhausted",
      "providerId": "forge.module.gtfo.enemy",
      "handler": "gtfo.enemy.wave_exhausted",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    },
    {
      "id": "forge.module.gtfo.enemy.binding.wave_cleared",
      "capabilityId": "forge.trigger.objective.wave_cleared",
      "providerId": "forge.module.gtfo.enemy",
      "handler": "gtfo.enemy.wave_cleared",
      "role": "observe",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;
}
