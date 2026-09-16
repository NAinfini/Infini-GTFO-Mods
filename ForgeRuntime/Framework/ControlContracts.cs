using System;
using System.Collections.Generic;
using System.Linq;

namespace ForgeRuntime.Framework;

/// <summary>The first-step control vocabulary, owned by the kernel: `forge.control.flow.*` is native and versioned
/// the same way as <see cref="CombatContracts"/>. The kernel dispatches `control`-kind steps internally — it reads
/// their declared inputs and routes to the successor table in output order (see RuntimeKernel.AdvanceCore) — so it
/// never looks a control binding up in <c>RuntimeRegistry.Handlers</c> and no handler function is supplied here.
/// <see cref="RuntimeRegistry.WithModule"/> only requires a supplied handler for `action`-kind capabilities bound
/// with role `execute`; a `control`-kind capability bound the same way is exempt, but every implemented binding —
/// `control` included — still needs exactly one <see cref="BindingSupport"/> row.
/// The declared port shapes are the contract the plan loader re-derives every control step against: `next` is the
/// exit, `pulse`/`body` is the region the next activation starts from, and the value outputs are what a later step
/// reads through `fromStepSlot`. The vocabulary is the authoring node list's own flow nodes: branch, sequence,
/// parallel, delay, repeat and its periodic form (`interval`), and for-each, plus `cancel`, which the end-of-flow
/// node is the landing point for, and `present`, the when-present guard of rule 142.3 whose one value port is the
/// author's own class rather than one this table pins. The two branch fan-outs declare their exits as `branch_1`..`branch_N` then `next`
/// and take no seed: the host draws the branch when the step runs. `forge.control.flow.cancel_scope` and everything
/// else in the catalog stay second step: a control outside this table is refused by id with `control-unsupported`.</summary>
public static class ControlContracts
{
    private const string ProviderId = "forge.contract.control";
    private static readonly string[] Domains = { "map", "room", "enemy", "weapon", "tool", "consumable", "player", "logic" };
    private static readonly (string Kind, string Capability, string Binding)[] Vocabulary =
    {
        ("branch", "forge.control.flow.branch", "forge.contract.control.binding.branch"),
        ("sequence", "forge.control.flow.sequence", "forge.contract.control.binding.sequence"),
        ("parallel_all", "forge.control.flow.parallel_all", "forge.contract.control.binding.parallel_all"),
        ("random_branch", "forge.control.flow.random_branch", "forge.contract.control.binding.random_branch"),
        ("delay", "forge.control.flow.delay", "forge.contract.control.binding.delay"),
        ("interval", "forge.control.flow.interval", "forge.contract.control.binding.interval"),
        ("repeat", "forge.control.flow.repeat", "forge.contract.control.binding.repeat"),
        ("for_each", "forge.control.flow.for_each", "forge.contract.control.binding.for_each"),
        ("cancel", "forge.control.flow.cancel", "forge.contract.control.binding.cancel"),
        ("restart", "forge.control.flow.restart", "forge.contract.control.binding.restart"),
        ("present", "forge.control.flow.present", "forge.contract.control.binding.present")
    };

    public static RuntimeModule Module() => new(RuntimeKernel.ApiVersion, RuntimeJson.From(new
    {
        providers = new[] { new { id = ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Vocabulary.Select(entry => new
        {
            id = entry.Capability,
            owner = ProviderId,
            kind = "control",
            label = Labels[entry.Kind],
            version = "1.0.0",
            parameters = new { description = Descriptions[entry.Kind] },
            graph = Graph(entry.Kind)
        }).ToArray(),
        bindings = Vocabulary.Select(entry => new
        {
            id = entry.Binding,
            capabilityId = entry.Capability,
            providerId = ProviderId,
            handler = "runtime.control." + entry.Kind,
            role = "execute",
            status = "implemented",
            dependencies = Array.Empty<string>(),
            requires = Array.Empty<string>()
        }).ToArray()
    }).GetRawText(),
    new Dictionary<string, CommandHandler>(), Vocabulary.Select(entry => new BindingSupport(entry.Binding, "implementation-only", Array.Empty<string>())).ToArray());

    private static readonly Dictionary<string, string> Labels = new(StringComparer.Ordinal)
    {
        ["branch"] = "条件分支",
        ["sequence"] = "顺序执行",
        ["parallel_all"] = "同时执行并等待全部结束",
        ["random_branch"] = "随机走一个分支",
        ["delay"] = "延迟进入",
        ["interval"] = "周期脉冲",
        ["repeat"] = "有限重复",
        ["for_each"] = "逐目标",
        ["cancel"] = "取消任务",
        ["restart"] = "重启计时器",
        ["present"] = "有值时"
    };
    private static readonly Dictionary<string, string> Descriptions = new(StringComparer.Ordinal)
    {
        ["branch"] = "条件成立走一边，不成立走另一边。",
        ["sequence"] = "按输出顺序深度优先逐个走完。",
        ["parallel_all"] = "同时从每个分支后继开始，全部结束后走 next。",
        ["random_branch"] = "主机在执行时挑一个分支后继，只走那一个；作者不填种子，结果随这次执行确定。",
        ["delay"] = "计时结束时进入 next；timer 句柄在进入时写出。",
        ["interval"] = "立即进入 next，每跳从 pulse 后继走一遍；脉冲不派发新事件。省略 count 表示一直跑到取消或作用域结束。",
        ["repeat"] = "同一派发内循环 count 轮，每轮写 index 后走 body。",
        ["for_each"] = "按候选集合逐目标循环，每轮写 item 与 index 后走 body。",
        ["cancel"] = "取消 task 句柄持有的调度，输出实际取消数。",
        ["restart"] = "把 task 句柄持有的计时器从现在起重排一次，句柄继续有效；排期已结束的句柄按名拒绝。",
        ["present"] = "值存在时走 present，不存在时走 missing；present 分支里的步骤可以把这个值当非空值用。"
    };

    /// <summary>One control's declared graph, port order included: the plan's successor table is indexed by the
    /// execution outputs in this order, so `next` is always first and the region output — when the kind has one —
    /// always second. Unit-bearing counts are ticks, and the timer handle's kind/lifetime are fixed here.</summary>
    private static object Graph(string kind)
    {
        object[] Inputs(params object[] extra) => new object[] { new { id = "in", type = "execution" } }.Concat(extra).ToArray();
        var execution = new { id = "next", type = "execution" };
        var timer = new { id = "timer", type = "handle", handleKind = "timer", lifetime = "encounter" };
        return kind switch
        {
            "branch" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "condition", type = "boolean" }),
                outputs = new object[] { new { id = "then", type = "execution" }, new { id = "otherwise", type = "execution" } },
                parameters = Array.Empty<object>()
            },
            "sequence" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(),
                outputs = new object[] { new { id = "step_1", type = "execution" }, new { id = "step_2", type = "execution" } },
                parameters = new object[] { new { id = "step_count", type = "integer", role = "structural", required = false, minimum = 2, maximum = 32 } },
                variadic = new { side = "outputs", parameter = "step_count", port = new { id = "step", type = "execution" } }
            },
            "parallel_all" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(),
                outputs = new object[]
                {
                    new { id = "branch_1", type = "execution" }, new { id = "branch_2", type = "execution" }, execution
                },
                parameters = new object[] { new { id = "branch_count", type = "integer", role = "structural", required = false, minimum = 2, maximum = 32 } },
                portGroups = new object[]
                {
                    new { id = "branches", side = "outputs", parameter = "branch_count", minimum = 2, maximum = 32,
                        slots = new object[] { new { id = "branch", type = "execution" } } }
                }
            },
            "random_branch" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(),
                outputs = new object[]
                {
                    new { id = "branch_1", type = "execution" }, new { id = "branch_2", type = "execution" }, execution
                },
                parameters = new object[] { new { id = "branch_count", type = "integer", role = "structural", required = false, minimum = 2, maximum = 32 } },
                portGroups = new object[]
                {
                    new { id = "branches", side = "outputs", parameter = "branch_count", minimum = 2, maximum = 32,
                        slots = new object[] { new { id = "branch", type = "execution" } } }
                }
            },
            "delay" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "duration", type = "integer", unit = "tick" }),
                outputs = new object[] { execution, timer },
                parameters = Array.Empty<object>()
            },
            "interval" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "interval", type = "integer", unit = "tick" }, new { id = "count", type = "integer", optional = true }),
                outputs = new object[] { execution, new { id = "pulse", type = "execution" }, timer },
                parameters = new object[] { new { id = "first_pulse", type = "enum", role = "structural", required = true, set = "pulse_start" } }
            },
            "repeat" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "count", type = "integer" }),
                outputs = new object[] { execution, new { id = "body", type = "execution" }, new { id = "index", type = "integer" } },
                parameters = Array.Empty<object>()
            },
            "for_each" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "candidates", type = "entity", cardinality = "many" }, new { id = "budget", type = "integer" }),
                outputs = new object[] { execution, new { id = "body", type = "execution" }, new { id = "item", type = "entity" }, new { id = "index", type = "integer" } },
                parameters = Array.Empty<object>()
            },
            "cancel" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "task", type = "handle", handleKind = "timer", lifetime = "encounter" }),
                outputs = new object[] { execution, new { id = "cancelled", type = "integer" } },
                parameters = Array.Empty<object>()
            },
            // The rolling window: a live timer is re-armed from now and the handle it published stays valid, so a
            // flow can start its window over on every event without holding a handle in a variable (a variable
            // holds no handle). The pulse count and the schedule's identity keep counting — a re-armed timer never
            // replays an occurrence the ledger already recorded — and a handle whose schedule already ended is
            // refused by name rather than served as a second timer.
            "restart" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "task", type = "handle", handleKind = "timer", lifetime = "encounter" }),
                outputs = new object[] { execution, new { id = "restarted", type = "boolean" } },
                parameters = Array.Empty<object>()
            },
            // The glue rule 142.3 adds: one possibly-absent value, the two ways out, and the value itself
            // republished for the `present` branch. The member list is the author's choice of value class and its
            // order is the wire basis of the `value_type` constant, so it is the website rule's own list, in its
            // own order (`Tools/Forge/capability-rules-flow.ts` `flow.present`, whose tail is that file's
            // `graphGuardedHandleLifetimes`). A handle member spells both halves of a handle's contract — its kind
            // and the lifetime that goes with it (rule 142.1) — which is why it reads `handle:<kind>`; the
            // lifetime is resolved by `RuntimeGraphContracts.GuardedHandleKind`.
            "present" => new
            {
                domains = Domains,
                execution = "host",
                inputs = Inputs(new { id = "value", type = "entity", valueTypeParameter = "value_type", optional = true, nullable = true }),
                outputs = new object[]
                {
                    new { id = "present", type = "execution" }, new { id = "missing", type = "execution" },
                    new { id = "value", type = "entity", valueTypeParameter = "value_type", nullable = true }
                },
                parameters = new object[] { new { id = "value_type", type = "enum", role = "structural", required = true,
                    values = new[] { "number", "integer", "boolean", "string", "entity", "vector3",
                        "handle:timer", "handle:subscription", "handle:effect" } } }
            },
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind)
        };
    }
}
