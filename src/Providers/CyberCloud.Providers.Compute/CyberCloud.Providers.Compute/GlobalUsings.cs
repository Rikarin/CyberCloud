// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same note every provider
// implementation carries: Orleans ships a PUBLIC `Orleans.ErrorCode`, and Microsoft.Orleans.Sdk's
// build props inject `global using Orleans;`, which reaches a provider TRANSITIVELY through
// CyberCloud.ResourceManager.Contracts.
//
// ⚠ `CyberCloud.Tenancy.Contracts` is here for `QuotaMeter` alone — docs/plan/06 § Quota owns the
// families. It also makes IQuotaGrain and IResourceIndexGrain NAMEABLE from a provider, and naming
// either from a reconciler is a review failure rather than a compile one.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.Providers.Compute.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
