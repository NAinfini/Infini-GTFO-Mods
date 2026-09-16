// The native doubles are process-wide static state: the objective manager, the checkpoint manager and the
// landing are singletons in the game too, and a case that left one behind would change the next case's world.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
