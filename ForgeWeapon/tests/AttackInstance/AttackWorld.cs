using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.AttackInstance;

/// <summary>One case's world: a kernel carrying the framework contract module the three rows belong to, the
/// fixture's own registration of the attack-instance bindings, and the production
/// <see cref="AttackInstanceModule"/> over the fixture's own <see cref="IAttackNativeReads"/>.
///
/// The reads are a fixture rather than the production equipment adapter on purpose. The adapter's own job —
/// turning a backpack readback into an equipment life and recording a shot against it — is what the package's
/// own suites cover; what this slice adds is the burst and dry-fire decisions over that life, and the narrow
/// interface is exactly that boundary. A case therefore drives a life directly and reads the facts the module
/// publishes from the one publication path.</summary>
internal sealed class AttackWorld : IDisposable
{
    internal const string PlayerKind = "gtfo.player";
    internal const string EquipmentKind = "gtfo.equipment";
    internal const long WorldEpoch = 7;

    private readonly RuntimeModuleHandle _attackRegistration, _entities;
    private long _equipment;

    internal RuntimeKernel Kernel { get; }
    internal Reads Native { get; }
    internal AttackInstanceModule Attack { get; }
    internal List<string> Reports { get; } = new();
    internal List<string> Infos { get; } = new();
    /// <summary>When clear, no fact is published and the fixture's reads answer nothing, which is the state a
    /// machine that is not the host is in.</summary>
    internal bool Authoritative { get; set; } = true;

    internal AttackWorld()
    {
        Kernel = new RuntimeKernel(new("fixture.attack.instance", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        // The framework contract module every `forge.trigger.*` row belongs to in this build. A package module
        // may not declare a capability another provider owns, which is why the three declarations travel to the
        // integration batch as a fragment and this fixture restamps their owner for its own registration.
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        Native = new Reads(this);
        var equipment = EquipmentKind;
        _entities = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry("fixture.entities"),
            new Dictionary<string, CommandHandler>(), Array.Empty<BindingSupport>(),
            new Dictionary<string, Func<EntityReference, bool>>
            {
                [equipment] = reference => Native.LiveEquipment.Contains(reference),
                [PlayerKind] = reference => Native.LivePlayers.Contains(reference)
            }), RuntimeLogLevel.Off);
        _attackRegistration = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, AttackRegistry(),
            new Dictionary<string, CommandHandler>(), AttackInstanceContract.Support()), RuntimeLogLevel.Off);
        Attack = new AttackInstanceModule(Native, () => Kernel, Reports.Add, Infos.Add);
    }

    /// <summary>Starts the runtime and settles one authoritative tick: the world every case's facts belong to.</summary>
    internal void Start()
    {
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, Authoritative);
    }

    internal void LoseAuthority()
    {
        Authoritative = false;
        Kernel.Advance(Kernel.CurrentTick + 1, false);
    }

    /// <summary>Every fact the module handed to Runtime, in publication order.</summary>
    internal List<RuntimeEvent> Facts { get; } = new();

    /// <summary>The facts one binding was published under, in order.</summary>
    internal List<RuntimeEvent> FactsOf(string binding)
        => Facts.Where(value => value.BindingId == binding).ToList();

    /// <summary>The status of the most recent dispatch, so a case can tell a published fact from a rejected one
    /// as well as from a suppressed one.</summary>
    internal string LastStatus { get; private set; } = "none";

    internal Speaker Player() => Native.Player();

    internal Equipment Rig(Gear.BulletWeapon weapon, Speaker owner) => Native.Rig(weapon, owner);

    /// <summary>Publishes one fact through the fixture's own path, the way the adapter's `Publish` does: recorded
    /// locally, then handed to the module's registration so the runtime answers with a real dispatch result.</summary>
    private DispatchResult Publish(RuntimeEvent value)
    {
        Facts.Add(value);
        var result = _attackRegistration.Publish(value);
        LastStatus = result.Status == "rejected" ? "rejected:" + result.Code : result.Status;
        return result;
    }

    /// <summary>The fixture's registration: one provider, the three catalog documents with their owner restamped
    /// for this fixture, and the contract's own binding and support rows.</summary>
    private string AttackRegistry()
    {
        var capabilities = new JsonArray();
        foreach (var row in AttackInstanceContract.Rows())
        {
            var owned = JsonNode.Parse(row.GetRawText())!.AsObject();
            owned["owner"] = ModuleDefinition.ProviderId;
            capabilities.Add(owned);
        }
        var bindings = new JsonArray();
        foreach (var row in AttackInstanceContract.Bindings())
            bindings.Add(JsonNode.Parse(RuntimeJson.From(row).GetRawText()));
        var registry = new JsonObject
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
        };
        return registry.ToJsonString();
    }

    private static string Registry(string provider) => RuntimeJson.From(new
    {
        providers = new[] { new { id = provider, kind = "extension", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<object>(), bindings = Array.Empty<object>()
    }).GetRawText();

    /// <summary>The slice's native facts as a fixture: which equipment life a weapon belongs to and the player
    /// reference its owner resolves to. The module is the only reader.</summary>
    internal sealed class Reads : IAttackNativeReads
    {
        private readonly AttackWorld _world;
        private readonly Dictionary<IntPtr, (Equipment Equipment, Speaker Owner)> _lives = new();
        private long _players;

        internal Reads(AttackWorld world) => _world = world;

        internal HashSet<EntityReference> LiveEquipment { get; } = new();
        internal HashSet<EntityReference> LivePlayers { get; } = new();

        /// <summary>Whether the module may publish. The production answer is "this machine is the host with a
        /// ready runtime"; the fixture's is the same question asked of the case.</summary>
        public bool Authoritative() => _world.Authoritative;

        public (EntityReference Source, EntityReference Equipment)? AttackTarget(Item? weapon)
            => weapon is not null && _lives.TryGetValue(weapon.Pointer, out var life)
                ? (life.Owner.Reference, life.Equipment.Reference)
                : null;

        public DispatchResult Publish(RuntimeEvent value) => _world.Publish(value);

        /// <summary>The fixture publishes every row it drives: no plan is mounted here, so the gate the module
        /// asks before it builds a fact is answered "subscribed" rather than standing a plan up per case.</summary>
        public bool Unsubscribed(string binding) => false;

        internal Speaker Player()
        {
            var reference = new EntityReference(PlayerKind + ":" + (++_players), WorldEpoch, 1);
            LivePlayers.Add(reference);
            return new Speaker(reference);
        }

        internal Equipment Rig(Gear.BulletWeapon weapon, Speaker owner)
        {
            var equipment = new Equipment(weapon, owner, new EntityReference(
                EquipmentKind + ":" + WorldEpoch + "." + (++_world._equipment), WorldEpoch, 1));
            _lives[weapon.Pointer] = (equipment, owner);
            LiveEquipment.Add(equipment.Reference);
            return equipment;
        }
    }

    /// <summary>One player and the reference the fixture's own kind answers for it.</summary>
    internal sealed class Speaker
    {
        internal Speaker(EntityReference reference) => Reference = reference;
        internal EntityReference Reference { get; }
    }

    /// <summary>One recorded equipment life.</summary>
    internal sealed class Equipment
    {
        internal Equipment(Gear.BulletWeapon weapon, Speaker owner, EntityReference reference)
        {
            Weapon = weapon;
            Owner = owner;
            Reference = reference;
        }

        internal Gear.BulletWeapon Weapon { get; }
        internal Speaker Owner { get; }
        internal EntityReference Reference { get; }
    }

    internal static string ReferenceOf(RuntimeEvent value, string port)
        => value.Outputs.GetProperty(port).GetProperty("id").GetString() ?? "";

    public void Dispose()
    {
        _attackRegistration.Dispose();
        _entities.Dispose();
    }
}
