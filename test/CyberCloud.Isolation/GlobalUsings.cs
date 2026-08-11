// ⚠ TWO aliases, and the second one is the trap this suite walks straight into.
//
// `ErrorCode` is ambiguous because Orleans ships a PUBLIC `Orleans.ErrorCode` and the SDK adds
// `global using Orleans;` — the same repair as CyberCloud.ResourceManager/GlobalUsings.cs.
//
// `ObjectRef` is ambiguous because this project references BOTH CyberCloud.Kubernetes.Contracts (a
// cluster object) and CyberCloud.Authorization.Contracts (a ReBAC object), exactly as
// CyberCloud.ResourceManager does. The Kubernetes one is named far more often here, so the simple
// name is pinned to it and the ReBAC one is spelled out where tuples are written.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
global using Shouldly;
