using System;

namespace ForgeEnemy.Receivers;

/// <summary>Native-facing state is accessed only on its creating simulation thread.</summary>
internal abstract class EnemyThreadBoundary
{
    private readonly int _thread = Environment.CurrentManagedThreadId;

    protected void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _thread)
            throw new InvalidOperationException("gtfo.enemy.wrong_thread");
    }
}
