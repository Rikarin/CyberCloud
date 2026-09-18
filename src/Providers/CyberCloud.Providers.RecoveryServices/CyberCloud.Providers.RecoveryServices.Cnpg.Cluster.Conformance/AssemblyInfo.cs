using CyberCloud.Providers.RecoveryServices.CnpgConformance;

// ⚠ ONE TEST CLASS AT A TIME, for the reason test/CyberCloud.Cluster.Conformance/AssemblyInfo.cs gives:
// the harness binds per-provider state to a static, and two classes at once would tear down each
// other's connection.
//
// ⚠ AND THE OPERATOR, INSTALLED ONCE FOR THE WHOLE ASSEMBLY, BEFORE ANY CLASS. The one class here takes
// CloudNativePgInstalled in its constructor, so the install runs before the harness derives a CRD
// stub — a stub created first would make `helm install` refuse the real definitions as somebody
// else's. See CloudNativePgInstalled.

[assembly: CollectionBehavior(DisableTestParallelization = true)]
[assembly: AssemblyFixture(typeof(CloudNativePgInstalled))]
