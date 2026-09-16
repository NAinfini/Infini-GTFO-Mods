// Stand-ins for the wave members the fact observer reads, following build 20403457's interop member kinds:
// `Mastermind.MastermindEvent.EventID` is the ushort identity every hook reads (dump.cs
// Mastermind/MastermindEvent.EventID), `SurvivalWave` derives from it and carries the native pointer the
// observer compares, `EnemyGroup.Members` is the native `List<EnemyAgent>` a batch harvests, and
// `Mastermind.m_activeGroups` is the list the cleared check reads after the Mastermind's own maintenance.
// Behaviour is synthetic and NOT game-verified.
namespace Enemies
{
    /// <summary>The enemy a wave's group spawned. `GlobalID` is the game's own per-life key and `Pointer` is the
    /// native instance identity the observer compares instead of a wrapper reference.</summary>
    public sealed class EnemyAgent
    {
        public ushort GlobalID;
        public IntPtr Pointer { get; set; }
    }

    /// <summary>One spawned group. `Members` is the native member list; it is settable so a case can say what the
    /// game had produced at the moment the batch closed.</summary>
    public sealed class EnemyGroup
    {
        public IntPtr Pointer { get; set; }
        public List<EnemyAgent>? Members { get; set; } = new();
    }
}

/// <summary>The Mastermind singleton and the event base every wave is. Both are in the global namespace in the
/// game's own assembly, and `MastermindEvent` is nested inside `Mastermind`.</summary>
public class Mastermind
{
    public class MastermindEvent
    {
        public ushort EventID { get; set; }
    }

    public static Mastermind? Current { get; set; }
    public List<Enemies.EnemyGroup>? m_activeGroups { get; set; } = new();
}

/// <summary>A survival wave: the event base plus the native pointer the observer keys its own table with.</summary>
public sealed class SurvivalWave : Mastermind.MastermindEvent
{
    public IntPtr Pointer { get; set; }
}
