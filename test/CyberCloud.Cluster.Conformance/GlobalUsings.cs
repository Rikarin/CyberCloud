// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Conformance/GlobalUsings.cs. Orleans ships a PUBLIC `Orleans.ErrorCode` and
// the Orleans SDK adds `global using Orleans;`, so any file that also imports CyberCloud.Core has two
// candidates for the simple name and the compiler reports CS0104.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Conformance;
global using CyberCloud.Core;
global using CyberCloud.Core.Contracts;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
global using Shouldly;
