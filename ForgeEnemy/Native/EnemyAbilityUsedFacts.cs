using System;
using System.Collections.Generic;
using Agents;
using Enemies;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The module-side half of the ability-used observation: the fact `forge.trigger.enemy.ability_used`
/// publishes, read from the game's own attack-start datum. The Harmony class that calls into it is in
/// `EnemyNodeHooks.cs`; nothing here writes the world.
///
/// The datum is `Enemies.pES_EnemyAttackData`, the body `ES_EnemyAttackBase` hands to every peer when an enemy
/// begins an attack ability. It carries the ability kind and index, the agent the attack was aimed at and the
/// length the state was told to run for, which is exactly the row's own four payload ports; the enemy the fact is
/// about comes from the state that sent it, because the datum itself names only the target.
///
/// Three readings are refused rather than reported: an enemy this module does not track (a stale pointer or a
/// life that is not the one the reference names), an ability kind outside the table this provider owns as
/// resources, and a target handle whose agent is already gone. In each case nothing is published — a fact that
/// names an entity the kernel cannot resolve would be a different fact, and the capability's own payload check
/// would refuse it anyway.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The binding this observation publishes through; the capability row is declared in
    /// `EnemyAbilityUsedContract`, which is also where the ports below are spelled.</summary>
    internal const string AbilityUsedBinding = EnemyAbilityUsedContract.BindingId;

    /// <summary>Native callback entry (the `ES_EnemyAttackBase.RecieveAttackStart` postfix): one attack the game
    /// itself has already decided is happening. The enemy is handed in by the hook rather than read here, because
    /// the state's own agent field is not part of the interop surface this package compiles against.</summary>
    internal void AfterAbilityUsed(EnemyAgent? enemy, pES_EnemyAttackData attack)
    {
        CheckThread();
        if (enemy == null || !CanPublishNodeFacts(AbilityUsedBinding)) return;
        // The reference must be the one this module minted for this life: a pointer that changed under the state,
        // or a reference that no longer resolves, is not a fact about a live enemy.
        if (!_entities.TryGetValue(enemy.GlobalID, out var entry)
            || entry.EnemyPointer != enemy.Pointer || Resolve(entry.Reference) == null) return;
        // The kind is a resource this provider owns, or the fact is not one this provider can name.
        if (!EnemyAbilityResources.TryReference((byte)attack.AbilityType, out var ability)) return;
        var ports = new Dictionary<string, object>(StringComparer.Ordinal)
        {
            ["enemy"] = entry.Reference,
            ["ability"] = ability
        };
        // An attack aimed at a position instead of an agent, and a target handle whose agent was torn down, both
        // leave the port absent rather than naming something that is not there — the port is optional, so the
        // framework reads its absence as "no target" instead of refusing the fact.
        try
        {
            if (attack.TargetAgent.TryGet(out Agent? target) && target != null && ResolveAgent(target) is { } resolved)
                ports["target"] = resolved;
        }
        catch (Exception) { }
        // The length is published in the clock's own unit, converted from the datum's seconds; the port is
        // optional, so a datum whose own length is not a finite number publishes none at all rather than a zero
        // no plan could tell from a real length.
        if (float.IsFinite(attack.Duration)) ports["duration"] = Ticks(attack.Duration);
        PublishNodeFact(AbilityUsedBinding, entry.Reference, ports);
    }
}
