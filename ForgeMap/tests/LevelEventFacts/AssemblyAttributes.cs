// The kernel's registration ledger is process-wide and a case that leaves a world behind would change the next
// case's answers, exactly as the production host has one kernel per process.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
