// ⚠ THE `ErrorCode` ALIAS IS HERE NOW, AND IT USED TO SAY THE OPPOSITE. This file recorded "no
// alias here — nothing in this assembly names an error code" for as long as that was true; the
// feed grain's contract (IFeedGrain.cs) names ErrorCode.Conflict, ErrorCode.ResourceAlreadyExists
// and ErrorCode.ResourceNotFound in the returns it promises, and Orleans ships a PUBLIC
// `Orleans.ErrorCode` that Microsoft.Orleans.Sdk's build props put in scope through
// `global using Orleans;`. So the simple name is ambiguous and the alias is the fix — the same
// repair as CyberCloud.ResourceManager.Contracts/GlobalUsings.cs. `CyberCloud.Core` is here for
// Result<T>, which the same interface returns.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
