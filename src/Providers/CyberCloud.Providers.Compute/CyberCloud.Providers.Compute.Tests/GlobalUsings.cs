// ⚠ `ErrorCode` is ambiguous here for the reason CyberCloud.ResourceManager.Contracts/GlobalUsings.cs
// records: Orleans ships a PUBLIC `Orleans.ErrorCode` and the SDK adds `global using Orleans;`, both
// arriving transitively.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.Providers.Compute.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
// For `QuotaMeter` alone — docs/plan/06 § Quota owns the families.
global using CyberCloud.Tenancy.Contracts;
global using Shouldly;
