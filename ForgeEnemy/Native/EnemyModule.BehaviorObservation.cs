using ForgeRuntime.Framework;
using ForgeEnemy.Native.Observation;

namespace ForgeEnemy.Native;

internal sealed partial class EnemyModule
{
    /// <summary>Explicit current-life AI observation for future E4 consumers; no public binding yet.</summary>
    internal EnemyBehaviorObserver.Snapshot? ObserveBehavior(EntityReference target)
    {
        CheckThread();
        if (_kernel.StartupState != RuntimeStartupState.Ready || !CanExecute) return null;
        var entry = Resolve(target);
        if (entry == null) return null;
        EnemyBehaviorObserver.Snapshot? snapshot;
        try { snapshot = EnemyBehaviorObserver.Read(entry.Enemy, target); }
        catch (Exception) { return null; }
        return snapshot?.Reference == target && CanExecute && Resolve(target) == entry ? snapshot : null;
    }
}
