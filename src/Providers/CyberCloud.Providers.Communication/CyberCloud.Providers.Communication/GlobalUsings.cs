// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as every provider before it. Orleans ships a PUBLIC `Orleans.ErrorCode`, and
// Microsoft.Orleans.Sdk's build props inject `global using Orleans;`, which reaches here
// transitively through CyberCloud.ResourceManager.Contracts.
//
// ⚠ `CyberCloud.Tenancy.Contracts` is here for `QuotaMeter` alone — docs/plan/06 § Quota owns the
// families. It also makes IQuotaGrain and IResourceIndexGrain NAMEABLE from a provider, and steps 6
// and 7 of docs/plan/08 § The write path, end to end are the manager's alone.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Communication.Contracts;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Providers.Communication.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
