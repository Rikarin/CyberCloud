// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode` and Microsoft.Orleans.Sdk injects `global using Orleans;`, so the unqualified
// name binds to two types the moment anything here names one — which a reconciler does, on every
// failure path.
//
// ⚠ `CyberCloud.Tenancy.Contracts` is here for `QuotaMeter` alone — docs/plan/06 § Quota owns the
// families. It also makes IQuotaGrain and IResourceIndexGrain NAMEABLE from a provider, and steps 6
// and 7 of docs/plan/08 § The write path, end to end are the manager's alone. Nothing in this
// assembly may call either, and docs/plan/07 § The enforcement seam is the reason.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.Providers.Terminal.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
