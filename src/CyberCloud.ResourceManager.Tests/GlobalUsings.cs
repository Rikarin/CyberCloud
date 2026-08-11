global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.ResourceManager;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.ResourceManager.Contracts.Registry;
global using CyberCloud.ResourceManager.Grains;
global using CyberCloud.ResourceManager.Tests.Infrastructure;
global using CyberCloud.Tenancy.Contracts;
global using Shouldly;

// ⚠ Orleans ships a PUBLIC `Orleans.ErrorCode` and the SDK imports the `Orleans` namespace globally,
// so the simple name is ambiguous wherever CyberCloud.Core is also in scope — the same trap as
// CyberCloud.Tenancy/GlobalUsings.cs.
global using ErrorCode = CyberCloud.Core.ErrorCode;
