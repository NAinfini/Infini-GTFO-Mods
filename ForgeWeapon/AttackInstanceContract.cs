using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The eight `forge.trigger.combat.attack_*` / `burst_*` / `dry_fire` rows this slice owns, declared port for
/// port as the website's `catalog/capability-catalog.json` spells them. Each row is carried as its own JSON
/// document and parsed once, so a declared port cannot drift from the catalog by a hand-copied field: the shape
/// a plan compiles against is the shape the catalog publishes, down to port order, `nullable` and `schema`.
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

    /// <summary>One shot is asked for, before the weapon's own body decides anything. `phase` is the catalog's
    /// `command_phase` script and is always `requested` here: this fact exists only at the request.</summary>
    public const string AttackRequestedCapability = "forge.trigger.combat.attack_requested";
    /// <summary>The attack was not refused: the native body registered the shot. The evidence behind that
    /// signal is `PlayerSync.RegisterFiredBullets`, whose only callers in this build are the two unsynced
    /// `Fire` bodies — see `evidence/weapon-attack-instance.json`.</summary>
    public const string AttackAcceptedCapability = "forge.trigger.combat.attack_accepted";
    /// <summary>The native body returned. For a ranged weapon this is the `Fire` body itself, so a shotgun's
    /// several pellets are already resolved; for melee it is `OnAttackHitDone`, the end of the swing.</summary>
    public const string AttackCompletedCapability = "forge.trigger.combat.attack_completed";
    /// <summary><b>Not implemented.</b> See <see cref="CancelledGap"/>: this build has no native signal for a
    /// shot or a swing that was interrupted, and the design's `reason` is a native reason code that does not
    /// exist. The row is declared because the catalog and the release manifest read the declaration set.</summary>
    public const string AttackCancelledCapability = "forge.trigger.combat.attack_cancelled";
    /// <summary>The attack closed with no hit candidate inside its scope.</summary>
    public const string AttackMissedCapability = "forge.trigger.combat.attack_missed";
    /// <summary>A burst sequence opened, counted by the weapon's own `m_burstMax`.</summary>
    public const string BurstStartedCapability = "forge.trigger.combat.burst_started";
    /// <summary>A burst sequence closed. `count` is the weapon's own burst length, never a Forge tally.</summary>
    public const string BurstEndedCapability = "forge.trigger.combat.burst_ended";
    /// <summary>The weapon's own empty-clip path ran: `BWA_Burst.OnFireShotEmptyClip` / `BWA_Auto.OnFireShotEmptyClip`.</summary>
    public const string DryFireCapability = "forge.trigger.combat.dry_fire";

    public const string AttackRequestedBinding = ProviderId + ".binding.attack_requested";
    public const string AttackAcceptedBinding = ProviderId + ".binding.attack_accepted";
    public const string AttackCompletedBinding = ProviderId + ".binding.attack_completed";
    public const string AttackCancelledBinding = ProviderId + ".binding.attack_cancelled";
    public const string AttackMissedBinding = ProviderId + ".binding.attack_missed";
    public const string BurstStartedBinding = ProviderId + ".binding.burst_started";
    public const string BurstEndedBinding = ProviderId + ".binding.burst_ended";
    public const string DryFireBinding = ProviderId + ".binding.dry_fire";

    /// <summary>The permission the observation half of every row here reads under. It is the same
    /// `gtfo.weapon.combat.read` the package's `shot_committed` binding already declares: these facts come from
    /// the weapon's own body and from the equipment life the adapter recorded, and nothing here writes.</summary>
    public const string CombatReadPermission = ModuleDefinition.CombatReadPermission;

    /// <summary>The capability ids this slice declares, in catalog order. The three that are declared and
    /// unimplemented are listed with the rest: they live in the catalog and the manifest reads the set, and
    /// <see cref="Gaps"/> is where the absence is recorded rather than in a binding with no body.</summary>
    public static readonly IReadOnlyList<string> CapabilityIds = Array.AsReadOnly(new[]
    {
        AttackRequestedCapability, AttackAcceptedCapability, AttackCompletedCapability, AttackCancelledCapability,
        AttackMissedCapability, BurstStartedCapability, BurstEndedCapability, DryFireCapability
    });

    /// <summary>The rows that <see cref="AttackInstanceModule"/> actually publishes, in catalog order. Exactly
    /// the complement of <see cref="Gaps"/>: a binding is declared for these and for nothing else.</summary>
    public static readonly IReadOnlyList<string> ImplementedCapabilityIds = Array.AsReadOnly(new[]
    {
        AttackRequestedCapability, AttackAcceptedCapability, AttackCompletedCapability,
        AttackMissedCapability, BurstStartedCapability, BurstEndedCapability, DryFireCapability
    });

    /// <summary>One declared row and the native capability it is still missing. The same table is what the
    /// evidence file and the slice report quote, so the absence is described once.</summary>
    public sealed record Declared(string Capability, string Binding, string Gap);

    /// <summary><b>`attack_cancelled` is not implemented.</b> The design asked for the interruption of one
    /// attack scope by a reload, a weapon switch or a death, with `reason` read from a native reason code. This
    /// build offers no such signal: the four `Fire` bodies are plain void methods with no cancellation result,
    /// `PlayerInventoryBase` exposes no abort entry point for an in-flight trigger pull, and the only native
    /// `reason`-like strings in the weapon path belong to the deployable teardown
    /// (`SentryGunInstance.OnDespawn` / `OnDestroy`), which is an equipment ending and not an attack ending.
    /// Deriving a cancellation from "no shot was registered" would fold every dry fire, every refused pull and
    /// every early return into one code the native never produced, which is a fabricated fact — the same reason
    /// the package's `forge.action.weapon.reload_cancel` row carries a gap instead of a handler.</summary>
    public const string CancelledGap =
        "missing native cancellation signal: no Fire body reports an abort, no inventory abort entry point exists, and no native reason code is reachable for an interrupted attack";

    public static readonly IReadOnlyList<Declared> Gaps = new[]
    {
        new Declared(AttackCancelledCapability, AttackCancelledBinding, CancelledGap)
    };

    // ---- What the integration batch registers -----------------------------------------------------------

    /// <summary>One `observe` binding per implemented row, spelled the way <see cref="ModuleDefinition"/>
    /// spells its own: this provider's namespace, the catalog capability, the handler name the native half
    /// answers to, and the `observe` role. No binding is offered for a row in <see cref="Gaps"/>.</summary>
    public static IReadOnlyList<object> Bindings() => new object[]
    {
        Binding(AttackRequestedBinding, AttackRequestedCapability),
        Binding(AttackAcceptedBinding, AttackAcceptedCapability),
        Binding(AttackCompletedBinding, AttackCompletedCapability),
        Binding(AttackMissedBinding, AttackMissedCapability),
        Binding(BurstStartedBinding, BurstStartedCapability),
        Binding(BurstEndedBinding, BurstEndedCapability),
        Binding(DryFireBinding, DryFireCapability)
    };

    /// <summary>One support line per binding above, each requiring the one combat-read permission.</summary>
    public static IReadOnlyList<BindingSupport> Support()
    {
        var rows = new List<BindingSupport>(ImplementedCapabilityIds.Count);
        foreach (var binding in new[]
        {
            AttackRequestedBinding, AttackAcceptedBinding, AttackCompletedBinding, AttackMissedBinding,
            BurstStartedBinding, BurstEndedBinding, DryFireBinding
        }) rows.Add(new BindingSupport(binding, "implementation-only", new[] { CombatReadPermission }));
        return rows;
    }

    /// <summary>The handler name a binding resolves to. The native half supplies these bodies through
    /// <c>WeaponNativeSession.Attack</c>, so the row is registered by the integration batch together with that
    /// handler set or not at all.</summary>
    public static string Handler(string capability) => capability switch
    {
        AttackRequestedCapability => "gtfo.weapon.attack_requested",
        AttackAcceptedCapability => "gtfo.weapon.attack_accepted",
        AttackCompletedCapability => "gtfo.weapon.attack_completed",
        AttackMissedCapability => "gtfo.weapon.attack_missed",
        BurstStartedCapability => "gtfo.weapon.burst_started",
        BurstEndedCapability => "gtfo.weapon.burst_ended",
        DryFireCapability => "gtfo.weapon.dry_fire",
        _ => throw new ArgumentOutOfRangeException(nameof(capability), capability,
            "No such implemented attack-instance row.")
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
        AttackRequestedDocument, AttackAcceptedDocument, AttackCompletedDocument, AttackCancelledDocument,
        AttackMissedDocument, BurstStartedDocument, BurstEndedDocument, DryFireDocument
    };

    private const string AttackRequestedDocument = """
    {
      "id": "forge.trigger.combat.attack_requested",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "攻击意图请求",
      "version": "1.0.0",
      "parameters": { "description": "有人想发起一次攻击，还没通过检查。这一组说的是整次攻击（近战、远程都算）；远程武器每打出一发另有「一次射击已提交」。" },
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
            "id": "source",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity",
            "nullable": true
          },
          {
            "id": "phase",
            "type": "enum",
            "schema": "command_phase"
          }
        ],
        "parameters": []
      }
    }
    """;
    private const string AttackAcceptedDocument = """
    {
      "id": "forge.trigger.combat.attack_accepted",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "攻击成本与合法性通过",
      "version": "1.0.0",
      "parameters": { "description": "攻击的合法性和消耗都过了。这一组说的是整次攻击（近战、远程都算）；远程武器每打出一发另有「一次射击已提交」。" },
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
            "id": "source",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity",
            "nullable": true
          }
        ],
        "parameters": []
      }
    }
    """;
    private const string AttackCompletedDocument = """
    {
      "id": "forge.trigger.combat.attack_completed",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "攻击完成",
      "version": "1.0.0",
      "parameters": { "description": "攻击动作走完了。这一组说的是整次攻击（近战、远程都算）；远程武器每打出一发另有「一次射击已提交」。" },
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
            "id": "source",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity",
            "nullable": true
          }
        ],
        "parameters": []
      }
    }
    """;
    private const string AttackCancelledDocument = """
    {
      "id": "forge.trigger.combat.attack_cancelled",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "攻击取消",
      "version": "1.0.0",
      "parameters": { "description": "攻击被取消。这一组说的是整次攻击（近战、远程都算）；远程武器每打出一发另有「一次射击已提交」。" },
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
            "id": "source",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity",
            "nullable": true
          },
          {
            "id": "reason",
            "type": "string"
          }
        ],
        "parameters": []
      }
    }
    """;
    private const string AttackMissedDocument = """
    {
      "id": "forge.trigger.combat.attack_missed",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "攻击生命周期结束且未达命中条件",
      "version": "1.0.0",
      "parameters": { "description": "一次攻击整个打空了。" },
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
            "id": "source",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity",
            "nullable": true
          }
        ],
        "parameters": []
      }
    }
    """;
    private const string BurstStartedDocument = """
    {
      "id": "forge.trigger.combat.burst_started",
      "owner": "forge.module.gtfo.weapon",
      "kind": "trigger",
      "label": "连发序列开始",
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
            "id": "source",
            "type": "entity"
          },
          {
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
      "label": "连发序列结束",
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
            "id": "source",
            "type": "entity"
          },
          {
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
      "label": "有效攻击请求因弹药不足失败",
      "version": "1.0.0",
      "parameters": { "description": "想开枪但没子弹。工具或消耗品本身被拒绝使用用「使用失败并给出原因」。" },
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
            "id": "actor",
            "type": "entity"
          },
          {
            "id": "equipment",
            "type": "entity"
          }
        ],
        "parameters": []
      }
    }
    """;
}
