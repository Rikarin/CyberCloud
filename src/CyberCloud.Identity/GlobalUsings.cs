// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode` and Microsoft.Orleans.Server pulls in `global using Orleans;`, so any file that
// also imports CyberCloud.Core has two candidates for the simple name and the compiler reports
// CS0104. See CyberCloud.Tenancy/GlobalUsings.cs, where this was first hit.

global using ErrorCode = CyberCloud.Core.ErrorCode;

global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Identity.Contracts;
