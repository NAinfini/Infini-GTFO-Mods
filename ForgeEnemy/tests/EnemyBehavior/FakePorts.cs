using System;
using System.Collections.Generic;
using System.Linq;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>The suite's own bridge over `IBehaviorPorts`: it answers exactly what a case set and records exactly
/// what the decisions asked for. Because the rules under test name no game type — the bridge is the only thing
/// that would — this suite compiles the production rule sources and no interop assembly at all, and the answers a
/// case gives are the answers the real bridge would return from the game.
///
/// `Pointer` is an arbitrary marker here; the production code only ever compares it for identity, and the suite
/// checks that the marker the trigger reached is the marker the ledger kept.</summary>
internal sealed class FakePorts : IBehaviorPorts
{
    internal sealed class Enemy
    {
        internal string Key = "";
        internal bool Alive = true;
        internal bool CanTrigger = true;
        internal readonly Dictionary<byte, int> Index = new();
        internal readonly Dictionary<byte, IntPtr> Component = new();
        internal readonly HashSet<byte> Done = new();
        internal bool Submitted;
        internal bool Triggered = true;
        internal int UseAbilityCalls;
        internal byte LastAbility;
        internal int LastIndex = -1;
        /// <summary>Set to make the submission fail before it reaches the game, which is a refusal rather than an
        /// unknown: the attempt was never made.</summary>
        internal bool SubmissionUnavailable;
        internal bool ComponentGone;
        internal Exception? ThrowOnUse;
    }

    internal readonly Dictionary<string, Enemy> Enemies = new(StringComparer.Ordinal);
    internal bool CanExecuteAll = true;
    internal (double X, double Y, double Z) NoisePosition;
    internal double NoiseRadius;
    internal int NoiseCalls;
    internal bool NoiseNodeFound = true;
    internal Exception? ThrowOnNoise;

    /// <summary>One ability end the decisions reported, with the machine's own answer for the dropped row and the
    /// reason the end path named. This is the ledger half of the interruption: whether the row is published at all
    /// is the module's decision, which this suite does not compile.</summary>
    internal sealed record DroppedAbility(string Key, byte Ability, bool Finished, string Reason);

    internal readonly List<DroppedAbility> Dropped = new();

    internal Enemy Track(string key, params byte[] abilities)
    {
        var enemy = new Enemy { Key = key };
        foreach (var ability in abilities) Register(enemy, ability, abilities.ToList().IndexOf(ability));
        Enemies[key] = enemy;
        return enemy;
    }

    internal static void Register(Enemy enemy, byte ability, int index, long component = 4242)
    {
        enemy.Index[ability] = index;
        enemy.Component[ability] = new IntPtr(component);
    }

    public bool CanExecute => CanExecuteAll;

    public bool IsCurrent(string key) => Enemies.TryGetValue(key, out var enemy) && enemy.Alive || Enemies.ContainsKey(key);

    public bool IsAlive(string key) => Enemies.TryGetValue(key, out var enemy) && enemy.Alive;

    public bool CanTrigger(string key) => Enemies.TryGetValue(key, out var enemy) && enemy.CanTrigger;

    public bool TryAbilityIndex(string key, byte ability, out int index)
    {
        index = -1;
        return Enemies.TryGetValue(key, out var enemy) && enemy.Index.TryGetValue(ability, out index);
    }

    public bool TryUseAbility(string key, byte ability, int index, out bool triggered)
    {
        triggered = false;
        if (!Enemies.TryGetValue(key, out var enemy)) return false;
        enemy.UseAbilityCalls++;
        enemy.LastAbility = ability;
        enemy.LastIndex = index;
        if (enemy.ThrowOnUse != null) throw enemy.ThrowOnUse;
        if (enemy.SubmissionUnavailable) return false;
        enemy.Submitted = true;
        triggered = enemy.Triggered;
        return true;
    }

    public bool TryAbilityState(string key, byte ability, out IntPtr component, out bool done)
    {
        component = IntPtr.Zero;
        done = false;
        if (!Enemies.TryGetValue(key, out var enemy) || enemy.ComponentGone) return false;
        if (!enemy.Component.TryGetValue(ability, out component)) return false;
        done = enemy.Done.Contains(ability);
        return true;
    }

    public void AbilityDropped(RunningBehavior dropped, bool finished, string reason)
        => Dropped.Add(new DroppedAbility(dropped.EnemyKey, dropped.Ability, finished, reason));

    public bool TryEmitNoise(string key, (double X, double Y, double Z) position, double radius)
    {
        if (!Enemies.TryGetValue(key, out _)) return false;
        NoiseCalls++;
        if (ThrowOnNoise != null) throw ThrowOnNoise;
        if (!NoiseNodeFound) return false;
        NoisePosition = position;
        NoiseRadius = radius;
        return true;
    }
}
