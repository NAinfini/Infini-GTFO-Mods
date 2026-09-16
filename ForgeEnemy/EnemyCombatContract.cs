using System.Collections.Generic;
using ForgeRuntime.Framework;

namespace ForgeEnemy;

/// <summary>The game-independent half of the `C-enemy-combat` slice: the capability rows, binding rows, handler
/// shapes and support rows this provider's execute handlers are registered with. The integration step picks them
/// up from here, so the registry spelling lives in one place instead of being repeated inside `EnemyModule`.
///
/// Three rows were surveyed against build 20403457. Two are declared here and registered by the integration
/// patch:
///   * `forge.action.combat.stagger` writes through the game's one hitreact entry,
///     `ES_HitreactBase.CanHitreact` then `ActivateState`, and carries the `reaction` strength as a structural
///     parameter because that entry point cannot omit it.
///   * `forge.action.combat.attack_interrupt` names the enemies whose in-flight attack is to be interrupted —
///     `enemies:entity` of kind `gtfo.enemy`, `many` — and proves the attack through
///     `ES_EnemyAttackBase.IsPerformingAttack`/`IsChargingAttack` before writing the same hitreact state machine.
///     It consumes no handle: no provider in this tree casts an attack-instance transaction handle, so a handle
///     input would be a row no plan could ever reach. An enemy that is not mid-attack is reported with the
///     controlled code `enemy-not-attacking` and a `none` commit state.
/// The third, `forge.action.combat.impulse`, is implemented and kept, but deliberately **not** registered:
/// the audit found the only reachable native write is the limb's own `LimbForceApplicator`, which only flying
/// enemies answer to, and a submission cannot be verified afterwards. Its row is carried in
/// <see cref="ImpulseCapabilityRow"/> and its reason in <see cref="Unregistered"/>, so re-enabling it after
/// review is a one-line wiring job rather than a redesign.
///
/// The native evidence behind every claim here is `ForgeEnemy/evidence/enemy-combat-actions.json`.</summary>
public static class EnemyCombatContract
{
    /// <summary>The provider every row here belongs to; the same string `EnemyModule.ProviderId` carries.</summary>
    public const string ProviderId = "forge.module.gtfo.enemy";

    /// <summary>`forge.action.combat.stagger`: put a target that has a hitreact entry into a hitreact.</summary>
    public const string StaggerCapability = "forge.action.combat.stagger";
    public const string StaggerBinding = ProviderId + ".binding.stagger";
    public const string StaggerHandler = "gtfo.enemy.stagger";

    /// <summary>`forge.action.combat.attack_interrupt`: interrupt the attack the named enemies are performing or
    /// charging.</summary>
    public const string AttackInterruptCapability = "forge.action.combat.attack_interrupt";
    public const string AttackInterruptBinding = ProviderId + ".binding.attack_interrupt";
    public const string AttackInterruptHandler = "gtfo.enemy.attack_interrupt";

    /// <summary>`forge.action.combat.impulse`: implemented, not registered (see <see cref="Unregistered"/>). The
    /// names stay here so the handler's binding is spelled once, next to the row it belongs to.</summary>
    public const string ImpulseCapability = "forge.action.combat.impulse";
    public const string ImpulseBinding = ProviderId + ".binding.impulse";
    public const string ImpulseHandler = "gtfo.enemy.impulse";

    /// <summary>The two execute handlers this contract registers.</summary>
    public static readonly string[] Handlers = { StaggerHandler, AttackInterruptHandler };

    /// <summary>The two bindings this contract registers. Every id here is a binding id; a capability id is a
    /// separate namespace in the kernel registry and may never be reused as one.</summary>
    public static readonly string[] Bindings = { StaggerBinding, AttackInterruptBinding };

    /// <summary>The capability rows, in the website catalog's own row shape: identity, owner, kind, the label the
    /// website shows, and the graph the handler is resolved against. Both rows carry their recipient collection
    /// `targets`/`enemies` first, so the whole declared width of that collection separates `source` — the only
    /// role input — from the scalar inputs that follow: the order <see cref="StaggerShape"/> and
    /// <see cref="AttackInterruptShape"/> resolve against.
    ///
    /// `reaction` is the one structural parameter the native hitreact entry point forces. `immunity_policy` keeps
    /// the catalog's two members: `respect` reads the game's own `CanHitreact` gate and refuses when the game says
    /// no, `ignore` clears the game's `HitreactForbidden` flag and asks `CanHitreact` with the retrigger timer
    /// overridden, which is the strongest interrupt build 20403457 exposes.
    ///
    /// `attack_interrupt` carries no structural parameter: the row it replaces had `refund_policy` only because it
    /// consumed a transaction handle, and with the handle gone there is no reservation to refund. Its result is one
    /// row per enemy, the same shape the other recipient actions answer with.</summary>
    public const string CapabilityRows = """
        {
          "id": "forge.action.combat.stagger",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "让敌人硬直 / 击倒",
          "version": "1.0.0",
          "parameters": {
            "description": "请求让目标打出硬直。",
            "summary": "让有硬直入口的目标进入指定强度的硬直。",
            "summaryEn": "Puts a target that has a hitreact entry into the chosen reaction strength.",
            "labelEn": "Stagger",
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
                "id": "targets",
                "type": "entity",
                "cardinality": "many"
              },
              {
                "id": "source",
                "type": "entity"
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
                "schema": "forge.result.combat.stagger",
                "codes": [
                  "reaction-unsupported",
                  "immunity-policy-unsupported",
                  "too-many-targets",
                  "authority-or-phase",
                  "stale-or-unsupported-recipient",
                  "missing-health-receiver",
                  "health-receiver-owner-mismatch",
                  "not-alive",
                  "missing-locomotion",
                  "no-hitreact-entry",
                  "hitreact-gate-exception",
                  "stagger-immune",
                  "native-commit-exception",
                  "receiver-changed-during-commit",
                  "unexpected-reaction-readback",
                  "readback-exception",
                  "stagger-all-rejected",
                  "stagger-all-unknown"
                ],
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
                    "id": "reaction",
                    "type": "string"
                  },
                  {
                    "id": "target_count",
                    "type": "integer"
                  }
                ]
              }
            ],
            "parameters": [
              {
                "id": "reaction",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "micro",
                  "light",
                  "heavy"
                ]
              },
              {
                "id": "immunity_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "respect",
                  "ignore"
                ]
              }
            ],
            "recipients": {
              "input": "targets",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "motion.stagger"
              ],
              "result": "result"
            }
          }
        },
        {
          "id": "forge.action.combat.attack_interrupt",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "打断敌人的攻击",
          "version": "1.0.0",
          "parameters": {
            "description": "打断一次可被打断的攻击。",
            "summary": "打断这些敌人正在进行的攻击。",
            "summaryEn": "Interrupts the attack the named enemies are performing or charging.",
            "labelEn": "Interrupt attack",
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
                "id": "enemies",
                "type": "entity",
                "cardinality": "many"
              },
              {
                "id": "source",
                "type": "entity"
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
                "schema": "forge.result.combat.attack_interrupt",
                "codes": [
                  "too-many-targets",
                  "authority-or-phase",
                  "stale-or-unsupported-recipient",
                  "missing-health-receiver",
                  "health-receiver-owner-mismatch",
                  "not-alive",
                  "missing-locomotion",
                  "attack-state-unreadable",
                  "enemy-not-attacking",
                  "no-hitreact-entry",
                  "hitreact-gate-exception",
                  "attack-interrupt-forbidden",
                  "native-commit-exception",
                  "receiver-changed-during-commit",
                  "unexpected-reaction-readback",
                  "readback-exception",
                  "attack-interrupt-all-rejected",
                  "attack-interrupt-all-unknown"
                ],
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
              }
            ],
            "parameters": [],
            "recipients": {
              "input": "enemies",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "attack.interrupt"
              ],
              "result": "result"
            }
          }
        }
        """;

    /// <summary>The two binding rows the same declaration registers, one per row above. `owner` is this provider
    /// because it owns the native write path; a binding row's `requires` list stays empty where the capability's
    /// own recipient contract already names the permission.</summary>
    public const string BindingRows = """
        {
          "id": "forge.module.gtfo.enemy.binding.stagger",
          "capabilityId": "forge.action.combat.stagger",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.stagger",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        },
        {
          "id": "forge.module.gtfo.enemy.binding.attack_interrupt",
          "capabilityId": "forge.action.combat.attack_interrupt",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.attack_interrupt",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """;

    /// <summary>The `forge.action.combat.impulse` row this contract carries but does not register. The native write
    /// it was built on is real — `LimbForceApplicator.AddForce(Vector3 force, float duration)` on a limb — but the
    /// audit that followed the slice found it can only move the two flying state machines
    /// (`ES_HitreactFlyer`, `ES_ShooterAttackFlyer`) and that a submission leaves no readable displacement, so the
    /// row is shelved until the user reviews it. It is a row of its own, not part of <see cref="CapabilityRows"/>,
    /// so that registering it later is the same edit as naming it in the registered text.</summary>
    public const string ImpulseCapabilityRow = """
        {
          "id": "forge.action.combat.impulse",
          "owner": "forge.module.gtfo.enemy",
          "kind": "action",
          "label": "施加支持物理体的受限冲量",
          "version": "1.0.0",
          "parameters": {
            "description": "给带物理体的目标部位一个推力，持续时间内由原版物理体结算。",
            "summary": "给带物理体的目标部位一个推力。",
            "summaryEn": "Shoves a target limb that has a physics body, for the given duration.",
            "labelEn": "Apply impulse",
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
                "id": "targets",
                "type": "entity",
                "cardinality": "many"
              },
              {
                "id": "source",
                "type": "entity"
              },
              {
                "id": "force",
                "type": "vector3"
              },
              {
                "id": "magnitude",
                "type": "number"
              },
              {
                "id": "duration",
                "type": "number",
                "unit": "seconds"
              },
              {
                "id": "limb",
                "type": "integer",
                "optional": true
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
                "schema": "forge.result.combat.impulse",
                "codes": [
                  "mass-policy-unsupported",
                  "invalid-force",
                  "magnitude-out-of-range",
                  "duration-out-of-range",
                  "invalid-limb",
                  "too-many-targets",
                  "authority-or-phase",
                  "stale-or-unsupported-recipient",
                  "missing-health-receiver",
                  "health-receiver-owner-mismatch",
                  "not-alive",
                  "no-force-applicator",
                  "native-commit-exception",
                  "receiver-changed-during-commit",
                  "readback-exception",
                  "submitted-unverified",
                  "impulse-all-rejected",
                  "impulse-all-unknown"
                ],
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
              }
            ],
            "parameters": [
              {
                "id": "mass_policy",
                "type": "enum",
                "role": "structural",
                "required": true,
                "values": [
                  "scaled",
                  "absolute"
                ]
              }
            ],
            "recipients": {
              "input": "targets",
              "target": "entity",
              "cardinality": "many",
              "requires": [
                "motion.impulse"
              ],
              "result": "result"
            }
          }
        }
        """;

    /// <summary>The matching binding row, for the same one-line wiring job.</summary>
    public const string ImpulseBindingRow = """
        {
          "id": "forge.module.gtfo.enemy.binding.impulse",
          "capabilityId": "forge.action.combat.impulse",
          "providerId": "forge.module.gtfo.enemy",
          "handler": "gtfo.enemy.impulse",
          "role": "execute",
          "status": "implemented",
          "dependencies": [],
          "requires": []
        }
        """;

    /// <summary>One support row per registered binding. The permission is the native write the handler performs:
    /// a hitreact through the game's own state machine, which is what both registered rows end in. Neither is a
    /// read the provider already holds.</summary>
    public static readonly BindingSupport[] Support =
    {
        new(StaggerBinding, "implementation-only", new[] { "gtfo.enemy.motion.write" }),
        new(AttackInterruptBinding, "implementation-only", new[] { "gtfo.enemy.motion.write" })
    };

    /// <summary>The stagger handler's own ports, resolved once at registration against the capability row above.
    /// `targets` is the recipient collection, whose whole declared width separates `source` from the rest; both
    /// inputs are read only to prove the roles the kernel validated are the ones the row carries.</summary>
    public static HandlerShape StaggerShape() => new HandlerShape()
        .Inputs("targets", "source").Outputs("result").Parameters("reaction", "immunity_policy");

    /// <summary>The attack-interrupt handler's own ports: `enemies` is the recipient collection and `source` the
    /// only other role. The row declares no structural parameter, so the shape declares none either — a handler
    /// shape and its capability graph are the two halves of one contract and neither may name a port the other
    /// does not carry.</summary>
    public static HandlerShape AttackInterruptShape() => new HandlerShape()
        .Inputs("enemies", "source").Outputs("result");

    /// <summary>The impulse handler's own ports. `limb` is optional and therefore only ever read through a
    /// presence check, never assumed. The shape is spelled even though the row is not registered, so the handler
    /// stays compilable and testable next to the row it will be registered with.</summary>
    public static HandlerShape ImpulseShape() => new HandlerShape()
        .Inputs("targets", "source", "force", "magnitude", "duration", "limb").Outputs("result").Parameters("mass_policy");

    public static Dictionary<string, HandlerShape> Shapes() => new(System.StringComparer.Ordinal)
    {
        [StaggerHandler] = StaggerShape(),
        [AttackInterruptHandler] = AttackInterruptShape()
    };

    /// <summary>The rows of this slice that have no registered capability row here, with the reason each one is
    /// absent. The native detail is in `ForgeEnemy/evidence/enemy-combat-actions.json`; this list is what a reader
    /// of the code needs to know before looking for a handler that is not registered.
    ///
    /// `impulse` is the one row of the three that is not registered. Its handler and its native write path are
    /// implemented and tested; the row is held back because the audit judged the effect unreachable in game: the
    /// only writable impulse entry in build 20403457 is `LimbForceApplicator.AddForce` on a damage limb, the
    /// `IForceApplyer` interface behind it is implemented by the two flying enemy state machines only
    /// (`ES_HitreactFlyer`, `ES_ShooterAttackFlyer`), and a ground enemy's locomotion drives its own position, so
    /// a force on a walking enemy has nothing to move. `AddForce` returns void and the physics step integrates it
    /// later, so a submission cannot be verified either. Registering the row would advertise an effect this
    /// provider can neither guarantee nor observe, so it waits for the user's review.</summary>
    public static readonly (string Capability, string Reason)[] Unregistered =
    {
        ("forge.action.combat.impulse",
            "未注册：审计判定游戏内做不到且提交无法验证。原生唯一可达的冲量写入口是敌人伤害部位的 "
            + "LimbForceApplicator.AddForce(Vector3, float)（ForgeEnemy/Native/EnemyCombatActions.cs 的 EnemyCombatWrite）；"
            + "其下的 IForceApplyer 只由两个飞行敌人状态机实现（ES_HitreactFlyer、ES_ShooterAttackFlyer），"
            + "地面敌人的位置由自身 locomotion 驱动，limb 冲量没有可推动的物理体；AddForce 返回 void、由物理步在之后积分，"
            + "提交后读不到位移，只能记 unknown。行与绑定行仍保留在 ImpulseCapabilityRow / ImpulseBindingRow，"
            + "处理器与测试仍然保留并通过；用户审阅后再按一行接入注册。")
    };
}
