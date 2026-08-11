// ⚠ NO `ErrorCode` ALIAS HERE, AND THE SIBLING IMPLEMENTATION ASSEMBLY HAS ONE. That difference is
// correct rather than drift, and it is worth a line because the obvious move is to copy the file.
// Orleans ships a PUBLIC `Orleans.ErrorCode` and Microsoft.Orleans.Sdk's build props inject
// `global using Orleans;`, which reaches here transitively — so the ambiguity is real and the alias
// would fix it. But nothing in this assembly names an error code, and an unused alias is IDE0005,
// which is an error (Directory.Build.props § Warnings and analysis). Add the alias the moment a file
// here needs `ErrorCode`, and not before.
//
// ⚠ Only what is used, for the same reason: an aspirational global using does not compile.

global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
