// ⚠ `ErrorCode` is aliased because Budgets.ToSpec refuses by name, and Orleans ships a PUBLIC
// `Orleans.ErrorCode` that reaches here through CyberCloud.ResourceManager.Contracts — the split
// CyberCloud.Providers.Communication.Contracts records.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Billing.Contracts;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
