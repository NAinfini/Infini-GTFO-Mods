using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ForgeRuntime.Framework;
using HarmonyLib;

namespace ForgeDevelopment.Native;

/// <summary>
/// The Forge kernel's own records. The kernel already writes one <c>RuntimeLogRecord</c> per plan load, trigger,
/// command, step and world change; the host owns its log sink and offers no subscription, so the recorder installs a
/// postfix on the kernel's single record point and copies each record into the `forge` channel. The postfix is
/// installed by reflection and reports honestly when the host build does not carry the method: a kernel version that
/// moved its record point is a report line, not a silent hole in the session.
/// </summary>
internal static class RecForge
{
    private static readonly object Gate = new();
    private static readonly Dictionary<string, long> Codes = new(StringComparer.Ordinal);
    private static long _records;
    private static string? _failure;
    private static bool _installed;
    private static string _lastManifest = "";

    internal static bool Installed { get { lock (Gate) return _installed; } }
    internal static string? Failure { get { lock (Gate) return _failure; } }
    internal static long Records { get { lock (Gate) return _records; } }

    /// <summary>Installs the postfix. It is a manual patch because the target is the host's own private record
    /// point, not a game method: no attribute can name it and nothing else in this package should be patched this
    /// way. A failure is recorded and the recorder keeps working without the kernel channel.</summary>
    internal static void Start(Harmony harmony)
    {
        ArgumentNullException.ThrowIfNull(harmony);
        if (Installed) return;
        try
        {
            var kernel = typeof(RuntimeKernel);
            var target = kernel.GetMethod("WriteLog", BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(kernel.FullName, "WriteLog");
            var postfix = typeof(ForgeKernelPostfix).GetMethod(nameof(ForgeKernelPostfix.Postfix), BindingFlags.Static | BindingFlags.Public)
                ?? throw new MissingMethodException(typeof(ForgeKernelPostfix).FullName, "Postfix");
            harmony.Patch(target, postfix: new HarmonyMethod(postfix));
            lock (Gate) _installed = true;
        }
        catch (Exception error)
        {
            lock (Gate) _failure = error.GetType().Name + ": " + error.Message;
        }
    }

    internal static void Stop(Harmony? harmony)
    {
        if (!Installed || harmony == null) return;
        try
        {
            var target = typeof(RuntimeKernel).GetMethod("WriteLog", BindingFlags.Instance | BindingFlags.NonPublic);
            if (target != null) harmony.Unpatch(target, HarmonyPatchType.Postfix, harmony.Id);
        }
        catch (Exception error)
        {
            lock (Gate) _failure = "unpatch: " + error.Message;
        }
        lock (Gate) _installed = false;
    }

    /// <summary>Copies one kernel record into the channel. Everything the record carries is written as it is: the
    /// fields here are the kernel's own vocabulary, so a query can filter by code, plan, entry, step and binding
    /// without this package inventing a second name for any of them.</summary>
    internal static void Record(in RuntimeLogRecord record)
    {
        if (!RecSession.Active) return;
        lock (Gate)
        {
            _records++;
            Codes[record.Code ?? "unknown"] = Codes.TryGetValue(record.Code ?? "unknown", out var count) ? count + 1 : 1;
        }
        var local = record;
        RecSession.Write("forge", "kernel", json =>
        {
            json.WriteString("code", local.Code ?? "");
            json.WriteString("level", local.Level.ToString());
            json.WriteString("provider", local.Provider ?? "");
            if (BehaviorTraceContract.TryClassify(local.Code, out var phase))
            {
                json.WriteString("traceScope", phase.Scope);
                json.WriteString("tracePhase", phase.Moment);
            }
            Optional(json, "subjectProvider", local.SubjectProvider);
            Optional(json, "commandId", local.CommandId);
            Optional(json, "eventId", local.EventId);
            Optional(json, "causeId", local.CauseId);
            Optional(json, "rootEventId", local.RootEventId);
            Optional(json, "path", local.Path);
            Optional(json, "entry", local.Entry);
            Optional(json, "step", local.Step);
            Optional(json, "nodeKind", local.NodeKind);
            Optional(json, "binding", local.Binding);
            if (local.GateAccumulated is long gateAccumulated) json.WriteNumber("gateAccumulated", gateAccumulated);
            if (local.GateFired is long gateFired) json.WriteNumber("gateFired", gateFired);
            if (local.Plan is { } plan)
            {
                json.WriteStartObject("plan");
                json.WriteString("planId", plan.PlanId);
                json.WriteString("resourceId", plan.ResourceId);
                json.WriteString("resourceRevision", plan.ResourceRevision);
                json.WriteEndObject();
            }
            if (local.Result is { } result)
            {
                json.WriteStartObject("result");
                json.WriteString("status", result.Status);
                Optional(json, "commit", result.Commit);
                json.WriteString("reason", result.Reason);
                json.WriteEndObject();
            }
            if (local.Permissions is { } permissions)
            {
                json.WriteStartArray("permissions");
                foreach (var permission in permissions) json.WriteStringValue(permission);
                json.WriteEndArray();
            }
            if (local.Frame is { } frame) json.WriteNumber("kernelFrame", frame);
            json.WriteNumber("kernelTick", local.Tick);
            json.WriteNumber("kernelWorldEpoch", local.WorldEpoch);
        });
    }

    /// <summary>Authoring-only deep behavior trace. Unlike the regular kernel log this carries the values a node
    /// actually saw and produced. Runtime clones these values only while a subscriber exists, and this callback
    /// serializes them immediately into the bounded recorder queue.</summary>
    internal static void RecordBehaviorTrace(RuntimeBehaviorTraceRecord record)
    {
        if (!RecSession.Active) return;
        RecSession.Write("forge", "behavior", json =>
        {
            json.WriteString("traceScope", record.Scope);
            json.WriteString("tracePhase", record.Phase);
            json.WriteString("provider", record.Provider ?? "");
            json.WriteString("binding", record.Binding ?? "");
            json.WriteString("eventId", record.EventId ?? "");
            Optional(json, "causeId", record.CauseId);
            json.WriteString("rootEventId", record.RootEventId ?? record.EventId ?? "");
            Optional(json, "entry", record.Entry);
            Optional(json, "step", record.Step);
            Optional(json, "nodeKind", record.NodeKind);
            Optional(json, "commandId", record.CommandId);
            if (record.Plan is { } plan)
            {
                json.WriteStartObject("plan");
                json.WriteString("planId", plan.PlanId);
                json.WriteString("resourceId", plan.ResourceId);
                json.WriteString("resourceRevision", plan.ResourceRevision);
                json.WriteEndObject();
            }
            if (record.Inputs is { } inputs) { json.WritePropertyName("inputs"); inputs.WriteTo(json); }
            if (record.Parameters is { } parameters) { json.WritePropertyName("parameters"); parameters.WriteTo(json); }
            if (record.Outputs is { } outputs) { json.WritePropertyName("outputs"); outputs.WriteTo(json); }
            if (record.Result is { } result)
            {
                json.WriteStartObject("result");
                json.WriteString("status", result.Status);
                Optional(json, "commit", result.Commit);
                json.WriteString("reason", result.Reason);
                json.WriteEndObject();
            }
            json.WriteNumber("kernelTick", record.Tick);
            json.WriteNumber("kernelWorldEpoch", record.WorldEpoch);
        });
    }

    /// <summary>The same channel carries the diagnostic rows this package itself records, which the kernel sink never
    /// sees: those are the four <c>forge.action.diagnostic.*</c> rows behind <see cref="DevelopmentModule.Sink"/>.
    /// The kernel's own report writer keeps owning the report file; this is the same decision copied into the
    /// session's own channel so one query can see both.</summary>
    internal static void RecordDiagnostic(DiagnosticRecord record)
    {
        if (!RecSession.Active || record == null) return;
        RecSession.Write("forge", "diagnostic", json =>
        {
            json.WriteString("category", record.Category ?? "");
            json.WriteString("stage", record.Stage ?? "");
            json.WriteString("subject", record.Subject ?? "");
            json.WriteNumber("elapsedMs", record.ElapsedMs);
            json.WriteStartObject("fields");
            if (record.Fields != null)
                foreach (var field in record.Fields) json.WriteString(field.Key, field.Value ?? "");
            json.WriteEndObject();
        });
    }

    /// <summary>
    /// What the kernel exposes as state rather than as a record: the registry and the loaded-plan set, the world
    /// epoch and tick, and the queue depth. It is polled, and written only when the text actually changed, so a quiet
    /// session does not grow. Everything the kernel has no public read for — a variable write, a g-wait timeout, a
    /// checkpoint restore — is named in integration.json instead of being guessed at here.
    /// </summary>
    internal static void Poll()
    {
        var kernel = ForgeRuntime.Plugin.Runtime;
        if (kernel == null || !RecSession.Active) return;
        string manifest;
        int plans, queued;
        long epoch, tick;
        try
        {
            manifest = kernel.ExportManifest();
            plans = kernel.LoadedPlans;
            queued = kernel.QueuedEvents;
            epoch = kernel.WorldEpoch;
            tick = kernel.CurrentTick;
        }
        catch (Exception error)
        {
            lock (Gate) _failure = "poll: " + error.GetType().Name + ": " + error.Message;
            return;
        }
        var fingerprint = Fingerprint(manifest) + "|" + plans.ToString(CultureInfo.InvariantCulture) + "|" + queued.ToString(CultureInfo.InvariantCulture);
        lock (Gate)
        {
            if (fingerprint == _lastManifest) return;
            _lastManifest = fingerprint;
        }
        RecSession.Write("forge", "kernel_state", json =>
        {
            json.WriteNumber("loadedPlans", plans);
            json.WriteNumber("queuedEvents", queued);
            json.WriteNumber("worldEpoch", epoch);
            json.WriteNumber("tick", tick);
            json.WriteNumber("manifestBytes", Encoding.UTF8.GetByteCount(manifest));
            json.WriteString("manifestSha256", Fingerprint(manifest));
            if (manifest.Length <= 32768) json.WriteString("manifest", manifest);
        });
    }

    /// <summary>The kernel-code histogram the session summary carries, so a session says what the kernel actually
    /// did without every record being read back.</summary>
    internal static string CodeCounts()
    {
        lock (Gate)
        {
            var builder = new StringBuilder();
            foreach (var code in Codes)
            {
                if (builder.Length != 0) builder.Append(", ");
                builder.Append(code.Key).Append('=').Append(code.Value.ToString(CultureInfo.InvariantCulture));
            }
            return builder.ToString();
        }
    }

    private static string Fingerprint(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16].ToLowerInvariant();

    private static void Optional(Utf8JsonWriter json, string name, string? value)
    {
        if (value != null) json.WriteString(name, value);
    }
}

/// <summary>The postfix itself. It writes the record the kernel already decided to keep, and never touches the
/// kernel's own sink: a recorder that replaced the host's log writer would change the host's behaviour.</summary>
internal static class ForgeKernelPostfix
{
    public static void Postfix(in RuntimeLogRecord record)
    {
        try { RecForge.Record(in record); }
        catch (Exception) { /* A recorder failure inside the kernel's dispatch must not become the kernel's failure. */ }
    }
}
