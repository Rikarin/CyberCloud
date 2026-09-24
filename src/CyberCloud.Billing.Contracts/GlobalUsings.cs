// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Metering.Contracts/GlobalUsings.cs. Orleans ships a PUBLIC `Orleans.ErrorCode`
// and Microsoft.Orleans.Sdk adds `global using Orleans;`.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Metering.Contracts;
