// The compiled session and its synthetic adapter share process-wide static state; cases must not interleave.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
