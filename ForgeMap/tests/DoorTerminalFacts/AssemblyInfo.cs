using Xunit;

// Every case in this project drives the production halves against the game's own static entry points — the
// double the door reader, the level's zone table substitute, `SNet.IsMaster` and the live facts half — so two
// cases running at once would see each other's world. The collection is serialized for that reason and not
// because any case is slow.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
