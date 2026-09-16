using System;
using Globals;

namespace ForgeMap.Native;

/// <summary>
/// The one place a level's own identity is read off the game. Both values the `level` matcher compares come
/// from the running expedition rather than from a name: `Global.RundownIdToLoad` is the rundown block the
/// process loaded, and `RundownManager.GetActiveExpeditionData()` carries the tier and the zero-based index of
/// the level inside it. The string key the game builds for the same expedition (`Local_31_TierA_0`) is written
/// to the log as a second reading of the same fact, never used for the comparison: it carries a transport
/// prefix this side would have to predict instead of read.
///
/// A read makes no claims and throws nothing. A process between levels, an unloaded rundown, a member that does
/// not read and a tier this vocabulary cannot spell all answer null, which the matcher reports once and treats
/// as "no level matches here" — never as "every level matches".
/// </summary>
internal static class LevelIdentity
{
    /// <summary>The active expedition's identity, or null when this process cannot name one. The one line it
    /// writes names the identity the matcher compares against and the game's own key for the same expedition,
    /// so a level whose behaviours never fired can be told from a level that never read its identity. The reader
    /// is asked at most once per world, so this is one line per level.</summary>
    internal static MapLevelReference? Read(Action<string> log)
    {
        try
        {
            var active = RundownManager.GetActiveExpeditionData();
            var key = RundownManager.ActiveExpeditionUniqueKey ?? "";
            var level = MapLevelReference.FromNative(Global.RundownIdToLoad, (int)active.tier, active.expeditionIndex);
            log("map.level-identity " + (level is { } identity ? identity.ToString() : "<unreadable>") + " activeKey=" + key);
            return level;
        }
        catch (Exception error)
        {
            log("map.level-identity <unreadable: " + error.GetType().Name + ">");
            return null;
        }
    }
}
