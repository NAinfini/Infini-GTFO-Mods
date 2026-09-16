using System;

namespace ForgeEnemy.Native;

/// <summary>Stands in for the game-load spawn requirement source (see the csproj note) so the plugin
/// entry point compiles here. Attach is one-shot, exactly like the real subscription.</summary>
internal static class EnemySpawnRequirementSource
{
    private static bool _attached;

    internal static void Attach(Action<string> report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (_attached) throw new InvalidOperationException("The spawn requirement source attaches once.");
        _attached = true;
    }
}
