// The shipped hooks and the fixture's life table are process-wide static state, and one case must not publish
// through another case's registration.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
