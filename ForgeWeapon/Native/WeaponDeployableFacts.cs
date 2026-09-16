using System;
using System.Collections.Generic;
using System.Globalization;
using ForgeRuntime.Framework;
using Gear;
using HarmonyLib;
using UnityEngine;

namespace ForgeWeapon.Native;

/// <summary>
/// The three deployed-device facts of <see cref="WeaponDeployableFactsContract"/>, read from the bodies that
/// really produce them and published the way this package's other facts are.
///
/// A sentry shot is the firing component's own master update, and the fact is the ammunition that update moved:
/// the device core's own <c>Ammo</c> read before and after the body. A drop is one shot; a value that reached zero
/// is the depletion. Neither is inferred from a clip this package tracks, and the instance's own
/// <c>OnBulletFired</c>/<c>OnAmmoDepleated</c> callbacks are deliberately not hooked — they are presentation
/// actions the instance assigns to itself, while the ammunition is the fact worth reading.
///
/// A glue-gun shot is the hand-held tool's own launch bodies, deduped per frame so a burst of glue reads as the
/// one trigger pull it was. That is the half of `e-glue` this package owns; the enemy that ends up stuck is the
/// enemy domain's own fact.
///
/// A detonation is the mine's own trigger, and the position is the device's own transform rather than the trigger's
/// parameter, which is a detection range and not a place.
///
/// Every hook is inert unless the session is live, exactly like the package's other observers: the bodies below
/// only choose when to read.
/// </summary>
internal sealed class WeaponDeployableFacts : IDisposable
{
    /// <summary>The live observer, or null before the session is built and after it is disposed.</summary>
    internal static WeaponDeployableFacts? Current { get; private set; }

    private readonly RuntimeKernel _kernel;
    private readonly Func<bool> _canExecute;
    private readonly Func<IntPtr, EntityReference?> _deployedOf;
    private readonly Func<Item?, EntityReference?> _equipmentOf;
    private readonly Func<Item?, EntityReference?> _ownerOf;
    private readonly Func<EntityReference?, bool> _isCurrent;
    private readonly FactRouter _publish;
    private readonly Func<string, bool> _unsubscribed;
    private readonly Action<string> _report, _info;
    private readonly Dictionary<IntPtr, float> _sentryAmmo = new();
    private readonly Dictionary<IntPtr, long> _glueFrame = new();
    private long _world = -1, _sequence;
    private bool _disposed;

    internal WeaponDeployableFacts(RuntimeKernel kernel, Func<bool> canExecute, Func<IntPtr, EntityReference?> deployedOf,
        Func<Item?, EntityReference?> equipmentOf, Func<Item?, EntityReference?> ownerOf, Func<EntityReference?, bool> isCurrent,
        FactRouter publish, Action<string> report, Action<string> info, Func<string, bool> unsubscribed)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _canExecute = canExecute ?? throw new ArgumentNullException(nameof(canExecute));
        _deployedOf = deployedOf ?? throw new ArgumentNullException(nameof(deployedOf));
        _equipmentOf = equipmentOf ?? throw new ArgumentNullException(nameof(equipmentOf));
        _ownerOf = ownerOf ?? throw new ArgumentNullException(nameof(ownerOf));
        _isCurrent = isCurrent ?? throw new ArgumentNullException(nameof(isCurrent));
        _publish = publish ?? throw new ArgumentNullException(nameof(publish));
        _unsubscribed = unsubscribed ?? throw new ArgumentNullException(nameof(unsubscribed));
        _report = report ?? throw new ArgumentNullException(nameof(report));
        _info = info ?? throw new ArgumentNullException(nameof(info));
    }

    /// <summary>Installs the observer. Called by the session before its hooks are patched, so no hook can run
    /// over a half-built observer.</summary>
    internal static WeaponDeployableFacts Attach(RuntimeKernel kernel, Func<bool> canExecute,
        Func<IntPtr, EntityReference?> deployedOf, Func<Item?, EntityReference?> equipmentOf,
        Func<Item?, EntityReference?> ownerOf, Func<EntityReference?, bool> isCurrent, FactRouter publish,
        Action<string> report, Action<string> info, Func<string, bool> unsubscribed)
    {
        if (Current != null) throw new InvalidOperationException("Deployed-device facts are single-instance.");
        var facts = new WeaponDeployableFacts(kernel, canExecute, deployedOf, equipmentOf, ownerOf, isCurrent, publish,
            report, info, unsubscribed);
        Current = facts;
        return facts;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sentryAmmo.Clear();
        _glueFrame.Clear();
        if (ReferenceEquals(Current, this)) Current = null;
    }

    /// <summary>The ammunition one firing update started from, remembered between the body's own prefix and its
    /// postfix. Keyed by the component, so two sentries firing in the same frame never read each other's value.</summary>
    internal void RememberSentryAmmo(IntPtr firing, float ammo) => _sentryAmmo[firing] = ammo;

    /// <summary>
    /// One sentry firing update, read as the ammunition the body moved. A drop is one shot, and the shot's own
    /// `ammo` port is the value after it. A body that ended at zero publishes both facts, in that order: the shot
    /// happened, and it was the last one the device had.
    /// </summary>
    internal void SentryFired(IntPtr firing, IntPtr device, float after)
    {
        if (!Authoritative()) return;
        if (!_sentryAmmo.Remove(firing, out var before) || after >= before) return;
        var deployed = Placed(device, WeaponDeployableFactsContract.FiredBinding);
        if (deployed == null) return;
        Publish(WeaponDeployableFactsContract.FiredBinding, RuntimeJson.From(new
        {
            device = deployed,
            equipment_kind = "sentry_gun",
            equipment_action = "primary",
            ammo = (int)MathF.Max(0f, after)
        }));
        if (after <= 0f) Publish(WeaponDeployableFactsContract.AmmoDepletedBinding,
            RuntimeJson.From(new { device = deployed }));
    }

    /// <summary>One glue-gun launch. A burst fires several projectiles in one trigger pull, so a second launch
    /// inside the same frame is the same shot and is not published again.</summary>
    internal void GlueFired(Item? tool)
    {
        if (tool == null || !Authoritative()) return;
        var pointer = tool.Pointer;
        var frame = Time.frameCount;
        if (_glueFrame.TryGetValue(pointer, out var last) && last == frame) return;
        _glueFrame[pointer] = frame;
        // The hand-held glue gun is an equipment life, not a placement: its device port is the tool the launch
        // came from, and the actor is that tool's own owner.
        var device = EquipmentOf(tool);
        if (device == null) return;
        Publish(WeaponDeployableFactsContract.FiredBinding, RuntimeJson.From(new
        {
            device,
            actor = (EntityReference?)_ownerOf(tool),
            equipment_kind = "glue_gun",
            equipment_action = "primary"
        }));
    }

    /// <summary>One mine detonation, at the device's own position. The trigger component is not the item: the
    /// instance the placement table tracks is reached through the component's own core, and a component whose core
    /// is already gone publishes nothing.</summary>
    internal void Detonated(MineDeployerInstance_Detonate_Explosive? trigger)
    {
        var device = trigger == null ? null : trigger.m_core?.TryCast<Item>();
        if (device == null || !Authoritative()) return;
        var position = Position(device);
        if (position == null) return;
        var deployed = Placed(device.Pointer, WeaponDeployableFactsContract.DetonatedBinding);
        if (deployed == null) return;
        Publish(WeaponDeployableFactsContract.DetonatedBinding,
            RuntimeJson.From(new { device = deployed, position }));
    }

    /// <summary>Finite, readable world position of a native item, or null. A destroyed Unity object reads as null
    /// through its own equality and its transform then throws, so both are checked before the components are read;
    /// a non-finite component is refused rather than published.</summary>
    private static double[]? Position(Item? instance)
    {
        var transform = instance == null ? null : instance.transform;
        if (transform == null) return null;
        var position = transform.position;
        if (!float.IsFinite(position.x) || !float.IsFinite(position.y) || !float.IsFinite(position.z)) return null;
        return new[] { (double)position.x, (double)position.y, (double)position.z };
    }

    /// <summary>The equipment life a hand-held tool is, asked of the same slot table the placement facts use: the
    /// materialized item's own slot, which the identity session already recorded. A tool this build has not
    /// recorded answers null and its fact is not published, because the row's `device` port is required.</summary>
    private EntityReference? EquipmentOf(Item tool) => _equipmentOf(tool);

    /// <summary>The deployed instance a native object is, or null. Only a reference the session's own resolver
    /// still answers for is returned, so a device whose life already ended can never reach a fact.</summary>
    private EntityReference? Placed(IntPtr device, string binding)
    {
        var reference = _deployedOf(device);
        if (reference != null && _isCurrent(reference)) return reference;
        Say("weapon." + binding + ": the subject is not a current deployable instance, so no fact was published.");
        return null;
    }

    private void Publish(string binding, System.Text.Json.JsonElement outputs)
    {
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this call
        // is about to build, so the event value is never built.
        if (_unsubscribed(binding)) return;
        var world = _kernel.WorldEpoch;
        var tick = Math.Max(0, _kernel.CurrentTick);
        var id = "gtfo.equipment.fired:" + Number(world) + ":" + Number(checked(++_sequence));
        var result = _publish(new RuntimeEvent(id, binding, world, tick, "gtfo.world:" + Number(world), outputs));
        if (result.Status == "rejected") _report("weapon.device-fact-rejected: " + result.Code + " binding=" + binding);
        else _info("weapon.device-fact binding=" + binding + " status=" + result.Status + " id=" + id);
    }

    /// <summary>The world this observer's facts belong to, read from the kernel on every use so a fact can never
    /// be stamped with an epoch the kernel has already left. The tables are keyed by native pointers of objects in
    /// one world, so they are dropped with it rather than carried into the next one.</summary>
    private bool Authoritative()
    {
        var world = _kernel.WorldEpoch;
        if (world != _world) { _world = world; _sentryAmmo.Clear(); _glueFrame.Clear(); _sequence = 0; }
        if (!_canExecute()) return false;
        var state = _kernel.Lifecycle;
        return state.StartupState == RuntimeStartupState.Ready && state.IsHost == true;
    }

    private void Say(string message)
    {
        try { _report(message); } catch (Exception) { /* a reporter that fails must not stop observation. */ }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}

// The sentry's own firing update, read as the pair of ammunition values around it: the prefix remembers what the
// device had, the postfix reads what it has and publishes the difference as one shot. The update is the master's
// own body — the client copy runs `UpdateFireClient` — so the fact is published where the ammunition really moves.
[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireMaster))]
internal static class SentryFireStarted
{
    [HarmonyPrefix, HarmonyPriority(Priority.First)]
    private static void Prefix(SentryGunInstance_Firing_Bullets __instance)
        => WeaponNativeSession.Current?.Guard(_ => WeaponDeployableFacts.Current?.RememberSentryAmmo(__instance.Pointer,
            __instance.m_core == null ? 0f : __instance.m_core.Ammo));
}

[HarmonyPatch(typeof(SentryGunInstance_Firing_Bullets), nameof(SentryGunInstance_Firing_Bullets.UpdateFireMaster))]
internal static class SentryFired
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(SentryGunInstance_Firing_Bullets __instance)
        => WeaponNativeSession.Current?.Guard(_ => WeaponDeployableFacts.Current?.SentryFired(__instance.Pointer,
            __instance.m_core == null ? IntPtr.Zero : __instance.m_core.Pointer,
            __instance.m_core == null ? 0f : __instance.m_core.Ammo));
}

// The glue gun's two launch bodies. Either one is a trigger pull; the observer dedupes a burst's several
// projectiles into the one shot they were.
[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.FireBurst))]
internal static class GlueGunBurst
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(GlueGun __instance)
        => WeaponNativeSession.Current?.Guard(_ => WeaponDeployableFacts.Current?.GlueFired(__instance));
}

[HarmonyPatch(typeof(GlueGun), nameof(GlueGun.FireSingle))]
internal static class GlueGunSingle
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(GlueGun __instance)
        => WeaponNativeSession.Current?.Guard(_ => WeaponDeployableFacts.Current?.GlueFired(__instance));
}

// The mine's own trigger. `DoExplode` is the damage half and runs from this one, so hooking the trigger publishes
// once per detonation rather than once per damage application.
[HarmonyPatch(typeof(MineDeployerInstance_Detonate_Explosive), nameof(MineDeployerInstance_Detonate_Explosive.TriggerDetonate))]
internal static class MineDetonated
{
    [HarmonyPostfix, HarmonyPriority(Priority.Last)]
    private static void Postfix(MineDeployerInstance_Detonate_Explosive __instance)
        => WeaponNativeSession.Current?.Guard(_ => WeaponDeployableFacts.Current?.Detonated(__instance));
}
