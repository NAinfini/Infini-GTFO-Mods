using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.PlacementFacts;

/// <summary>
/// One case's world for the placement family: a real kernel carrying the framework's own trigger contract — which
/// is where the `equipment_kind` set lives, and therefore the only place a member's compiled index can be resolved
/// — the two entity namespaces the facts name, and the production contract's rows registered the way the weapon
/// provider registers them.
///
/// The rows are taken from <see cref="WeaponPlacementContract.RowsJson"/> rather than restated, and only their
/// owner is restamped: the fragment names the trigger contract as their owner, and a module may only declare
/// capabilities of its own. Everything else about a row — its ports, their order, the enum schema, the optional
/// flags — is the production declaration, and the kernel refuses to register it if any of it is malformed.
///
/// A row the framework's own trigger contract already declares is not re-declared here. The integration batch
/// replaces that provider's `deploy_completed` row with this package's shape and adds the recall row; until it
/// does, registering the row again would be a capability conflict, and the fact would then be checked against the
/// older declaration instead of this one. <see cref="DeclaresRow"/> tells a case which world it is in, and
/// <see cref="PlacementRowTests"/> reads the declared rows out of <see cref="WeaponPlacementContract.RowsJson"/>
/// directly so the declaration cases assert this package's own artifact either way.
/// </summary>
internal sealed class PlacementWorld : IDisposable
{
    internal const string EquipmentKind = "gtfo.equipment";
    internal const string PlayerKind = "gtfo.player";
    internal const long WorldEpoch = 4;

    private readonly RuntimeModuleHandle _placement;

    internal RuntimeKernel Kernel { get; }
    /// <summary>Whether the framework's trigger contract already declares the placement row with the ports this
    /// package publishes. True once integration has applied the row fragment, false on the older declaration.</summary>
    internal bool DeclaresRow { get; }

    internal PlacementWorld()
    {
        Kernel = new RuntimeKernel(new("fixture.weapon.placement", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        // The framework's own trigger contract, because that provider owns the shape of every trigger id and the
        // `equipment_kind` set a member index is resolved through.
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        DeclaresRow = Declares(WeaponPlacementContract.DeployCapability, "equipment_kind");
        _placement = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            new Dictionary<string, CommandHandler>(), Support(),
            new Dictionary<string, Func<EntityReference, bool>>
            {
                [EquipmentKind] = _ => true, [PlayerKind] = _ => true
            }), RuntimeLogLevel.Off);
    }

    /// <summary>Starts the runtime and settles one authoritative tick: the world every case's facts belong to.</summary>
    internal void Start()
    {
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, true);
    }

    /// <summary>The declared row one capability names, read back from the exported manifest: the ports, their order
    /// and their flags are what a plan compiles against, and the manifest is the text the release export writes.</summary>
    internal JsonElement Row(string id)
    {
        var capabilities = JsonDocument.Parse(Kernel.ExportManifest()).RootElement
            .GetProperty("registry").GetProperty("capabilities");
        foreach (var row in capabilities.EnumerateArray())
            if (row.GetProperty("id").GetString() == id) return row.Clone();
        throw new InvalidOperationException("The manifest carries no row " + id + ".");
    }

    /// <summary>Whether the registered capability named <paramref name="id"/> carries the port, read from the
    /// manifest the kernel exports.</summary>
    private bool Declares(string id, string port)
    {
        var manifest = JsonDocument.Parse(Kernel.ExportManifest()).RootElement
            .GetProperty("registry").GetProperty("capabilities");
        foreach (var row in manifest.EnumerateArray())
        {
            if (row.GetProperty("id").GetString() != id) continue;
            foreach (var candidate in row.GetProperty("graph").GetProperty("outputs").EnumerateArray())
                if (candidate.GetProperty("id").GetString() == port) return true;
            return false;
        }
        return false;
    }

    /// <summary>The registration: one provider, the contract's rows that no other provider already declares, with
    /// their owner restamped for this fixture, and the contract's own bindings. The support rows are the contract's
    /// too, because the registry requires one per implemented binding.</summary>
    private string Registry()
    {
        var declared = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in JsonDocument.Parse(Kernel.ExportManifest()).RootElement
            .GetProperty("registry").GetProperty("capabilities").EnumerateArray())
            declared.Add(row.GetProperty("id").GetString()!);
        var capabilities = new JsonArray();
        foreach (var row in WeaponPlacementContract.RowsJson())
        {
            var owned = JsonNode.Parse(row.GetRawText())!.AsObject();
            owned["owner"] = ModuleDefinition.ProviderId;
            if (!declared.Contains(owned["id"]!.GetValue<string>())) capabilities.Add(owned);
        }
        var bindings = new JsonArray();
        foreach (var row in WeaponPlacementContract.Rows)
            bindings.Add(new JsonObject
            {
                ["id"] = row.Binding, ["capabilityId"] = row.Capability, ["providerId"] = ModuleDefinition.ProviderId,
                ["handler"] = row.Binding, ["role"] = "observe", ["status"] = "implemented",
                ["dependencies"] = new JsonArray(), ["requires"] = new JsonArray()
            });
        return new JsonObject
        {
            ["providers"] = new JsonArray
            {
                new JsonObject
                {
                    ["id"] = ModuleDefinition.ProviderId, ["kind"] = "native",
                    ["version"] = ModuleDefinition.Version, ["dependencies"] = new JsonArray()
                }
            },
            ["capabilities"] = capabilities,
            ["bindings"] = bindings
        }.ToJsonString();
    }

    /// <summary>One support row per binding: these facts are answered by this package's own native observation and
    /// by nothing the runtime can check on its own, which is the same `implementation-only` verification every
    /// other row this provider carries declares.</summary>
    private static IReadOnlyList<BindingSupport> Support()
        => WeaponPlacementContract.Rows
            .Select(row => new BindingSupport(row.Binding, "implementation-only",
                new[] { WeaponPlacementContract.DeployableReadPermission })).ToArray();

    public void Dispose()
    {
        _placement.Dispose();
        Kernel.StopRuntime();
    }
}
