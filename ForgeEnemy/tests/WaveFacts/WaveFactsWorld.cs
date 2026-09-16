using System.Text.Json;
using Enemies;
using ForgeEnemy;
using ForgeEnemy.Native;
using ForgeRuntime.Framework;

/// <summary>One case's world: a kernel carrying the wave contract rows this package declares, the wave observer
/// over them, and a recording action to drive plans with. Nothing here is a loader, a session or a hook — the
/// observer is a plain object, so a case calls the same entry points the native callbacks call and reads the
/// frames that come out.
///
/// The world owns no game state: waves, groups and members are the suite's own doubles, and the wave table the
/// observer keeps is keyed by the native pointer and the EventID exactly as it is in the game.</summary>
internal sealed class WaveFactsWorld : IDisposable
{
    internal const long WorldEpoch = 7;

    private readonly RuntimeModuleHandle _enemyModule, _recorder;
    internal RuntimeKernel Kernel { get; }
    internal EnemyWaveFacts Facts { get; }
    internal List<string> Reports { get; } = new();
    internal List<CommandContext> Records { get; } = new();
    /// <summary>The lives this fixture has handed out, by the reference the observer answers with.</summary>
    internal readonly Dictionary<EnemyAgent, EntityReference> Lives = new();
    /// <summary>The observer's own gate; a case turns it off only to stand for a host that lost the role.</summary>
    internal bool CanObserve { get; set; } = true;
    internal ushort NextEventId { get; set; } = 1;
    private long _pointer = 100;
    private int _life;

    internal WaveFactsWorld()
    {
        Mastermind.Current = new Mastermind();
        Kernel = new RuntimeKernel(new("fixture.wave.facts", "1.0.0", RuntimeKernel.ApiVersion, "synthetic-no-game"));
        Kernel.BeginWorld(WorldEpoch);
        Kernel.RegisterModule(TriggerContracts.Module(), RuntimeLogLevel.Off);
        LocalPlan.OwnMounts(Kernel);
        _enemyModule = Kernel.RegisterModule(new RuntimeModule(RuntimeKernel.ApiVersion, EnemyRegistry(),
            new Dictionary<string, CommandHandler>(), EnemyWaveContract.Support(),
            new Dictionary<string, Func<EntityReference, bool>>
            {
                // The kind the batch's members are named in; without its owner the kernel refuses a frame that
                // carries one, exactly as it does in the game.
                ["gtfo.enemy"] = reference => Lives.ContainsValue(reference)
            }), RuntimeLogLevel.Off);
        _recorder = Kernel.RegisterModule(WaveRecorder.Module(context => Records.Add(context)), RuntimeLogLevel.Off);
        Facts = new EnemyWaveFacts(Kernel, _enemyModule, () => CanObserve, Reports.Add, ReferenceOf);
    }

    /// <summary>The registry seed the receiver's own module carries once the integration batch wires this family
    /// in: this provider's four binding rows, from the contract file, and no capability row at all — every
    /// `forge.trigger.*` row, the wave rows included, is declared by the trigger module this world registers
    /// first.</summary>
    private static string EnemyRegistry() => RuntimeJson.From(new
    {
        providers = new[] { new { id = ModuleDefinition.ProviderId, kind = "native", version = "1.0.0", dependencies = Array.Empty<string>() } },
        capabilities = Array.Empty<JsonElement>(),
        bindings = RuntimeJson.Parse("[" + EnemyWaveContract.BindingRowsJson + "]").EnumerateArray().ToArray()
    }).GetRawText();

    internal void Start() => Kernel.StartRuntime(() => { });

    /// <summary>One authoritative tick: the dispatched commands run here and land in <see cref="Records"/>.</summary>
    internal TickResult Tick(long tick) => Kernel.Advance(tick, true);

    /// <summary>A tick of a machine that is not the host, which is the phase the kernel's own publish gate refuses.</summary>
    internal TickResult TickAsClient(long tick) => Kernel.Advance(tick, false);

    /// <summary>A wave instance as the replicator hands it over: the EventID the wave's own spawn body assigns and
    /// the native pointer every later hook is matched against.</summary>
    internal SurvivalWave Wave(ushort eventId)
    {
        var wave = new SurvivalWave { EventID = eventId, Pointer = new IntPtr(_pointer++) };
        NextEventId = (ushort)(eventId + 1);
        return wave;
    }

    /// <summary>A member the receiver has a live life for: the reference the observer publishes is this one.</summary>
    internal EntityReference Member(EnemyAgent agent)
    {
        var reference = new EntityReference("gtfo.enemy:" + agent.GlobalID, WorldEpoch, ++_life);
        Lives[agent] = reference;
        return reference;
    }

    internal EnemyAgent Enemy(ushort id)
    {
        var agent = new EnemyAgent { GlobalID = id, Pointer = new IntPtr(_pointer++) };
        Member(agent);
        return agent;
    }

    /// <summary>A group the wave produced, carrying the members the game had put in it.</summary>
    internal EnemyGroup Group(params EnemyAgent[] members)
        => new() { Pointer = new IntPtr(_pointer++), Members = members.ToList() };

    /// <summary>The lives this fixture answers for; an agent no longer in the table is a despawned one, which is
    /// exactly what the observer must refuse to publish.</summary>
    private EntityReference? ReferenceOf(EnemyAgent? agent)
        => agent != null && Lives.TryGetValue(agent, out var reference) ? reference : null;

    internal void Retire(EnemyAgent agent) => Lives.Remove(agent);

    /// <summary>The Mastermind's own group list, which is what the cleared check reads.</summary>
    internal void SetActiveGroups(params EnemyGroup[] groups) => Mastermind.Current!.m_activeGroups = groups.ToList();

    /// <summary>The full native batch: one `SpawnGroup` call in which the wave registered
    /// <paramref name="delivered"/> groups. What the wave paid for is its own budget's business and no fact
    /// reports it, so the batch is the registered groups and nothing else.</summary>
    internal void RunBatch(SurvivalWave wave, params EnemyGroup[] delivered)
    {
        Facts.BeforeWaveGroupStep(wave);
        foreach (var group in delivered) Facts.AfterWaveGroup(wave, group);
        Facts.AfterWaveGroupStep(wave);
    }

    /// <summary>One plan per fact: the fact to subscribe to, plus whatever of its ports the case reads. Every one
    /// of these plans compiles the fixture's own wave reference into the recorder's `wave_resource` recipient,
    /// because a forge action has to receive something: the wave rows carry no entity port a plan could hand an
    /// action, and the one port that names the wave instance is declared optional and so cannot be wired at all.
    /// The ports the fact does carry are the ones an assertion reads.</summary>
    internal void LoadPlan(string planId, string triggerBinding, params (string EventOutput, string ActionInput)[] wires)
        => Load(planId, triggerBinding, WaveRecorder.WaveBindingId, wires);

    /// <summary>The same, over the recorder that takes a batch's members as its recipients.</summary>
    internal void LoadMembersPlan(string planId, string triggerBinding, params (string EventOutput, string ActionInput)[] wires)
        => Load(planId, triggerBinding, WaveRecorder.MembersBindingId, wires);

    private void Load(string planId, string triggerBinding, string actionBinding, (string EventOutput, string ActionInput)[] wires)
    {
        var literals = actionBinding == WaveRecorder.WaveBindingId
            ? new[] { ("wave_resource", (object)new { id = WaveRecorder.WaveResourceId, revision = "1" }) }
            : Array.Empty<(string, object)>();
        var plan = LocalPlan.Build(Kernel, planId, triggerBinding, actionBinding, wires, literals, RuntimeJson.EmptyObject,
            triggerParameters: WaveAddress());
        try { LocalPlan.Load(Kernel, plan); }
        catch (RuntimeContractException error)
        { throw new RuntimeContractException(error.Code, error.Code + " at " + error.Message + " in " + plan.Json); }
    }

    /// <summary>The author's wave address, which every wave row declares as a required structural `resource`
    /// parameter: the plan compiles it into the entrypoint's own constants frame, and a plan that leaves it null is
    /// refused with `missing-constant` before it can subscribe to anything. The fixture's reference is the same one
    /// the recorder step receives, because both name the wave the case observes.</summary>
    private static JsonElement WaveAddress()
        => RuntimeJson.From(new { resource = new { id = WaveRecorder.WaveResourceId, revision = "1" } });

    public void Dispose()
    {
        _recorder.Dispose();
        _enemyModule.Dispose();
        Mastermind.Current = null;
    }
}
