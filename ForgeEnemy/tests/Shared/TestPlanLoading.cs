using ForgeRuntime.Framework;

/// <summary>Test-only adapter over the I-PACK D-009 batch <see cref="RuntimeKernel.LoadPlans"/>, which is
/// intentionally non-throwing so one file's rejection never affects another's. This keeps the pre-existing
/// single-file, throw-on-reject assertions (Reject/RejectCode, <see cref="LocalPlan.Load"/>) working with a minimal
/// signature change; it is not a production compatibility layer, and lives only in test code, the same way other
/// fixture helpers are test-only. Mirrors the identical adapter in the Framework/GameBindings/GraphContracts/
/// LifecycleWork test projects, which compile their own copy directly against runtime source rather than a
/// separately-referenced production assembly.</summary>
internal static class TestPlanLoading
{
    internal static void LoadPlan(this RuntimeKernel kernel, string json)
    {
        var outcome = kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/plan.plan.json", json) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
    }
}
