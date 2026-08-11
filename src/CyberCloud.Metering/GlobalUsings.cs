// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix — the same trap, and the same
// repair, as CyberCloud.Tenancy/GlobalUsings.cs. Orleans ships a PUBLIC `Orleans.ErrorCode` and
// Microsoft.Orleans.Sdk adds `global using Orleans;`, so any file that also imports CyberCloud.Core
// has two candidates for the simple name and the compiler reports CS0104.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;

// The meter vocabulary and every wire type are named on nearly every file here.
//
// ⚠ CyberCloud.Tenancy.Contracts is deliberately NOT imported. QuotaMeter reaches this assembly only
// as the type of MeteredQuantity.Family, which is always inferred and never spelled — and nothing
// here calls IQuotaGrain. That is the shape docs/plan/22 § Quota describes: quota is "distinct from
// billing and enforced earlier", by the resource manager's write path, and metering reads the same
// vocabulary rather than taking a second reservation. The day this import becomes necessary is the
// day something here started enforcing quota, which is the wrong layer.
global using CyberCloud.Metering.Contracts;
