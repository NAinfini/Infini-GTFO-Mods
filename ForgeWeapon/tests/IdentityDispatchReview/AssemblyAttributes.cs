// The compiled dispatcher, session and the fixture action share process-wide static state; cases must not interleave.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
