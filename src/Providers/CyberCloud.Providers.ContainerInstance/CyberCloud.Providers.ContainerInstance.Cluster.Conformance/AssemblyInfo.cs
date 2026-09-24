// ⚠ ONE TEST CLASS AT A TIME, AND IT IS NOT OPTIONAL — it is a property of the shared harness rather
// than of this provider. test/CyberCloud.Cluster.Conformance/AssemblyInfo.cs carries the full reason:
// the harness binds per-provider state to a static because Orleans constructs a silo configurator
// with `new()`, and the silo-kill class rebuilds a cluster over the same containers the lifecycle
// class is using. `CollectionBehavior` is per-assembly, so every assembly that derives from those
// suites needs this line; there is no way to inherit it.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
