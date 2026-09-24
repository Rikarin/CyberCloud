// ⚠ `ErrorCode` is ambiguous in this assembly for the reason
// CyberCloud.ContainerRegistry.Contracts/GlobalUsings.cs records: Orleans ships a PUBLIC
// `Orleans.ErrorCode`, Microsoft.Orleans.Sdk puts `global using Orleans;` in scope, and
// IKeyVaultGrain's results name error codes.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
