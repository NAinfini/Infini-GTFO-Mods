using ForgeRuntime.Framework;

/// <summary>Test-only adapter over the I-PACK D-009 batch <see cref="RuntimeKernel.LoadPlans"/>, which is
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
}
