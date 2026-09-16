// The kernel the three fact families publish into is process-wide state, and the module's tables are per world,
// so the cases in this project run one after another rather than beside each other.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
