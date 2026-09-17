using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The holder-client action rows: the two actions of the node list whose write is only correct on the machine
/// that holds the weapon.
///
/// `forge.action.weapon.reload` and `forge.action.weapon.clip_set` both change the magazine of one equipment
/// instance. That state is per-machine: a clip belongs to the inventory that owns the weapon, and the game's own
/// reload path (`PlayerInventoryLocal.TriggerReload`/`DoReload`) and clip write (`BulletWeapon.SetCurrentClip`)
/// act on whichever machine runs them. A host that executed either for a client-held weapon would write a
/// different player's inventory, so both rows declare the `owner` execution tier: the host decides *when* (which
/// plan, which step, which inputs), the holder's own client performs the write, and the game's own replication
/// carries the result back.
///
/// The rows are declared here, not by <see cref="ModuleDefinition"/>: they belong to the holder provider
/// (see <see cref="WeaponHolderChannelContract"/>), which has its own registration and its own owner-session
/// resolver. The declaration is a complete catalog row — ports, parameters, result schema — so the shape a plan
/// compiles against is the shape this file publishes, and nothing about the tier is inferred from a port.
/// </summary>
public static class WeaponHolderActionsContract
{
    /// <summary>The reload row, at the identity the website catalog already carries.</summary>
    public const string ReloadCapability = "forge.action.weapon.reload";
    /// <summary>The clip write row. The catalog has no separate magazine row — `forge.action.inventory.stack_set`
    /// is the backpack stack and `forge.action.weapon.ammo_*` the reserve pools — so this is a new canonical id of
    /// the weapon domain, named after what it sets.</summary>
    public const string ClipSetCapability = "forge.action.weapon.clip_set";
    /// <summary>The fire row. Its canonical id is this package's own and its tier is the same `owner` one the
    /// magazine rows carry, because the same machine owns both: the weapon fires where the inventory that holds
    /// it lives — the game's own `Fire` writes the clip, the recoil and the replicated shot count of that
    /// machine's weapon — so a host that fired a client's weapon would be firing the wrong copy.</summary>
    public const string AutoFireCapability = "forge.action.weapon.auto_fire";

    public const string ReloadBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_reload";
    public const string ClipSetBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_clip_set";
    public const string AutoFireBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_auto_fire";
    public const string ReloadHandler = "gtfo.weapon.holder.reload";
    public const string ClipSetHandler = "gtfo.weapon.holder.clip_set";
    public const string AutoFireHandler = "gtfo.weapon.holder.auto_fire";

    /// <summary>The permission all three rows write under: they change the magazine of one equipment instance or
    /// spend one of its rounds.</summary>
    public const string MagazineWritePermission = "gtfo.equipment.magazine.write";

    /// <summary>The three `required_state` members, so a body that checks one and a plan that asks for it spell
    /// the value once. `none` fires whatever is in hand; `aiming` and `charging` are the game's own weapon states
    /// and never a re-derivation of them.</summary>
    public const string StateNone = "none";
    public const string StateAiming = "aiming";
    public const string StateCharging = "charging";

    /// <summary>What one owner-tier action asks the holder's machine to do. The kind is a structural value of the
    /// step, not a port: one capability is one action, and the channel body switches on this constant rather than
    /// parsing an id out of a plan.</summary>
    public enum HolderAction
    {
        Reload,
        ClipSet,
        AutoFire
    }

    /// <summary>One declared row and the native entry point its handler calls. The three travel together: a
    /// binding is only declared by a registration that also supplies its handler, so a row whose native body is
    /// missing is not declared at all.</summary>
    public sealed record Row(string Capability, string Binding, string Handler, string NativeEntry);

    /// <summary>The three rows, in catalog order.</summary>
    public static readonly IReadOnlyList<Row> Rows = new[]
    {
        new Row(ReloadCapability, ReloadBinding, ReloadHandler,
            "PlayerInventoryLocal.TriggerReload"),
        new Row(ClipSetCapability, ClipSetBinding, ClipSetHandler,
            "BulletWeapon.SetCurrentClip"),
        new Row(AutoFireCapability, AutoFireBinding, AutoFireHandler,
            "BulletWeapon.Fire")
    };

    /// <summary>The capability rows as parsed documents, one per row, ready for a `capabilities` array.</summary>
    public static IReadOnlyList<JsonElement> Capabilities()
    {
        var rows = new List<JsonElement>(3)
        {
            RuntimeJson.Parse(ReloadRow), RuntimeJson.Parse(ClipSetRow), RuntimeJson.Parse(AutoFireRow)
        };
        return rows;
    }

    /// <summary>The binding rows: this provider's own namespace, the canonical capability, the handler name, the
    /// `execute` role every action carries, and `implemented` because the registration supplies the handler.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        Binding(ReloadBinding, ReloadCapability, ReloadHandler),
        Binding(ClipSetBinding, ClipSetCapability, ClipSetHandler),
        Binding(AutoFireBinding, AutoFireCapability, AutoFireHandler)
    };

    /// <summary>The registration support rows, one per binding. All three write the magazine or fire it, so all
    /// three carry the same write permission.</summary>
    public static IReadOnlyList<BindingSupport> Support() => new[]
    {
        new BindingSupport(ReloadBinding, "implementation-only", new[] { MagazineWritePermission }),
        new BindingSupport(ClipSetBinding, "implementation-only", new[] { MagazineWritePermission }),
        new BindingSupport(AutoFireBinding, "implementation-only", new[] { MagazineWritePermission })
    };

    /// <summary>The shape of each handler, resolved at registration against the row below. Every row reads the
    /// equipment it addresses, the holder it addresses it for and the plan's own request fields; the clip row
    /// additionally reads the amount it was asked for, and the fire row reads nothing else because the state it
    /// requires is a structural parameter the native check reads out of the weapon itself.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [ReloadHandler] = new HandlerShape().Inputs("equipment", "holder").Outputs("result").Parameters("chamber_policy", "transfer_policy"),
        [ClipSetHandler] = new HandlerShape().Inputs("equipment", "holder", "amount").Outputs("result").Parameters("clip_policy"),
        [AutoFireHandler] = new HandlerShape().Inputs("equipment", "holder").Outputs("result").Parameters("required_state")
    };

    /// <summary>Which row one capability id is, or null when this contract does not carry it. The channel's own
    /// dispatcher uses this instead of comparing ids at each call site.</summary>
    public static HolderAction? KindOf(string capabilityId)
    {
        if (string.Equals(capabilityId, ReloadCapability, StringComparison.Ordinal)) return HolderAction.Reload;
        if (string.Equals(capabilityId, ClipSetCapability, StringComparison.Ordinal)) return HolderAction.ClipSet;
        if (string.Equals(capabilityId, AutoFireCapability, StringComparison.Ordinal)) return HolderAction.AutoFire;
        return null;
    }

    /// <summary>
    /// The holder provider's whole registration, built here rather than in the native half so the focused test
    /// project can register exactly this module and exercise its two rows without a game.
    ///
    /// <paramref name="holders"/> is the owner-session resolver the host's `owner` dispatch asks for the session
    /// that holds the equipment a step names; the native half supplies the one that reads it out of the game, and
    /// a test supplies its own. The two handler bodies are the only part that touches the game, so they are handed
    /// in as well; a registration built without them declares both rows and answers neither, which is what the
    /// declaration-only shape is for. <paramref name="logLevel"/> is passed straight to the kernel by the caller.
    /// </summary>
    public static RuntimeModule Module(Func<EntityReference, string?>? holders, CommandHandler? reload = null,
        CommandHandler? clipSet = null, CommandHandler? autoFire = null)
    {
        var sessions = new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal);
        if (holders != null)
            foreach (var capability in WeaponHolderChannelContract.Capabilities) sessions[capability] = holders;
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (reload != null) handlers[ReloadHandler] = reload;
        if (clipSet != null) handlers[ClipSetHandler] = clipSet;
        if (autoFire != null) handlers[AutoFireHandler] = autoFire;
        return new RuntimeModule(RuntimeKernel.ApiVersion,
            RuntimeJson.From(new
            {
                providers = new[]
                {
                    new
                    {
                        id = WeaponHolderChannelContract.ProviderId, kind = "native",
                        version = WeaponHolderChannelContract.Version,
                        // No declared package dependency. The holder rows read equipment identities this package
                        // minted, but that read happens at dispatch time through the kernel's entity resolver and
                        // answers null — a named refusal — when the weapon provider is absent, so a package
                        // dependency would claim a load-order relationship this registration does not need. It
                        // could not be expressed correctly here either: a dependency names a package and a
                        // version, and this module ships inside the same package as the provider it reads.
                        dependencies = Array.Empty<string>()
                    }
                },
                capabilities = Capabilities().ToArray(),
                bindings = Bindings().ToArray()
            }).GetRawText(),
            handlers, Support())
        {
            Shapes = Shapes(),
            OwnerSessions = sessions
        };
    }

    private static object Binding(string id, string capabilityId, string handler) => new
    {
        id, capabilityId, providerId = WeaponHolderChannelContract.ProviderId, handler, role = "execute",
        status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    /// <summary>`forge.action.weapon.reload`, port for port the catalog's row, with the one difference the tier
    /// makes: `execution` is `owner`, because the write is the holder's. The two structural parameters stay the
    /// catalog's (`chamber_policy` "keep"/"drop" and `transfer_policy` "magazine"/"reserve"); the native reload
    /// implements one member of each, and the handler refuses the other by name rather than quietly substituting
    /// it. `reload_profile` is optional here for the same reason it is refused: this build's reload reads no
    /// profile resource, and a required port nothing can supply would make every reload step unloadable.</summary>
    private const string ReloadRow = """
    {
      "id": "forge.action.weapon.reload",
      "owner": "forge.module.gtfo.weapon.holder",
      "kind": "action",
      "label": "强制换弹",
      "version": "1.0.0",
      "parameters": { "description": "请求装填。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "owner",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "holder", "type": "entity" },
          { "id": "reload_profile", "type": "resource", "resourceKind": "profile", "schema": "forge.resource.profile", "optional": true }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.weapon.reload", "fields": [
            { "id": "target", "type": "entity" },
            { "id": "status", "type": "enum", "schema": "execution_outcome" },
            { "id": "committed", "type": "enum", "schema": "commit_state" },
            { "id": "code", "type": "string" },
            { "id": "clip", "type": "integer" }
          ] }
        ],
        "parameters": [
          { "id": "chamber_policy", "type": "enum", "role": "structural", "required": true, "values": ["keep", "drop"] },
          { "id": "transfer_policy", "type": "enum", "role": "structural", "required": true, "values": ["magazine", "reserve"] }
        ],
        "recipients": {
          "input": "equipment", "target": "entity", "cardinality": "one",
          "requires": ["weapon.reload"], "result": "result"
        }
      }
    }
    """;

    /// <summary>The clip row. `amount` is a bullet count for the addressed magazine, and `clip_policy` is the one
    /// structural choice: `set` writes the count as asked and refuses a count above the magazine's own capacity
    /// rather than clamping it, `fill` writes the magazine's capacity and ignores the amount. Clamping is
    /// deliberately not a policy member: a plan that asked for 40 bullets in a 30-round magazine asked for
    /// something the weapon cannot be, and the refusal names the capacity.</summary>
    private const string ClipSetRow = """
    {
      "id": "forge.action.weapon.clip_set",
      "owner": "forge.module.gtfo.weapon.holder",
      "kind": "action",
      "label": "设置弹匣子弹数",
      "version": "1.0.0",
      "parameters": { "description": "把武器的弹匣设成指定数量，或者直接装满。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "owner",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "holder", "type": "entity" },
          { "id": "amount", "type": "integer" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.weapon.clip_set", "fields": [
            { "id": "target", "type": "entity" },
            { "id": "status", "type": "enum", "schema": "execution_outcome" },
            { "id": "committed", "type": "enum", "schema": "commit_state" },
            { "id": "code", "type": "string" },
            { "id": "clip", "type": "integer" }
          ] }
        ],
        "parameters": [
          { "id": "clip_policy", "type": "enum", "role": "structural", "required": true, "values": ["set", "fill"] }
        ],
        "recipients": {
          "input": "equipment", "target": "entity", "cardinality": "one",
          "requires": ["weapon.clip"], "result": "result"
        }
      }
    }
    """;

    /// <summary>
    /// The fire row. `required_state` is the one structural parameter: `none` fires whatever is in hand, `aiming`
    /// and `charging` refuse a shot the weapon is not in the state to take rather than firing anyway. The check
    /// is the game's own state — the holder's sight trigger and the weapon's charge — and not a re-derivation of
    /// it, so a plan that asked for an aimed shot is answered by the same flag the game's own firing path reads.
    /// The shot itself is the native `Fire` body, so the clip, the fire rate, the recoil and the replicated shot
    /// count all behave exactly as they do for a pressed trigger; this row never bypasses the weapon. The result
    /// carries the four fixed columns alone: one `Fire` body is one shot, so a count of shots here would be a
    /// constant and not a reading.
    /// </summary>
    private const string AutoFireRow = """
    {
      "id": "forge.action.weapon.auto_fire",
      "owner": "forge.module.gtfo.weapon.holder",
      "kind": "action",
      "label": "让武器自动开火",
      "version": "1.0.0",
      "parameters": { "description": "由计划让武器开火一次，走武器原生开火路径。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "owner",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "holder", "type": "entity" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          { "id": "result", "type": "result", "schema": "forge.result.weapon.auto_fire", "fields": [
            { "id": "target", "type": "entity" },
            { "id": "status", "type": "enum", "schema": "execution_outcome" },
            { "id": "committed", "type": "enum", "schema": "commit_state" },
            { "id": "code", "type": "string" }
          ] }
        ],
        "parameters": [
          { "id": "required_state", "type": "enum", "role": "structural", "required": true, "values": ["none", "aiming", "charging"] }
        ],
        "recipients": {
          "input": "equipment", "target": "entity", "cardinality": "one",
          "requires": ["weapon.clip"], "result": "result"
        }
      }
    }
    """;
}
