// ⚠ The same CS0104 the shipping assemblies hit: Orleans ships a public `Orleans.ErrorCode` and
// Microsoft.Orleans.Sdk adds `global using Orleans;`, which arrives here transitively through
// CyberCloud.Communication.Contracts.
global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Communication.Contracts;
global using Shouldly;
