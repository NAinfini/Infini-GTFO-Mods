using ForgeRuntime.Framework;

// The compiled Map module and the fixture's synthetic sources share process-wide static state, and the module
// checks that every readback arrives on one thread.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
