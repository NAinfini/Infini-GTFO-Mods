// The shipped native hooks and their game doubles share process-wide static state; cases must not interleave.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
