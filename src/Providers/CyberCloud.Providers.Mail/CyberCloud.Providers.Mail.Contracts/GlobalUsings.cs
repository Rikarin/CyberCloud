// ⚠ The `ErrorCode` alias is here since the mailbox type: MailMailboxes.ParsePasswordRef answers a
// Result naming a code, the shape VirtualMachines.ParseCloudInitRef established in its own
// .Contracts. Orleans ships a PUBLIC `Orleans.ErrorCode` and Microsoft.Orleans.Sdk's build props
// inject `global using Orleans;`, which reaches here transitively — the alias is what keeps the
// name unambiguous.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Kubernetes.Contracts;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
