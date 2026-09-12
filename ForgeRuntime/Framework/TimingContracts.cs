using System;
using System.Collections.Generic;

namespace ForgeRuntime.Framework;

public enum FirstPulse { Immediate, AfterInterval }
public enum MissedPulsePolicy { SkipMissed, CatchUp }

/// <summary>Tick units are provided by the host. Lifetime end is exclusive; the earliest finite bound wins.</summary>
public sealed record PulseSchedule(long IntervalTicks, FirstPulse FirstPulse, MissedPulsePolicy MissedPulsePolicy,
    int? MaxPulses = null, long? LifetimeTicks = null);
public sealed record ScheduleResult(string Status, string Code, RuntimeScheduleHandle? Handle);
public sealed record ScheduleReceipt(string ProviderId, string ScheduleId, long WorldEpoch, long SimulationTick,
    string Status, string Code, int DispatchedPulses, int SkippedPulses);

public sealed class RuntimeScheduleHandle : IDisposable
{
    private readonly RuntimeKernel kernel;
    internal RuntimeScheduleHandle(RuntimeKernel kernel, string providerId, string scheduleId, long worldEpoch)
    { this.kernel = kernel; ProviderId = providerId; ScheduleId = scheduleId; WorldEpoch = worldEpoch; }
    public string ProviderId { get; }
    public string ScheduleId { get; }
    public long WorldEpoch { get; }
    public string Status { get; internal set; } = "active";
    public string Code { get; internal set; } = "scheduled";
    public int DispatchedPulses { get; internal set; }
    public int SkippedPulses { get; internal set; }
    public long? NextTick { get; internal set; }
    public bool Cancel() => kernel.CancelSchedule(this);
    public void Dispose() => Cancel();
}

/// <summary>Independent source contribution, recomputed from base; this does not mutate a game property.</summary>
public sealed record NumericLeaseRequest(string LeaseId, string DefinitionId, string DefinitionVersion,
    EntityReference Target, string ScopeId, string StackGroup, EntityReference? Source,
    long DurationTicks, double Additive = 0, double Multiplier = 1);
public sealed record NumericLeaseResult(string Status, string Code, RuntimeStateLeaseHandle? Handle);
public sealed record StateLeaseReceipt(string ProviderId, string LeaseId, string DefinitionId,
    EntityReference Target, string Status, string Code);
public sealed record NumericStateResult(double BaseValue, double Additive, double Multiplier, double Value, int Contributions);

public sealed class RuntimeStateLeaseHandle : IDisposable
{
    private readonly RuntimeKernel kernel;
    internal RuntimeStateLeaseHandle(RuntimeKernel kernel, string providerId, NumericLeaseRequest request)
    { this.kernel = kernel; ProviderId = providerId; Request = request; }
    internal NumericLeaseRequest Request { get; }
    public string ProviderId { get; }
    public string LeaseId => Request.LeaseId;
    public EntityReference Target => Request.Target;
    public string Status { get; internal set; } = "active";
    public string Code { get; internal set; } = "acquired";
    public long ExpiresAtTick { get; internal set; }
    public bool Release() => kernel.ReleaseLease(this);
    public void Dispose() => Release();
}

public sealed partial class RuntimeModuleHandle
{
    public ScheduleResult Schedule(RuntimeEvent template, PulseSchedule schedule) => kernel.Schedule(this, template, schedule);
    public NumericLeaseResult AcquireNumericLease(NumericLeaseRequest request) => kernel.AcquireNumericLease(this, request);
}
