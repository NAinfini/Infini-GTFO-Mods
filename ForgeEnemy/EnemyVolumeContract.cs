using System;
using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The one effect-volume row this package owns: `forge.action.combat.effect_volume`, the native volume
/// the game's own `EffectVolumeManager` runs against a sphere the game's own `EV_Sphere` describes.
///
/// The row is anchored at an entity rather than at a bare coordinate: the catalog has no `entity_or_position`
/// port type, and inventing one is the runtime's call rather than this module's, so the row takes the entity
/// collection every other action in this family takes and reads each anchor's own position. `follow_anchor`
/// moves the volume with the anchor it was placed on; without it the volume stays where the anchor stood.
///
/// The volume is the gameplay half: `contents` selects which of the target's own two values the volume writes
/// (`all`, `health`, `infection`), `modification` whether it inflicts or shields it, `scale` the amount the
/// volume's own `ComputeAmount` is multiplied by, and `radius_min` / `radius_max` the two distances the game
/// interpolates between, so a target at the centre receives the full amount and one at or beyond the outer
/// radius receives none. A plan that wants a status or a damage tick inside the volume composes it from the
/// generic damage and effect cards, exactly as the R2 ruling says: this row carries the volume, not a status
/// registry.
///
/// The volume's visible body is the game's own fog sphere (`FogSphereAllocator`), allocated at the same position
/// and radius so the volume can be seen where it is. That half is presentation: a fog-sphere budget with no room
/// left does not fail the submission, and the result row's own `code` column says which of the two happened
/// (`volume-placed` or `volume-placed-undrawn`), because the row's result schema has the four fixed columns and
/// the count and no sixth column of its own.
///
/// The row carries no lifetime port. Its clock is the step's own `effect` block (I-PLAN §3.4): the kernel casts
/// the handle, times the instance and calls the module's own restore callback when it ends, so the volumes one
/// dispatch placed are released together — by their duration, by a cancellation, by a released plan or by the
/// world. The catalog ports are `in / anchors` in and `next / result / volumes` out: `volumes` is that same
/// kernel handle, published into the port when the card asked for a cancel handle, and absent for a card that
/// asked for no effect at all, whose volumes live until the module releases them.</summary>
public static class EnemyVolumeContract
{
    /// <summary>The provider every row here belongs to; the same string `ModuleDefinition.ProviderId` carries.</summary>
    public const string ProviderId = ModuleDefinition.ProviderId;

    /// <summary>`forge.action.combat.effect_volume`: register one native effect volume per anchor.</summary>
    public const string VolumeCapability = "forge.action.combat.effect_volume";
    public const string VolumeBinding = ProviderId + ".binding.effect_volume";
    public const string VolumeHandler = "gtfo.combat.effect_volume";

    /// <summary>The permission the binding writes the world through: a value written to every target standing
    /// inside the volume the game itself keeps registered.</summary>
    public const string VolumePermission = "gtfo.effect_volume.write";

    /// <summary>The `contents` vocabulary, which is the game's own `eEffectVolumeContents` in declaration order.
    /// The names are the native members in lowercase snake case, so the value an author writes and the enum the
    /// manager reads cannot disagree.</summary>
    public static readonly string[] Contents = { "all", "health", "infection" };

    /// <summary>The `modification` vocabulary, which is the game's own `eEffectVolumeModification` in declaration
    /// order.</summary>
    public static readonly string[] Modifications = { "inflict", "shield" };

    /// <summary>The capability row, in the website catalog's own row shape.</summary>
    public const string CapabilityRow = """
    {
      "id": "forge.action.combat.effect_volume",
      "owner": "forge.module.gtfo.enemy",
      "kind": "action",
      "label": "生成效果体积",
      "version": "1.0.0",
      "parameters": {
        "description": "以目标敌人为中心生成一块球状效果体积，寿命由动作卡的持续效果决定。",
        "summary": "在敌人身上放一块会跟着它走的球形影响区：按内容（全部/生命/感染）和方式（施加/护盾）影响区内的目标，同时用原版雾球把这块区域显示出来；动作卡的持续效果结束时，体积和雾球一起消失。",
        "summaryEn": "Places a spherical effect volume on the target enemies: it writes the chosen content (all, health or infection) to whatever stands inside it, in the chosen direction (inflict or shield), and shows itself with the game's own fog sphere. The card's own effect duration ends the volume and takes its fog sphere away.",
        "labelEn": "Effect volume",
        "support": "implementation-only"
      },
      "graph": {
        "domains": [
          "map",
          "room",
          "enemy",
          "weapon",
          "tool",
          "consumable",
          "player"
        ],
        "execution": "host",
        "inputs": [
          {
            "id": "in",
            "type": "execution"
          },
          {
            "entityKinds": [
              "gtfo.enemy"
            ],
            "id": "anchors",
            "type": "entity",
            "cardinality": "many"
          }
        ],
        "outputs": [
          {
            "id": "next",
            "type": "execution"
          },
          {
            "id": "result",
            "type": "result",
            "schema": "forge.result.combat.effect_volume",
            "fields": [
              {
                "id": "target",
                "type": "entity"
              },
              {
                "id": "status",
                "type": "enum",
                "schema": "execution_outcome"
              },
              {
                "id": "committed",
                "type": "enum",
                "schema": "commit_state"
              },
              {
                "id": "code",
                "type": "string"
              },
              {
                "id": "target_count",
                "type": "integer"
              }
            ]
          },
          {
            "id": "volumes",
            "type": "handle",
            "handleKind": "effect",
            "lifetime": "encounter"
          }
        ],
        "parameters": [
          {
            "id": "contents",
            "type": "enum",
            "role": "structural",
            "required": true,
            "values": [
              "all",
              "health",
              "infection"
            ]
          },
          {
            "id": "modification",
            "type": "enum",
            "role": "structural",
            "required": true,
            "values": [
              "inflict",
              "shield"
            ]
          },
          {
            "id": "scale",
            "type": "number",
            "role": "value",
            "required": true
          },
          {
            "id": "radius_min",
            "type": "number",
            "role": "value",
            "required": true,
            "unit": "m"
          },
          {
            "id": "radius_max",
            "type": "number",
            "role": "value",
            "required": true,
            "unit": "m"
          },
          {
            "id": "follow_anchor",
            "type": "boolean",
            "role": "value",
            "required": false
          }
        ],
        "recipients": {
          "input": "anchors",
          "target": "entity",
          "cardinality": "many",
          "requires": [
            "effect.volume"
          ],
          "result": "result",
          "handle": "volumes"
        }
      }
    }
    """;

    /// <summary>One binding row, in the provider's own binding format.</summary>
    public static string BindingRowJson => $$"""
    {
      "id": "{{VolumeBinding}}",
      "capabilityId": "{{VolumeCapability}}",
      "providerId": "{{ProviderId}}",
      "handler": "{{VolumeHandler}}",
      "role": "execute",
      "status": "implemented",
      "dependencies": [],
      "requires": []
    }
    """;

    /// <summary>The one support row a registration appends to its `BindingSupport` table.</summary>
    public static BindingSupport[] Support() =>
        new[] { new BindingSupport(VolumeBinding, "implementation-only", new[] { VolumePermission }) };

    /// <summary>The handler's ports: `anchors` is the recipient collection and `volumes` the kernel's own effect
    /// handle, published into that port when the card asked for a cancel handle. The four numeric and boolean
    /// choices are the row's own structural parameters, so the shape declares only the ports.</summary>
    public static HandlerShape Shape() => new HandlerShape()
        .Inputs("anchors").Outputs("result", "volumes");

    /// <summary>The one handler name this contract declares, for the registration that composes it.</summary>
    public static readonly string[] HandlerNames = { VolumeHandler };

    /// <summary>The one binding id this contract declares.</summary>
    public static readonly string[] BindingIds = { VolumeBinding };
}
