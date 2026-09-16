using Enemies;
using ForgeRuntime.Framework;
using static Checks;

/// <summary>The kernel's `gtfo.enemy` instance resolver. A native agent is only ever named by the life this
/// module registered: an unknown agent, a replaced wrapper and a retired life all answer null, so a caller can
/// leave its port out instead of publishing a reference nothing stands behind.</summary>
internal static class InstanceResolverCases
{
    private static EntityReference? Resolve(RuntimeKernel kernel, object instance)
        => kernel.ResolveEntityInstance("gtfo.enemy", instance);
    /// <summary>Runs on a thread the module does not own; `Task.Run` may run inline on the owning thread here,
    /// so only a dedicated thread is deterministic.</summary>
    private static void OnForeignThread(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() => { try { action(); } catch (Exception error) { failure = error; } });
        thread.Start(); thread.Join();
        if (failure != null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static void Run()
    {
        Case("instance.resolves_registered_life", () =>
        {
            using var s = new Scene();
            Require(Resolve(s.Kernel, s.Enemy) == s.Ref, "A registered life was not answered by its own agent.");
            Require(Resolve(s.Kernel, s.Enemy)!.LifeEpoch == s.Ref.LifeEpoch, "The resolver changed the life epoch.");
        });
        Case("instance.unknown_agent_unresolved", () =>
        {
            using var s = new Scene();
            Require(Resolve(s.Kernel, Scene.NewEnemy(8, 20)) == null, "An agent no life registered was named.");
            Require(Resolve(s.Kernel, new object()) == null, "A non-agent instance was named.");
            // A null instance is a caller bug, not an unresolved kind: the SDK refuses it as an argument.
            bool refused = false;
            try { _ = Resolve(s.Kernel, null!); } catch (ArgumentNullException) { refused = true; }
            Require(refused, "A null instance was accepted as a lookup key.");
        });
        Case("instance.replaced_wrapper_unresolved", () =>
        {
            using var s = new Scene(); var stale = s.Enemy;
            // The same native id with a new pointer is a new life: the old wrapper must not answer for it.
            var replacement = Scene.NewEnemy(7, 40); s.Module.TrackSpawn(replacement);
            Require(Resolve(s.Kernel, stale) == null, "A replaced wrapper still answered for the enemy id.");
            Require(Resolve(s.Kernel, replacement) != null && Resolve(s.Kernel, replacement) != s.Ref,
                "The replacement life was not named by its own wrapper.");
        });
        Case("instance.retired_life_unresolved", () =>
        {
            using var s = new Scene(); s.Module.TrackDespawn(s.Enemy);
            Require(Resolve(s.Kernel, s.Enemy) == null, "A despawned life was still resolvable.");
        });
        Case("instance.kind_without_a_resolver_unresolved", () =>
        {
            using var s = new Scene();
            // Ruling 53: a kind no installed package registered is unresolved, not a contract error.
            Require(s.Kernel.ResolveEntityInstance("test.unregistered", s.Enemy) == null, "An unregistered kind was answered.");
            Code(() => s.Kernel.ResolveEntityInstance("test", s.Enemy), "entity-resolver");
        });
        Case("instance.wrong_thread_rejected", () =>
        {
            using var s = new Scene();
            Code(() => OnForeignThread(() => Resolve(s.Kernel, s.Enemy)), "wrong-thread");
        });
    }
}
