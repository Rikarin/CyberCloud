using CyberCloud.Agent.Host;

// ⚠ EVERY LINE OF COMPOSITION LIVES IN AgentComposition AND NOT HERE, for the reason the silo and
// the gateway give: top-level statements cannot be called from a test.
var app = AgentComposition.Build(args);

await app.RunAsync();

/// <summary>The entry point's generated class, so a test project can reference this assembly.</summary>
public partial class Program;
