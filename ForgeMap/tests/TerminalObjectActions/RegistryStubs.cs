using ForgeRuntime.Framework;

// The game-independent and session symbols the production sources under test read, declared with the values and
// the types the package really carries. This project has to build on its own — the game-independent Map assembly
// and the native session are separate build products, and the native one pulls in the loader — so `RegistryStubs`
// stands in for exactly the members those files touch and nothing else. What a case asserts about them is that a
// change on the production side fails here: the shape fixture resolves the contract's own port shapes against the
// contract's own capability rows, and the provider identity is checked against `ForgeMap/manifest.json`.

namespace ForgeMap
{
    /// <summary>The provider identity the contract's binding and handler ids are built from, with the values
    /// `ForgeMap/ModuleDefinition.cs` carries.</summary>
    public static class ModuleDefinition
    {
        public const string ProviderId = "forge.module.gtfo.map";
        public const string Version = "0.1.0";
    }

    /// <summary>The map-object kinds the address's category names are derived from, with the members and the
    /// order `ForgeMap/MapIdentityContracts.cs` declares. A category is the lower-case name of one of these, so a
    /// member renamed there changes the address the action is asked for.</summary>
    internal enum MapObjectKind { Zone, Geomorph, Area, Plug, Door, Terminal, Scan }
}

namespace ForgeMap.Native
{
    /// <summary>The package's own plugin, of which the production address readers read one member: the session
    /// whose world epoch keys the zone table. `Session` is null here, which is the state the table reads as "no
    /// world key": each case stands its own level up and the fixture drops the table between cases.
    ///
    /// It is a stand-in and not the production `Plugin`, because that type is the loader's entry point and pulls
    /// the whole session in. `ForgeMap/tests/MapNativeAdapter` compiles the real one and owns the loader path;
    /// what matters here is the one member and its type, both of which this declaration keeps.</summary>
    public static class Plugin
    {
        public static MapPluginSession? Session => null;
    }

    /// <summary>The session the plugin publishes. Only its type is needed: the production readers reach the
    /// world key through it, and a null session means no world key, which is this fixture's state.</summary>
    public sealed class MapPluginSession
    {
        internal long WorldEpoch => 0;
    }
}
