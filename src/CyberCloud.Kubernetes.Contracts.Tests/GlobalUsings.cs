global using CyberCloud.Core;

// ⚠ See CyberCloud.Kubernetes.Contracts/GlobalUsings.cs — Orleans.ErrorCode is public and the
// Orleans namespace is imported globally by Microsoft.Orleans.Sdk, so the simple name is ambiguous
// wherever CyberCloud.Core is also in scope.
global using ErrorCode = CyberCloud.Core.ErrorCode;
