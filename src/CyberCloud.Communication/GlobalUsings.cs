// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Metering/GlobalUsings.cs. Orleans ships a PUBLIC `Orleans.ErrorCode` and
// Microsoft.Orleans.Sdk adds `global using Orleans;`, so any file that also imports CyberCloud.Core
// has two candidates for the simple name and the compiler reports CS0104.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;

// Every wire type, seam and grain interface here is named on nearly every file.
global using CyberCloud.Communication.Contracts;
