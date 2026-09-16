// The doubles' static world — the puzzle list, the two data block tables and the master flag — is process-wide
// state, so the cases in this project run one after another rather than beside each other.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
