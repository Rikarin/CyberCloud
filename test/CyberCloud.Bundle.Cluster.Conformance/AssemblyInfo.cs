// ⚠ ONE TEST CLASS AT A TIME, for the reason every other cluster-backed assembly carries this line:
// the k3s container is a per-process singleton, and xunit runs test COLLECTIONS in parallel by
// default. The two classes here would otherwise overlap, and the Docker-free one shells out to
// install.sh --dry-run while the other is holding the cluster — two bash processes reading the same
// component.yaml is harmless, but the ordering of their output in a failure report is not, and a
// failure report nobody can read is the only output that matters when this suite goes red.
//
// `CollectionBehavior` is per-assembly and cannot be inherited from the harness assembly, which is
// what test/CyberCloud.Cluster.Conformance/AssemblyInfo.cs says at greater length.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
