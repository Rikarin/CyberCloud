// ⚠ ONE TEST CLASS AT A TIME, for the shared harness's reason: test/CyberCloud.Cluster.Conformance binds
// per-provider state to statics because Orleans constructs a silo configurator with `new()`, and
// the console tests move the one ConformanceClock the silo and the gateway both read.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
