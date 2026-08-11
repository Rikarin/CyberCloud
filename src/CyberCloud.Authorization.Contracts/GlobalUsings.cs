// See CyberCloud.Tenancy/GlobalUsings.cs: Orleans ships a public `Orleans.ErrorCode` and
// Microsoft.Orleans.Sdk adds `global using Orleans;`, so the simple name is ambiguous wherever
// CyberCloud.Core is also imported. A using-alias beats a namespace import.
global using ErrorCode = CyberCloud.Core.ErrorCode;
