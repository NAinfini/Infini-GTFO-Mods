using System.Collections;
using System.Reflection;
using ForgeRuntime.Framework;

/// <summary>Test-only adapter over the I-PACK batch <see cref="RuntimeKernel.LoadPlans"/>, which is
/// intentionally non-throwing so one file's rejection never affects another's. This keeps the pre-existing
/// single-file, throw-on-reject assertions (Reject/RejectCode) working with a minimal signature change; it is not a
/// production compatibility layer, and lives only in test code, the same way Fixture.Plan is test-only.</summary>
internal static class TestPlanLoading
{
    internal static void LoadPlan(this RuntimeKernel kernel, string json)
    {
        var outcome = kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/plan.plan.json", json) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
    }

    /// <summary>The frame descriptor of the plan a kernel holds, or null while it holds none.</summary>
    internal static PlanFrames? Frames(this RuntimeKernel kernel, string planId) => kernel.Resolved(planId)?.Frames;

    /// <summary>The resolved plan a kernel holds under one planId. The kernel keeps it private because nothing in
    /// production reaches back into a loaded plan by id; a frame assertion has no other way in.</summary>
    internal static ResolvedPlan? Resolved(this RuntimeKernel kernel, string planId)
    {
        var plans = (IDictionary)typeof(RuntimeKernel).GetField("plans", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(kernel)!;
        if (!plans.Contains(planId)) return null;
        var loaded = plans[planId];
        return (ResolvedPlan)loaded!.GetType().GetProperty("Plan")!.GetValue(loaded)!;
    }
}
