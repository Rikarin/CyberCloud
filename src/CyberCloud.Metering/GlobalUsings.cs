// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Tenancy/GlobalUsings.cs. Orleans ships a PUBLIC `Orleans.ErrorCode` and
// Microsoft.Orleans.Sdk adds `global using Orleans;`, so any file that also imports CyberCloud.Core
// has two candidates for the simple name and the compiler reports CS0104.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;

// The meter vocabulary and every wire type are named on nearly every file here.
global using CyberCloud.Metering.Contracts;
global using CyberCloud.Tenancy.Contracts;
