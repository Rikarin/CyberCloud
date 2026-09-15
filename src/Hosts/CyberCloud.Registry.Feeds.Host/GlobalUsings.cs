// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode` and Orleans.Multitenant pulls in `global using Orleans;`, so any file that
// also imports CyberCloud.Core has two candidates for the simple name and the compiler reports
// CS0104. See CyberCloud.Tenancy/GlobalUsings.cs, where this was first hit.

global using ErrorCode = CyberCloud.Core.ErrorCode;
global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.Providers.ContainerRegistry.Contracts;
global using CyberCloud.ResourceManager.Contracts;

// The Sdk here is Microsoft.NET.Sdk rather than Microsoft.NET.Sdk.Web (the host is composed by
// OrleansApplication.CreateClient, not by the web SDK's implicit builder), so ASP.NET Core's usings
// are not implicit. These are in almost every file.
global using Microsoft.AspNetCore.Http;
