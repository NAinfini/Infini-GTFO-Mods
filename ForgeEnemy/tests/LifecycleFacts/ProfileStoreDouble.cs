namespace ForgeEnemy.Native;

/// <summary>Stands in for the enemy profile store (see the csproj note) so the plugin entry point compiles here.
/// The real discovery reads the filesystem and the game assembly; this suite's statement is the session lifetime,
/// so it needs the one member <c>Plugin.Load</c> calls and nothing about what a document may say. The reader
/// itself is exercised by the EnemyProfile suite, which compiles the production source.</summary>
internal sealed class EnemyProfileStore
{
    internal static EnemyProfileStore Discover(string bepInExRoot) => new();
}
