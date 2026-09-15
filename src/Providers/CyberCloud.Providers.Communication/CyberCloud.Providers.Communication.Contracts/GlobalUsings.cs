// ⚠ `ErrorCode` IS aliased here, unlike every other provider's .Contracts — because this one names
// error codes. The functions that turn a body into a wire type refuse by name (a BYO channel with no
// handles, an argument with no `=`), and Orleans ships a PUBLIC `Orleans.ErrorCode` that reaches
// here transitively through CyberCloud.ResourceManager.Contracts.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Communication.Contracts;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
