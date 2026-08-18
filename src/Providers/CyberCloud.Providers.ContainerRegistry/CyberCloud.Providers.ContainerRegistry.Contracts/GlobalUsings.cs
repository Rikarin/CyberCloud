// ⚠ NO `ErrorCode` ALIAS HERE, and the sibling implementation assembly has one — the same split
// CyberCloud.Providers.DBforMySQL.Contracts records. Orleans ships a PUBLIC `Orleans.ErrorCode` and
// Microsoft.Orleans.Sdk's build props inject `global using Orleans;`, which reaches here
// transitively; nothing in this assembly names an error code, and an unused alias is IDE0005, which
// is an error (Directory.Build.props § Warnings and analysis).

global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
