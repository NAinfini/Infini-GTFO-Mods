using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The second declaration file of the Map-object provider: the identity namespace a map object is
/// registered under, declared apart from the provider half so a game-independent contract can name the namespace
/// without pulling the native side, the player identity module and the level reader in with it.
///
/// `MapObjectContract` needs this value to build the `gtfo.map_object.read` permission every map-object binding
/// declares, and a contract file that referenced the module for it would make the contract un-compilable on its
/// own — which is exactly what happened while this file did not exist. The value is one constant and it is
/// declared once, which is the whole point of the split.</summary>
public sealed partial class MapObjectModule
{
    /// <summary>The one entity namespace every map object — a door, a terminal, a scanned object — is
    /// registered under.</summary>
    public const string EntityKind = "gtfo.map_object";
}
