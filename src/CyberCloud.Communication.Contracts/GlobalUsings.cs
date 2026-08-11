// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Metering.Contracts/GlobalUsings.cs. Orleans ships a PUBLIC `Orleans.ErrorCode`
// and Microsoft.Orleans.Sdk adds `global using Orleans;`, so any file that also imports
// CyberCloud.Core has two candidates for the simple name and the compiler reports CS0104.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;

// GrainKeys, for CommunicationGrainKeys — ADR-002 makes it the only type allowed to build the
// within-tenant part of a key, and this assembly is where a caller outside the module gets one.
global using CyberCloud.Core.Resources;
