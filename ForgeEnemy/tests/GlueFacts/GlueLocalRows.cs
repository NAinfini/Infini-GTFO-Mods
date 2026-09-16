using System;
using System.Collections.Generic;
using ForgeEnemy;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The registration this slice's focused suite puts in front of the kernel: one provider carrying exactly
/// the row this slice implements — the contract's own capability text, binding text, support row and handler shape
/// — with the handler the provider implements. Nothing here is a second registry: the contract text is
/// <see cref="GlueContract"/>'s own, so a shape that drifts from what the provider publishes cannot pass here.
///
/// It is compiled into the suite's own assembly and disappears together with the integration patch, when this row
/// lives in the provider's own `Registry()`.</summary>
internal static class GlueLocalRows
{
    /// <summary>The suite's provider id. It is deliberately not the production provider's: the production
    /// declaration is one provider's own row set, and a second copy of the same id would be the duplicate this
    /// slice exists to avoid.</summary>
    internal const string TestProviderId = "forge.module.gtfo.enemy.glue";

    /// <summary>The capability the suite's provider declares. The kernel lets a module declare only its own
    /// capabilities, so the canonical row is filed under this provider's id; every port, label and field inside it
    /// is the contract's own text.</summary>
    internal const string TestFoamingCapability = TestProviderId + ".capability.foaming";

    /// <summary>The binding the suite's provider declares for that capability. The handler name is the contract's
    /// own, because it is the name the provider's handler table answers to.</summary>
    internal const string TestFoamingBinding = TestProviderId + ".binding.foaming";

    /// <summary>One declaration carrying this slice's row, bound to the handler instance the scene built.</summary>
    internal static RuntimeModule Module(CommandHandler foaming)
    {
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [GlueContract.FoamingHandler] = foaming
        };
        return new RuntimeModule(RuntimeKernel.ApiVersion, Registry(), handlers, Support())
        {
            Shapes = GlueContract.Shapes()
        };
    }

    /// <summary>The contract's own support rows, filed under the binding this provider declares.</summary>
    private static IReadOnlyList<BindingSupport> Support()
    {
        var rows = new List<BindingSupport>();
        foreach (var row in GlueContract.Support)
            rows.Add(row.BindingId == GlueContract.FoamingBinding ? row with { BindingId = TestFoamingBinding } : row);
        return rows;
    }

    private static string Registry() => "{\n  \"providers\": [\n    {\n      \"id\": \"" + TestProviderId
        + "\",\n      \"kind\": \"native\",\n      \"version\": \"1.0.0\",\n      \"dependencies\": []\n    }\n  ],\n"
        + "  \"capabilities\": [\n" + Owned(GlueContract.CapabilityRows) + "\n  ],\n  \"bindings\": [\n"
        + Owned(GlueContract.BindingRows) + "\n  ]\n}";

    /// <summary>The rows as this provider may declare them: the canonical capability id, its owner, and the binding
    /// id and binding owner are re-filed under the suite's provider, because a module declares only its own
    /// capabilities and bindings. Every other character is the contract's own text.</summary>
    private static string Owned(string text) => text
        .Replace("\"id\": \"" + GlueContract.FoamingCapability + "\"",
            "\"id\": \"" + TestFoamingCapability + "\"", StringComparison.Ordinal)
        .Replace("\"owner\": \"" + GlueContract.ProviderId + "\"",
            "\"owner\": \"" + TestProviderId + "\"", StringComparison.Ordinal)
        .Replace("\"capabilityId\": \"" + GlueContract.FoamingCapability + "\"",
            "\"capabilityId\": \"" + TestFoamingCapability + "\"", StringComparison.Ordinal)
        .Replace("\"id\": \"" + GlueContract.FoamingBinding + "\"",
            "\"id\": \"" + TestFoamingBinding + "\"", StringComparison.Ordinal)
        .Replace("\"providerId\": \"" + GlueContract.ProviderId + "\"",
            "\"providerId\": \"" + TestProviderId + "\"", StringComparison.Ordinal);
}
