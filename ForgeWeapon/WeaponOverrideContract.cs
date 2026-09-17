using System;
using System.Collections.Generic;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeWeapon;

/// <summary>
/// The `forge.action.weapon.*` instance-override rows this slice declares and registers, port for port as the
/// website's `catalog/capability-catalog.json` spells them after ruling 84. Three of them are the named values
/// that describe how one instance shoots — its rate, its spread and its recoil — which is the whole of the node
/// list's `a-w-stats`; the fourth is the generic form of the same write, one named field of the same vocabulary
/// with the operation that name is changed by, and it is where every "change a weapon number for a while" card
/// lands whose number has no named row of its own.
///
/// Each row carries a body, because each of them is an instance-level block replacement this package can
/// actually perform: <see cref="WeaponActionRuntime"/> decides the values and
/// <c>ForgeWeapon.Native.WeaponOverrideApplier</c> is the only code that writes them. The state family those
/// rows were once paired with is gone: `reload` is the one row of it the node list keeps, and it is declared by
/// the framework's own action vocabulary.
///
/// A row and its binding travel together — a declared action nothing answers would be a promise this package
/// does not keep — so <see cref="Capabilities"/> returns the rows and <see cref="Bindings"/> the bindings of
/// exactly the rows that have a handler. The integration batch appends both to this provider's own registry seed;
/// nothing here registers itself, because a registration is a session's decision and not a declaration's.
/// </summary>
public static class WeaponOverrideContract
{
    public const string FireRateCapability = "forge.action.weapon.fire_rate";
    public const string SpreadCapability = "forge.action.weapon.spread";
    public const string RecoilCapability = "forge.action.weapon.recoil";
    public const string PropertyCapability = "forge.action.weapon.property";

    public const string FireRateBinding = ModuleDefinition.ProviderId + ".binding.weapon_fire_rate";
    public const string SpreadBinding = ModuleDefinition.ProviderId + ".binding.weapon_spread";
    public const string RecoilBinding = ModuleDefinition.ProviderId + ".binding.weapon_recoil";
    public const string PropertyBinding = ModuleDefinition.ProviderId + ".binding.weapon_property";

    public const string FireRateHandler = "gtfo.weapon.fire_rate";
    public const string SpreadHandler = "gtfo.weapon.spread";
    public const string RecoilHandler = "gtfo.weapon.recoil";
    public const string PropertyHandler = "gtfo.weapon.property";

    /// <summary>The permission every row here writes under: each one changes an equipment instance's own block,
    /// which is a write to state the wield bindings already read under their read permission.</summary>
    public const string WieldWritePermission = "gtfo.equipment.wield.write";

    /// <summary>Every row of this family, in catalog order.</summary>
    public static readonly IReadOnlyList<string> All = new[]
    {
        FireRateCapability, SpreadCapability, RecoilCapability, PropertyCapability
    };

    /// <summary>Each capability against the binding and handler it travels with.</summary>
    public static readonly IReadOnlyDictionary<string, (string Binding, string Handler)> WiredBindings =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal)
        {
            [FireRateCapability] = (FireRateBinding, FireRateHandler),
            [SpreadCapability] = (SpreadBinding, SpreadHandler),
            [RecoilCapability] = (RecoilBinding, RecoilHandler),
            [PropertyCapability] = (PropertyBinding, PropertyHandler)
        };

    /// <summary>The handler port sets, resolved at registration against the rows below. Each one declares exactly
    /// the ports its row has on the request side, so a port this package reads but the row does not declare — or
    /// the reverse — fails at registration instead of at a plan load.</summary>
    public static IReadOnlyDictionary<string, HandlerShape> Shapes()
        => new Dictionary<string, HandlerShape>(StringComparer.Ordinal)
        {
            [FireRateHandler] = new HandlerShape().Inputs("equipment", "source", "rate", "duration")
                .Outputs("next", "result", "modifier").Parameters(),
            [SpreadHandler] = new HandlerShape().Inputs("equipment", "cone", "seed", "movement_scale", "aim_scale")
                .Outputs("next", "result").Parameters("pattern"),
            [RecoilHandler] = new HandlerShape()
                .Inputs("equipment", "horizontal", "vertical", "recovery", "camera_kick")
                .Outputs("next", "result").Parameters(),
            [PropertyHandler] = new HandlerShape().Inputs("equipment", "value")
                .Outputs("next", "result", "property").Parameters("field", "operation")
        };

    /// <summary>The declared rows of this family, parsed once each: portable into a `capabilities` array without
    /// a second serialization step.</summary>
    public static IReadOnlyList<JsonElement> Rows()
    {
        var rows = new List<JsonElement>(Documents.Length);
        foreach (var text in Documents) rows.Add(RuntimeJson.Parse(text));
        return rows;
    }

    /// <summary>The wrapped form of <see cref="Rows"/>, ready to append to a `capabilities` list the way
    /// <see cref="ModuleDefinition"/> builds one.</summary>
    public static IReadOnlyList<object> Capabilities()
    {
        var rows = new List<object>(Documents.Length);
        foreach (var row in Rows()) rows.Add(row);
        return rows;
    }

    /// <summary>The bindings of the rows, each already `execute` and `implemented` — the handler named is one
    /// <see cref="Handlers"/> supplies, and the two are only ever added together.</summary>
    public static IReadOnlyList<object> Bindings()
    {
        var bindings = new List<object>(All.Count);
        foreach (var capability in All)
        {
            var (binding, handler) = WiredBindings[capability];
            bindings.Add(new
            {
                id = binding, capabilityId = capability, providerId = ModuleDefinition.ProviderId, handler,
                role = "execute", status = "implemented", dependencies = Array.Empty<string>(),
                requires = Array.Empty<string>()
            });
        }
        return bindings;
    }

    /// <summary>The support entry of each binding, under the one write permission the whole family uses.</summary>
    public static IReadOnlyList<BindingSupport> Support()
    {
        var support = new List<BindingSupport>(All.Count);
        foreach (var capability in All)
            support.Add(new BindingSupport(WiredBindings[capability].Binding, "implementation-only",
                new[] { WieldWritePermission }));
        return support;
    }

    /// <summary>The handler table as the session supplies it: one entry per row, keyed by the handler name its
    /// binding names. A session that cannot supply one of these registers the rest and leaves that row
    /// undeclared, which is what a declaration with no body is.</summary>
    public static IReadOnlyDictionary<string, CommandHandler> Handlers(CommandHandler fireRate,
        CommandHandler spread, CommandHandler recoil, CommandHandler property)
        => new Dictionary<string, CommandHandler>(StringComparer.Ordinal)
        {
            [FireRateHandler] = fireRate ?? throw new ArgumentNullException(nameof(fireRate)),
            [SpreadHandler] = spread ?? throw new ArgumentNullException(nameof(spread)),
            [RecoilHandler] = recoil ?? throw new ArgumentNullException(nameof(recoil)),
            [PropertyHandler] = property ?? throw new ArgumentNullException(nameof(property))
        };

    /// <summary>The four rows that carry a body, in catalog order: `fire_rate`, `spread`, `recoil`, `property`.</summary>
    public static readonly string[] Documents =
        { FireRateDocument, SpreadDocument, RecoilDocument, PropertyDocument };

    private const string FireRateDocument = """
    {
      "id": "forge.action.weapon.fire_rate",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "改变射速",
      "version": "1.0.0",
      "parameters": { "description": "临时改变射速。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "entityKinds": ["gtfo.player"], "id": "source", "type": "entity" },
          { "id": "rate", "type": "number" },
          { "id": "duration", "type": "integer", "unit": "tick" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          {
            "id": "result",
            "type": "result",
            "schema": "forge.result.weapon.fire_rate",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "rate", "type": "number" }
            ]
          },
          { "id": "modifier", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }
        ],
        "parameters": [],
        "recipients": {
          "input": "equipment",
          "target": "entity",
          "cardinality": "one",
          "requires": ["weapon.fire_rate"],
          "result": "result",
          "handle": "modifier"
        }
      }
    }
    """;

    private const string SpreadDocument = """
    {
      "id": "forge.action.weapon.spread",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "改变散布",
      "version": "1.0.0",
      "parameters": { "description": "改变散布和精度。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "cone", "type": "number", "unit": "deg" },
          { "id": "seed", "type": "integer" },
          { "id": "movement_scale", "type": "number" },
          { "id": "aim_scale", "type": "number" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          {
            "id": "result",
            "type": "result",
            "schema": "forge.result.weapon.spread",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "cone", "type": "number", "unit": "deg" }
            ]
          }
        ],
        "parameters": [
          {
            "id": "pattern",
            "type": "enum",
            "role": "structural",
            "required": true,
            "values": ["random", "fixed", "spiral"]
          }
        ],
        "recipients": {
          "input": "equipment",
          "target": "entity",
          "cardinality": "one",
          "requires": ["weapon.spread"],
          "result": "result"
        }
      }
    }
    """;

    private const string RecoilDocument = """
    {
      "id": "forge.action.weapon.recoil",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "应用后坐力",
      "version": "1.0.0",
      "parameters": { "description": "应用后座配置或一次冲击。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "horizontal", "type": "number" },
          { "id": "vertical", "type": "number" },
          { "id": "recovery", "type": "number" },
          { "id": "camera_kick", "type": "number" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          {
            "id": "result",
            "type": "result",
            "schema": "forge.result.weapon.recoil",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "horizontal", "type": "number" }
            ]
          }
        ],
        "parameters": [],
        "recipients": {
          "input": "equipment",
          "target": "entity",
          "cardinality": "one",
          "requires": ["weapon.recoil"],
          "result": "result"
        }
      }
    }
    """;

    /// <summary>
    /// The generic row. Its `field` enum is the ledger's own vocabulary in the ledger's own order, and the test
    /// that reads both is what keeps the two from drifting: a name this row offers and the applier cannot write
    /// would be a card the editor shows and the game ignores. `value` carries the field's own unit, so `add` on
    /// `fire_rate` is a frequency and `add` on `spread_cone` is a pellet count; the native half is what turns
    /// either into the member the game reads.
    ///
    /// This row carries no `duration` port: its life is the plan's own `effect` block (plan §3.4 动作卡), which the
    /// kernel times and ends by calling this package's restore callback, and its `property` handle is the kernel's
    /// effect handle. One clock, one name — a duration port beside the effect block would be the second one.
    /// </summary>
    private const string PropertyDocument = """
    {
      "id": "forge.action.weapon.property",
      "owner": "forge.module.gtfo.weapon",
      "kind": "action",
      "label": "改武器属性",
      "version": "1.0.0",
      "parameters": { "description": "临时改一项武器数值：设值、加值或减值。" },
      "graph": {
        "domains": ["weapon", "tool", "consumable"],
        "execution": "host",
        "inputs": [
          { "id": "in", "type": "execution" },
          { "entityKinds": ["gtfo.equipment"], "id": "equipment", "type": "entity" },
          { "id": "value", "type": "number" }
        ],
        "outputs": [
          { "id": "next", "type": "execution" },
          {
            "id": "result",
            "type": "result",
            "schema": "forge.result.weapon.property",
            "fields": [
              { "id": "target", "type": "entity" },
              { "id": "status", "type": "enum", "schema": "execution_outcome" },
              { "id": "committed", "type": "enum", "schema": "commit_state" },
              { "id": "code", "type": "string" },
              { "id": "field", "type": "string" },
              { "id": "value", "type": "number" }
            ]
          },
          { "id": "property", "type": "handle", "handleKind": "effect", "lifetime": "entity_life" }
        ],
        "parameters": [
          {
            "id": "field",
            "type": "enum",
            "role": "structural",
            "required": true,
            "values": [
              "fire_rate",
              "burst_count",
              "spread_cone",
              "spread_movement_scale",
              "spread_aim_scale",
              "recoil_horizontal",
              "recoil_vertical",
              "recoil_recovery",
              "recoil_camera_kick"
            ]
          },
          {
            "id": "operation",
            "type": "enum",
            "role": "structural",
            "required": true,
            "values": ["set", "add", "subtract"]
          }
        ],
        "recipients": {
          "input": "equipment",
          "target": "entity",
          "cardinality": "one",
          "requires": ["weapon.property"],
          "result": "result",
          "handle": "property"
        }
      }
    }
    """;
}
