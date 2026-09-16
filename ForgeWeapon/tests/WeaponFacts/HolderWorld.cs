using System;
using System.Collections.Generic;
using System.Linq;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Tests.WeaponFacts;

/// <summary>
/// One case's world for the holder provider: a real kernel carrying the framework contract module, this
/// fixture's own entity namespace, and the production provider registered through
/// <see cref="WeaponHolderActionsContract.Module"/> exactly as the native half registers it.
///
/// The two handler bodies are the fixture's, because they are the only part of the registration that touches the
/// game; the rows, the tier, the owner-session table and the shapes are the production contract's, and a kernel
/// only accepts them if they are well formed. That is what these cases assert: registration is itself the
/// contract check, and the exported manifest is the same text the release export writes.
/// </summary>
internal sealed class HolderWorld : IDisposable
{
    internal const string EquipmentKind = "gtfo.equipment";
    internal const string PlayerKind = "gtfo.player";
    internal const long WorldEpoch = 4;

    private readonly RuntimeModuleHandle _entities;
    private RuntimeModuleHandle? _holder;
    private long _life = 1;

    internal RuntimeKernel Kernel { get; }
    /// <summary>How many times each handler body ran, keyed by the handler's own name.</summary>
    internal Dictionary<string, int> Calls { get; } = new(StringComparer.Ordinal);

    internal HolderWorld()
    {
        Kernel = new RuntimeKernel(new("fixture.weapon.holder", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        // The provider the holder rows declare as their dependency, because they read equipment identities it
        // mints. It is declared here rather than left out so the dependency is exercised: the registry refuses a
        // package whose dependency is not registered at all.
        Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(ModuleDefinition.ProviderId),
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>()), RuntimeLogLevel.Off);
        _entities = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("fixture.holder.entities"),
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>>
            {
                [EquipmentKind] = reference => true,
                [PlayerKind] = reference => true
            }), RuntimeLogLevel.Off);
    }

    /// <summary>Registers the production holder provider with the fixture's handler bodies and the fixture's own
    /// owner-session answer. A null <paramref name="holder"/> registers the same rows with no resolver at all,
    /// which is the declaration-only shape.</summary>
    internal void Register(Func<EntityReference, string?>? holder)
    {
        if (_holder != null) throw new InvalidOperationException("The holder provider is registered once per world.");
        var module = holder == null
            ? WeaponHolderActionsContract.Module(null, Reload, ClipSet)
            : WeaponHolderActionsContract.Module(holder, Reload, ClipSet);
        _holder = Kernel.RegisterModule(module, RuntimeLogLevel.Off);
    }

    /// <summary>Starts the runtime and settles one authoritative tick: the world every case's facts belong to.</summary>
    internal void Start()
    {
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, true);
    }

    /// <summary>One equipment instance of this world, for a case that needs a real reference to hand a resolver
    /// or a handler frame.</summary>
    internal EntityReference Equipment()
        => new(EquipmentKind + ":life:" + WorldEpoch + ":" + _life++, WorldEpoch, 1);

    internal EntityReference Player(string id) => new(id, WorldEpoch, 1);

    /// <summary>The declared row one capability is, read back from the exported manifest: the tier, the ports and
    /// the parameters a plan compiles against. The manifest is the text the release export writes, so a case here
    /// reads what the site reads.</summary>
    internal System.Text.Json.JsonElement Row(string capability)
        => Manifest().GetProperty("registry").GetProperty("capabilities").EnumerateArray()
            .First(row => row.GetProperty("id").GetString() == capability);

    /// <summary>The bindings this provider registered, by id.</summary>
    internal List<string> RegisteredBindings()
    {
        var ids = new List<string>();
        foreach (var row in Manifest().GetProperty("registry").GetProperty("bindings").EnumerateArray())
            ids.Add(row.GetProperty("id").GetString()!);
        return ids;
    }

    internal bool ProviderRegistered()
    {
        foreach (var row in Manifest().GetProperty("registry").GetProperty("providers").EnumerateArray())
            if (row.GetProperty("id").GetString() == WeaponHolderChannelContract.ProviderId) return true;
        return false;
    }

    private System.Text.Json.JsonElement Manifest()
        => System.Text.Json.JsonDocument.Parse(Kernel.ExportManifest()).RootElement;

    /// <summary>One handler body. It records that it ran and then answers with the row's own result schema, which
    /// the kernel's result validation accepts: a handler that could not keep the row's promise would fail here
    /// rather than in the game.</summary>
    private CommandResult Body(string handler, CommandContext context)
    {
        Calls[handler] = Calls.TryGetValue(handler, out var count) ? count + 1 : 1;
        var equipment = RuntimeJson.Entity(context.Inputs.GetProperty("equipment"));
        return CommandResult.Succeeded(RuntimeJson.From(new
        {
            rows = new[] { new { target = equipment, status = "succeeded", committed = "confirmed", code = "", clip = 7 } }
        }));
    }

    private CommandResult Reload(CommandContext context) => Body(WeaponHolderActionsContract.ReloadHandler, context);

    private CommandResult ClipSet(CommandContext context) => Body(WeaponHolderActionsContract.ClipSetHandler, context);

    /// <summary>One empty provider of the given id. A `forge.` id is reserved for a native provider by the
    /// registry's own rule, which is why the kind is chosen from the id rather than passed in.</summary>
    private static string Registry(string provider) => RuntimeJson.From(new
    {
        providers = new[]
        {
            new
            {
                id = provider,
                kind = provider.StartsWith("forge.", StringComparison.Ordinal) ? "native" : "extension",
                version = "1.0.0", dependencies = Array.Empty<string>()
            }
        },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText();

    public void Dispose()
    {
        _holder?.Dispose();
        _entities.Dispose();
        Kernel.StopRuntime();
    }
}
