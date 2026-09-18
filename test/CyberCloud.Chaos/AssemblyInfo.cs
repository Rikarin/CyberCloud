// ⚠ ONE TEST CLASS AT A TIME, AND IT IS A CORRECTNESS RULE RATHER THAN A THROTTLE — the same rule,
// for a stronger reason, as test/CyberCloud.Cluster.Conformance/AssemblyInfo.cs.
//
// Every test here breaks the topology the others depend on: one stops a PostgreSQL shard, one
// FLUSHALLs the Redis every silo's reminders live in, one stops the k3s every reconcile applies to,
// two kill silos. Two of them running at once would be two faults nobody induced, and an invariant
// that failed under a fault its test did not inject is a red run that means nothing. Each test
// restores what it broke in a `finally` — the shard or the k3s started again, the cluster brought
// back to three silos — so the next one starts from a whole cluster whether this one returned or
// threw. ⚠ The first version of this file said "before it returns" and meant it literally: a test
// that threw mid-fault left the fault in place, and the review's second run had invariant 1 die on
// a refused PUT and hand invariants 5 and 3 a two-silo cluster with the storm's drivers still going.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
