// ⚠ `ErrorCode` is ambiguous in this assembly and the alias is the fix. Orleans ships a PUBLIC
// `Orleans.ErrorCode` and Microsoft.Orleans.Server pulls in `global using Orleans;`, so any file that
// also imports CyberCloud.Core has two candidates for the simple name and the compiler reports
// CS0104. See CyberCloud.Tenancy/GlobalUsings.cs, where this was first hit.

global using ErrorCode = CyberCloud.Core.ErrorCode;

// The Sdk here is Microsoft.NET.Sdk rather than Microsoft.NET.Sdk.Web (the host is composed by
// OrleansApplication.CreateClient, not by the web SDK's implicit builder), so ASP.NET Core's usings
// are not implicit. These three are in almost every file.
global using Microsoft.AspNetCore.Http;

global using CyberCloud.Core;
global using CyberCloud.Core.Resources;
global using CyberCloud.ResourceManager.Contracts;
global using CyberCloud.Tenancy.Contracts;
