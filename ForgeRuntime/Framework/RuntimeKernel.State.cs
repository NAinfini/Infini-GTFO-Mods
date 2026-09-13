using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace ForgeRuntime.Framework;

public sealed partial class RuntimeKernel
{
    public const int MaximumStateLeases = 512;
    private readonly Dictionary<(string Provider, string Id), NumericLease> stateLeases = new();
    private sealed record LeaseKey(string Provider, EntityReference Target, EntityReference? Source,
        string Scope, string Definition, string StackGroup);
    private sealed record NumericLease(RuntimeStateLeaseHandle Handle, long Generation, LeaseKey Key, string Order);

    private void NumericDefinition(string id, string? version = null)
    {
        RuntimeJson.Require(RuntimeJson.IsId(id) && registry.Capabilities.TryGetValue(id, out _), "state-definition", id);
        var definition = registry.Capabilities[id];
        RuntimeJson.Require(RuntimeJson.Text(definition, "kind") == "state" && definition.GetProperty("parameters").TryGetProperty("valueType", out var type)
            && type.ValueKind == JsonValueKind.String && type.GetString() == "numeric-contribution", "state-value-type", id);
        if (version != null) RuntimeJson.Require(RuntimeJson.Text(definition, "version") == version, "state-version", id);
    }
    internal NumericLeaseResult AcquireNumericLease(RuntimeModuleHandle owner, NumericLeaseRequest request)
    {
        Thread();
        try
        {
            RuntimeJson.Require(owner.IsRegistered, "module-unregistered", owner.ProviderId);
            RuntimeJson.Require(worldStarted && worldHost == true, "not-host", "Numeric state requires an authoritative simulation tick.");
            RuntimeJson.Text(request.LeaseId); RuntimeJson.Text(request.ScopeId); RuntimeJson.Text(request.StackGroup);
            RuntimeJson.Text(request.DefinitionVersion); NumericDefinition(request.DefinitionId, request.DefinitionVersion);
            RuntimeJson.Integer(request.DurationTicks, 1);
            RuntimeJson.Require(request.DurationTicks <= RuntimeJson.MaxSafeInteger - CurrentTick, "lease-overflow", request.LeaseId);
            RuntimeJson.Require(double.IsFinite(request.Additive) && double.IsFinite(request.Multiplier) && request.Multiplier >= 0, "state-number", request.LeaseId);
            RuntimeJson.Require(!cancelled.Contains((owner.ProviderId, request.ScopeId)), "scope-cancelled", request.ScopeId);
            CheckEntity(request.Target); if (request.Source != null) CheckEntity(request.Source);
            var key = owner.ProviderId + "\0lease\0" + request.LeaseId;
            var fingerprint = Fingerprint(request);
            if (history.TryGetValue(key, out var previous)) return new NumericLeaseResult(previous == fingerprint ? "duplicate" : "rejected", previous == fingerprint ? "duplicate-lease" : "lease-id-conflict", null);
            RuntimeJson.Require(history.Count < MaximumEventHistory, "event-history-budget", "Replay ledger capacity reached.");
            // Cleanup precedes capacity/key checks so expired contributions never block a new source lease.
            CleanStateLifetimes(null);
            RuntimeJson.Require(stateLeases.Count < MaximumStateLeases, "state-lease-budget", "Active state lease capacity reached.");
            var sourceKey = new LeaseKey(owner.ProviderId, request.Target, request.Source, request.ScopeId, request.DefinitionId, request.StackGroup);
            RuntimeJson.Require(!stateLeases.Values.Any(l => l.Key == sourceKey), "state-source-conflict", "A source key has one contribution; release it before creating a new lease.");
            var handle = new RuntimeStateLeaseHandle(this, owner.ProviderId, request) { ExpiresAtTick = CurrentTick + request.DurationTicks };
            history.Add(key, fingerprint);
            stateLeases.Add((owner.ProviderId, request.LeaseId), new NumericLease(handle, owner.Generation, sourceKey, RuntimeJson.StableText(RuntimeJson.From(sourceKey))));
            return new NumericLeaseResult("acquired", "contribution-recorded", handle);
        }
        catch (RuntimeContractException ex) { return new NumericLeaseResult("rejected", ex.Code, null); }
    }
    private void EndLease(NumericLease lease, string status, string code)
    {
        lease.Handle.Status = status; lease.Handle.Code = code;
        stateLeases.Remove((lease.Handle.ProviderId, lease.Handle.LeaseId));
    }
    internal bool ReleaseLease(RuntimeStateLeaseHandle handle)
    {
        // Terminal handle disposal is cleanup, not new runtime work.
        ReadThread(); NoLifecycleMutation();
        if (!stateLeases.TryGetValue((handle.ProviderId, handle.LeaseId), out var lease) || !ReferenceEquals(lease.Handle, handle)) return false;
        EndLease(lease, "released", "explicit-release"); return true;
    }
    private void CleanStateLifetimes(List<StateLeaseReceipt>? reports)
    {
        if (stateLeases.Count == 0) return;
        foreach (var lease in stateLeases.Values.ToArray())
        {
            var handle = lease.Handle; var request = handle.Request; string? code = null;
            if (!IsRegistered(handle.ProviderId, lease.Generation) || request.Target.WorldEpoch != WorldEpoch) code = "source-lifecycle";
            else if (cancelled.Contains((handle.ProviderId, request.ScopeId))) code = "scope-cancelled";
            else if (CurrentTick >= handle.ExpiresAtTick) code = "lifetime-ended";
            else
            {
                try { CheckEntity(request.Target); if (request.Source != null) CheckEntity(request.Source); }
                catch (RuntimeContractException ex) { code = ex.Code; }
            }
            if (code == null) continue;
            var status = code == "lifetime-ended" ? "expired" : "cancelled";
            EndLease(lease, status, code);
            reports?.Add(new StateLeaseReceipt(handle.ProviderId, handle.LeaseId, request.DefinitionId, request.Target, status, code));
        }
    }
    private void StopStateSource(string? provider, string? scope, string code)
    {
        foreach (var lease in stateLeases.Values.Where(l => (provider == null || l.Handle.ProviderId == provider) && (scope == null || l.Key.Scope == scope)).ToArray())
            EndLease(lease, "cancelled", code);
    }
    public NumericStateResult EvaluateNumericState(EntityReference target, string definitionId, string stackGroup, double baseValue)
    {
        Thread(); RuntimeJson.Require(worldStarted && worldHost == true, "not-host", "Numeric state requires an authoritative simulation tick.");
        NumericDefinition(definitionId); RuntimeJson.Text(stackGroup); RuntimeJson.Require(double.IsFinite(baseValue), "state-number", "Base value must be finite.");
        CheckEntity(target); CleanStateLifetimes(null);
        var additive = 0d; var multiplier = 1d; var count = 0;
        // Stable source order makes the result independent of registration/acquisition order. Never undo by division.
        foreach (var lease in stateLeases.Values.Where(l => l.Key.Target == target && l.Key.Definition == definitionId && l.Key.StackGroup == stackGroup).OrderBy(l => l.Order, StringComparer.Ordinal))
        { additive += lease.Handle.Request.Additive; multiplier *= lease.Handle.Request.Multiplier; count++; }
        var value = (baseValue + additive) * multiplier;
        RuntimeJson.Require(double.IsFinite(additive) && double.IsFinite(multiplier) && double.IsFinite(value), "state-overflow", definitionId);
        return new NumericStateResult(baseValue, additive, multiplier, value, count);
    }
}
