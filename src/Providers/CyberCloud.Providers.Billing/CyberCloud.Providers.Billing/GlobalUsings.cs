// ⚠ `ErrorCode` is ambiguous here and the alias is the fix — the same trap, and the same repair, as
// every provider before it.
//
// ⚠ `CyberCloud.Tenancy.Contracts` is here for `QuotaMeter` alone — docs/plan/06 § Quota owns the
// families.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Billing.Contracts;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Providers.Billing.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.Tenancy.Contracts;
