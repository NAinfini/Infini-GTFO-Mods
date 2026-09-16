using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using Enemies;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;
using SNetwork;
using static Probe;

/// <summary>What this suite holds the three control rows to: the declared row is the website catalog's shape, the
/// handler refuses everything the native path cannot express by name, the commit is only ever claimed when the
/// game's own state was read back, and the authority, lifetime and world-epoch gates refuse before anything is
/// written.</summary>
internal static class EnemyControlFacts
{
    /// <summary>The catalog rows' own port ids, in declared order, read from the website's
    /// `catalog/capability-catalog.json`. Only the parts this provider is responsible for are carried here,
    /// because that is what the registry row has to agree with.</summary>
    private const string AwakenShape = """
    {
      "inputs": ["in", "enemies", "source", "alert_amount", "reason"],
      "outputs": ["next", "result"],
      "parameters": ["wake_policy"],
      "resultFields": ["target", "status", "committed", "code", "alert_amount", "target_count"]
    }
    """;
    private const string SleepShape = """
    {
      "inputs": ["in", "enemies", "duration"],
      "outputs": ["next", "result"],
      "parameters": ["sleep_policy", "interrupt_policy"],
      "resultFields": ["target", "status", "committed", "code", "duration", "target_count"]
    }
    """;
    private const string MoveToShape = """
    {
      "inputs": ["in", "enemies", "destination", "area", "speed", "arrival_tolerance"],
      "outputs": ["next", "result"],
      "parameters": [],
      "resultFields": ["target", "status", "committed", "code", "speed", "target_count"]
    }
    """;
    /// <summary>The three rows that must stay undeclared: their ports are provider-minted `lease` handles, which
    /// the runtime gives no provider a way to create or read.</summary>
    private static readonly string[] Unimplemented =
    {
        "forge.action.enemy.target_set", "forge.action.enemy.target_clear", "forge.action.enemy.state_request"
    };

    internal static void Run()
    {
        AwakenShapeCases();
        AwakenCases();
        SleepCases();
        MoveToCases();
    }

    private static void AwakenShapeCases()
    {
        Case("awaken.row-is-the-catalog-shape", () =>
        {
            using var world = new EnemyControlWorld();
            var capability = Capability(world, EnemyModule.AwakenCapability);
            AssertShape(capability, AwakenShape);
            Check(capability.GetProperty("graph").GetProperty("execution").GetString() == "host", "Awaken is not host-tier.");
            Check(capability.GetProperty("graph").GetProperty("recipients").GetProperty("input").GetString() == "enemies",
                "Awaken's recipient input drifted.");
            Check(Binding(world, EnemyModule.AwakenCapability).GetProperty("role").GetString() == "execute",
                "Awaken's binding is not an execute binding.");
        });
        Case("family.the-three-handle-rows-stay-undeclared", () =>
        {
            using var world = new EnemyControlWorld();
            var declared = new[] { EnemyModule.AwakenCapability, EnemyModule.SleepCapability, EnemyModule.MoveToCapability };
            foreach (var id in Unimplemented)
                Check(!declared.Contains(id), "A handle row was declared without a way to mint its handle: " + id);
            Check(EnemyControlContract.CapabilityIds.SequenceEqual(declared), "The declared row list drifted from the provider's own ids.");
        });
    }

    private static void AwakenCases()
    {
        Case("awaken.immediate-commits-through-the-native-wake", () =>
        {
            using var world = new EnemyControlWorld();
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            var result = world.Awaken(new[] { world.Reference });
            Check(result.Status == CommandStatuses.Succeeded && result.CommitState == CommitStates.Confirmed,
                "Awaken did not commit: " + result.Status + "/" + result.Code);
            Check(world.Locomotion.HibernateWakeup!.Wakeups == 1, "The native wake entry was not submitted exactly once.");
            Check(world.Locomotion.CurrentStateEnum == ES_StateEnum.HibernateWakeUp,
                "The locomotion machine was not in the wake state after the call.");
            var row = Row(result, 0);
            Check(row.GetProperty("status").GetString() == CommandStatuses.Succeeded
                && row.GetProperty("committed").GetString() == CommitStates.Confirmed
                && row.GetProperty("code").GetString() == "committed", "Awaken's row did not report the commit.");
            Check(row.GetProperty("target").GetProperty("id").GetString() == world.Reference.Id, "Awaken's row lost its target.");
            Check(row.GetProperty("alert_amount").GetDouble() == 0, "Awaken's row reported an alert that was not applied.");
            Check(row.GetProperty("target_count").GetInt32() == 1, "Awaken's row target_count is not the recipient count.");
        });
        Case("awaken.refuses-an-awake-enemy", () =>
        {
            using var world = new EnemyControlWorld();
            var result = world.Awaken(new[] { world.Reference });
            Check(result.Status == CommandStatuses.Rejected && Row(result, 0).GetProperty("code").GetString() == "not-hibernating",
                "Awaken did not refuse an enemy that is not asleep.");
            Check(world.Locomotion.HibernateWakeup!.Wakeups == 0, "A refused awaken still submitted the native call.");
        });
        Case("awaken.refuses-the-policy-and-input-it-cannot-carry", () =>
        {
            using var world = new EnemyControlWorld();
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            Check(world.Awaken(new[] { world.Reference }, policy: "gradual").Code == "wake-policy-unsupported",
                "A gradual wake was not refused.");
            Check(world.Awaken(new[] { world.Reference }, alertAmount: 0.5).Code == "alert-amount-unsupported",
                "A non-zero alert amount was not refused.");
            Check(world.Locomotion.HibernateWakeup!.Wakeups == 0, "A refused awaken still submitted the native call.");
        });
        Case("awaken.unknown-when-the-native-call-throws", () =>
        {
            using var world = new EnemyControlWorld();
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            world.Locomotion.HibernateWakeup!.OnActivate = () => throw new InvalidOperationException("native");
            var result = world.Awaken(new[] { world.Reference });
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown,
                "A throwing native call was not reported as an unknown commit.");
            Check(Row(result, 0).GetProperty("committed").GetString() == CommitStates.Unknown,
                "The row claimed a commit the native call did not make.");
        });
        Case("awaken.unknown-when-the-state-is-not-read-back", () =>
        {
            using var world = new EnemyControlWorld();
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            world.Locomotion.HibernateWakeup!.StayInWakeState = false;
            var result = world.Awaken(new[] { world.Reference });
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown,
                "A wake the locomotion machine did not take was reported as committed.");
            Check(Row(result, 0).GetProperty("code").GetString() == "wake-unseen", "The unseen wake lost its reason.");
        });
        Case("awaken.partial-over-two-recipients", () =>
        {
            using var world = new EnemyControlWorld(secondEnemy: true);
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            var result = world.Awaken(new[] { world.Reference, world.SecondReference! });
            Check(result.Status == CommandStatuses.Partial && result.CommitState == CommitStates.Confirmed,
                "One committed and one refused recipient was not reported as partial: " + result.Status + "/" + result.CommitState);
            Check(Row(result, 0).GetProperty("committed").GetString() == CommitStates.Confirmed
                && Row(result, 1).GetProperty("committed").GetString() == CommitStates.None,
                "The two rows do not report what each recipient did.");
            Check(world.Locomotion.HibernateWakeup!.Wakeups == 1, "The awake second recipient was written anyway.");
        });
        Case("awaken.stale-life-and-old-world-are-refused", () =>
        {
            using var world = new EnemyControlWorld();
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            Check(Code(world.Awaken(new[] { world.Reference with { LifeEpoch = world.Reference.LifeEpoch + 1 } }), 0)
                == "stale-or-unsupported-recipient", "A stale life was not refused.");
            // A despawn retires the tracked life, and every action then refuses the reference the same way.
            world.Module.Retire(world.Enemy);
            Check(Code(world.Awaken(new[] { world.Reference }), 0) == "stale-or-unsupported-recipient",
                "A retired life was not refused.");
            Check(Code(world.Sleep(new[] { world.Reference }), 0) == "stale-or-unsupported-recipient",
                "Sleep accepted a retired life.");
            Check(Code(world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d, 3d }), 0) == "stale-or-unsupported-recipient",
                "move_to accepted a retired life.");
            world.Kernel.BeginWorld(2);
            Check(Code(world.Awaken(new[] { world.Reference }), 0) == "stale-or-unsupported-recipient",
                "An old-world reference was not refused.");
            Check(world.Locomotion.HibernateWakeup!.Wakeups == 0, "A stale recipient still reached the native call.");
        });
        Case("awaken.authority-and-phase-gate", () =>
        {
            using var world = new EnemyControlWorld();
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            world.Allowed = false;
            Check(world.Awaken(new[] { world.Reference }).Code == "authority-or-phase", "The phase gate was bypassed.");
            world.Allowed = true;
            SNet.IsMaster = false;
            Check(world.Awaken(new[] { world.Reference }).Code == "authority-or-phase", "A client claimed the native write.");
            SNet.IsMaster = true;
            Check(world.Locomotion.HibernateWakeup!.Wakeups == 0, "A gated command still reached the native call.");
        });
    }

    private static void SleepCases()
    {
        Case("sleep.row-is-the-catalog-shape", () =>
        {
            using var world = new EnemyControlWorld();
            AssertShape(Capability(world, EnemyModule.SleepCapability), SleepShape);
            Check(Capability(world, EnemyModule.SleepCapability).GetProperty("graph").GetProperty("parameters")
                .EnumerateArray().Count(p => p.GetProperty("required").GetBoolean()) == 2, "Sleep's two policies are not both required.");
        });
        Case("sleep.immediate-commits-through-the-behaviour-machine", () =>
        {
            using var world = new EnemyControlWorld();
            var result = world.Sleep(new[] { world.Reference });
            Check(result.Status == CommandStatuses.Succeeded, "Sleep did not commit: " + result.Status + "/" + result.Code);
            Check(world.Behaviour.Changes.SequenceEqual(new[] { EB_States.Hibernating }), "The behaviour machine was not moved to Hibernating.");
            var row = Row(result, 0);
            Check(row.GetProperty("duration").GetInt32() == 0, "Sleep reported a duration the native path has no timer for.");
            Check(row.GetProperty("target_count").GetInt32() == 1, "Sleep's row target_count is not the recipient count.");
        });
        Case("sleep.refuses-a-timer-and-a-damage-only-interrupt", () =>
        {
            using var world = new EnemyControlWorld();
            Check(world.Sleep(new[] { world.Reference }, duration: 120).Code == "duration-unsupported",
                "A timed sleep was not refused.");
            Check(world.Sleep(new[] { world.Reference }, interrupt: "damage_only").Code == "interrupt-policy-unsupported",
                "An interrupt filter the game does not expose was not refused.");
            Check(world.Sleep(new[] { world.Reference }, policy: "when_ready").Code == "sleep-policy-unsupported",
                "An undeclared sleep policy was not refused.");
            Check(world.Behaviour.Changes.Count == 0, "A refused sleep still wrote the behaviour state.");
        });
        Case("sleep.when-idle-checks-the-game-own-idle", () =>
        {
            using var world = new EnemyControlWorld();
            world.Ai.m_mode = Agents.AgentMode.Patrolling;
            world.Ai.IsTargetValid = false;
            world.Ai.m_detection.m_biggestDetectionBuildup = 0;
            Check(world.Sleep(new[] { world.Reference }, policy: "when_idle").Status == CommandStatuses.Succeeded,
                "An idle enemy was refused by when_idle.");
            using var busy = new EnemyControlWorld();
            busy.Ai.m_mode = Agents.AgentMode.Agressive;
            busy.Ai.IsTargetValid = true;
            Check(Code(busy.Sleep(new[] { busy.Reference }, policy: "when_idle"), 0) == "not-idle",
                "An enemy in combat was put to sleep by when_idle.");
            Check(busy.Behaviour.Changes.Count == 0, "A refused when_idle still wrote the behaviour state.");
        });
        Case("sleep.refuses-an-enemy-that-is-already-asleep", () =>
        {
            using var world = new EnemyControlWorld();
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            Check(Code(world.Sleep(new[] { world.Reference }), 0) == "already-hibernating", "A sleeping enemy was put to sleep again.");
            Check(world.Behaviour.Changes.Count == 0, "A refused sleep still wrote the behaviour state.");
        });
        Case("sleep.partial-and-unknown-over-two-recipients", () =>
        {
            // The first recipient is already asleep, so the second is the only one written: one committed row and
            // one refused row are partial, and the refused recipient must not have been written.
            using var world = new EnemyControlWorld(secondEnemy: true);
            world.Behaviour.m_currentStateName = EB_States.Hibernating;
            var result = world.Sleep(new[] { world.Reference, world.SecondReference! });
            Check(result.Status == CommandStatuses.Partial && result.CommitState == CommitStates.Confirmed,
                "One committed and one refused recipient was not partial: " + result.Status + "/" + result.CommitState);
            Check(Row(result, 0).GetProperty("code").GetString() == "already-hibernating"
                && Row(result, 1).GetProperty("committed").GetString() == CommitStates.Confirmed,
                "The two rows do not report what each recipient did.");
            // Only the second recipient's machine was written: the first was already in the state the row claims.
            Check(world.Second!.AI.m_behaviour.m_currentStateName == EB_States.Hibernating
                && world.Behaviour.m_currentStateName == EB_States.Hibernating,
                "The refused recipient was written anyway.");
            // A native write that throws after it has been entered is an unknown commit, never a claimed one.
            using var throwing = new EnemyControlWorld();
            throwing.Behaviour.OnChange = _ => throw new InvalidOperationException("native");
            var unknown = throwing.Sleep(new[] { throwing.Reference });
            Check(unknown.Status == CommandStatuses.Failed && unknown.CommitState == CommitStates.Unknown,
                "A throwing native sleep was not an unknown commit.");
        });
        Case("sleep.authority-gate", () =>
        {
            using var world = new EnemyControlWorld();
            world.Allowed = false;
            Check(world.Sleep(new[] { world.Reference }).Code == "authority-or-phase", "The phase gate was bypassed.");
            world.Allowed = true;
            SNet.IsMaster = false;
            Check(world.Sleep(new[] { world.Reference }).Code == "authority-or-phase", "A client claimed the native write.");
            SNet.IsMaster = true;
            Check(world.Behaviour.Changes.Count == 0, "A gated command still wrote the behaviour state.");
        });
    }

    private static void MoveToCases()
    {
        Case("move_to.row-is-the-catalog-shape", () =>
        {
            using var world = new EnemyControlWorld();
            AssertShape(Capability(world, EnemyModule.MoveToCapability), MoveToShape);
            var inputs = Capability(world, EnemyModule.MoveToCapability).GetProperty("graph").GetProperty("inputs").EnumerateArray().ToArray();
            Check(inputs.Single(p => p.GetProperty("id").GetString() == "destination").GetProperty("type").GetString() == "vector3",
                "move_to's destination is not a vector3.");
            Check(inputs.Single(p => p.GetProperty("id").GetString() == "area").GetProperty("resourceKind").GetString() == "area_field",
                "move_to's area resource kind drifted from the catalog.");
        });
        Case("move_to.writes-the-goal-every-native-move-writes", () =>
        {
            using var world = new EnemyControlWorld();
            var result = world.MoveTo(new[] { world.Reference }, new[] { 12.5d, 3.0d, -4.0d });
            Check(result.Status == CommandStatuses.Succeeded, "move_to did not commit: " + result.Status + "/" + result.Code);
            Check(Math.Abs(world.Ai.NavmeshAgentGoal.x - 12.5f) < 0.001f
                && Math.Abs(world.Ai.NavmeshAgentGoal.y - 3.0f) < 0.001f
                && Math.Abs(world.Ai.NavmeshAgentGoal.z + 4.0f) < 0.001f, "The navigation goal was not written.");
            Check(Row(result, 0).GetProperty("speed").ValueKind == JsonValueKind.Null,
                "move_to reported a speed it was never asked for.");
            Check(Row(result, 0).GetProperty("target_count").GetInt32() == 1, "move_to's row target_count is not the recipient count.");
        });
        Case("move_to.applies-and-reads-back-a-requested-speed", () =>
        {
            using var world = new EnemyControlWorld();
            var result = world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d, 3d }, speed: 4.5);
            Check(result.Status == CommandStatuses.Succeeded, "move_to with a speed did not commit.");
            Check(Math.Abs(world.Ai.m_navMeshAgent.speed - 4.5f) < 0.001f, "The requested speed was not applied.");
            Check(Math.Abs(Row(result, 0).GetProperty("speed").GetDouble() - 4.5) < 0.001, "The row did not report the speed it applied.");
        });
        Case("move_to.refuses-what-the-navigation-contract-cannot-carry", () =>
        {
            using var world = new EnemyControlWorld();
            Check(world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d, 3d }, speed: 0.0001).Code == "speed-out-of-range",
                "A speed outside the declared band was accepted.");
            Check(world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d, 3d }, speed: 5000).Code == "speed-out-of-range",
                "An absurd speed was accepted.");
            Check(world.MoveTo(new[] { world.Reference }, new[] { 1e9d, 2d, 3d }).Code == "invalid-destination",
                "A destination outside the accepted band was accepted.");
            Check(world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d }).Code == "invalid-destination",
                "A two-component destination is not the vector3 the contract declares.");
            Check(world.Ai.NavmeshAgentGoal.x == 0 && world.Ai.NavmeshAgentGoal.y == 0 && world.Ai.NavmeshAgentGoal.z == 0,
                "A refused move_to wrote a goal before validating.");
        });
        Case("move_to.refuses-an-agent-without-navigation", () =>
        {
            using var world = new EnemyControlWorld();
            world.Ai.m_navMeshAgent = null!;
            Check(Code(world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d, 3d }, speed: 4.5), 0) == "navigation-unavailable",
                "A speed was requested of an agent with no navigation component.");
        });
        Case("move_to.unknown-when-the-goal-is-not-read-back", () =>
        {
            using var world = new EnemyControlWorld();
            world.Ai.DropGoalWrites = true;
            var result = world.MoveTo(new[] { world.Reference }, new[] { 9d, 9d, 9d });
            Check(result.Status == CommandStatuses.Failed && result.CommitState == CommitStates.Unknown,
                "A goal the AI did not keep was reported as committed.");
            Check(Row(result, 0).GetProperty("code").GetString() == "unexpected-goal-readback", "The unseen goal lost its reason.");
        });
        Case("move_to.authority-gate", () =>
        {
            using var world = new EnemyControlWorld();
            world.Allowed = false;
            Check(world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d, 3d }).Code == "authority-or-phase", "The phase gate was bypassed.");
            world.Allowed = true;
            SNet.IsMaster = false;
            Check(world.MoveTo(new[] { world.Reference }, new[] { 1d, 2d, 3d }).Code == "authority-or-phase", "A client claimed the native write.");
            SNet.IsMaster = true;
            Check(world.Ai.NavmeshAgentGoal.x == 0 && world.Ai.NavmeshAgentGoal.y == 0, "A gated command still wrote the goal.");
        });
    }

    private static void AssertShape(JsonElement capability, string catalogShape)
    {
        var expected = RuntimeJson.Parse(catalogShape);
        var graph = capability.GetProperty("graph");
        var inputs = Ids(graph.GetProperty("inputs"));
        var outputs = Ids(graph.GetProperty("outputs"));
        var parameters = Ids(graph.GetProperty("parameters"));
        var names = expected.GetProperty("inputs").EnumerateArray().Select(v => v.GetString()!).ToArray();
        Check(inputs.SequenceEqual(names), "Declared inputs drifted from the catalog: " + string.Join(",", inputs));
        names = expected.GetProperty("outputs").EnumerateArray().Select(v => v.GetString()!).ToArray();
        Check(outputs.SequenceEqual(names), "Declared outputs drifted from the catalog: " + string.Join(",", outputs));
        names = expected.GetProperty("parameters").EnumerateArray().Select(v => v.GetString()!).ToArray();
        Check(parameters.SequenceEqual(names), "Declared parameters drifted from the catalog: " + string.Join(",", parameters));
        var result = graph.GetProperty("outputs").EnumerateArray().Single(p => p.GetProperty("id").GetString() == "result");
        var fields = Ids(result.GetProperty("fields"));
        names = expected.GetProperty("resultFields").EnumerateArray().Select(v => v.GetString()!).ToArray();
        Check(fields.SequenceEqual(names), "Declared result fields drifted from the catalog: " + string.Join(",", fields));
    }

    /// <summary>The capability row as declared, read from the contract text through the SDK's own JSON reader.</summary>
    private static JsonElement Capability(EnemyControlWorld world, string id)
    {
        var rows = RuntimeJson.Parse("{\n  \"providers\": [],\n  \"capabilities\": [\n"
            + string.Join(",", EnemyControlContract.CapabilityRowText()) + "\n  ],\n  \"bindings\": []\n}");
        return rows.GetProperty("capabilities").EnumerateArray()
            .Single(row => row.GetProperty("id").GetString() == id);
    }

    /// <summary>The binding that implements one capability. The provider's own row set is the only place a
    /// binding is declared, so the binding is found by the capability it names.</summary>
    private static JsonElement Binding(EnemyControlWorld world, string capabilityId)
    {
        var rows = RuntimeJson.Parse("{\n  \"providers\": [],\n  \"capabilities\": [],\n  \"bindings\": [\n"
            + string.Join(",", EnemyControlContract.BindingRowText()) + "\n  ]\n}");
        return rows.GetProperty("bindings").EnumerateArray()
            .Single(b => b.GetProperty("capabilityId").GetString() == capabilityId);
    }

    private static IEnumerable<string> Ids(JsonElement rows)
        => rows.EnumerateArray().Select(row => row.GetProperty("id").GetString()!);
}
