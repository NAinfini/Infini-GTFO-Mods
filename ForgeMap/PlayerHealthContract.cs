using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The one canonical combat row this provider implements for `gtfo.player`: `forge.action.combat.heal`
/// is declared and owned by `forge.contract.combat`, and this package binds it to its own handler exactly as the
/// enemy package binds the same capability to its own. The capability is the authority for the ports, the result
/// schema and the codes; nothing here restates them, and no second capability is invented for a player.
///
/// The row lives in the game-independent assembly because the runtime module, the manifest and the website read
/// it there; the handler and the native write path are the native assembly's `PlayerHealthAction` and
/// `PlayerHealthReceiver`, which answer through this same shape. A registration only declares what it answers,
/// which is why this file is a declaration and not part of the identity table.</summary>
public static class PlayerHealthContract
{
    public const string CapabilityId = "forge.action.combat.heal";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.heal";
    public const string HandlerName = "gtfo.player.heal";

    /// <summary>The one shape of this handler, resolved at registration against the canonical capability: the
    /// recipient collection, the required source reference, the requested amount and the optional ceiling, and
    /// the one structural overheal policy. It is declared here rather than by the registration site, so the
    /// native half cannot quietly describe a different port layout.</summary>
    public static readonly HandlerShape Shape = new HandlerShape()
        .Inputs("targets", "source", "amount", "cap").Outputs("result").Parameters("overheal_policy");

    /// <summary>The one execute binding row: this provider's own id under its own namespace, the canonical
    /// capability it implements, and the handler the native half supplies. It requires no other binding, so the
    /// closure of a plan that pins it is the row itself.</summary>
    public static object Row() => new
    {
        id = BindingId,
        capabilityId = CapabilityId,
        providerId = ModuleDefinition.ProviderId,
        handler = HandlerName,
        role = "execute",
        status = "implemented",
        dependencies = System.Array.Empty<string>(),
        requires = System.Array.Empty<string>()
    };

    /// <summary>The row's registration support. This binding carries no permission: it reads and writes the
    /// health receiver of a life the provider already tracks, and it owns no object a plan would have to
    /// declare.</summary>
    public static BindingSupport Support()
        => new(BindingId, "implementation-only", System.Array.Empty<string>());
}
