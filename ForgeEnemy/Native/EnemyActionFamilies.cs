using System;
using System.Collections.Generic;
using System.Globalization;
using Enemies;
using ForgeRuntime.Framework;
using SNetwork;

namespace ForgeEnemy.Native;

/// <summary>The composition half of the action families this package carries beside the node-list family: the
/// enemy control actions, the combat actions, the foam action, the behaviour actions and the profile's
/// `phase_set`, plus the one presentation audience this provider answers for.
///
/// Each family declares its own rows, handlers, shapes and support rows next to the code that answers them. This
/// file is the one place those declarations are composed into the provider's registration, so a family cannot be
/// registered half-way — a handler whose shape is missing is refused at registration, and a binding whose row is
/// missing is a binding nobody can reach.
///
/// The foam family's handler lives on its own class rather than on this partial, so the module holds one instance
/// of it; it is built on the first foam request rather than in the constructor, because it is handed the
/// registration this module only has after `RegisterModule` returns.</summary>
internal sealed partial class EnemyModule
{
    /// <summary>The foam family's one handler and the ledger that honours a Forge-side foam lifetime.</summary>
    private GlueActions? _glue;
    private GlueActions Glue => _glue ??= new GlueActions(_registration, GlueTargetOf, () => CanExecute, _kernel);

    /// <summary>What a foam request resolves a reference to: the module's own tracked life, so a reference no
    /// live enemy answers for is refused instead of being written through a stale pointer.</summary>
    private GlueTarget? GlueTargetOf(EntityReference reference)
    {
        var entry = Resolve(reference);
        return entry == null ? null : new GlueTarget(entry.Enemy, entry.EnemyPointer, entry.Enemy.Damage);
    }

    /// <summary>Every handler the four action families register, in the order their rows are appended.</summary>
    internal Dictionary<string, CommandHandler> ActionFamilyHandlers()
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        // The control family's handler names are its declaration's, in the game-independent assembly; this half
        // supplies the one body per name it declares.
        handlers[EnemyControlContract.AwakenHandler] = Awaken;
        handlers[EnemyControlContract.SleepHandler] = Sleep;
        handlers[EnemyControlContract.MoveToHandler] = MoveTo;
        handlers[EnemyCombatContract.StaggerHandler] = Stagger;
        handlers[EnemyCombatContract.AttackInterruptHandler] = AttackInterrupt;
        handlers[GlueContract.FoamingHandler] = context => Glue.Foaming(context);
        // The volume family's one execute handler: the native effect volume and the fog sphere that draws it.
        handlers[VolumeHandler] = EffectVolume;
        foreach (var handler in BehaviorHandlers(this)) handlers[handler.Key] = handler.Value;
        // The profile family is one execute handler; its row and binding are declared in the root contract the
        // registration composes, so the two halves cannot drift.
        handlers[EnemyProfileContract.PhaseSetHandler] = PhaseSet;
        return handlers;
    }

    /// <summary>Every shape those handlers resolve against, keyed by the same names.</summary>
    internal static Dictionary<string, HandlerShape> ActionFamilyShapes()
    {
        var shapes = new Dictionary<string, HandlerShape>(StringComparer.Ordinal);
        foreach (var shape in EnemyControlContract.Shapes()) shapes[shape.Key] = shape.Value;
        foreach (var shape in EnemyCombatContract.Shapes()) shapes[shape.Key] = shape.Value;
        foreach (var shape in GlueContract.Shapes()) shapes[shape.Key] = shape.Value;
        foreach (var shape in EnemyBehaviorContract.Shapes()) shapes[shape.Key] = shape.Value;
        foreach (var shape in EnemyProfileContract.Shapes()) shapes[shape.Key] = shape.Value;
        shapes[VolumeHandler] = VolumePorts;
        return shapes;
    }

    /// <summary>The sessions one `presentation` step of this provider is addressed to. The package's one
    /// presentation row is `forge.action.enemy.mark`, whose audience is the whole realm (ruling 122.3): every
    /// player sees a Forge mark, so the answer is the session hub's own player list rather than a second table of
    /// players kept here.
    ///
    /// The references the step's own `recipients` port carries name the enemies a mark is placed on, not the
    /// players it is shown to, which is why they do not narrow this answer. Null — no session hub, or a hub that
    /// names nobody — is the refusal the kernel reports as `presentation-recipient`; a step nobody can be
    /// addressed with is never widened to an implicit everyone by this side.</summary>
    internal static IReadOnlyList<string>? PresentationAudience(IReadOnlyList<EntityReference>? recipients)
    {
        _ = recipients;
        try
        {
            var players = SNet.SessionHub?.PlayersInSession;
            if (players == null) return null;
            var sessions = new List<string>(players.Count);
            var seen = new HashSet<string>(StringComparer.Ordinal);
            for (var index = 0; index < players.Count; index++)
            {
                var player = players[index];
                if (player == null) continue;
                ulong key = player.Lookup;
                // The zero key is the game's own "no account" spelling; a session nobody can be addressed by is
                // not a recipient.
                if (key == 0) continue;
                var session = key.ToString(CultureInfo.InvariantCulture);
                if (seen.Add(session)) sessions.Add(session);
            }
            if (sessions.Count == 0) return null;
            sessions.Sort(StringComparer.Ordinal);
            return sessions;
        }
        catch (Exception) { return null; }
    }

    /// <summary>Drops the action families' state with the module that owns it: the foam ledger's timers and the
    /// effects it still holds are released here, and every effect volume this package registered is unregistered
    /// from the game's own manager, so a package that unloads mid-level leaves neither behind.</summary>
    internal void DisposeActionFamilies()
    {
        _glue?.Dispose();
        _glue = null;
        ReleaseVolumes();
    }
}
