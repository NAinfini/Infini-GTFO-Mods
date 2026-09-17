using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;

namespace ForgeWeapon.Native;

/// <summary>The native reads one reload life is decided from. The observer holds this interface and no game type,
/// so the rules below are exercised against a double and the native half is only these reads. Every member answers
/// about one machine's own state: a read that answers null leaves the fact out instead of publishing a placeholder,
/// and no member ever asks another machine anything, because a fact synthesised from another machine's state would
/// name a reload this machine never saw.</summary>
internal interface IReloadNativeReads
{
    /// <summary>The magazine's own readback for one native item: the rounds in it right now, or null when the
    /// item cannot answer.</summary>
    int? Clip(object item);
    /// <summary>The item's own reload flag.</summary>
    bool IsReloading(object item);
    /// <summary>The recorded equipment life a native item is right now, or null when this machine holds no live
    /// identity for it.</summary>
    EntityReference? EquipmentOf(object item);
    /// <summary>The `gtfo.player` reference of the item's owner, resolved through that domain's own lookup and
    /// never derived here, or null when the owning domain has no current reference.</summary>
    EntityReference? OwnerOf(object item);
}

/// <summary>One slot of one player's backpack as the native half reads it. The item is the native object the
/// transfer and the drop position are read from; it is null for an empty slot.</summary>
internal sealed record NativeSlot(string Name, object? Item, IntPtr ItemIdentity, IntPtr InstanceIdentity,
    EntityReference? Equipment);

/// <summary>Where a transfer's amount was read. A reload's rounds leave the player's own pool and enter the
/// magazine; the magazine and the pool are two readbacks of that one movement, kept apart so a published fact says
/// which of them produced it and so a pool that grew outside a reload is never published here.</summary>
internal enum ReloadAmountSource
{
    /// <summary>The magazine gained rounds over the open life's baseline.</summary>
    Magazine,
    /// <summary>The player's pool gained rounds inside a reload window, which is how a reload this machine did not
    /// run is still read as a transfer rather than as a refill.</summary>
    Pool
}

/// <summary>The three combat reload rows, decided from one observation each:
///
/// `reload_started` opens a life when a reload is really under way — the item's own flag read true. The
/// inventory's reload entry point and the flag's own setter are the same event seen from two places, so the second
/// one confirms the life the first opened instead of opening another.
///
/// `reload_transferred` is the amount that actually entered the magazine: the difference between the magazine read
/// back now and the magazine the life recorded when it opened, published once per advance with the life's baseline
/// moved forward, so a second readback of the same difference publishes nothing. A pool that grew inside the reload
/// window is the same movement seen from the pack side and is published as a transfer too, never as a refill — the
/// refill row is the inventory family's and is not published from a readback that already carried a transfer.
///
/// `reload_completed` closes a life that moved at least one round. A life that moved none closes silently: the node
/// list's `e-w-reload` is start and completion, so an interrupted reload is not a node and carries no fact.
///
/// Nothing here reads a second machine's state. Evidence `evidence/reload-facts.json` records that whether a
/// client's reload start edge reaches the host is not established by this build's metadata, so a fact is published
/// only from a machine that can read the state behind it. The consequence is deliberate and recorded there: a
/// reload no machine holding this package could read back produces no fact rather than a guessed one.</summary>
internal sealed class ReloadObserver
{
    /// <summary>One open reload. The baseline is the magazine at the moment the life opened and moves forward with
    /// every reported advance, so a published amount is always what this readback added and never a total that was
    /// already reported.</summary>
    private sealed class Life
    {
        internal Life(EntityReference equipment, EntityReference actor, int baseline)
        { Equipment = equipment; Actor = actor; Baseline = baseline; }

        internal EntityReference Equipment { get; }
        internal EntityReference Actor { get; }
        internal int Baseline { get; set; }
        internal long Transferred { get; set; }
        /// <summary>Set once this machine has seen the item's own flag read true. A life opened by the reload entry
        /// point alone, with the flag never reading true, is a reload this machine did not actually watch and is
        /// closed without a fact.</summary>
        internal bool Confirmed { get; set; }
    }

    private readonly IReloadNativeReads _native;
    private readonly Func<EntityReference, bool> _isCurrent;
    private readonly Func<long> _world, _tick;
    private readonly Func<bool> _authoritative;
    private readonly Action<RuntimeEvent> _publish;
    private readonly Func<string, bool> _unsubscribed;
    private readonly Action<string> _report, _info;
    private readonly Dictionary<string, Life> _open = new(StringComparer.Ordinal);
    private long _epoch = -1, _sequence;

    /// <summary>`isCurrent` is the equipment namespace's own resolver, the one the runtime and every plan already
    /// ask before it acts on a reference, so a life that is no longer current is refused a transfer here for the
    /// same reason it would be refused anywhere else.</summary>
    internal ReloadObserver(IReloadNativeReads native, Func<EntityReference, bool> isCurrent,
        Func<long> worldEpoch, Func<long> tick, Func<bool> authoritative,
        Action<RuntimeEvent> publish, Action<string> report, Action<string> info, Func<string, bool> unsubscribed)
    {
        _native = native ?? throw new ArgumentNullException(nameof(native));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _world = worldEpoch ?? throw new ArgumentNullException(nameof(worldEpoch));
        _tick = tick ?? throw new ArgumentNullException(nameof(tick));
        _authoritative = authoritative ?? throw new ArgumentNullException(nameof(authoritative));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _unsubscribed = unsubscribed ?? throw new ArgumentNullException(nameof(unsubscribed));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    internal int OpenCount => _open.Count;

    /// <summary>The reload entry point ran with this item wielded. It opens a life only when the item's own flag
    /// reads true right after, so a reload the game refused produces nothing here; the refusal is `use_failed`'s,
    /// which reads the game's own gate instead.</summary>
    internal void Requested(object item)
    {
        if (!Enter(item, out var key)) return;
        if (!_native.IsReloading(item)) return;
        Confirm(key, item);
    }

    /// <summary>The item's own reload flag changed: the rising edge opens the life, the falling edge closes it. A
    /// falling edge with no open life is a flag that was already false, which is not a reload this machine watched
    /// and publishes nothing.</summary>
    internal void FlagChanged(object item, bool reloading)
    {
        if (!Enter(item, out var key)) return;
        if (reloading) Confirm(key, item);
        else Close(key);
    }

    /// <summary>The magazine was read back after a body that could have moved rounds into it. A gain over the open
    /// life's baseline is the transfer; a readback outside an open life publishes nothing, because a magazine that
    /// changed with no reload under way is not a reload transfer.</summary>
    internal void MagazineRead(object item)
    {
        if (!Enter(item, out var key)) return;
        if (!_open.TryGetValue(key.Id, out var life)) return;
        var clip = _native.Clip(item);
        if (clip == null) return;
        Advance(life, clip.Value - life.Baseline, ReloadAmountSource.Magazine);
    }

    /// <summary>The player's own pool was read back after a body that could have filled it. Inside a reload window
    /// a gain here is the same movement the magazine side would show, so it is published as a transfer; outside one
    /// it is a refill and belongs to the inventory family, which is why nothing is published here then. A life whose
    /// magazine side already advanced is not measured again from the pool: the two readbacks describe one movement
    /// and publishing both would report it twice.</summary>
    internal void PoolRead(EntityReference? owner, long gain)
    {
        if (owner == null || gain <= 0 || !Ready()) return;
        foreach (var life in new List<Life>(_open.Values))
        {
            if (life.Actor != owner || life.Transferred != 0 || !life.Confirmed) continue;
            Advance(life, gain, ReloadAmountSource.Pool);
        }
    }

    /// <summary>Whether one player has a reload life open right now. The two families read the same pool, so this
    /// is what keeps one pool movement from being published as a refill and as a reload transfer at once; it
    /// answers from this observer's own table and keeps no second copy anywhere.</summary>
    internal bool OpenFor(EntityReference? owner)
    {
        if (owner == null) return false;
        foreach (var life in _open.Values) if (life.Actor == owner) return true;
        return false;
    }

    /// <summary>One life's advance. A non-positive amount is no movement and publishes nothing, which is what
    /// makes a repeated readback of an already reported change harmless.</summary>
    private void Advance(Life life, long amount, ReloadAmountSource source)
    {
        if (amount <= 0 || !Live(life)) return;
        life.Transferred += amount;
        life.Baseline += checked((int)amount);
        Publish("reload_transferred", life, ReloadInventoryContract.ReloadTransferredBinding, source,
            () => new RuntimeEvent(
                "gtfo.weapon.reload:" + Number(_epoch) + ":" + Number(checked(++_sequence)),
                ReloadInventoryContract.ReloadTransferredBinding, _epoch, Tick(), "gtfo.equipment:" + life.Equipment.Id,
                RuntimeJson.From(new { actor = (EntityReference?)life.Actor, equipment = life.Equipment,
                    amount = checked((int)amount) })));
    }

    /// <summary>Opens the life, or confirms the one already open: the flag's setter and the inventory's entry point
    /// are one event, so the second to arrive must not open a second life. A life with no owner to name is not
    /// opened at all — every reload row's `actor` port is a required entity, and a fact whose actor this machine
    /// could not resolve would be a fact the runtime refuses rather than one worth publishing.</summary>
    private void Confirm(EntityReference equipment, object item)
    {
        if (_open.TryGetValue(equipment.Id, out var open)) { open.Confirmed = true; return; }
        var baseline = _native.Clip(item);
        var actor = _native.OwnerOf(item);
        if (baseline == null || actor == null) return;
        var life = new Life(equipment, actor, baseline.Value) { Confirmed = true };
        _open[equipment.Id] = life;
        Publish("reload_started", life, ReloadInventoryContract.ReloadStartedBinding, null,
            () => new RuntimeEvent(
                "gtfo.weapon.reload:" + Number(_epoch) + ":" + Number(checked(++_sequence)),
                ReloadInventoryContract.ReloadStartedBinding, _epoch, Tick(), "gtfo.equipment:" + equipment.Id,
                RuntimeJson.From(new { actor = (EntityReference?)life.Actor, equipment = life.Equipment })));
    }

    /// <summary>Closes one life. A life this machine never saw the item's own flag confirm is dropped without a
    /// fact, and so is a life that moved no ammunition: `reload_completed` is the only ending this family
    /// publishes, and it is published only for a reload that really transferred something.</summary>
    private void Close(EntityReference equipment)
    {
        if (!_open.TryGetValue(equipment.Id, out var life)) return;
        _open.Remove(equipment.Id);
        if (!life.Confirmed || life.Transferred <= 0) return;
        Publish("reload_completed", life, ReloadInventoryContract.ReloadCompletedBinding, null,
            () => new RuntimeEvent(
                "gtfo.weapon.reload:" + Number(_epoch) + ":" + Number(checked(++_sequence)),
                ReloadInventoryContract.ReloadCompletedBinding,
                _epoch, Tick(), "gtfo.equipment:" + equipment.Id,
                RuntimeJson.From(new { actor = (EntityReference?)life.Actor, equipment = life.Equipment })));
    }

    /// <summary>Every observation enters through here: the world epoch and the authority gate are read once, and a
    /// world change drops every open life without a fact. A world transition is a hard boundary, so a reload that
    /// spanned it has no ending this machine can date.</summary>
    private bool Enter(object item, out EntityReference key)
    {
        key = null!;
        SyncWorld();
        if (!Ready()) return false;
        var equipment = _native.EquipmentOf(item);
        if (equipment == null || equipment.WorldEpoch != _epoch) return false;
        key = equipment;
        return true;
    }

    /// <summary>The world epoch this observer's open lives belong to, and the drop a change causes. Called at the
    /// front of every observation, so no fact can be published under an epoch the life was not opened in.</summary>
    internal void SyncWorld()
    {
        var world = _authoritative() ? _world() : -1;
        if (world == _epoch) return;
        if (_epoch != -1 && _open.Count != 0)
            _info("weapon.reload-lives-cleared world=" + Number(_epoch) + " count=" + Number(_open.Count)
                + " reason=" + (world == -1 ? "not-authoritative" : "world-changed"));
        _open.Clear();
        _epoch = world;
    }

    /// <summary>Drops every open life for a disposal. Nothing is published: the machine is going away.</summary>
    internal void Clear()
    {
        if (_open.Count != 0)
            _info("weapon.reload-lives-cleared world=" + Number(_epoch) + " count=" + Number(_open.Count)
                + " reason=dispose");
        _open.Clear();
        _epoch = -1;
    }

    /// <summary>A life whose equipment is no longer current is not live. The check is the identity table's own,
    /// which is the same answer the runtime gives a plan about that reference.</summary>
    private bool Live(Life life) => life.Equipment.WorldEpoch == _epoch && _isCurrent(life.Equipment);

    /// <summary>Publishes one row's fact, and builds it only when somebody subscribes to the row. Nothing is
    /// listening on a closed binding: the kernel would answer `no-consumer` for the event, so it is never reached
    /// and the fact is not reported — and the event value itself, its id, its ports and its payload, is work no
    /// consumer would ever read. Reading the gate here rather than at the call site is what keeps every publish
    /// point from having to remember the order.</summary>
    private void Publish(string kind, Life life, string binding, ReloadAmountSource? source, Func<RuntimeEvent> fact)
    {
        if (_unsubscribed(binding)) return;
        var built = fact();
        try { _publish(built); }
        catch (RuntimeContractException error)
        { _report("weapon.reload-fact-rejected: " + kind + " " + error.Code); return; }
        _info("weapon.reload-fact kind=" + kind + " equipment=" + life.Equipment.Id
            + " transferred=" + Number(life.Transferred)
            + (source == null ? "" : " source=" + source.Value.ToString().ToLowerInvariant()));
    }

    private bool Ready() => _epoch != -1;

    private long Tick() => Math.Max(0, _tick());

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

// ---------------------------------------------------------------------------------------------------------------------
