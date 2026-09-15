// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode`, and Microsoft.Orleans.Sdk's build props inject `global using Orleans;` —
// which reaches here transitively through CyberCloud.Identity.Contracts. See
// CyberCloud.Tenancy/GlobalUsings.cs, where this was first hit.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
