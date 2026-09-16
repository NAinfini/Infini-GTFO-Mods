using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;
using Gear;
using Player;
using SNetwork;

namespace ForgeWeapon.Native;

/// <summary>Native equipment observation lifetime on the host's single Runtime, started by <see cref="Plugin"/>.
/// Owners are never created here: the adapter asks the SDK for the gtfo.player references ForgeMap records,
/// and the identity session re-checks them through the same SDK entry.</summary>
internal sealed class WeaponNativeSession : IDisposable
{
    internal static WeaponNativeSession? Current { get; private set; }
    internal static string? LastCleanupDiagnostic { get; private set; }
    private readonly RuntimeKernel _kernel;
    private readonly Action<string> _report;
    private readonly Action _removeHooks;
    private readonly int _threadId = Environment.CurrentManagedThreadId;
    private bool _disposed, _faulted, _unwired;
    internal EquipmentNativeAdapter Adapter { get; private set; } = null!;
    internal EquipmentIdentitySession Identity { get; private set; } = null!;
    internal WeaponCombatObserver Combat { get; private set; } = null!;
    /// <summary>The attack instance around each native firing body: one scope per trigger pull, accepted when
    /// the body registers the shot and closed when it returns. It reads the same equipment life the shot
    /// observer counts and never counts a shot itself.</summary>
    internal AttackInstanceModule Attack { get; private set; } = null!;
    /// <summary>The deployed-device facts: one sentry firing pair, the glue gun's two launch bodies and the mine's
    /// own trigger, read against the placement table this session's adapter owns.</summary>
    internal WeaponDeployableFacts Facts { get; private set; } = null!;
    /// <summary>The holder provider: the reload and clip-set rows this package executes on the machine that holds
    /// the equipment, registered as its own module so a plan can address it.</summary>
    internal WeaponHolderModule Holder { get; private set; } = null!;
    /// <summary>The melee hits two native bodies report, deduped per target in the one observer.</summary>
    internal WeaponMeleeHitFacts Melee { get; private set; } = null!;
    /// <summary>The ammunition pair's two bodies: one write on a player's own pool with the readback that proves
    /// it, and the refusal a pool this machine does not own gets. Built before the registration for the same
    /// reason the mark row is.</summary>
    internal WeaponSupplyAdapter Supply { get; private set; } = null!;
    /// <summary>The inventory pair's bodies, give and consume. There is no third one: the node list has no drop
    /// node, so the drop row was deleted at integration (ruling 110.5) and its body followed it (ruling 133.3).
    /// The adapter is built before the registration for the same reason the mark row is.</summary>
    internal InventoryActionAdapter Inventory { get; private set; } = null!;
    /// <summary>The instance-override writer: the ledger every accepted field set is remembered in, and the only
    /// code in the package that writes a weapon instance's own archetype block.</summary>
    internal WeaponOverrideApplier Overrides { get; private set; } = null!;
    /// <summary>The three instance-override row bodies, answered on the machine the command reaches.</summary>
    internal WeaponActionAdapter OverrideActions { get; private set; } = null!;
    /// <summary>The authored gear-part poses, read from disk once in <see cref="Start"/> and never reloaded: the
    /// applier only ever writes what this snapshot holds.</summary>
    internal GearPartTransformApplier GearParts { get; private set; } = null!;
    /// <summary>The loadout policies accepted from disk, and the one pool narrowing this package performs. It is
    /// created last because it is what the hooks it installs reach for.</summary>
    internal GearLoadoutSession Loadout { get; private set; } = null!;
    internal bool Faulted => _faulted;
    internal string? LastFault { get; private set; }
    internal string? LastReporterFailure { get; private set; }

    private WeaponNativeSession(RuntimeKernel kernel, Action<string> report, Action removeHooks)
    { _kernel = kernel; _report = report; _removeHooks = removeHooks; }

    internal static WeaponNativeSession Start(RuntimeKernel kernel, RuntimeLogLevel logLevel, Func<bool> canExecute,
        Action<string> report, Action<string> info, Action installHooks, Action removeHooks, string gearPartsRoot)
    {
        ArgumentNullException.ThrowIfNull(kernel); ArgumentNullException.ThrowIfNull(canExecute);
        ArgumentNullException.ThrowIfNull(report); ArgumentNullException.ThrowIfNull(info);
        ArgumentNullException.ThrowIfNull(installHooks); ArgumentNullException.ThrowIfNull(removeHooks);
        ArgumentNullException.ThrowIfNull(gearPartsRoot);
        if (Current != null)
            throw new InvalidOperationException("Weapon native observation is single-instance per process.");
        if (!kernel.IsRegistrationOpen)
            throw new InvalidOperationException("Weapon must register during dependent plugin Load, before Runtime startup.");
        var session = new WeaponNativeSession(kernel, report, removeHooks);
        session._unwired = true;
        bool hooksAttempted = false;
        try
        {
            session.Adapter = new EquipmentNativeAdapter(kernel, () => !session._faulted && canExecute(), report, info);
            // The bodies this provider executes itself, built before the registration because the registration
            // carries them: the ammunition pair, the three instance-override rows and the inventory pair. Each
            // reads the player behind a `gtfo.player` reference through the adapter's own lookup, which confirms
            // every candidate against the owning domain and never derives a player here.
            session.Supply = new WeaponSupplyAdapter(kernel, () => !session._faulted && canExecute(),
                session.Adapter.PlayerOf, report);
            session.Inventory = new InventoryActionAdapter(kernel, () => !session._faulted && canExecute(),
                WeaponSupplyItems.TryResolvePocketItem, session.Adapter.PlayerOf, report);
            session.Overrides = new WeaponOverrideApplier(session.Adapter.WeaponOf, session.Adapter.EntityOf, report, info);
            session.OverrideActions = new WeaponActionAdapter(kernel, () => !session._faulted && canExecute(),
                session.Overrides, report);
            // Duplicate provider registration throws here, before any detour is attempted.
            session.Identity = new EquipmentIdentitySession(kernel, logLevel, session.Adapter.IsNativeCurrent, kernel.IsEntityCurrent,
                equipmentObserver: session.Adapter.ObserveEquipment, deployedObserver: session.Adapter.ObserveDeployable,
                deployedCurrent: session.Adapter.HoldsDeployable,
                gearBlockMatcher: session.Adapter.MatchesGearBlock,
                module: ModuleDefinition.Create(
                    ammoAdd: session.Supply.HandleAdd, ammoConsume: session.Supply.HandleConsume,
                    overrides: WeaponOverrideContract.Handlers(session.OverrideActions.FireRate,
                        session.OverrideActions.Spread, session.OverrideActions.Recoil),
                    inventoryGive: session.Inventory.HandleGive, inventoryConsume: session.Inventory.HandleConsume),
                observe: value => ObserveOverrides(session.Overrides.Ledger, value));
            session.Adapter.Attach(session.Identity);
            session.Combat = new WeaponCombatObserver(session.Adapter, () => kernel, report, info);
            // The attack scope shares the shot observer's own equipment life and reads its counters; it keeps no
            // tally of shots and no ledger of hits of its own.
            session.Attack = new AttackInstanceModule(session.Adapter, () => kernel, report, info);
            // The deployed-device facts answer from the same placement table the life-cycle observation uses, so
            // the adapter's own lookups are handed in rather than a second index kept here.
            session.Facts = WeaponDeployableFacts.Attach(kernel, () => !session._faulted && canExecute(),
                pointer => session.Adapter.PlacementOf(pointer)?.Entity, session.Adapter.EntityOf,
                session.Adapter.OwnerOf, reference => reference != null && kernel.IsEntityCurrent(reference),
                session.Adapter.Publish, report, info, session.Identity.Unsubscribed);
            // The melee hit reads the adapter's authority gate and publication path, exactly like the shot facts.
            session.Melee = WeaponMeleeHitFacts.Attach(session.Adapter, kernel, report, info);
            // The reload and inventory observers behind the reload family's hooks: they publish through the same
            // equipment registration, and their hook list is installed with the rest by the caller's own loop.
            ReloadInventoryHooks.Install(kernel, session.Identity, session.Adapter, report, info);
            // The holder tier's own provider: the reload and clip-set rows are registered here, and the channel
            // statics below are what its session resolver and its two handlers answer through.
            session.Holder = WeaponHolderModule.Register(kernel, logLevel, report);
            WeaponHolderChannel.Adapter = session.Adapter;
            WeaponHolderChannel.Kernel = kernel;
            WeaponHolderChannel.CanExecute = () => !session._faulted && canExecute();
            WeaponHolderChannel.LocalSession = () =>
            {
                if (!SNet.HasLocalPlayer || SNet.LocalPlayer == null) return "";
                try { return SNet.LocalPlayer.PlayerSlotIndex().ToString(); }
                catch (Exception) { return ""; }
            };
            // One reading of the package data, before any hook can ask for it; a rejected file is refused on its
            // own and reported once, and the process keeps working with the files that were accepted.
            var gearParts = GearPartTransformData.Load(gearPartsRoot, report);
            session.GearParts = new GearPartTransformApplier(gearParts, report, info);
            // The loadout policies are read from disk once, in this same pre-hook window and from the same install
            // root: a file that fails its own pins or bytes is refused and reported on its own, and the process
            // keeps working with the policies that were accepted. The hook bodies are reached only through `Guard`,
            // which stays inert until this assignment, so a hook cannot run over a half-built session.
            var loadouts = LoadoutPolicyData.Load(gearPartsRoot, LoadoutPolicyWiring.PinSource(),
                message => report(message));
            session.Loadout = new GearLoadoutSession(loadouts, report, info);
            Current = session;
            hooksAttempted = true; installHooks();
            if (gearParts.BlockCount != 0) info("weapon.gear-part-blocks-loaded count=" + gearParts.BlockCount);
            if (loadouts.PolicyCount != 0) info("weapon.loadout-policies-loaded count=" + loadouts.PolicyCount);
            session._unwired = false;
            return session;
        }
        catch (Exception original)
        {
            session._faulted = true;
            var errors = new List<Exception>();
            if (hooksAttempted) Cleanup(removeHooks, errors);
            // A narrowed pool belongs to the game, so a failed start puts it back before the session disappears.
            if (session.Loadout != null) Cleanup(session.Loadout.Restore, errors);
            // The observers and the holder registration go with the session: a start that failed after the holder
            // was registered must not leave a provider behind that no session answers for.
            Cleanup(ClearChannel, errors);
            Cleanup(ReloadInventoryHooks.Clear, errors);
            if (session.Melee != null) Cleanup(session.Melee.Dispose, errors);
            if (session.Facts != null) Cleanup(session.Facts.Dispose, errors);
            if (session.Holder != null) Cleanup(session.Holder.Dispose, errors);
            if (session.Identity != null) Cleanup(session.Identity.Dispose, errors);
            if (ReferenceEquals(Current, session)) Current = null;
            session._disposed = true;
            if (errors.Count != 0) PreserveCleanupFailure(original, "ForgeWeapon.CleanupFailures", new AggregateException(errors), report);
            throw;
        }
    }

    /// <summary>
    /// The instance-override ledger's own level lifecycle, observed through the identity session's one
    /// subscription. A world the game starts or ends is the unconditional revert ruling 84 makes it: the previous
    /// epoch's entries are given up and every instance still held is restored. A checkpoint reload is the same
    /// revert without a new epoch — the level's gear is rebuilt from the game's data, so an override the running
    /// world accepted no longer exists on any instance — and a runtime that fails or stops ends the world
    /// outright. Nothing here reads the game: the ledger keeps only what a plan asked for.
    /// </summary>
    private static void ObserveOverrides(WeaponOverrideLedger ledger, RuntimeLifecycleEvent value)
    {
        if (value.Kind is RuntimeLifecycleKind.Snapshot or RuntimeLifecycleKind.WorldChanged)
            ledger.BeginWorld(value.Current.WorldEpoch);
        if (value.Kind == RuntimeLifecycleKind.CheckpointRestored) ledger.RestoreAll();
        if (value.Current.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) ledger.EndWorld();
    }

    internal void Guard(Action<WeaponNativeSession> callback)
    {
        CheckThread();
        ArgumentNullException.ThrowIfNull(callback);
        if (_disposed || _faulted || _unwired || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        try { callback(this); }
        catch (Exception error)
        {
            // Contract rejections are handled per observation inside the adapter; anything reaching here is unexpected,
            // including a missing gtfo.player instance lookup, which must stop observation rather than record ownerless items.
            _faulted = true;
            var errors = new List<Exception>();
            Cleanup(Adapter.Clear, errors);
            LastFault = Describe(error);
            try { _report("Weapon equipment observation disabled until restart: " + LastFault + "; cleanupErrors=" + errors.Count); }
            catch (Exception reporting) { LastReporterFailure = reporting.GetType().Name; }
        }
    }

    /// <summary>The loading hook's body: the pool narrowing is the policy's own business and reports its own
    /// failures, so it never faults the equipment observation this session also carries. Where the game keeps its
    /// pool is read here — the one place that knows — and every access after this hands the narrowing one live list
    /// of it, so the lists it rewrites are the game's own instances and never a copy.</summary>
    internal void GuardNarrow()
    {
        CheckThread();
        if (_disposed || _faulted || _unwired || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return;
        var live = GearManager.Current?.m_gearPerSlot;
        if (live == null) return;
        Loadout.Narrow(index => index < live.Length && live[index] != null ? new Il2CppGearPoolSlot(live[index]!) : null);
    }

    /// <summary>The projection hook's body: the offer as the game just built it, narrowed to the policy's gears or
    /// handed back as it is. The same gate as <see cref="Guard"/> applies, so a session that is not wired yet, is
    /// shutting down, or has already faulted offers nothing and the game's own array is published unchanged.</summary>
    internal IReadOnlyList<GearIDRange>? Offer(InventorySlot slot, IReadOnlyList<GearIDRange> offered)
    {
        CheckThread();
        if (_disposed || _faulted || _unwired || _kernel.StartupState is RuntimeStartupState.Failed or RuntimeStartupState.Stopped) return null;
        return Loadout.Offer(slot, offered);
    }

    public void Dispose()
    {
        CheckThread();
        if (_disposed) return;
        // Unregister first; Runtime rejects disposal during dispatch and the session stays intact.
        Identity.Dispose();
        // The holder provider is a second registration and goes with the first, before the hooks that could reach
        // it are removed.
        Holder.Dispose();
        _faulted = true;
        // Nothing answers the holder channel once the session is going away, so its statics are cleared: a
        // resolver asked during shutdown answers null rather than a half-disposed adapter.
        ClearChannel();
        // The reload and inventory observers answer nothing once the session is going away, and the hooks that
        // reach them are removed last, so a hook firing in that window finds no observer rather than a half-dead one.
        ReloadInventoryHooks.Clear();
        Melee.Dispose();
        Facts.Dispose();
        // The narrowed gear pool is the game's own state, so it is put back before the hooks that could narrow it
        // again are removed; nothing is reported here because the process is going away.
        Loadout.Dispose();
        Adapter.Clear();
        var errors = new List<Exception>();
        Cleanup(_removeHooks, errors);
        _disposed = true;
        if (ReferenceEquals(Current, this)) Current = null;
        if (errors.Count != 0) throw new AggregateException("Weapon shutdown cleanup failed.", errors);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Weapon session requires its owning simulation thread.");
    }

    /// <summary>The holder channel's statics, put back to the answers a machine with no session gives. It is one
    /// call because both teardown paths — a failed start and a disposal — have to leave the same inert state
    /// behind, and a resolver or handler reached afterwards must find no adapter to write through.</summary>
    private static void ClearChannel()
    {
        WeaponHolderChannel.Adapter = null;
        WeaponHolderChannel.Kernel = null;
        WeaponHolderChannel.CanExecute = () => false;
        WeaponHolderChannel.LocalSession = () => "";
    }

    private static string Describe(Exception error)
    {
        string message;
        try { message = error.Message ?? ""; }
        catch (Exception unavailable) { message = "<message unavailable: " + unavailable.GetType().Name + ">"; }
        var description = error.GetType().Name + ": " + message;
        return description.Length <= 2048 ? description : description[..2048];
    }

    internal static void PreserveCleanupFailure(Exception original, string key, Exception cleanup, Action<string> report)
    {
        // One bounded diagnostic. Hostile exception accessors or a broken logger must not replace the primary exception.
        string diagnostic = key + ": " + Describe(cleanup);
        try { original.Data[key] = cleanup; }
        catch (Exception attachment) { diagnostic += "; attachment=" + attachment.GetType().Name; }
        try { report(diagnostic); }
        catch (Exception reporter) { diagnostic += "; reporter=" + reporter.GetType().Name; }
        LastCleanupDiagnostic = diagnostic.Length <= 4096 ? diagnostic : diagnostic[..4096];
    }

    private static void Cleanup(Action action, List<Exception> errors)
    {
        try { action(); }
        catch (Exception error) { errors.Add(error); }
    }
}
