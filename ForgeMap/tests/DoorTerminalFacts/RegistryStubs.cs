using ForgeRuntime.Framework;

// The game-independent symbols the production sources under test read, declared with the values and the types
// the package really carries. This project has to build on its own — the game-independent Map assembly and the
// native session are separate build products, and the native one pulls in the loader — so this file stands in
// for exactly the members those sources touch and nothing else. What a case asserts about them is that a change
// on the production side fails here: the provider identity is checked against `ForgeMap/manifest.json`, and the
// entity kind is checked against `MapObjectModule.EntityKind`.

namespace ForgeMap
{
    /// <summary>The provider identity the contracts' binding and handler ids are built from, with the values
    /// `ForgeMap/ModuleDefinition.cs` carries.</summary>
    public static class ModuleDefinition
    {
        public const string ProviderId = "forge.module.gtfo.map";
        public const string Version = "1.0.0";
    }

    /// <summary>The map-object kinds the address's category names are derived from, with the members and the
    /// order `ForgeMap/MapIdentityContracts.cs` declares: a category is the lower-case name of one of these, so a
    /// member renamed there changes the address a fact is published for.</summary>
    internal enum MapObjectKind { Zone, Geomorph, Area, Plug, Door, Terminal, Scan }

    /// <summary>The one map-object identity namespace, with the value `MapObjectModule.EntityKind` carries. It is
    /// a stand-in because the production `MapObjectModule` is the provider half and pulls the player identity
    /// module, the zone index and the level reader in with it; the real value is asserted against
    /// `ForgeMap/manifest.json` by `tests/MapContracts`.</summary>
    internal static class MapObjectModule
    {
        internal const string EntityKind = "gtfo.map_object";
    }

    /// <summary>The map-object contract's own members these sources read, with the values
    /// `ForgeMap/MapObjectContract.cs` declares: the terminal command row's capability id and fact kind, and the
    /// permission every map-object binding's registration row declares.</summary>
    public static class MapObjectContract
    {
        public const string TerminalCommandCapability = "forge.trigger.interaction.terminal_command";
        internal const string TerminalCommandFact = "terminal_command";
        public const string MapObjectReadPermission = "gtfo.map_object.read";
    }
}

namespace ForgeMap.Native
{
    /// <summary>The package's own plugin. Only its type is needed: the production facts half reads no member of
    /// it, because the patch classes (which are not compiled here) are the only code that reaches a session.
    /// It exists because `Plugin.Session` is the type a production event observer would name.</summary>
    public static class Plugin
    {
        public static MapPluginSession? Session => null;
    }

    /// <summary>The session the plugin publishes; only its type is needed here.</summary>
    public sealed class MapPluginSession
    {
        internal long WorldEpoch => 0;
    }
}
