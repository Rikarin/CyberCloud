// ⚠ `ErrorCode` is ambiguous here and the alias is the fix. Orleans ships a PUBLIC `Orleans.ErrorCode`
// and the Orleans SDK adds `global using Orleans;`, so any file that also imports CyberCloud.Core has
// two candidates for the simple name and the compiler reports CS0104. See
// CyberCloud.Tenancy/GlobalUsings.cs, where this was first hit.

global using ErrorCode = CyberCloud.Core.ErrorCode;
