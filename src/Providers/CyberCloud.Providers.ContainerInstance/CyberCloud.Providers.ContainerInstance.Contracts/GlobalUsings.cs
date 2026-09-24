// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode`, and Microsoft.Orleans.Sdk's build props inject `global using Orleans;` — which
// reaches a provider TRANSITIVELY through CyberCloud.ResourceManager.Contracts. This .Contracts
// assembly names an error code: ContainerGroups.BodyProblem refuses what the schema cannot say.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
