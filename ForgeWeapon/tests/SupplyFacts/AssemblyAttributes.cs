// The game doubles are process-wide static tables — the backpack map, the pools and the item block table — and one
// case must not read or write another case's world through them.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
