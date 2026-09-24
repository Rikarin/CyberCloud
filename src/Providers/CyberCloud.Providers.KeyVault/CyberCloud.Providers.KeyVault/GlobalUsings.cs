// ⚠ `ErrorCode` is ambiguous here for the reason CyberCloud.Providers.ContainerRegistry/GlobalUsings.cs
// records: Orleans ships a PUBLIC `Orleans.ErrorCode` and this assembly references
// Microsoft.Orleans.Server, whose build props add `global using Orleans;`.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Providers.KeyVault.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
