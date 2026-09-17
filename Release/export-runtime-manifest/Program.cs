using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using ForgeTrigger;
using ForgeWeapon;

// Writes the same runtime-manifest.json the host writes during its first fixed update, without starting the
// game: the Runtime's own contract modules register first, then every domain package Release/release.json
// declares with audience=player. A package this tool cannot build a module for stops the export — the release
// set is either complete or it is not published, and no manifest row is ever invented for a missing package.
if (args.Length != 4 || args[0] != "--release" || args[2] != "--output")
{
    Console.Error.WriteLine("usage: export-runtime-manifest --release <Release/release.json> --output <runtime-manifest.json>");
    return 2;
}
try
{
    return Export(Path.GetFullPath(args[1]), Path.GetFullPath(args[3]));
}
catch (Exception error)
{
    Console.Error.WriteLine("export-runtime-manifest: " + (error is RuntimeContractException contract
        ? contract.Code + ": " + contract.Message : error.Message));
    Console.Error.WriteLine(error.StackTrace);
    return 1;
}

static int Export(string releasePath, string outputPath)
{
    using var document = JsonDocument.Parse(File.ReadAllBytes(releasePath));
    var players = new List<Package>();
    foreach (var row in document.RootElement.GetProperty("packages").EnumerateArray())
    {
        if (row.GetProperty("audience").GetString() != "player") continue;
        var providerId = row.GetProperty("providerId");
        players.Add(new Package(row.GetProperty("packageName").GetString()!,
            providerId.ValueKind == JsonValueKind.Null ? null : providerId.GetString(),
            row.GetProperty("providerIds").EnumerateArray().Select(item => item.GetString()!).ToArray(),
            row.GetProperty("version").GetString()!));
    }
    var host = players.SingleOrDefault(package => package.ProviderId == HostIdentity.ProviderId)
        ?? throw new InvalidDataException("release.json declares no player package with provider id " + HostIdentity.ProviderId + ".");
    string gameBuild = ReadGameBuild(releasePath);
    var kernel = new RuntimeKernel(new RuntimeIdentity(host.ProviderId!, host.Version, RuntimeKernel.ApiVersion, gameBuild));
    // The host reaches these three through the SDK-internal RegisterBuiltinModule, which differs from this call
    // only in the log level it hands over; no manifest row depends on that level. The Trigger contract registers
    // before the packages below, because their bindings name the capability rows it owns.
    kernel.RegisterModule(CombatContracts.Module(), RuntimeLogLevel.Off);
    kernel.RegisterModule(ControlContracts.Module(), RuntimeLogLevel.Off);
    kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
    // The kernel's own read of any entity a plan holds; like the two contracts above it is registered before the
    // packages, so a manifest row of it is present for a plan that pins it.
    kernel.RegisterModule(ObservationContracts.Module(), RuntimeLogLevel.Off);
    RuntimeModule? map = null;    foreach (var package in players)
    {
        if (package.ProviderId == host.ProviderId) continue;
        switch (package.ProviderId)
        {
            case ForgeTrigger.ModuleDefinition.ProviderId:
                kernel.RegisterModule(ForgeTrigger.ModuleDefinition.Create(), RuntimeLogLevel.Off); break;
            case ForgeMap.ModuleDefinition.ProviderId:
                map = MapModule();
                kernel.RegisterModule(map, RuntimeLogLevel.Off); break;
            case ForgeWeapon.ModuleDefinition.ProviderId:
                // Every action row this package declares is carried here with the refusing stand-in the release
                // process can honestly offer: the row, its shape and the handler name are the declaration's, and
                // only the bodies belong to the game-bound assembly. The inventory pair is give and consume —
                // `drop` declares no row at all (ruling 110.5) — and the three override rows travel as the one
                // handler table the contract builds.
                kernel.RegisterModule(ForgeWeapon.ModuleDefinition.Create(
                    ammoAdd: ExportOnlyHandler(ForgeWeapon.WeaponSupplyContract.AmmoAddHandler),
                    ammoConsume: ExportOnlyHandler(ForgeWeapon.WeaponSupplyContract.AmmoConsumeHandler),
                    overrides: ForgeWeapon.WeaponOverrideContract.Handlers(
                        ExportOnlyHandler(ForgeWeapon.WeaponOverrideContract.FireRateHandler),
                        ExportOnlyHandler(ForgeWeapon.WeaponOverrideContract.SpreadHandler),
                        ExportOnlyHandler(ForgeWeapon.WeaponOverrideContract.RecoilHandler),
                        ExportOnlyHandler(ForgeWeapon.WeaponOverrideContract.PropertyHandler)),
                    inventoryGive: ExportOnlyHandler(ForgeWeapon.InventoryActionContract.GiveHandler),
                    inventoryConsume: ExportOnlyHandler(ForgeWeapon.InventoryActionContract.ConsumeHandler)),
                    RuntimeLogLevel.Off);
                // The holder tier is a second provider inside this same package, so the running registry carries
                // it and the manifest has to as well. Its session resolver and its two bodies belong to the
                // game-bound assembly; the owner tier's resolver is answered per capability and this process
                // dispatches nothing, so the declaration-only shape is what is registered here.
                kernel.RegisterModule(ForgeWeapon.WeaponHolderActionsContract.Module(
                    holders: null,
                    reload: ExportOnlyHandler(ForgeWeapon.WeaponHolderActionsContract.ReloadHandler),
                    clipSet: ExportOnlyHandler(ForgeWeapon.WeaponHolderActionsContract.ClipSetHandler),
                    autoFire: ExportOnlyHandler(ForgeWeapon.WeaponHolderActionsContract.AutoFireHandler)),
                    RuntimeLogLevel.Off);
                break;
            case EnemyRegistration.ProviderId:
                // The provider's rows are registered from their own declaration, never from the game-bound module:
                // every body travels as the stand-in `EnemyDeclaration` hands out, and no row is invented here.
                kernel.RegisterModule(EnemyDeclaration.Module(kernel), RuntimeLogLevel.Off); break;
            default:
                throw new InvalidDataException(package.Name + " declares provider id " + (package.ProviderId ?? "<null>")
                    + ", which this tool has no module for; the release set cannot be exported without it.");
        }
    }
    string manifest = kernel.ExportManifest();
    VerifyProviders(manifest, players);
    // Same writer as FrameworkFiles.WriteManifest: UTF-8 without BOM, no added newline.
    File.WriteAllText(outputPath, manifest, new UTF8Encoding(false));
    Console.WriteLine("Wrote " + outputPath + ": " + players.Count + " player packages, runtime " + host.Version
        + " on game build " + gameBuild + ".");
    return 0;
}

/// <summary>The one Map declaration this tool registers: the game-independent registration the game-side session
/// builds as well, so the manifest is the running registry's own and not a second description of it. Every body
/// belongs to the game-bound assembly and no row is invented here: the rows, the capability rows, the support
/// lines, the shape table and the names that need a body are the shared declaration's own, and each body is
/// replaced by the refusing stand-in below.</summary>
static RuntimeModule MapModule() => ForgeMap.ModuleRegistration.Create(
    ForgeMap.ModuleRegistration.CommandHandlers
        .ToDictionary(handler => handler, ExportOnlyHandler, StringComparer.Ordinal),
    ForgeMap.ModuleRegistration.EvaluatorHandlers
        .ToDictionary(handler => handler, ExportOnlyEvaluator, StringComparer.Ordinal));

/// <summary>The stand-in an evaluator whose reading belongs to a game-bound assembly is registered with: the row
/// is exported, and this process answers no query at all, so the only honest body is one that refuses.</summary>
static EvaluatorHandler ExportOnlyEvaluator(string handler) => _ =>
    throw new InvalidOperationException("The export process does not evaluate the " + handler + " row.");

/// <summary>The stand-in a handler whose body lives in a game-bound assembly is registered with here. The manifest
/// is a declaration and nothing in this process dispatches a command, so the row must still be exported — but the
/// body cannot be compiled without the game, and an invented one would be a second implementation. This one
/// refuses the moment it would run, which is the only honest answer available at release time.</summary>
static CommandHandler ExportOnlyHandler(string handler) => _ =>
    throw new InvalidOperationException("The export process does not execute the " + handler + " handler.");

/// <summary>The manifest's providers must be exactly the release set's provider ids plus the host identity: a
/// package that silently registered nothing, or a provider nobody declares, is a wrong release set, not a warning.</summary>
static void VerifyProviders(string manifest, List<Package> players)
{
    using var document = JsonDocument.Parse(manifest);
    var actual = document.RootElement.GetProperty("registry").GetProperty("providers").EnumerateArray()
        .Select(row => row.GetProperty("id").GetString()!)
        .Append(document.RootElement.GetProperty("runtime").GetProperty("id").GetString()!)
        .ToHashSet(StringComparer.Ordinal);
    var declared = players.SelectMany(package => package.ProviderIds).ToHashSet(StringComparer.Ordinal);
    var missing = declared.Except(actual, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    var extra = actual.Except(declared, StringComparer.Ordinal).OrderBy(id => id, StringComparer.Ordinal).ToArray();
    if (missing.Length != 0 || extra.Length != 0)
        throw new InvalidDataException("The exported manifest does not match the release set: missing ["
            + string.Join(", ", missing) + "], undeclared [" + string.Join(", ", extra) + "].");
}

/// <summary>The game build is host identity and the host assembly is its only source: read that one constant
/// instead of repeating the build number here, so a build bump cannot leave a stale copy in this tool.</summary>
static string ReadGameBuild(string releasePath)
{
    string root = Path.GetDirectoryName(Path.GetDirectoryName(releasePath))
        ?? throw new InvalidDataException("release.json must live in <repository>/Release/.");
    string path = Path.Combine(root, "ForgeRuntime", "GameBindings", "GameRuntimeBridge.cs");
    if (!File.Exists(path)) throw new FileNotFoundException("No host source to read the game build from: " + path);
    var match = Regex.Match(File.ReadAllText(path), "GameBuild\\s*=\\s*\"([^\"]+)\"");
    return match.Success ? match.Groups[1].Value : throw new InvalidDataException("No GameBuild constant in " + path);
}

/// <summary>The host identity id: the kernel is constructed with it and the ForgeRuntime package declares it as
/// its provider id in release.json, so it is the one id that is not a module provider.</summary>
internal static class HostIdentity
{
    internal const string ProviderId = "forge.runtime";
}

internal sealed record Package(string Name, string? ProviderId, string[] ProviderIds, string Version);
