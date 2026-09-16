using System;
using Enemies;

namespace ForgeEnemy.Native.Observation;

/// <summary>
/// The official enemy type an agent was built from: the <c>EnemyDataBlock.persistentID</c> a plan's
/// <c>enemy-type</c> mount names (interop `GameData.EnemyDataBlock Enemies.EnemyAgent::get_EnemyData()` plus
/// `System.UInt32 GameData.GameDataBlockBase`1&lt;GameData.EnemyDataBlock&gt;::get_persistentID()`, both frozen in
/// evidence/native-api-20403457.json). The read is explicit-instance and read-only: the agent is judged first,
/// its block is read once and read back, and an agent whose block cannot be read at all has no type here — an
/// unreadable type never matches a mount.
/// </summary>
internal static class EnemyTypeReader
{
    internal static uint? Read(EnemyAgent enemy)
    {
        try
        {
            if (enemy == null || enemy.Pointer == IntPtr.Zero || !enemy.IsSetup) return null;
            var block = enemy.EnemyData;
            if (block == null) return null;
            uint id = block.persistentID;
            // The block getter is native: a block replaced while it was being read is not a reading of either block.
            return ReferenceEquals(enemy.EnemyData, block) ? id : null;
        }
        catch (Exception)
        {
            // A match question is asked from the game's own dispatch, so a native getter that throws answers
            // "no readable type" instead of escaping into the caller.
            return null;
        }
    }
}
