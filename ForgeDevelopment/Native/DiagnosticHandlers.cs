using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;
using ForgeRuntime.Framework;

namespace ForgeDevelopment.Native;

/// <summary>One record a diagnostic row decided to keep. The handlers hand the decision to this type and nothing
/// else: the diagnostic pipeline behind <see cref="DevelopmentModule.Sink"/> owns the report file, the classifier
/// and the write queue, so a row never opens a log of its own.</summary>
internal sealed record DiagnosticRecord(string Category, string Stage, string Subject,
    Dictionary<string, string> Fields, double ElapsedMs);

/// <summary>
/// The four diagnostic handlers this build registers. Each one decides inside the session ledger and writes one
/// record through the sink; none of them reads or writes world state, publishes a fact, or reaches the game, and
/// `inspect`'s only read is the kernel's own budgeted entity inspection.
///
/// Every result row this file writes is a row of the row's own schema: the four fixed columns first, then the
/// columns that row declares. The command result itself is non-committing — the `presentation` tier requires
/// exactly that, because a presentation step never commits world state — so the kernel accepts these results and
/// the plan reads the row's own `status`/`committed`/`code` columns for the detail.
/// </summary>
internal sealed class DiagnosticHandlers
{
    private const string Refusal = CommandStatuses.Rejected;
    private const string Partial = CommandStatuses.Partial;

    private readonly RuntimeKernel _kernel;
    private readonly Func<DiagnosticSessions?> _ledger;
    private readonly Action<DiagnosticRecord> _sink;

    internal DiagnosticHandlers(RuntimeKernel kernel, Func<DiagnosticSessions?> ledger, Action<DiagnosticRecord> sink)
    {
        _kernel = kernel;
        _ledger = ledger;
        _sink = sink;
    }

    /// <summary>`forge.action.diagnostic.trace`: one record per execution carrying the plan, node, event and cause
    /// identities the row's own `effectRequirements` names, sampled at the step's rate and charged to the session's
    /// per-tick budget. A record the budget refuses is counted, and the row reports `partial` with that count
    /// instead of claiming a complete trace.</summary>
    internal CommandResult Trace(CommandContext context)
    {
        if (!TrySession(context, out var sessions, out var token, out var refusal)) return refusal;
        if (!TrySampleRate(context, out var sampleRate, out refusal)) return refusal;
        if (!sessions.TrySample(token, sampleRate, out var sampled, out var code))
            return Row(context, Refusal, code, sampleRate);
        if (!sampled) return Row(context, CommandStatuses.Succeeded, "sampled-out", sampleRate);
        if (!sessions.TryRecord(token, out code))
        {
            _sink(new DiagnosticRecord("diagnostic_trace", context.NodeId, context.PlanId, Fields(context), 0));
            return Row(context, Partial, code, sampleRate);
        }
        _sink(new DiagnosticRecord("diagnostic_trace", context.NodeId, context.PlanId, Fields(context), 0));
        return Row(context, CommandStatuses.Succeeded, "traced", sampleRate);
    }

    /// <summary>`forge.action.diagnostic.metric`: one sample folded into the session's aggregate for that node.
    /// `window` is the number of ticks one aggregate covers before it restarts, `sample_rate` is the session's own
    /// rate, and the running aggregate travels back in the record so a session's log is readable on its own.</summary>
    internal CommandResult Metric(CommandContext context)
    {
        if (!TrySession(context, out var sessions, out var token, out var refusal)) return refusal;
        if (!TrySampleRate(context, out var sampleRate, out refusal)) return refusal;
        if (!TryInteger(context, "window", 1, out var window, out refusal)) return refusal;
        if (!TryNumber(context, "value", out var value, out refusal)) return refusal;
        if (!sessions.TrySample(token, sampleRate, out var sampled, out var code))
            return Row(context, Refusal, code, sampleRate);
        if (!sampled) return Row(context, CommandStatuses.Succeeded, "sampled-out", sampleRate);
        if (!sessions.TryRecord(token, out code))
        {
            _sink(new DiagnosticRecord("diagnostic_metric", context.NodeId, context.PlanId, Fields(context), 0));
            return Row(context, Partial, code, sampleRate);
        }
        var name = context.NodeId + "@" + context.PlanId;
        if (!sessions.TryMetric(token, name, value, window, out var snapshot, out code))
            return Row(context, Refusal, code, sampleRate);
        var fields = Fields(context);
        fields["metric"] = snapshot.Name;
        fields["window"] = window.ToString(CultureInfo.InvariantCulture);
        fields["samples"] = snapshot.Samples.ToString(CultureInfo.InvariantCulture);
        fields["sum"] = Number(snapshot.Sum);
        fields["minimum"] = Number(snapshot.Minimum);
        fields["maximum"] = Number(snapshot.Maximum);
        fields["last"] = Number(snapshot.Last);
        _sink(new DiagnosticRecord("diagnostic_metric", context.NodeId, context.PlanId, fields, 0));
        return Row(context, CommandStatuses.Succeeded, "metric-recorded", sampleRate);
    }

    /// <summary>`forge.action.diagnostic.assert`: the two policies this build really implements. `report` writes
    /// the record and continues; `record` writes the same record and counts the failure into the session's own
    /// tally. Any other member — a halt in particular — is refused by name, because the kernel owns no pause state
    /// and this row will not pretend to stop a plan it cannot stop.</summary>
    internal CommandResult Assert(CommandContext context)
    {
        if (!TrySession(context, out var sessions, out var token, out var refusal)) return refusal;
        if (!TryPolicy(context, "policy", new[] { "report", "record" }, out var policy, out refusal)) return refusal;
        if (!TrySeverity(context, out var severity, out refusal)) return refusal;
        if (!context.Inputs.TryGetProperty("condition", out var condition)
            || condition.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            return Row(context, Refusal, "diagnostic-condition-invalid", 0);
        var message = context.Inputs.TryGetProperty("message", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString() ?? "" : "";
        var failed = !condition.GetBoolean();
        var fields = Fields(context);
        fields["policy"] = policy;
        fields["severity"] = severity;
        fields["outcome"] = failed ? "failed" : "held";
        fields["condition"] = condition.GetBoolean() ? "true" : "false";
        fields["failures"] = sessions.FailedAssertions.ToString(CultureInfo.InvariantCulture);
        if (message.Length != 0) fields["message"] = message;
        sessions.CountAssertion(token, failed);
        _sink(new DiagnosticRecord("diagnostic_assert", context.NodeId, context.PlanId, fields, 0));
        if (!failed) return Row(context, CommandStatuses.Succeeded, "assert-held", 0);
        return Row(context, policy == "record" ? Partial : Refusal,
            policy == "record" ? "assert-failed" : "assert-reported", 0);
    }

    /// <summary>`forge.action.diagnostic.inspect`: a read-only snapshot of the referenced entities projected onto
    /// the field names the row asked for. The read goes through the kernel's own budgeted entity inspection, so an
    /// unavailable observer is a refusal and never an empty snapshot; the snapshot itself is the text the row's
    /// `snapshot` port declares.</summary>
    internal CommandResult Inspect(CommandContext context)
    {
        if (!TryInteger(context, "budget", 0, out var budget, out var refusal)) return refusal;
        if (!TryTargets(context, out var targets, out refusal)) return refusal;
        if (budget <= 0 || budget > DiagnosticSessions.MaximumInspectedEntities)
            return InspectResult(context, Refusal, "diagnostic-budget-invalid", budget, targets.Count, "");
        if (targets.Count > budget)
            return InspectResult(context, Partial, DiagnosticCodes.Budget, budget, targets.Count, "");
        var declared = context.Inputs.TryGetProperty("fields", out var fields) && fields.ValueKind == JsonValueKind.String
            ? fields.GetString() ?? "" : "";
        if (!DiagnosticProjection.TryParse(declared, out var projection, out var projectionCode))
            return InspectResult(context, Refusal, projectionCode, budget, targets.Count, "");
        var inspection = _kernel.InspectEntities(targets);
        var text = new StringBuilder();
        var unavailable = 0;
        foreach (var item in inspection.Items)
        {
            if (item.Snapshot == null) { unavailable++; continue; }
            text.Append(DiagnosticProjection.Render(item.Snapshot, projection)).Append('\n');
        }
        var status = unavailable == 0 ? CommandStatuses.Succeeded : Partial;
        var code = unavailable == 0 ? "inspected" : DiagnosticCodes.EntityUnavailable;
        _sink(new DiagnosticRecord("diagnostic_inspect", context.NodeId, context.PlanId, Fields(context), 0));
        return InspectResult(context, status, code, budget, targets.Count, text.ToString());
    }

    // ---- shared plumbing -------------------------------------------------------------------------------

    private static CommandResult Rejected(JsonElement outputs, string code)
        => CommandResult.Create(CommandStatuses.Rejected, CommitStates.None, code, "", outputs);

    private bool TrySession(CommandContext context, out DiagnosticSessions sessions,
        out DiagnosticSessions.SessionToken token, out CommandResult refusal)
    {
        refusal = null!;
        token = default;
        sessions = _ledger()!;
        if (sessions == null)
        {
            refusal = Row(context, Refusal, DiagnosticCodes.SessionMissing, 0);
            return false;
        }
        if (!context.Inputs.TryGetProperty("session", out var session))
        {
            refusal = Row(context, Refusal, DiagnosticCodes.SessionMissing, 0);
            return false;
        }
        if (sessions.TryResolve(session, out token, out var code)) return true;
        refusal = Row(context, Refusal, code, 0);
        return false;
    }

    private static bool TrySampleRate(CommandContext context, out int sampleRate, out CommandResult refusal)
    {
        refusal = null!;
        sampleRate = 1;
        if (!context.Inputs.TryGetProperty("sample_rate", out var value)) return true;
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt32(out sampleRate) || sampleRate < 1)
        {
            refusal = Row(context, Refusal, DiagnosticCodes.SampleRateInvalid, sampleRate < 1 ? 0 : sampleRate);
            return false;
        }
        return true;
    }

    private static bool TryInteger(CommandContext context, string port, int minimum, out int value, out CommandResult refusal)
    {
        refusal = null!;
        value = minimum;
        if (!context.Inputs.TryGetProperty(port, out var input) || input.ValueKind != JsonValueKind.Number
            || !input.TryGetInt32(out value) || value < minimum)
        {
            refusal = Row(context, Refusal, "diagnostic-argument-invalid", 0);
            return false;
        }
        return true;
    }

    private static bool TryNumber(CommandContext context, string port, out double value, out CommandResult refusal)
    {
        refusal = null!;
        value = 0;
        if (!context.Inputs.TryGetProperty(port, out var input) || input.ValueKind != JsonValueKind.Number
            || !input.TryGetDouble(out value) || !double.IsFinite(value))
        {
            refusal = Row(context, Refusal, "diagnostic-argument-invalid", 0);
            return false;
        }
        return true;
    }

    private static bool TryTargets(CommandContext context, out IReadOnlyList<EntityReference> targets, out CommandResult refusal)
    {
        refusal = null!;
        var parsed = new List<EntityReference>();
        if (!context.Inputs.TryGetProperty("targets", out var declared) || declared.ValueKind != JsonValueKind.Array)
        {
            targets = parsed;
            refusal = Row(context, Refusal, "diagnostic-targets-missing", 0);
            return false;
        }
        try
        {
            foreach (var item in declared.EnumerateArray()) parsed.Add(RuntimeJson.Entity(item));
        }
        catch (RuntimeContractException error)
        {
            targets = parsed;
            refusal = Row(context, Refusal, error.Code, 0);
            return false;
        }
        targets = parsed;
        return true;
    }

    private static bool TryPolicy(CommandContext context, string parameter, string[] supported, out string policy, out CommandResult refusal)
    {
        refusal = null!;
        policy = supported[0];
        var value = context.Parameters.TryGetProperty(parameter, out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString() : null;
        if (value == null) return true;
        foreach (var candidate in supported)
            if (string.Equals(candidate, value, StringComparison.Ordinal)) { policy = candidate; return true; }
        refusal = Row(context, Refusal, DiagnosticCodes.PolicyUnsupported, 0);
        return false;
    }

    private static bool TrySeverity(CommandContext context, out string severity, out CommandResult refusal)
    {
        refusal = null!;
        severity = "info";
        var value = context.Parameters.TryGetProperty("severity", out var declared) && declared.ValueKind == JsonValueKind.String
            ? declared.GetString() : null;
        if (value == null) return true;
        if (value is "info" or "warning" or "error") { severity = value; return true; }
        refusal = Row(context, Refusal, "diagnostic-severity-unknown", 0);
        return false;
    }

    /// <summary>The identities one record carries: the row's own `effectRequirements` name them, and every one of
    /// them is a value the dispatch handed the handler rather than something re-derived here.</summary>
    private static Dictionary<string, string> Fields(CommandContext context) => new(StringComparer.Ordinal)
    {
        ["graphId"] = context.ResourceId,
        ["planId"] = context.PlanId,
        ["nodeId"] = context.NodeId,
        ["eventId"] = context.EventId,
        ["causeId"] = context.CauseId ?? "",
        ["commandId"] = context.CommandId,
        ["scopeId"] = context.ScopeId,
        ["worldEpoch"] = context.WorldEpoch.ToString(CultureInfo.InvariantCulture),
        ["tick"] = context.SimulationTick.ToString(CultureInfo.InvariantCulture)
    };

    private static string Number(double value) => value.ToString("R", CultureInfo.InvariantCulture);

    // ---- result rows ------------------------------------------------------------------------------------

    /// <summary>The `target` column: the first entity the row addressed, by the reference's own id, or null when
    /// the row addressed none. A row that could not read its references reports null rather than a placeholder.</summary>
    private static string? Target(CommandContext context)
    {
        if (!context.Inputs.TryGetProperty("targets", out var targets) || targets.ValueKind != JsonValueKind.Array
            || targets.GetArrayLength() == 0) return null;
        try { return RuntimeJson.Entity(targets[0]).Id; }
        catch (RuntimeContractException) { return null; }
    }

    private static CommandResult Row(CommandContext context, string status, string code, int sampleRate)
        => Rejected(RuntimeJson.From(new
        {
            results = new[] { new TraceRow(Target(context), status, CommitStates.None, code, sampleRate) }
        }), code);

    private static CommandResult InspectResult(CommandContext context, string status, string code, int budget,
        int targetCount, string snapshot)
        => Rejected(RuntimeJson.From(new
        {
            results = new[] { new InspectRow(Target(context), status, CommitStates.None, code, budget, targetCount) },
            snapshot
        }), code);

    /// <summary>The `trace`/`metric`/`assert` row: the four fixed columns plus the `sample_rate` column those
    /// three rows declare. Camel-cased on the wire by the serializer's own naming policy, which is the same
    /// mapping every other result row in this repository is written through.</summary>
    private sealed record TraceRow(string? Target, string Status, string Committed, string Code, int SampleRate);

    /// <summary>The `inspect` row: the four fixed columns plus its own `budget` and `target_count`.</summary>
    private sealed record InspectRow(string? Target, string Status, string Committed, string Code, int Budget, int TargetCount);
}

/// <summary>
/// The `fields` projection one `inspect` snapshot is rendered through. The names are exactly the entity
/// observation contract's own columns, so a field the runtime cannot read is refused by name rather than answered
/// with a placeholder; an empty declaration means every column.
/// </summary>
internal static class DiagnosticProjection
{
    private static readonly string[] All = { "kind", "faction", "lifeState", "tags", "receives", "position" };

    internal static bool TryParse(string declared, out string[] fields, out string code)
    {
        if (string.IsNullOrWhiteSpace(declared)) { fields = All; code = "inspected"; return true; }
        var parsed = new List<string>();
        foreach (var part in declared.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var matched = false;
            foreach (var name in All)
                if (string.Equals(name, part, StringComparison.Ordinal)) { matched = true; break; }
            if (!matched) { fields = Array.Empty<string>(); code = DiagnosticCodes.FieldsUnknown; return false; }
            if (!parsed.Contains(part)) parsed.Add(part);
        }
        fields = parsed.Count == 0 ? All : parsed.ToArray();
        code = "inspected";
        return true;
    }

    internal static string Render(RuntimeEntitySnapshot snapshot, string[] fields)
    {
        var text = new StringBuilder();
        text.Append("ref=").Append(snapshot.Ref.Id);
        foreach (var field in fields)
        {
            text.Append(';').Append(field).Append('=');
            switch (field)
            {
                case "kind": text.Append(snapshot.Kind); break;
                case "faction": text.Append(snapshot.Faction ?? "none"); break;
                case "lifeState": text.Append(snapshot.LifeState); break;
                case "tags": text.Append(string.Join('|', snapshot.Tags)); break;
                case "receives": text.Append(string.Join('|', snapshot.Receives)); break;
                case "position":
                    text.Append(snapshot.Position[0].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(snapshot.Position[1].ToString("R", CultureInfo.InvariantCulture)).Append(',')
                        .Append(snapshot.Position[2].ToString("R", CultureInfo.InvariantCulture));
                    break;
            }
        }
        return text.ToString();
    }
}
