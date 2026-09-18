// ⚠ ONE SCENARIO AT A TIME. Every scenario is a measurement of the same cluster, and two of them
// running together would each be measuring the other's load. The order they run in is xunit's;
// each starts from a cluster the previous one left idle, which the readers and writers below make
// sure of by draining before they return.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
