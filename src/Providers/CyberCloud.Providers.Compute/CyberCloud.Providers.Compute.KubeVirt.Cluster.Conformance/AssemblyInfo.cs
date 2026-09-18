// ⚠ ONE TEST CLASS AT A TIME, for the reason every other cluster-backed assembly carries this line:
// the k3s container is a per-process singleton, and xunit runs test COLLECTIONS in parallel by
// default. The two classes here would otherwise overlap — the Docker-free one shells out to
// install.sh --dry-run while the other is holding the cluster — and a failure report whose lines
// interleave is the only output that matters when this suite goes red.
//
// `CollectionBehavior` is per-assembly and cannot be inherited from the referenced assembly, which is
// what test/CyberCloud.Cluster.Conformance/AssemblyInfo.cs says at greater length.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
