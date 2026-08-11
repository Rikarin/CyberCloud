global using CyberCloud.Core;
global using CyberCloud.Kubernetes.Contracts;

// ⚠ See CyberCloud.Kubernetes.Contracts/GlobalUsings.cs — Orleans.ErrorCode is public and the
// Orleans namespace is imported globally by the Orleans SDK, so the simple name is ambiguous
// wherever CyberCloud.Core is also in scope.
global using ErrorCode = CyberCloud.Core.ErrorCode;
