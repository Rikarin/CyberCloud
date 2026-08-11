global using CyberCloud.Core;
global using CyberCloud.Kubernetes.Contracts;

// ⚠ See CyberCloud.Kubernetes.Contracts/GlobalUsings.cs — `Orleans.ErrorCode` is public and
// Microsoft.Orleans.Sdk imports the `Orleans` namespace globally, so the simple name is ambiguous
// wherever CyberCloud.Core is also in scope.
global using ErrorCode = CyberCloud.Core.ErrorCode;
