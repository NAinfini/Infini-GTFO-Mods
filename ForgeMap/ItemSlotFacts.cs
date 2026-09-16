using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>
/// The item-slot half of the Map provider: what a player puts a carried item into, and what a player takes out
/// of it again. It is the publisher behind `forge.trigger.interaction.item_placed` and
/// `forge.trigger.interaction.item_removed`, and it owns no registration of its own — the two rows are declared
/// on the one Map provider `<see cref="MapObjectModule"/>` already registers, because the runtime accepts exactly
/// one provider of one identity namespace and a carried item is addressed in that namespace.
///
/// Publication is deliberately narrow, exactly as the map-object half's is. A hook reports which of the game's
/// two own entries ran — its insertion entry and its removal entry — and this module re-reads the slot
/// afterwards; a slot whose address no longer reads the same way, or that no longer carries the item the entry
/// was called with, publishes nothing. The pair the two capabilities make is the game's own pairing: whether a
/// placement immediately removes the item again is the game's `RemoveItemOnInsert` decision, taken inside its own
/// insertion body, and this module never infers a removal from an insertion. It reports a removal exactly when
/// the game's own removal entry ran.
///
/// The host is read from the game's own insertion target and the item from the slot's own record, and each of
/// them is addressed as a map object; a subject that cannot be read that way leaves its port absent rather than
/// publishing a coordinate it was not read for.
/// </summary>
public sealed class ItemSlotFacts : IDisposable
{
    /// <summary>The fact kinds this half publishes, declared here so the contract rows and the module cannot
    /// drift into two collections of the same names.</summary>
    public const string PlacedFact = "item_placed";
    public const string RemovedFact = "item_removed";

    /// <summary>The entity namespace a player is addressed in, owned by this provider's own player half and read
    /// here only to ask the kernel for the actor of an insertion or a removal.</summary>
    internal const string PlayerKind = "gtfo.player";

    /// <summary>One slot as the game-bound half can read it: the address of the host the item is put into, and
    /// the item's own record. The half is game-bound only in what it reads, never in what it decides — address
    /// grammar, publication rules and refusal reasons all live here, so a stub reader exercises them without the
    /// game.</summary>
    public interface IItemSlotSource
    {
        /// <summary>The address of the host one insertion target is, or null when its own keys do not read. A
        /// host the native side cannot address has no fact at all.</summary>
        MapObjectReference? HostAddress(object slot);

        /// <summary>The slot's own record, re-read after the native entry returned. A read whose address no
        /// longer matches what the instance was addressed with reports `AddressMatches: false` instead of the new
        /// slot's state, and an unreadable slot answers null.</summary>
        ItemSlotSnapshot? Read(object slot);

        /// <summary>The item this slot holds right now: the address of the carried object the slot's own record
        /// names, or null when the slot holds nothing or names no addressable item.</summary>
        MapObjectReference? ItemAddress(object slot, string hostAddress);

        /// <summary>Whether the instance still reads as the address it was addressed with.</summary>
        bool IsCurrentAddress(object slot, MapObjectReference address);
    }

    /// <summary>One slot's own record: whether it still reads the way it was addressed, the host address its
    /// instance reports, and the item's own identity. `Resource` is the item's stable resource spelling — the
    /// carried object's own `ItemDataBlock` id when it has one and its gear checksum when it is a gear block —
    /// and is what the fact's `item` port carries once the item has no map-object address of its own.
    /// `Position` is the slot's own three coordinates, or null when they did not read.</summary>
    public sealed record ItemSlotSnapshot(bool AddressMatches, MapObjectReference? Host, string? Resource,
        double[]? Position);

    /// <summary>One slot state this module has published: the state key of the last fact of one kind for one
    /// host, and that row's own transition number.</summary>
    private sealed record Published(string StateKey, long Transition);

    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly IItemSlotSource _slots;
    private readonly Func<bool> _authority;
    private readonly Action<string> _report;
    private readonly Dictionary<string, Published> _published = new(StringComparer.Ordinal);
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private RuntimeLifecycleSubscription? _lifecycle;
    private DispatchResult? _last;
    private long _publishedFacts;
    private bool _disposed;

    /// <param name="registration">The one Map registration. This half publishes through the handle the map-object
    /// half owns and never registers a provider of its own.</param>
    /// <param name="authority">Whether this peer publishes. Every fact here reads host-only state, so a client
    /// reports one diagnostic and publishes nothing.</param>
    public ItemSlotFacts(RuntimeKernel kernel, RuntimeModuleHandle registration, IItemSlotSource slots,
        Func<bool> authority, Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _slots = slots ?? throw new ArgumentNullException(nameof(slots));
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>Starts observing world transitions. The per-world tables are dropped when the world changes:
    /// the kernel's epoch is what invalidates earlier ids, and this only releases the memory of a level that no
    /// longer exists.</summary>
    public void Observe()
    {
        CheckThread();
        if (_lifecycle != null) throw new InvalidOperationException("The item-slot half is already observing.");
        _lifecycle = _registration.ObserveLifecycle(change =>
        {
            if (change.Kind == RuntimeLifecycleKind.WorldChanged) BeginWorld();
        });
    }

    /// <summary>Events this half actually handed to the kernel (status `queued`). A client, an unreadable slot or
    /// a repeated state never contributes, which is what makes the count assertable.</summary>
    public long PublishedFacts => _publishedFacts;

    /// <summary>What the kernel answered to the last event this half offered it, or null when it offered none.
    /// The refusal reason is the kernel's own code — `no-consumer` for a fact no plan subscribes to, `rejected`
    /// for one the registry refused — and a caller asserts on it instead of reading the kernel's log.</summary>
    public DispatchResult? LastDispatch => _last;

    /// <summary>The payload of the last event this half offered the kernel, as text. It exists so a case can
    /// assert what the ports actually carried instead of only what the kernel answered.</summary>
    public string? LastPayload { get; private set; }

    /// <summary>Drops this half's own per-world table. Called by the lifecycle observer and directly by a caller
    /// that owns the transition.</summary>
    public void BeginWorld()
    {
        CheckThread();
        _published.Clear();
    }

    /// <summary>The game's own insertion entry ran for one slot: a carried item was put into a host by one
    /// actor. Whether the game then removes it again is the game's own decision inside that body — this fact
    /// reports the placement and nothing more. Returns whether a fact reached the kernel, so a caller can tell a
    /// publication from a refusal without reading the kernel's log.</summary>
    public bool ItemPlaced(object slot, object? actor)
    {
        CheckThread();
        return Publish(PlacedFact, slot, actor);
    }

    /// <summary>The game's own removal entry ran for one slot: a carried item was taken out of a host by one
    /// actor. The pairing with a placement is the game's — a body whose `RemoveItemOnInsert` is set runs this
    /// entry itself — so a removal is reported exactly when this entry ran, never inferred. Returns whether a
    /// fact reached the kernel.</summary>
    public bool ItemRemoved(object slot, object? actor)
    {
        CheckThread();
        return Publish(RemovedFact, slot, actor);
    }

    private bool Publish(string fact, object slot, object? actor)
    {
        if (_disposed || !_registration.IsRegistered) return false;
        if (slot == null) return false;
        if (_slots.HostAddress(slot) is not { } host)
        {
            ReportOnce("host-address:" + fact, "item-slot " + fact + " not published: the insertion target reports no "
                + "course node or no socket kind, so it has no map-object address.");
            return false;
        }
        var read = _slots.Read(slot);
        if (read == null || !read.AddressMatches || !_slots.IsCurrentAddress(slot, host))
        {
            ReportOnce("reread:" + host, "item-slot fact refused for " + host + ": the native instance no "
                + "longer reads as it was addressed.");
            return false;
        }
        var item = _slots.ItemAddress(slot, host.ToString());
        string stateKey = (item?.ToString() ?? "") + "|" + (read.Resource ?? "");
        if (_kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_authority())
        {
            ReportOnce("client:" + host, "item-slot fact observed on a non-authoritative peer: the host "
                + "publishes slot state, a client does not.");
            return false;
        }
        // Each fact kind has its own last-published record: a second removal of one host is that row's own next
        // transition, and the two facts of the pair are never one entry. The host address and the item are what a
        // transition is about, so a table keyed by the address alone would let one row's state refuse the other.
        string address = host.ToString();
        string key = fact + "|" + address;
        if (_published.TryGetValue(key, out var last) && last.StateKey == stateKey) return false;
        long transition = last == null ? 1 : last.Transition + 1;
        _published[key] = new Published(stateKey, transition);
        var outputs = Payload(
            // An entity port carries a reference, and a map object's reference is its address text in the one
            // `gtfo.map_object` namespace. The record itself is never serialized: its members are this package's
            // address grammar, not a reference, and the kernel would refuse the object it produced.
            ("host", Reference(host)),
            // The `item` port carries the carried object's own map-object address when the slot names one. A slot
            // whose item has no address at all — a carried object the level never placed, a gear block — leaves
            // the port absent instead of being published as a coordinate nothing was read for.
            ("item", item != null ? Reference(item) : null),
            ("actor", Actor(actor) is { } player ? RuntimeJson.From(player) : null));
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this
        // call is about to build, so the event value is never built. The state above is still recorded, because a
        // fact observed while nobody listened is a fact this half has already reported.
        string binding = ItemSlotContract.Binding(Capability(fact));
        if (_gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers) return false;
        _last = _registration.Publish(new RuntimeEvent(
            ItemSlotContract.EventId(fact, _kernel.WorldEpoch, address, transition),
            binding, _kernel.WorldEpoch, Math.Max(0, _kernel.CurrentTick),
            "gtfo.world:" + _kernel.WorldEpoch.ToString(System.Globalization.CultureInfo.InvariantCulture), outputs));
        LastPayload = outputs.GetRawText();
        if (_last.Status == "queued") { _publishedFacts++; return true; }
        if (_last.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + _last.Code, "item-slot " + fact + " fact rejected: " + _last.Code);
        return false;
    }

    /// <summary>One published payload, built port by port. A port whose value the native side could not read is
    /// left out instead of being published as a JSON null: an absent port is the framework's own way to say "not
    /// observable", and a null would claim the port was written.</summary>
    private static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return RuntimeJson.From(payload);
    }

    /// <summary>The reference of one map object: its own address text in the one `gtfo.map_object` namespace,
    /// carrying the world it was read in and the one life an addressed object has. The address is the id, so a
    /// reference reverses without a table and a world change invalidates it through the epoch it carries.</summary>
    private JsonElement Reference(MapObjectReference address)
        => RuntimeJson.From(new EntityReference(ItemSlotContract.EntityKind + ":" + address, _kernel.WorldEpoch, 1));

    /// <summary>The capability one fact kind belongs to: the two rows of this family are the only kinds this half
    /// publishes, so an unknown kind is a contract violation rather than a fact to publish.</summary>
    private static string Capability(string fact) => fact switch
    {
        PlacedFact => ItemSlotContract.PlacedCapability,
        RemovedFact => ItemSlotContract.RemovedCapability,
        _ => throw new RuntimeContractException("item-slot-fact", "Unknown item-slot fact kind.")
    };

    /// <summary>The actor of an insertion or a removal, resolved through the player domain's own instance lookup
    /// so a slot never carries a player identity of its own. A caller that cannot name the actor publishes the
    /// port absent rather than inventing one.</summary>
    private EntityReference? Actor(object? instance)
    {
        if (instance == null) return null;
        var player = _kernel.ResolveEntityInstance(PlayerKind, instance);
        return player != null && player.WorldEpoch == _kernel.WorldEpoch ? player : null;
    }

    private void ReportOnce(string key, string message)
    {
        if (_reported.Add(key)) _report(message);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Item slots require the runtime's own simulation thread.");
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        _disposed = true;
        _lifecycle?.Dispose();
        _lifecycle = null;
        _published.Clear();
    }
}
