// ⚠ ONE TEST CLASS AT A TIME, AND IT IS A CORRECTNESS RULE RATHER THAN A THROTTLE — the same rule,
// for a stronger reason, as test/CyberCloud.Cluster.Conformance/AssemblyInfo.cs.
//
// Every test here breaks the topology the others depend on: one stops a PostgreSQL shard, one
// FLUSHALLs the Redis every silo's reminders live in, one stops the k3s every reconcile applies to,
// two kill silos. Two of them running at once would be two faults nobody induced, and an invariant
// that failed under a fault its test did not inject is a red run that means nothing. Each test
// restores what it broke before it returns, so the next one starts from a whole cluster.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
