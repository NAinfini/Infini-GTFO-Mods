using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The three `forge.trigger.combat.burst_started` / `burst_ended` / `dry_fire` rows this slice owns, declared
/// port for port as the website's `catalog/capability-catalog.json` spells them. Each row is carried as its own
/// JSON document and parsed once, so a declared port cannot drift from the catalog by a hand-copied field: the
/// shape a plan compiles against is the shape the catalog publishes, down to port order, `nullable` and `schema`.
///
/// <b>These rows are declared and not placed in a contract by this file.</b> Every `forge.trigger.*` row in this
/// build is declared once by the framework's `forge.contract.trigger` provider
/// (`ForgeRuntime/Framework/TriggerContracts.cs`), and a second declaration of one id is refused at registration.
/// That file is a shared registration point this slice may not touch, so the documents live here — verbatim
/// catalog — for the integration batch to move into `TriggerContracts`' capability array, and
/// <see cref="Bindings"/> / <see cref="Support"/> are the rows this provider registers for its own observation.
/// The bindings are spelled here and are <b>not registered</b> by this slice either: the package's one
/// registration path is <c>ModuleDefinition.Create()</c>, which the integration batch extends.
///
/// Nothing here reads a plan, a world or a game type: the whole file is vocabulary and ids.
/// </summary>
public static class AttackInstanceContract
{
    /// <summary>The provider these bindings belong to: this package's own.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>A burst sequence opened, counted by the weapon's own `m_burstMax`.</summary>
    public const string BurstStartedCapability = "forge.trigger.combat.burst_started";
    /// <summary>A burst sequence closed. `count` is the weapon's own burst length, never a Forge tally.</summary>
    public const string BurstEndedCapability = "forge.trigger.combat.burst_ended";
    /// <summary>The weapon's own empty-clip path ran: `BWA_Burst.OnFireShotEmptyClip` / `BWA_Auto.OnFireShotEmptyClip`.</summary>
    public const string DryFireCapability = "forge.trigger.combat.dry_fire";

    public const string BurstStartedBinding = ProviderId + ".binding.burst_started";
    public const string BurstEndedBinding = ProviderId + ".binding.burst_ended";
    public const string DryFireBinding = ProviderId + ".binding.dry_fire";

    /// <summary>The permission the observation half of every row here reads under. It is the same
    /// `gtfo.weapon.combat.read` the package's `shot_committed` binding already declares: these facts come from
    /// the weapon's own body and from the equipment life the adapter recorded, and nothing here writes.</summary>
    public const string CombatReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The capability ids this slice declares, in catalog order.</summary>
    public static readonly IReadOnlyList<string> CapabilityIds = Array.AsReadOnly(new[]
    {
        BurstStartedCapability, BurstEndedCapability, DryFireCapability
    });

    // ---- What the integration batch registers -----------------------------------------------------------

    /// <summary>One `observe` binding per declared row, spelled the way <see cref="ModuleDefinition"/>
    /// spells its own: this provider's namespace, the catalog capability, the handler name the native half
    /// answers to, and the `observe` role.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        Binding(BurstStartedBinding, BurstStartedCapability),
        Binding(BurstEndedBinding, BurstEndedCapability),
        Binding(DryFireBinding, DryFireCapability)
    };

    /// <summary>One support line per binding above, each requiring the one combat-read permission.</summary>
    public static IReadOnlyList<BindingSupport> Support()
    {
        var rows = new List<BindingSupport>(CapabilityIds.Count);
        foreach (var binding in new[] { BurstStartedBinding, BurstEndedBinding, DryFireBinding })
            rows.Add(new BindingSupport(binding, "implementation-only", new[] { CombatReadPermission }));
        return rows;
    }

    /// <summary>The handler name a binding resolves to. The native half supplies these bodies through
    /// <c>WeaponNativeSession.Attack</c>, so the row is registered by the integration batch together with that
    /// handler set or not at all.</summary>
    public static string Handler(string capability) => capability switch
    {
        BurstStartedCapability => "gtfo.weapon.burst_started",
        BurstEndedCapability => "gtfo.weapon.burst_ended",
        DryFireCapability => "gtfo.weapon.dry_fire",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability,
            "No such attack-instance row.")
    };

    private static object Binding(string id, string capabilityId) => new
    {
        id, capabilityId, providerId = ProviderId, handler = Handler(capabilityId), role = "observe",
        status = "implemented", dependencies = Array.Empty<string>(), requires = Array.Empty<string>()
    };

    // ---- The declared capability documents (catalog rows, verbatim) --------------------------------------

    /// <summary>The parsed catalog rows, wrapped so a caller holding an `object[]` can append them to a
    /// `capabilities` array in one step.</summary>
    public static IReadOnlyList<object> Capabilities()
    {
        var rows = new List<object>(CapabilityIds.Count);
        foreach (var text in Documents) rows.Add(RuntimeJson.Parse(text));
        return rows;
    }

    /// <summary>The parsed rows as elements, in <see cref="CapabilityIds"/> order.</summary>
    public static IReadOnlyList<JsonElement> Rows()
    {
        var rows = new List<JsonElement>(Documents.Length);
        foreach (var text in Documents) rows.Add(RuntimeJson.Parse(text));
        return rows;
    }

    /// <summary>The row of one capability, or an exception naming the id: a caller asking for a row this
    /// contract does not carry has a typo or a stale constant, which is not a lookup that answers null.</summary>
    public static JsonElement Row(string capability)
    {
        foreach (var row in Rows())
            if (row.GetProperty("id").GetString() == capability) return row;
        throw new ArgumentOutOfRangeException(nameof(capability), capability,
            "No such declared attack-instance row.");
    }

    /// <summary>One row's output ports in declaration order, as `id:type` texts: the port list a plan wires and
    /// the thing a port-order drift would break. Used by this slice's own tests.</summary>
    public static IReadOnlyList<string> OutputPorts(string capability)
    {
        var ports = new List<string>();
        foreach (var port in Row(capability).GetProperty("graph").GetProperty("outputs").EnumerateArray())
        {
            var id = port.GetProperty("id").GetString() ?? "";
            var type = port.GetProperty("type").GetString() ?? "";
            var carrier = "";
            if (port.TryGetProperty("handleKind", out var kind)) carrier = "/" + kind.GetString();
            if (port.TryGetProperty("schema", out var schema)) carrier += ":" + schema.GetString();
            if (port.TryGetProperty("nullable", out var nullable) && nullable.GetBoolean()) carrier += "?";
            ports.Add(id + ":" + type + carrier);
        }
        return ports;
    }

    /// <summary>The literal JSON of each row, generated from the catalog and kept as text rather than as an
    /// object graph, so the declaration is the catalog's own document and a reviewer can diff it against
    /// `catalog/capability-catalog.json` without reading C#. Order is <see cref="CapabilityIds"/>.</summary>
    public static readonly string[] Documents =
    {
        BurstStartedDocument, BurstEndedDocument, DryFireDocument
    };

    private const string BurstStartedDocument = """
    {
      "id": "forge.trigger.combat.burst_started",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "开始 / 停止开火",
      "version": "1.0.0",
      "parameters": { "description": "一串连发开始。" },
      "graph": {
        "domains": [
          "enemy",
          "weapon",
          "tool",
          "consumable",
          "player"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "entityKinds": ["gtfo.player"],
            "id": "source",
            "type": "entity"
          },
          {
            "entityKinds": ["gtfo.equipment"],
            "id": "equipment",
            "type": "entity"
          },
          {
            "id": "count",
            "type": "integer"
          }
        ],
        "parameters": []
      }
    }
    """;
    private const string BurstEndedDocument = """
    {
      "id": "forge.trigger.combat.burst_ended",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "连发结束",
      "version": "1.0.0",
      "parameters": { "description": "一串连发结束。" },
      "graph": {
        "domains": [
          "enemy",
          "weapon",
          "tool",
          "consumable",
          "player"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "entityKinds": ["gtfo.player"],
            "id": "source",
            "type": "entity"
          },
          {
            "entityKinds": ["gtfo.equipment"],
            "id": "equipment",
            "type": "entity"
          },
          {
            "id": "count",
            "type": "integer"
          }
        ],
        "parameters": []
      }
    }
    """;
    private const string DryFireDocument = """
    {
      "id": "forge.trigger.combat.dry_fire",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "弹匣打空 / 全部没弹",
      "version": "1.0.0",
      "parameters": { "description": "想开枪但没子弹。" },
      "graph": {
        "domains": [
          "enemy",
          "weapon",
          "tool",
          "consumable",
          "player"
        ],
        "execution": "host",
        "inputs": [],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "entityKinds": ["gtfo.player"],
            "id": "actor",
            "type": "entity"
          },
          {
            "entityKinds": ["gtfo.equipment"],
            "id": "equipment",
            "type": "entity"
          }
        ],
        "parameters": []
      }
    }
    """;
}
