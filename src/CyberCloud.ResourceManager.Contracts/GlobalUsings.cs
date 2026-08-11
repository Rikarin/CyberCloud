// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Tenancy/GlobalUsings.cs and CyberCloud.Kubernetes.Contracts/GlobalUsings.cs.
// Orleans ships a PUBLIC `Orleans.ErrorCode` and Microsoft.Orleans.Sdk adds `global using Orleans;`,
// so any file that also imports CyberCloud.Core has two candidates for the simple name and the
// compiler reports CS0104.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;

// SecretRef. docs/plan/00 § Non-negotiables writes the rule platform-wide, so the type is
// platform-wide; it used to be declared in this assembly and a provider that references this one
// still names it unqualified.
global using CyberCloud.Core.Contracts;

// docs/plan/06 § Tags, locks, and the small stuff that is not small makes ProvisioningState the one
// vocabulary every provider uses. It is named on nearly every type here, so it is imported globally
// rather than repeated.
global using CyberCloud.Tenancy.Contracts;

// The reconcile seam names IKubeClusterConnection and the drift types name ObjectRef, both from the
// Kubernetes contracts. ⚠ That assembly does not reference KubernetesClient — see this project's
// .csproj on why importing it costs nothing of docs/plan/03 § Assembly graph rules, rule 3.
global using CyberCloud.Kubernetes.Contracts;
