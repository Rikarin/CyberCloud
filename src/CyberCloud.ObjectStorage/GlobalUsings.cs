// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.ResourceManager.Contracts/GlobalUsings.cs. Orleans ships a PUBLIC
// `Orleans.ErrorCode`, and Microsoft.Orleans.Sdk's build props inject `global using Orleans;`,
// which reaches here transitively through CyberCloud.ResourceManager.Contracts.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.ResourceManager.Contracts;
