// Several cases drive the same process-wide doubles — the level event manager's own `Current`, the environment
// state the two reads answer from and the session hub — so the cases here run one after another rather than
// beside each other.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
