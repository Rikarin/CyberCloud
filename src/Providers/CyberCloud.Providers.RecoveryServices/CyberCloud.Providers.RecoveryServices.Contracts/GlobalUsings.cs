// ⚠ `ErrorCode` IS ALIASED HERE, UNLIKE MOST PROVIDER .Contracts ASSEMBLIES. Orleans ships a PUBLIC
// `Orleans.ErrorCode` and Microsoft.Orleans.Sdk's build props inject `global using Orleans;`, which
// reaches here transitively. The other .Contracts assemblies name no error code and leave the alias
// out (an unused alias is IDE0005, an error); this one refuses a protected item by name from a
// contracts-level reader, so it needs the alias the implementation assemblies carry.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
