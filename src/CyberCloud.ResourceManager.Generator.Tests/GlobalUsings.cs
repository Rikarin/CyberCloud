global using Shouldly;
global using System.Text.Json.Nodes;

// The program under test. Aliased rather than named inline because `Program` is also what a
// Microsoft.Testing.Platform host generates for this assembly, and two of them in scope is a
// resolution question no reader should have to answer.
global using GeneratorProgram = CyberCloud.ResourceManager.Generator.Program;

// ⚠ Every test in this assembly drives Program.Main in-process, and Main writes to Console.Out and
// Console.Error. Console is process-global: two tests redirecting it at once would each capture a
// slice of the other's output, which is a flake that reads like a message-formatting bug. The runs
// are milliseconds and there are fewer than thirty of them, so serialising the assembly costs
// nothing worth having.
[assembly: Xunit.CollectionBehavior(DisableTestParallelization = true)]
