// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.ResourceManager.Contracts/GlobalUsings.cs. Orleans ships a PUBLIC
// `Orleans.ErrorCode`, and Microsoft.Orleans.Sdk's build props inject `global using Orleans;` — which
// reaches a provider TRANSITIVELY through CyberCloud.ResourceManager.Contracts. So a provider that
// names an error code needs this line even though it references no Orleans package itself.
//
// ⚠ The .Contracts sibling deliberately does NOT carry the alias: it names no error code, and an
// unused alias is IDE0005, which is an error here (Directory.Build.props § Warnings and analysis).
// The two files differing is correct, not drift.
//
// ⚠ `CyberCloud.Tenancy.Contracts` is here for `QuotaMeter` alone. docs/plan/06 § Quota owns the
// families, and CyberCloud.ResourceManager.Contracts' own .csproj calls this the weakest line in the
// reference set: it also makes IQuotaGrain and IResourceIndexGrain NAMEABLE from a provider, and
// steps 6 and 7 of docs/plan/08 § The write path, end to end are the manager's alone. Naming either
// from a reconciler is a review failure, not a compile one.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.Providers.Sample.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
