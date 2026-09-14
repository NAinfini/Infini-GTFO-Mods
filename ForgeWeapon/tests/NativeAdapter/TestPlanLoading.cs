using ForgeRuntime.Framework;

internal static class TestPlanLoading
{
    internal static void LoadPlan(this RuntimeKernel kernel, string json)
    {
        var outcome = kernel.LoadPlans(new[] { PlanCandidate.Loaded("test/plan.plan.json", json) })[0];
        if (!outcome.Loaded) throw new RuntimeContractException(outcome.Code!, outcome.Detail ?? outcome.Code!);
    }
}
