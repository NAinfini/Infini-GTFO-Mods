using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The `forge.selector.target.players` declaration: the capability's identity, the binding that
/// implements it, and the port shape its handler is resolved against. It sits in the game-independent Map
/// assembly because the runtime module, the manifest and the website read it there; the world-bound evaluator
/// is the native assembly's `PlayerSelector`, which answers through this same shape.</summary>
public static class PlayerSelectorContract
{
    public const string CapabilityId = "forge.selector.target.players";
    public const string BindingId = ModuleDefinition.ProviderId + ".binding.target.players";
    public const string HandlerName = "gtfo.map.players";
    /// <summary>The one shape of this handler: the entity collection it answers with, and the two structural
    /// enums it reads by declaration index. It is declared here rather than by each registration site, so a
    /// caller that only adds the native evaluator cannot quietly describe a different port layout.</summary>
    public static readonly HandlerShape Shape = new HandlerShape().Outputs("targets").Parameters("relation", "empty");
}
