using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeMap;

/// <summary>The trigger bindings this provider implements for map objects, one per catalog row. Every one of
/// them is an observation of native state, so each is registered with role `observe`, declares no handler and
/// no result row, and publishes its subject's state instead of acting on it.
///
/// The capability list, its domain list and its output ports are the catalog entry for the same id, unchanged;
/// the catalog is the authority for the shape and this module only implements it.</summary>
public static class MapObjectContract
{
    public const string DoorStateCapability = "forge.trigger.interaction.door_state";
    public const string LockStateCapability = "forge.trigger.interaction.lock_state";
    public const string TerminalCommandCapability = "forge.trigger.interaction.terminal_command";
    public const string TerminalResultCapability = "forge.trigger.interaction.terminal_result";
    public const string TerminalSessionCapability = "forge.trigger.interaction.terminal_session";
    public const string MapObjectReadPermission = MapObjectModule.EntityKind + ".read";

    /// <summary>Internal binding ids, by capability: what a native hook names when it reports a fact. The
    /// binding id is the capability's own suffix under the Map provider, so the counterpart of a row is
    /// readable from either side.</summary>
    public static string Binding(string capabilityId)
        => ModuleDefinition.ProviderId + ".binding." + capabilityId["forge.trigger.".Length..];

    internal static readonly string DoorStateFact = "door_state";
    internal static readonly string LockStateFact = "lock_state";
    internal static readonly string TerminalCommandFact = "terminal_command";
    internal static readonly string TerminalResultFact = "terminal_result";
    internal static readonly string TerminalSessionFact = "terminal_session";

    internal static string BindingFor(string fact) => Binding(Capability(fact));

    internal static string Capability(string fact) => fact switch
    {
        "door_state" => DoorStateCapability,
        "lock_state" => LockStateCapability,
        "terminal_command" => TerminalCommandCapability,
        "terminal_result" => TerminalResultCapability,
        "terminal_session" => TerminalSessionCapability,
        _ => throw new RuntimeContractException("map-object-fact", "Unknown map-object fact kind.")
    };

    /// <summary>The catalog's domain list for these five rows: a door or terminal is a map object that tools
    /// and consumables can act on, and each row names the same set.</summary>
    internal static readonly string[] Domains = { "map", "room", "tool", "consumable" };

    /// <summary>One trigger row: the catalog's label, description, domains and output ports, with the ports a
    /// row really publishes. A port this provider has no native read for stays unregistered rather than being
    /// declared as a promise nothing can keep. Only the rows whose shape the catalog does not carry yet are still
    /// declared here; every row whose shape `TriggerContracts` already owns is bound without a local copy.</summary>
    internal static object Row(string capability, string label, string description, params object[] outputs)
        => new
        {
            id = capability, owner = ModuleDefinition.ProviderId, kind = "trigger", label, version = "1.0.0",
            parameters = new { description },
            graph = new { domains = Domains, execution = "host", inputs = Array.Empty<object>(), outputs, parameters = Array.Empty<object>() }
        };

    internal static object Port(string id, string type) => new { id, type };

    internal static object Optional(string id, string type) => new { id, type, optional = true };

    internal static object BindingRow(string capability, string handler) => new
    {
        id = Binding(capability), capabilityId = capability, providerId = ModuleDefinition.ProviderId, handler,
        role = "observe", status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    /// <summary>All five rows in the order the module declares them, paired with the fact each one carries.</summary>
    internal static readonly IReadOnlyList<(string Fact, string Capability)> Rows = Array.AsReadOnly(new[]
    {
        (DoorStateFact, DoorStateCapability),
        (LockStateFact, LockStateCapability),
        (TerminalCommandFact, TerminalCommandCapability),
        (TerminalResultFact, TerminalResultCapability),
        (TerminalSessionFact, TerminalSessionCapability)
    });
}
