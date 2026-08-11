// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode` and Microsoft.Orleans.Server pulls in `global using Orleans;`, so any file that
// also imports CyberCloud.Core has two candidates for the simple name and the compiler reports
// CS0104. See CyberCloud.Tenancy/GlobalUsings.cs, where this was first hit.

global using ErrorCode = CyberCloud.Core.ErrorCode;

// ⚠ `ObjectRef` is ambiguous here in a way it is not in the contracts assembly: this one references
// BOTH CyberCloud.Kubernetes.Contracts (a cluster object) and CyberCloud.Authorization.Contracts (a
// ReBAC object), and both declare the name. The authorization one is used in exactly one file — the
// enforcement seam — so the simple name is pinned to the Kubernetes one and the seam spells the
// other out in full.
global using CyberCloud.Core;
global using CyberCloud.Core.Contracts;
global using CyberCloud.Core.Resources;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.Tenancy.Contracts;
global using CyberCloud.Kubernetes.Contracts;
