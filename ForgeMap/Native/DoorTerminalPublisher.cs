using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using ForgeMap;
using ForgeRuntime.Framework;

namespace ForgeMap.Native;

/// <summary>Publishes the door and terminal interaction facts this slice adds, and nothing else. The publisher is
/// handed the one Map registration by the session and answers with whether the kernel queued the event, so the
/// native callbacks stay one line each and a test can drive the same body without a Harmony patch.
///
/// Every fact carries an event id built from the same three things the package's existing map-object facts use —
/// the world, the subject's own address and the subject's transition number — so two facts about one door are
/// two events and a repeated sync of one state is none. The state key is what the transition counts: it is the
/// native values the fact reports, so a callback that fires twice for one state publishes once.</summary>
internal sealed class DoorTerminalPublisher
{
    /// <summary>The fact kind the terminal command row carries. It is the catalog's existing
    /// `forge.trigger.interaction.terminal_command` row, whose shape this provider already declares. The name is
    /// the map-object module's own fact kind rather than a second spelling of it — the module is a different
    /// assembly and names it `internal`, so this half states the same text beside the capability id it belongs
    /// to, and a test asserts the two agree.</summary>
    internal const string CommandFact = "terminal_command";

    private readonly RuntimeKernel _kernel;
    private readonly RuntimeModuleHandle _registration;
    private readonly IReadOnlyDictionary<string, RuntimeSubscriptionGate> _gates;
    private readonly Func<bool> _authority;
    private readonly Action<string> _report;
    private readonly DoorTerminalFactLedger _ledger = new();
    private readonly HashSet<string> _reported = new(StringComparer.Ordinal);

    internal DoorTerminalPublisher(RuntimeKernel kernel, RuntimeModuleHandle registration, Func<bool> authority,
        Action<string> report)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
        _gates = registration.SubscriptionGates();
        _authority = authority ?? throw new ArgumentNullException(nameof(authority));
        _report = report ?? throw new ArgumentNullException(nameof(report));
    }

    /// <summary>The facts this publisher handed to the kernel with status `queued`. A client, an unreadable
    /// instance and a repeated state never contribute, which is what makes the count assertable.</summary>
    internal long Published { get; private set; }

    /// <summary>How many state keys the ledger holds, for a test to assert a world transition released them.</summary>
    internal int Tracked => _ledger.Count;

    /// <summary>The last queued state of one fact for one address, or null when nothing was queued for it. It
    /// answers what a case asserts about the ports a fact carried without an event sink.</summary>
    internal (string Binding, string EventId, string StateKey, string Outputs)? Last(string fact, MapObjectReference address)
        => _ledger.Last(fact + "|" + MapObjectModule.EntityKind + ":" + address);

    /// <summary>The same answer for a fact whose subject is a runtime entity rather than a map address, which is
    /// how the two weak-door stages are keyed.</summary>
    internal (string Binding, string EventId, string StateKey, string Outputs)? Last(string fact, EntityReference subject)
        => _ledger.Last(fact + "|" + subject.Id);

    /// <summary>Drops the per-world state. A world transition invalidates every address a key could name, so
    /// the ledger is emptied rather than kept.</summary>
    internal void BeginWorld() => _ledger.Clear();

    /// <summary>One scan transition. The phase is the transition and the status is the native value the phase
    /// was derived from, so a plan can read either.</summary>
    internal bool Scan(MapObjectReference address, int phase, int status)
        => Publish(DoorTerminalEventContract.DoorScanFact, address,
            phase.ToString(CultureInfo.InvariantCulture) + ":" + status.ToString(CultureInfo.InvariantCulture), Payload(
                ("door", RuntimeJson.From(Reference(address))),
                ("phase", RuntimeJson.From(phase)),
                ("status", RuntimeJson.From(MapObjectDoorStatus.Name(status)))));

    /// <summary>One stage of a weak door: the door's own runtime entity, the zone it stands in and its world
    /// position, plus the attacker when the replication callback named one. The zone is published as the `zone`
    /// resource reference every other zone reader of this package answers with, and the door entity's namespace
    /// is the weak door's own, because a weak door has no map address to be named by.</summary>
    internal bool WeakDoor(EntityReference door, string phase, ResourceRef? zone, double[]? position,
        EntityReference? attacker)
    {
        int index = DoorTerminalEventContract.DoorPhaseIndex(phase);
        if (index < 0) return false;
        return Publish(DoorTerminalEventContract.DoorBrokenFact, door, "weak:" + phase, Payload(
            ("door", RuntimeJson.From(door)),
            ("phase", RuntimeJson.From(index)),
            ("zone", zone is { } resource ? RuntimeJson.From(resource) : (JsonElement?)null),
            ("position", position is { } point ? RuntimeJson.From(point) : (JsonElement?)null),
            ("attacker", attacker is { } player ? RuntimeJson.From(player) : (JsonElement?)null)));
    }

    /// <summary>One lock release. The cause is the interaction kind and the lock kind is the weak lock's own
    /// `eWeakLockType` when there is one; a security door's lock component is not a weak lock, so the port is
    /// left out rather than filled with a kind the door never had.</summary>
    internal bool LockBroken(MapObjectReference address, string cause, string? lockKind)
        => Publish(DoorTerminalEventContract.LockBrokenFact, address, "broken:" + cause + ":" + (lockKind ?? ""),
            Payload(
                ("door", RuntimeJson.From(Reference(address))),
                ("cause", RuntimeJson.From(DoorTerminalEventContract.LockCauseIndex(cause))),
                ("lock_kind", lockKind == null ? (JsonElement?)null : RuntimeJson.From(lockKind))));

    /// <summary>One terminal command the terminal accepted. The command is the native command name, the slot is
    /// the game's own one-based unique-command slot and is `0` for a command that is not one of the five, and
    /// the actor is absent because the native command entry carries no player.</summary>
    internal bool TerminalCommand(MapObjectReference address, int command, string? input, int? slot)
        => Publish(CommandFact, address, "command:" + command.ToString(CultureInfo.InvariantCulture), Payload(
            ("terminal", RuntimeJson.From(Reference(address))),
            ("command", RuntimeJson.From(MapObjectTerminalCommand.Name(command))),
            ("slot", slot is { } value and > 0 ? RuntimeJson.From(value) : (JsonElement?)null),
            ("input", DoorTerminalDerivations.InputLine(input) is { } line ? RuntimeJson.From(line) : (JsonElement?)null)));

    /// <summary>Whether any fact of one address is new under its own state key. The key is the fact's own
    /// binding id rather than the capability, so the two categories and the three facts cannot collide. The
    /// subject text is the entity id the address is named by — kind included — so the published event id and the
    /// reference a plan reads back are one spelling of one thing.</summary>
    private bool Publish(string fact, MapObjectReference address, string stateKey, JsonElement outputs)
        => Publish(fact, MapObjectModule.EntityKind + ":" + address, stateKey, outputs);

    /// <summary>The same publication for a subject that is already a runtime entity rather than a map address:
    /// a weak door is named by the reference this provider published, so the subject text is that reference's
    /// id. Both subjects share the ledger, the authority gate and the report-once table, because they are the
    /// same publisher's facts.</summary>
    private bool Publish(string fact, EntityReference subject, string stateKey, JsonElement outputs)
        => Publish(fact, subject.Id, stateKey, outputs);

    private bool Publish(string fact, string subjectText, string stateKey, JsonElement outputs)
    {
        if (!_registration.IsRegistered || _kernel.StartupState != RuntimeStartupState.Ready) return false;
        if (!_authority())
        {
            ReportOnce("client:" + subjectText,
                "door or terminal fact observed on a non-authoritative peer: the host publishes map-object state.");
            return false;
        }
        string key = fact + "|" + subjectText;
        if (!_ledger.Observe(key, stateKey, out long transition)) return false;
        string eventId = EventId(fact, subjectText, transition);
        string binding = DoorTerminalEventContract.Binding(fact);
        // Nothing is listening on this row's binding: the kernel would answer `no-consumer` for the event this call
        // is about to build, so the event value is never built. The state above is still observed, because a fact
        // seen while nobody listened is a fact this publisher has already reported.
        if (Unsubscribed(binding)) return false;
        var result = _registration.Publish(new RuntimeEvent(eventId, binding, _kernel.WorldEpoch,
            Math.Max(0, _kernel.CurrentTick), Scope(), outputs));
        _ledger.Remember(key, binding, eventId, stateKey, outputs.GetRawText());
        // One fact of one state is published exactly once. `queued` is the event the kernel admitted and queued
        // for the plans that claim the binding. Every other status is a refusal.
        if (result.Status == "queued") { Published++; return true; }
        if (result.Status == "rejected")
            ReportOnce("publish:" + fact + ":" + result.Code,
                "door or terminal fact rejected: " + result.Code);
        return false;
    }

    /// <summary>Whether no loaded plan is mounted on one of this provider's own bindings. The kernel precomputes
    /// the answer and refreshes it where the subscription table changes, so a native callback with nothing to say
    /// to anyone skips the event value instead of building one the kernel would refuse as `no-consumer`.</summary>
    private bool Unsubscribed(string binding) => _gates.TryGetValue(binding, out var gate) && !gate.HasSubscribers;

    /// <summary>The event id is the fact's own identity: the kind of the subject, the fact, the world, the
    /// subject's own text and the subject's transition number, which is the same rule the package's other
    /// map-object facts follow. The kind is read off the subject rather than hard-coded, because a weak door
    /// belongs to its own namespace: an event id that spelled the map-object kind for a weak door would name a
    /// map object the world does not have.</summary>
    private string EventId(string fact, string subject, long transition)
        => KindOf(subject) + "." + fact + ":" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture)
            + ":" + subject + ":" + transition.ToString(CultureInfo.InvariantCulture);

    /// <summary>The kind part of one subject's own text: everything before the first colon, which is how every
    /// entity id in this runtime spells its kind.</summary>
    private static string KindOf(string subject)
    {
        int colon = subject.IndexOf(':');
        return colon > 0 ? subject[..colon] : subject;
    }

    private string Scope() => "gtfo.world:" + _kernel.WorldEpoch.ToString(CultureInfo.InvariantCulture);

    /// <summary>The entity reference a port carries for one address in this world. It is built from the address
    /// and the world the fact was published in, exactly as the module's own map-object entity ids are, so a
    /// reference a plan reads back is the reference the kernel resolves.</summary>
    private EntityReference Reference(MapObjectReference address)
        => new(MapObjectModule.EntityKind + ":" + address, _kernel.WorldEpoch, 1);

    /// <summary>One published payload, port by port. A port whose value the native side could not read is left
    /// out instead of being published as a JSON null: an absent port is how this framework says "not
    /// observable", where a null would claim the port was written.</summary>
    private static JsonElement Payload(params (string Id, JsonElement? Value)[] ports)
    {
        var payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        foreach (var (id, value) in ports) if (value is { } present) payload[id] = present;
        return RuntimeJson.From(payload);
    }

    private void ReportOnce(string key, string message)
    {
        if (!_reported.Add(key)) return;
        _report(message);
    }
}
