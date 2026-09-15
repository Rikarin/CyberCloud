// ⚠ `ErrorCode` is ambiguous here for the reason CyberCloud.ResourceManager.Contracts/GlobalUsings.cs
// records: Orleans ships a PUBLIC `Orleans.ErrorCode` and the SDK adds `global using Orleans;`, both
// arriving transitively.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Communication.Contracts;
global using CyberCloud.Core;
global using CyberCloud.Core.Contracts;
global using CyberCloud.Core.Resources;
global using CyberCloud.Providers.Communication.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using Shouldly;
