// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode`, and Microsoft.Orleans.Sdk's build props inject `global using Orleans;`. It
// used to reach this assembly only transitively, through CyberCloud.ResourceManager.Contracts; since
// FeedGrain landed the assembly references Microsoft.Orleans.Server itself, and the ambiguity is the
// same either way.
//
// ⚠ `CyberCloud.Core` is global now, and it was a per-file import in three files until FeedGrain and
// ArtifactFeedReconciler made it five. Every file that names Result<T> would otherwise repeat the
// same three-line header explaining that the alias above wins over the Orleans.ErrorCode the import
// puts back in play. It does — an alias directive beats a namespace import for a simple name — and
// saying so once here is better than saying it five times.
//
// ⚠ The .Contracts sibling now carries the alias too, since IFeedGrain's contract names error codes.
//
// ⚠ `CyberCloud.Tenancy.Contracts` is here for `QuotaMeter` alone — docs/plan/06 § Quota owns the
// families. It also makes IQuotaGrain and IResourceIndexGrain NAMEABLE from a provider, and steps 6
// and 7 of docs/plan/08 § The write path, end to end are the manager's alone. Naming either from a
// reconciler fails the Assembly graph gate — docs/plan/03 § Assembly graph rules, rule 8, added
// by issue #90; until then it was a review failure and not a compile one.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.Providers.ContainerRegistry.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
