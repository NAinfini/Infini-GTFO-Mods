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

    public const string ReloadBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_reload";
    public const string ClipSetBinding = WeaponHolderChannelContract.ProviderId + ".binding.weapon_clip_set";
    public const string ReloadHandler = "gtfo.weapon.holder.reload";
    public const string ClipSetHandler = "gtfo.weapon.holder.clip_set";

    /// <summary>The permission both rows write under: they change the magazine of one equipment instance.</summary>
    public const string MagazineWritePermission = "gtfo.equipment.magazine.write";

    /// <summary>What one owner-tier action asks the holder's machine to do. The kind is a structural value of the
    /// step, not a port: one capability is one action, and the channel body switches on this constant rather than
    /// parsing an id out of a plan.</summary>
    public enum HolderAction
    {
        Reload,
        ClipSet
    }

    /// <summary>One declared row and the native entry point its handler calls. The three travel together: a
    /// binding is only declared by a registration that also supplies its handler, so a row whose native body is
    /// missing is not declared at all.</summary>
    public sealed record Row(string Capability, string Binding, string Handler, string NativeEntry);

    /// <summary>The two rows, in catalog order.</summary>
    public static readonly IReadOnlyList<Row> Rows = new[]
    {
        new Row(ReloadCapability, ReloadBinding, ReloadHandler,
            "PlayerInventoryLocal.TriggerReload"),
        new Row(ClipSetCapability, ClipSetBinding, ClipSetHandler,
            "BulletWeapon.SetCurrentClip")
    };

    /// <summary>The capability rows as parsed documents, one per row, ready for a `capabilities` array.</summary>
    public static IReadOnlyList<JsonElement> Capabilities()
    {
        var rows = new List<JsonElement>(2) { RuntimeJson.Parse(ReloadRow), RuntimeJson.Parse(ClipSetRow) };
        return rows;
    }

    /// <summary>The binding rows: this provider's own namespace, the canonical capability, the handler name, the
    /// `execute` role every action carries, and `implemented` because the registration supplies the handler.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        Binding(ReloadBinding, ReloadCapability, ReloadHandler),
        Binding(ClipSetBinding, ClipSetCapability, ClipSetHandler)
    };

    /// <summary>The registration support rows, one per binding.</summary>
    public static IReadOnlyList<BindingSupport> Support() => new[]
    {
        new BindingSupport(ReloadBinding, "implementation-only", new[] { MagazineWritePermission }),
        new BindingSupport(ClipSetBinding, "implementation-only", new[] { MagazineWritePermission })
    };

    /// <summary>The shape of each handler, resolved at registration against the row below. Both rows read the
    /// equipment they address, the holder they address it for and the plan's own request fields; the clip row
    /// additionally reads the amount it was asked for.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes() => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
    {
        [ReloadHandler] = new HandlerShape().Inputs("equipment", "holder").Outputs("result").Parameters("chamber_policy", "transfer_policy"),
        [ClipSetHandler] = new HandlerShape().Inputs("equipment", "holder", "amount").Outputs("result").Parameters("clip_policy")
    };

    /// <summary>Which row one capability id is, or null when this contract does not carry it. The channel's own
    /// dispatcher uses this instead of comparing ids at each call site.</summary>
    public static HolderAction? KindOf(string capabilityId)
    {
        if (string.Equals(capabilityId, ReloadCapability, StringComparison.Ordinal)) return HolderAction.Reload;
        if (string.Equals(capabilityId, ClipSetCapability, StringComparison.Ordinal)) return HolderAction.ClipSet;
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
        CommandHandler? clipSet = null)
    {
        var sessions = new Dictionary<string, Func<EntityReference, string?>>(StringComparer.Ordinal);
        if (holders != null)
            foreach (var capability in WeaponHolderChannelContract.Capabilities) sessions[capability] = holders;
        var handlers = new Dictionary<string, CommandHandler>(StringComparer.Ordinal);
        if (reload != null) handlers[ReloadHandler] = reload;
        if (clipSet != null) handlers[ClipSetHandler] = clipSet;
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

    // The two result rows carry the four fixed columns every action result carries, then this row's own field:
    // `clip` is the magazine the write left behind, which is the one number a plan can check its request against.
    private static object Result(string schema) => new
    {
        id = "result", type = "result", schema,
        fields = new object[]
        {
            new { id = "target", type = "entity" },
            new { id = "status", type = "enum", schema = "execution_outcome" },
            new { id = "committed", type = "enum", schema = "commit_state" },
            new { id = "code", type = "string" },
            new { id = "clip", type = "integer" }
        }
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
      "label": "请求装填",
      "version": "1.0.0",
      "parameters": { "description": "请求持有者本机装填这一件装备；决定由主机下达，写入由持枪者执行。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "owner",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "id": "equipment", "type": "entity" },
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
      "parameters": { "description": "把这一件装备的弹匣写成指定发数；写入在持枪者本机执行。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "owner",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "id": "equipment", "type": "entity" },
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
}
