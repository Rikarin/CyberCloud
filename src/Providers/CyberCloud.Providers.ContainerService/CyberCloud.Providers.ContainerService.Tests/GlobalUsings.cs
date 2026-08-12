// ⚠ `ErrorCode` is ambiguous here for the reason CyberCloud.ResourceManager.Contracts/GlobalUsings.cs
// records: Orleans ships a PUBLIC `Orleans.ErrorCode` and the SDK adds `global using Orleans;`, both
// arriving transitively.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.Providers.ContainerService.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
// ⚠ For `QuotaMeter` alone — docs/plan/06 § Quota owns the families, and this provider is the first to
// draw QuotaMeter.Clusters, which is a claim a test has to be able to spell.
global using CyberCloud.Tenancy.Contracts;
global using Shouldly;
