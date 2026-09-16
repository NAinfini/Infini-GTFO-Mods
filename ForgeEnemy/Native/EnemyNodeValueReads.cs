using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using Enemies;
using ForgeEnemy.Native.Observation;
using ForgeRuntime.Framework;

namespace ForgeEnemy.Native;

/// <summary>The six read-only value rows of the enemy domain (`v-e-health`, `v-e-alive`, `v-e-type`, `v-e-sleep`,
/// `v-e-where`, `v-e-tagged`).
///
/// Every one of them is a `query` step, and every one of them reads the world exactly once through the kernel's
/// budgeted session: the enemy it is about is resolved through `TrySnapshot`, which is what charges the query
/// budget and proves the reference still names a life. Four rows answer entirely from that snapshot — health and
/// its maximum, the life state, the behaviour state's own `aiState` member, and the position. The remaining two
/// need one reading the shared frame does not carry, and each is read from the same life the snapshot just
/// verified, with the instance re-checked afterwards: the official enemy type (`EnemyTypeReader`) and the tag
/// flag with its remaining seconds (`EnemyEntityObserver.ReadTag`). The zone `where` answers comes from the
/// kernel's own zone read (`RuntimeQuerySession.TryZone`), which is the provider's registered
/// `EntityZones` responder and the only zone path this package has.
///
/// A value that cannot be read is a refusal with a code, never a zero and never an empty answer: a snapshot
/// without health means the provider does not publish health for that life (`health-unavailable`), a behaviour
/// state no `ai_state` member covers is `aiState == "disabled"` and is refused rather than reported as awake, and
/// a life whose type block does not read has no type (`enemy-type-unavailable`). The one port that is genuinely
/// nullable is `where`'s `zone`: an enemy standing outside every zone is answered with JSON null, which is an
/// observation, and a zone read that could not be made refuses before the frame is built.
///
/// The evaluator table is built here and handed to the module's own registration, so the row names and the
/// handlers that answer them cannot drift apart.</summary>
internal sealed class EnemyNodeValueReads
{
    /// <summary>A value read with no enemy domain behind it. It is a refusal and not an empty value: the rows
    /// exist, this session just cannot read them.</summary>
    internal const string SourceUnavailableCode = "enemy-value-source-unavailable";
    internal const string HealthUnavailableCode = "health-unavailable";
    internal const string PositionUnavailableCode = "position-unavailable";
    internal const string TypeUnavailableCode = "enemy-type-unavailable";
    internal const string StateUnavailableCode = "enemy-state-unavailable";
    internal const string TagUnavailableCode = "enemy-tag-unavailable";
    internal const string MissingFieldCode = "missing-field";

    /// <summary>The member of the shared `ai_state` set that means "no state an author can act on". A behaviour
    /// machine that does not read is published as this member by the observer, and a question about sleep cannot
    /// be answered from it.</summary>
    private const string DisabledState = "disabled";
    /// <summary>The one member of that set which is hibernation; the observer maps both native sleep states to
    /// it, so the value row and `forge.trigger.enemy.awakened` cannot disagree about what sleep is.</summary>
    private const string HibernatingState = "hibernating";

    internal static EnemyNodeValueReads? Installed { get; private set; }

    private readonly RuntimeKernel _kernel;
    private readonly Func<EntityReference, EnemyAgent?> _enemyOf;
    private readonly int _threadId = Environment.CurrentManagedThreadId;

    /// <summary>`enemyOf` is the module's own current-instance lookup for `gtfo.enemy`: the one way this layer
    /// reaches a native agent, and the same lookup the module's own actions resolve recipients through.</summary>
    internal EnemyNodeValueReads(RuntimeKernel kernel, Func<EntityReference, EnemyAgent?> enemyOf)
    {
        _kernel = kernel ?? throw new ArgumentNullException(nameof(kernel));
        _enemyOf = enemyOf ?? throw new ArgumentNullException(nameof(enemyOf));
    }

    internal static EnemyNodeValueReads Attach(RuntimeKernel kernel, Func<EntityReference, EnemyAgent?> enemyOf)
    {
        Installed = new EnemyNodeValueReads(kernel, enemyOf);
        return Installed;
    }

    internal static void Detach(EnemyNodeValueReads reads)
    {
        if (ReferenceEquals(Installed, reads)) Installed = null;
    }

    /// <summary>The evaluator table this provider's registration composes: one entry per value row, each name
    /// declared with a shape in `EnemyNodeValueContract.ValueShapes`.</summary>
    internal static IReadOnlyDictionary<string, EvaluatorHandler> Evaluators() => new Dictionary<string, EvaluatorHandler>(StringComparer.Ordinal)
    {
        [EnemyNodeValueContract.HealthHandler] = Evaluate(Health),
        [EnemyNodeValueContract.AliveHandler] = Evaluate(Alive),
        [EnemyNodeValueContract.TypeHandler] = Evaluate(Type),
        [EnemyNodeValueContract.SleepingHandler] = Evaluate(Sleeping),
        [EnemyNodeValueContract.WhereHandler] = Evaluate(Where),
        [EnemyNodeValueContract.TaggedHandler] = Evaluate(Tagged)
    };

    private static EvaluatorHandler Evaluate(Func<EnemyNodeValueReads, EvaluationContext, EntityReference, RuntimeEntitySnapshot, JsonElement> answer)
        => context =>
        {
            if (Installed is not { } reads) throw new RuntimeContractException(SourceUnavailableCode, "No enemy value source is attached.");
            reads.CheckThread();
            var (reference, snapshot) = Read(context);
            return answer(reads, context, reference, snapshot);
        };

    /// <summary>The one budgeted read every value row starts with: the enemy the row is about, resolved through
    /// the session's own snapshot read. The row's `enemy` port is required, so a frame without one is refused by
    /// name; the entity is the row's, never the event's or the mount's.</summary>
    private static (EntityReference Reference, RuntimeEntitySnapshot Snapshot) Read(EvaluationContext context)
    {
        if (!context.Inputs.TryGetProperty(EnemyNodeValueContract.EnemyPort, out var value)
            || value.ValueKind != JsonValueKind.Object)
            throw new RuntimeContractException(MissingFieldCode, EnemyNodeValueContract.EnemyPort);
        var reference = RuntimeJson.Entity(value);
        if (!context.Query.TrySnapshot(reference, out var snapshot, out var code))
            throw new RuntimeContractException(code, "Enemy read failed: " + code);
        return (reference, snapshot!);
    }

    // The six answers, over the one budgeted read the evaluator already made. They are internal rather than
    // private so this package's focused tests can drive a row with a written snapshot and a written instance: a
    // value row's whole contract is which ports it carries and which refusal it raises, and neither needs a
    // dispatch to be exercised.

    internal static JsonElement AnswerHealth(RuntimeEntitySnapshot snapshot)
    {
        if (snapshot.Health is not { } current || snapshot.HealthMaximum is not { } maximum)
            throw new RuntimeContractException(HealthUnavailableCode, "This provider publishes no health for the life.");
        return RuntimeJson.From(new { value = current, maximum });
    }

    /// <summary>The life state is the one place the provider publishes whether an enemy is alive, so the answer
    /// is its own comparison: `alive` is alive, every other state the frame can carry is not.</summary>
    internal static JsonElement AnswerAlive(RuntimeEntitySnapshot snapshot)
        => RuntimeJson.From(new { value = snapshot.LifeState == "alive" });

    /// <summary>The official type id as decimal text — the same spelling the `enemy-type` mount matcher compares,
    /// so a value an author reads and a mount an author writes are one string.</summary>
    internal static JsonElement AnswerType(uint? type)
    {
        if (type is not { } id)
            throw new RuntimeContractException(TypeUnavailableCode, "This life's enemy block does not read.");
        return RuntimeJson.From(new { value = id.ToString(CultureInfo.InvariantCulture) });
    }

    /// <summary>Sleep is the snapshot's `aiState` compared with the shared set's own hibernation member. A frame
    /// with no behaviour state answers `disabled`, which is not an observation about sleep and is refused.</summary>
    internal static JsonElement AnswerSleeping(RuntimeEntitySnapshot snapshot)
    {
        if (snapshot.AiState is not { } state || state == DisabledState)
            throw new RuntimeContractException(StateUnavailableCode, "This life's behaviour state does not read.");
        return RuntimeJson.From(new { value = state == HibernatingState });
    }

    /// <summary>Position is the frame's own vector; the zone is the kernel's zone read, which answers through
    /// this provider's registered `EntityZones` responder. A frame without three coordinates refuses, and so does
    /// a life this provider cannot place — a course node, zone or layer that does not read, or a course node that
    /// names no zone at all — with the provider's own code, so "cannot be placed" never arrives as an absent zone
    /// a selector would drop the entity over.</summary>
    internal static JsonElement AnswerWhere(EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot)
    {
        if (snapshot.Position.Count != 3)
            throw new RuntimeContractException(PositionUnavailableCode, "This provider publishes no position for the life.");
        if (!context.Query.TryZone(reference, out var zone, out var code))
            throw new RuntimeContractException(code, "This life's zone does not read.");
        return RuntimeJson.From(new
        {
            position = new[] { snapshot.Position[0], snapshot.Position[1], snapshot.Position[2] },
            zone
        });
    }

    private static JsonElement AnswerTagged(EnemyEntityObserver.TagReading? tag)
    {
        if (tag is not { } reading)
            throw new RuntimeContractException(TagUnavailableCode, "This life's tag state does not read.");
        return RuntimeJson.From(new { value = reading.Tagged, remaining = (double)reading.RemainingSeconds });
    }

    private static JsonElement Health(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerHealth(snapshot);

    private static JsonElement Alive(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerAlive(snapshot);

    private static JsonElement Type(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerType(reads.ReadType(reference));

    private static JsonElement Sleeping(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerSleeping(snapshot);

    private static JsonElement Where(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerWhere(context, reference, snapshot);

    private static JsonElement Tagged(EnemyNodeValueReads reads, EvaluationContext context, EntityReference reference, RuntimeEntitySnapshot snapshot)
        => AnswerTagged(reads.ReadTag(reference));

    /// <summary>The type read: the native instance the module answers for the reference, re-checked after the
    /// block getter ran, because a native callback may replace an instance while it is read.</summary>
    private uint? ReadType(EntityReference reference)
    {
        var enemy = _enemyOf(reference);
        if (enemy == null) return null;
        var type = EnemyTypeReader.Read(enemy);
        return ReferenceEquals(_enemyOf(reference), enemy) ? type : null;
    }

    private EnemyEntityObserver.TagReading? ReadTag(EntityReference reference)
    {
        var enemy = _enemyOf(reference);
        if (enemy == null) return null;
        var tag = EnemyEntityObserver.ReadTag(enemy);
        return ReferenceEquals(_enemyOf(reference), enemy) ? tag : null;
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != _threadId)
            throw new RuntimeContractException("wrong-thread", "Enemy value reads require the runtime's own simulation thread.");
    }
}
