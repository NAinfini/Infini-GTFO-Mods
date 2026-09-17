using ForgeMap;

namespace ForgeMap.Tests.WorldEventFacts;

/// <summary>
/// The one member of the provider declaration the world-event contract names: the provider id every binding id is
/// built from. The declaration itself is the game-independent module's own file, which belongs to the assembly
/// that registers the provider rather than to this focused test — and the contract carries its own copy of the id
/// so this project compiles without it. The case below asserts the two agree, because two spellings of one
/// provider id would be a registration failure rather than a test failure.
/// </summary>
public static class ModuleDefinition
{
    public const string ProviderId = "forge.module.gtfo.map";
    public const string Version = "1.0.0";
}
