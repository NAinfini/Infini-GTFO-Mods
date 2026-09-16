// The attached observation halves and the player identity are process-wide static state, and one case must not
// publish through another case's registration.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
