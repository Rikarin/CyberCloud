// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix.
//
// Orleans ships a PUBLIC `Orleans.ErrorCode` (an internal-diagnostics enum in Orleans.Runtime), and
// Microsoft.Orleans.Sdk adds `global using Orleans;`. Any file here that also imports
// CyberCloud.Core therefore has two candidates for the simple name and the compiler reports CS0104.
// Observed on the first build of this project, in TenancyGrainKeys.cs.
//
// A using-alias beats a namespace import, so this pins the name assembly-wide to ours. The
// alternative — qualifying every mention as CyberCloud.Core.ErrorCode — would be noise on roughly
// every failure path in every grain, and would leave the trap armed for the next file.

global using ErrorCode = CyberCloud.Core.ErrorCode;
