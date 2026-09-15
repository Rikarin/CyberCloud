// ⚠ `ErrorCode` IS aliased here since the alert rules landed, and this file used to say the opposite.
// The workspace half of this assembly names no error code; the alert half does — MonitorAlertRules
// turns a body into an AlertRuleSpec and refuses by name (an action group naming another tenant's
// service, a path that is not a CyberCloud.Communication/services resource) — and Orleans ships a
// PUBLIC `Orleans.ErrorCode` that reaches here transitively through CyberCloud.ResourceManager.Contracts.
// The same split CyberCloud.Providers.Communication.Contracts records.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
