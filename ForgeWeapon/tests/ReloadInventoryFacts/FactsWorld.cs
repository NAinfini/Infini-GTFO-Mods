using System.Text.Json;
using System.Text.Json.Nodes;
using ForgeRuntime.Framework;
using ForgeWeapon.Native;

namespace ForgeWeapon.Tests.ReloadInventoryFacts;

/// <summary>One case's world: a kernel with the `gtfo.equipment` kind registered the way the weapon domain
/// registers it — a namespace resolver over the equipment lives this fixture created — plus the two observers over
/// a native-reads double. There is no loader, no session, no hook and no GTFO assembly: the observers are plain
/// objects driven directly, so these cases prove their decisions and not the game's player path.
///
/// The fixture's own identity table is the only source of an equipment reference, and the double's `EquipmentOf`
/// answers from it and from nothing else, which is what makes "an item with no live identity publishes nothing" a
/// case that can actually be written.</summary>
internal sealed class FactsWorld : IDisposable
{
    /// <summary>The kind this fixture registers, spelled as the production code spells it: the literal is kept so a
    /// reworded namespace fails a case instead of travelling with it.</summary>
    internal const string EquipmentKind = "gtfo.equipment";
    internal const string PlayerKind = "gtfo.player";
    /// <summary>The epoch the fixture opens its world at. A re-entry moves the kernel to the next one, so the
    /// readers below ask the kernel rather than holding a constant.</summary>
    internal const long InitialWorldEpoch = 11;
    internal long WorldEpoch => Kernel.WorldEpoch;

    /// <summary>The slot names the game's own `InventorySlot` declares that carry a pool. The fixture's pool table
    /// is keyed by name, which is what the refill row's slot lookup reads.</summary>
    internal static readonly string[] PooledSlots =
        { "GearStandard", "GearSpecial", "GearClass", "ResourcePack", "Consumable", "ConsumableHeavy" };

    private readonly RuntimeModuleHandle _equipment;
    private readonly List<RuntimeEvent> _published = new();
    private readonly List<FakePlayer> _players = new();
    private long _life = 1, _nextPlayer = 1;

    internal RuntimeKernel Kernel { get; }
    internal List<string> Reports { get; } = new();
    internal List<string> Infos { get; } = new();
    internal HashSet<EntityReference> Live { get; } = new();
    internal Dictionary<IntPtr, EntityReference> ByItem { get; } = new();
    internal Dictionary<IntPtr, EntityReference> ByPlayer { get; } = new();
    internal bool Authoritative { get; set; } = true;
    internal long Tick { get; set; }
    internal Reads Native { get; }
    internal ReloadObserver Reload { get; }
    internal InventoryObserver Inventory { get; }

    internal FactsWorld()
    {
        Kernel = new RuntimeKernel(new("fixture.reload.inventory", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(InitialWorldEpoch);
        var kind = EquipmentKind;
        // The registration carries no handler, no evaluator and no shape: every row here is `observe`, whose event
        // payload arrives as a whole frame, so the runtime asks for none of them — and the support rows are the
        // contract's own, because the registry requires one per implemented binding.
        _equipment = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, Registry(),
            new Dictionary<string, CommandHandler>(), ReloadInventoryContract.Support(),
            new Dictionary<string, Func<EntityReference, bool>> { [kind] = reference => Live.Contains(reference) }),
            RuntimeLogLevel.Off);
        Native = new Reads(this);
        // The fixture publishes every row it drives: no plan is mounted here, so the subscription gate this
        // observer asks is answered "subscribed" rather than standing a plan up behind every case.
        Reload = new ReloadObserver(Native, reference => Live.Contains(reference), () => WorldEpoch, () => Tick,
            () => Authoritative, Publish, Reports.Add, Infos.Add, _ => false);
        Inventory = new InventoryObserver(Native, owner => Reload.OpenFor(owner), () => WorldEpoch, () => Tick,
            () => Authoritative, Publish, Reports.Add, Infos.Add, _ => false);
    }

    internal IReadOnlyList<RuntimeEvent> Published => _published;

    /// <summary>Starts the runtime and settles one authoritative tick.</summary>
    internal void Start()
    {
        Kernel.StartRuntime(() => { });
        Kernel.Advance(0, Authoritative);
    }

    /// <summary>Turns the world non-authoritative from the next tick on, the way a machine that lost the master
    /// role sees it.</summary>
    internal void LoseAuthority()
    {
        Authoritative = false;
        Kernel.Advance(Kernel.CurrentTick + 1, false);
    }

    /// <summary>Answers the master role again over a new world. The kernel refuses host migration inside one
    /// world, so regaining authority is a new world epoch — which is also the boundary that drops every recorded
    /// table without a fact. The identity table moves with it: the lives of the old world are gone, while the
    /// players are the same people and get a reference of the new world.</summary>
    internal void ReenterWorld()
    {
        Authoritative = true;
        Live.Clear();
        ByItem.Clear();
        ByPlayer.Clear();
        Kernel.BeginWorld(InitialWorldEpoch + 1);
        Kernel.Advance(0, true);
        foreach (var player in _players)
            ByPlayer[player.Id] = new EntityReference(PlayerKind + ":" + _nextPlayer++, WorldEpoch, 1);
    }

    /// <summary>A player and the `gtfo.player` reference this machine answers for it.</summary>
    internal (FakePlayer Player, EntityReference Reference) Player()
    {
        var player = new FakePlayer();
        var reference = new EntityReference(PlayerKind + ":" + _nextPlayer++, WorldEpoch, 1);
        _players.Add(player);
        ByPlayer[player.Id] = reference;
        return (player, reference);
    }

    /// <summary>An equipment life for an item owned by a player.</summary>
    internal EntityReference Life(FakeItem item, FakePlayer owner)
    {
        item.Owner = owner;
        var reference = new EntityReference(EquipmentKind + ":" + WorldEpoch + "." + _life++, WorldEpoch, 1);
        Live.Add(reference);
        ByItem[item.Id] = reference;
        return reference;
    }

    internal void Retire(EntityReference reference) => Live.Remove(reference);

    /// <summary>A backpack belonging to a player, with an empty pool per slot the case asks for.</summary>
    internal FakeBackpack Backpack(FakePlayer owner, params string[] pooled)
    {
        var backpack = new FakeBackpack(owner);
        foreach (var slot in pooled) backpack.Pool[slot] = 0;
        return backpack;
    }

    internal static FakeBackpackItem Item(FakeItem item, IntPtr? instance = null) => new(item, instance);

    /// <summary>The events one binding was published under, in order.</summary>
    internal List<RuntimeEvent> Facts(string binding)
        => _published.Where(value => value.BindingId == binding).ToList();

    private void Publish(RuntimeEvent value)
    {
        _published.Add(value);
        var result = _equipment.Publish(value);
        if (result.Status == "rejected") throw new InvalidOperationException("fixture publish rejected: " + result.Code);
    }

    /// <summary>The fixture's registration: one module, one provider, and the shapes and binding rows taken from
    /// the contract itself. The rows the runtime's own trigger contract carries have their owner restamped for
    /// this fixture, because a module may only declare capabilities of its own and the shipped rows deliberately
    /// name the trigger contract as their owner. Nothing else about a row is touched, so a drifted port list still
    /// fails here: the runtime re-checks every published fact against the shape it just registered.</summary>
    private static string Registry()
    {
        var capabilities = new JsonArray();
        foreach (var row in TriggerContracts.Rows(ReloadInventoryContract.CapabilityIds))
        {
            var owned = JsonNode.Parse(row.GetRawText())!.AsObject();
            owned["owner"] = ModuleDefinition.ProviderId;
            capabilities.Add(owned);
        }
        var bindings = new JsonArray();
        foreach (var row in ReloadInventoryContract.Bindings())
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

    internal static string Text(RuntimeEvent value, string port)
        => value.Outputs.GetProperty(port).GetString() ?? "";

    internal static long Number(RuntimeEvent value, string port) => value.Outputs.GetProperty(port).GetInt64();

    internal static string Reference(RuntimeEvent value, string port)
        => value.Outputs.GetProperty(port).GetProperty("id").GetString() ?? "";

    internal static bool Has(RuntimeEvent value, string port) => value.Outputs.TryGetProperty(port, out _);

    /// <summary>One published fact's output ports, read as JSON so a case asserts on what a plan would see.</summary>
    internal static System.Text.Json.JsonElement Outputs(RuntimeEvent value) => value.Outputs;

    public void Dispose()
    {
        Reload.Clear();
        Inventory.Clear();
        _equipment.Dispose();
    }

    /// <summary>The game reads, answered from this fixture's tables. Every member is a direct read of the state a
    /// case just set, which is what makes the observers' decisions the only thing under test.</summary>
    internal sealed class Reads : IReloadNativeReads, IInventoryNativeReads
    {
        private readonly FactsWorld _world;

        internal Reads(FactsWorld world) => _world = world;

        public int? Clip(object item) => item is FakeItem value ? value.Clip : null;
        public bool IsReloading(object item) => item is FakeItem value && value.Reloading;
        public double[]? Position(object item) => (item as FakeItem)?.WorldPosition;

        /// <summary>The recorded equipment life, answered only while the fixture's identity table still holds it:
        /// a retired life is exactly what "this machine no longer answers for the reference" means, and the
        /// production reader's own current-check has to be beaten by the double for that to be testable.</summary>
        public EntityReference? EquipmentOf(object item)
            => item is FakeItem value && _world.ByItem.TryGetValue(value.Id, out var reference)
                && _world.Live.Contains(reference) ? reference : null;

        public EntityReference? EquipmentOfItem(object item) => EquipmentOf(item);

        public EntityReference? OwnerOf(object item)
            => item is FakeItem value && value.Owner != null
                && _world.ByPlayer.TryGetValue(value.Owner.Id, out var reference) ? reference : null;

        public EntityReference? OwnerOfItem(object item) => OwnerOf(item);

        public EntityReference? OwnerOfBackpack(object backpack)
            => backpack is FakeBackpack value && _world.ByPlayer.TryGetValue(value.Owner.Id, out var reference)
                ? reference : null;

        /// <summary>The one native member that answers both readers: for a weapon it is the magazine, for a tool its
        /// charge, and the two readers are declared apart so a type implementing both binds each on its own.</summary>
        public int? ItemCharge(object item) => Clip(item);

        public bool ItemIsReloading(object item) => IsReloading(item);

        public IReadOnlyList<NativeSlot>? Slots(object backpack)
        {
            if (backpack is not FakeBackpack value) return null;
            var slots = new List<NativeSlot>();
            foreach (var name in value.Names)
            {
                var entry = value.Slots.TryGetValue(name, out var found) ? found : null;
                var item = entry?.Item;
                slots.Add(new NativeSlot(name, item, entry?.Item.Id ?? IntPtr.Zero,
                    entry?.Instance ?? IntPtr.Zero, item == null ? null : EquipmentOf(item)));
            }
            return slots;
        }

        public IReadOnlyDictionary<string, long>? Pools(object backpack)
            => backpack is FakeBackpack value ? value.Pool : null;
    }
}
