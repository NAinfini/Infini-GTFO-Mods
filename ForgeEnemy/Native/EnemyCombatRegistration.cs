using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The registration this slice's focused suite puts in front of the kernel: one self-contained provider
/// that declares exactly the rows this slice registers, with the same `EnemyModule` handlers the production
/// provider registers. Nothing here is a second registry — the suite stands this declaration up beside the
/// provider's own so the kernel resolves every port name, and no shared registration point is edited to make that
/// possible, which is what lets this slice run while its siblings are mid-edit.
///
/// It is internal with the module it extends, and the focused suite compiles this file into its own assembly next
/// to `EnemyModule`. It disappears together with the integration patch, when these rows live in `Registry()`.</summary>
internal static class EnemyCombatRegistration
{
    /// <summary>The suite's provider id. It is deliberately not `EnemyModule.ProviderId`: the production
    /// provider is registered by the module under test, and a declaration is one provider's own row set, so a
    /// second copy of the same id would be the duplicate this slice exists to avoid. It is short because a
    /// capability id is this provider's id plus one segment and the runtime caps an id at 64 characters.</summary>
    internal const string TestProviderId = "forge.test.enemy.combat";

    /// <summary>The rows this slice registers, each paired with the binding row it resolves against and the method
    /// that implements it. The impulse row is absent on purpose: its capability and binding rows live in
    /// `EnemyCombatContract.ImpulseCapabilityRow`/`ImpulseBindingRow` and are not registered, which is what
    /// `EnemyCombatContract.Unregistered` records. Its handler is still compiled and exercised directly.</summary>
    private static readonly (string Handler, string Capability, string Binding, Func<EnemyModule, CommandHandler> Method)[] Rows =
    {
        (EnemyCombatContract.StaggerHandler, EnemyCombatContract.StaggerCapability, EnemyCombatContract.StaggerBinding,
            module => module.Stagger),
        (EnemyCombatContract.AttackInterruptHandler, EnemyCombatContract.AttackInterruptCapability,
            EnemyCombatContract.AttackInterruptBinding, module => module.AttackInterrupt)
    };

    /// <summary>The suite's own capability ids, one per row above, for a case that addresses a registered row.</summary>
    internal const string TestStaggerCapability = TestProviderId + ".capability.stagger";
    internal const string TestAttackInterruptCapability = TestProviderId + ".capability.attack_interrupt";

    /// <summary>The registry text the suite registers, exposed for a case that asserts exactly what the kernel
    /// was handed.</summary>
    internal static string RegistryText() => Registry();

    /// <summary>One declaration carrying this slice's rows, their handlers, shapes, support rows and binding rows,
    /// in the registry text the website catalog reads. The shapes are handed over with the handlers because a
    /// module's handler without one is refused at registration (`missing-shape`): the handler and the graph it
    /// resolves against are two halves of the same registration.</summary>
    internal static RuntimeModule Module(EnemyModule module)
    {
        ArgumentNullException.ThrowIfNull(module);
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        foreach (var row in Rows) handlers[row.Handler] = row.Method(module);
        var support = new List<BindingSupport>();
        foreach (var row in Rows) support.AddRange(Support(row.Binding));
        return new RuntimeModule(RuntimeKernel.ApiVersion, Registry(), handlers, support)
        {
            Shapes = new Dictionary<string, HandlerShape>(EnemyCombatContract.Shapes(), StringComparer.Ordinal)
        };
    }

    /// <summary>The registry text: this provider, the slice's capability rows and the matching binding rows. The
    /// rows themselves are the contract's own text, re-filed under this provider.
    ///
    /// A module may declare only its own capabilities, and the canonical rows belong to the production provider
    /// the module under test already registered. The rows are therefore re-filed under this provider's id here —
    /// `owner` and `id` for a capability, `providerId`, `capabilityId` and `id` for a binding — and nothing else
    /// about them changes. The cases assert the whole canonical row from the contract text, so the re-filing is
    /// the only difference the suite's own registration can hide.</summary>
    private static string Registry() => "{\n  \"providers\": [\n    {\n      \"id\": \"" + TestProviderId
        + "\",\n      \"kind\": \"native\",\n      \"version\": \"1.0.0\",\n      \"dependencies\": []\n    }\n  ],\n"
        + "  \"capabilities\": [\n" + Refile(EnemyCombatContract.CapabilityRows, binding: false)
        + "\n  ],\n  \"bindings\": [\n" + Refile(EnemyCombatContract.BindingRows, binding: true) + "\n  ]\n}";

    /// <summary>Re-files one table of the contract's rows under this provider: every field that names a provider,
    /// a capability or a binding is rewritten to this provider's own spelling of that same row, and no other field
    /// is touched. `graph` and `parameters` are nested halves of a capability row and are re-emitted as written,
    /// which is what lets a case compare them with the canonical text byte for byte.</summary>
    private static string Refile(string rows, bool binding)
    {
        var owned = new List<string>();
        foreach (var row in RuntimeJson.Parse("[" + rows + "]").EnumerateArray())
        {
            var fields = new List<string>();
            foreach (var field in row.EnumerateObject())
            {
                var value = field.Value;
                var text = value.ValueKind == JsonValueKind.String && NamesAnId(field.Name)
                    ? RuntimeJson.From(Own(field.Name, value.GetString()!, binding)).GetRawText()
                    : value.GetRawText();
                fields.Add("\"" + field.Name + "\":" + text);
            }
            owned.Add("{" + string.Join(",", fields) + "}");
        }
        return string.Join(",\n", owned);
    }

    /// <summary>The fields that carry an id this provider has to spell its own way. Everything else — a label, a
    /// handler name, a status — is a value, not a name.</summary>
    private static bool NamesAnId(string field) => field is "id" or "owner" or "providerId" or "capabilityId";

    /// <summary>This provider's spelling of one field of one re-filed row. A binding's own `id` keeps the binding
    /// namespace and a capability's `id` the capability namespace, so the two tables never name the same registry
    /// entry; `capabilityId` always points at the capability row, which is the row the binding implements.</summary>
    private static string Own(string field, string canonical, bool binding) => field switch
    {
        "owner" or "providerId" => TestProviderId,
        "capabilityId" => TestProviderId + ".capability." + LastSegment(canonical),
        _ => TestProviderId + (binding ? ".binding." : ".capability.") + LastSegment(canonical)
    };

    private static string LastSegment(string id) => id[(id.LastIndexOf('.') + 1)..];

    /// <summary>The support rows of one canonical binding, re-filed under this provider's own binding id. The
    /// kernel requires every support row to name a registered binding, so the suite cannot hand over the
    /// production provider's binding ids — and the permissions themselves are the contract's, unchanged.</summary>
    private static IReadOnlyList<BindingSupport> Support(string canonicalBinding)
    {
        var rows = new List<BindingSupport>();
        var owned = TestProviderId + ".binding." + LastSegment(canonicalBinding);
        foreach (var row in EnemyCombatContract.Support)
            if (row.BindingId == canonicalBinding)
                rows.Add(new BindingSupport(owned, row.Verification, row.RequiredPermissions));
        return rows;
    }
}
