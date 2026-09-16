// The registered Map player module, the player identity doubles and the kernel's per-tick query budget are
// process-wide static state, so the cases in this assembly never run in parallel with each other.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
