using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The module's own handle on the wave fact family: the observation state exists exactly while the
/// receiver does, and the native hooks reach it through the module they already guard on. It is created on the
/// first wave callback — the simulation thread — and held for the lifetime of the receiver, so its thread check
/// is the module's own thread. The declaration matches `EnemyModule.cs`'s own; a partial's accessibility is part
/// of the type, so the two move together.</summary>
internal sealed partial class EnemyModule
{
    private EnemyWaveFacts? _waveFacts;
    private EnemyWaveFacts WaveFacts => _waveFacts ??=
        new EnemyWaveFacts(_kernel, _registration, () => CanObserveFacts, _report, ReferenceOf);

    /// <summary>The member's current life, by the identity rule the rest of this module uses: the wrapper must be
    /// the tracked instance and the reference must still resolve.</summary>
    private EntityReference? ReferenceOf(EnemyAgent? agent)
    {
        if (agent == null || !_entities.TryGetValue(agent.GlobalID, out var entry)
            || !ReferenceEquals(entry.Enemy, agent) || entry.EnemyPointer != agent.Pointer) return null;
        return Resolve(entry.Reference) == entry ? entry.Reference : null;
    }

    internal EnemyWaveFacts.WaveSpawnObservation? BeforeWaveSpawn(SurvivalWave wave) => WaveFacts.BeforeWaveSpawn(wave);
    internal void AfterWaveSpawn(SurvivalWave wave, EnemyWaveFacts.WaveSpawnObservation? before)
        => WaveFacts.AfterWaveSpawn(wave, before);
    internal void BeforeWaveGroupStep(SurvivalWave wave) => WaveFacts.BeforeWaveGroupStep(wave);
    internal void AfterWaveGroup(SurvivalWave wave, EnemyGroup group) => WaveFacts.AfterWaveGroup(wave, group);
    internal void AfterWaveGroupStep(SurvivalWave wave) => WaveFacts.AfterWaveGroupStep(wave);
    internal void AfterWaveEndTest(SurvivalWave wave, bool ended) => WaveFacts.AfterWaveEndTest(wave, ended);
    internal void AfterGroupMaintenance() => WaveFacts.AfterGroupMaintenance();
    internal void BeforeWaveDespawn(SurvivalWave wave) => WaveFacts.BeforeWaveDespawn(wave);
}
