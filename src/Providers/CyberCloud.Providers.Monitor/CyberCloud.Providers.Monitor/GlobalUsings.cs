// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode`, and Microsoft.Orleans.Sdk's build props inject `global using Orleans;` — which
// reached a provider TRANSITIVELY through CyberCloud.ResourceManager.Contracts before this assembly
// referenced an Orleans package of its own, and reaches it directly now that it does.
//
// ⚠ `CyberCloud.Core` IS global here since the alert rules landed, and this file used to say it was
// not. The workspace half named `Result<decimal>` in one file and imported the namespace there; the
// alert half — a grain, a control plane, a reconciler and a handler — names Result, Error and
// GrainKeys on nearly every file. The alias above still wins over the `Orleans.ErrorCode` this import
// puts back in play, which is the same arrangement CyberCloud.Providers.Communication has.
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
global using CyberCloud.Providers.Monitor.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
