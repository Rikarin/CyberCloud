// The Sdk here is Microsoft.NET.Sdk rather than Microsoft.NET.Sdk.Web (the host is composed by
// AgentComposition rather than by the web SDK's implicit builder), so ASP.NET Core's usings are not
// implicit. These are in every file.

global using Microsoft.AspNetCore.Builder;
global using Microsoft.Extensions.DependencyInjection;
global using Microsoft.Extensions.Hosting;
global using Microsoft.Extensions.Logging;
