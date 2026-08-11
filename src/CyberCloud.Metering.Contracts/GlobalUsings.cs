// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Tenancy.Contracts/GlobalUsings.cs. Orleans ships a PUBLIC `Orleans.ErrorCode`
// and Microsoft.Orleans.Sdk adds `global using Orleans;`, so any file that also imports
// CyberCloud.Core has two candidates for the simple name and the compiler reports CS0104.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;

// QuotaMeter is the platform's one meter vocabulary (docs/plan/06 § Quota) and is named on nearly
// every type here — see MeterCatalog for why a billing meter is derived from it rather than
// declared beside it.
global using CyberCloud.Tenancy.Contracts;
