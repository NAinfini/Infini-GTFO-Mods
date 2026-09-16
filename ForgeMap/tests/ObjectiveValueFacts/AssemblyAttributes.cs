// The kernel's registration ledger and the double objective machine are process-wide, and a case that leaves a
// world behind would change the next case's answers, exactly as the production host has one kernel per process.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
