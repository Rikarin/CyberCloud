// Whether the checked-in Python and Go SDKs under generated/ are valid to their own toolchains —
// the reading half of the `Generated Python SDK compiles` and `Generated Go SDK compiles` gates in
// Build.Architecture.cs. Issue #40.
//
// Not a partial of Build, for the reason build/README.md gives about GeneratedSdkSurface.cs: what a
// gate reads is a separate concern from what it decides. Where that file hands C# to Roslyn in
// process, this one hands a directory to an interpreter or a compiler that may not be installed —
// and "not installed" is a fact the gate reports as ○, never as ✔, which is why the toolchain
// lookup is here beside the run rather than inlined into a gate that could forget to say so.

using Nuke.Common.IO;
using Nuke.Common.Tooling;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

/// <summary>
///     What one run of a toolchain over a checked-in package produced.
/// </summary>
/// <param name="ExitCode">The process's exit code; zero is the verdict the gate wants.</param>
/// <param name="Output">Every line the process wrote, either stream, in order.</param>
sealed record ToolRun(int ExitCode, IReadOnlyList<string> Output) {
    /// <summary>The output as one line per problem, for a violation list.</summary>
    public IEnumerable<string> Problems => Output.Where(x => x.Trim().Length > 0);
}

/// <summary>
///     Runs a toolchain over a generated package and counts what the package declares.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every run writes its cache under <c>artifacts/</c>, never into the tree.</b>
///         <c>compileall</c> writes <c>__pycache__</c> beside every module it compiles, <c>mypy</c>
///         writes <c>.mypy_cache</c> in the working directory, and <c>go</c> writes its build cache
///         where <c>GOCACHE</c> says. A gate that left any of those under <c>generated/</c> would
///         make the <c>Generated surfaces</c> row above it report a stale file the gate itself
///         produced, on the next run, on every machine.
///     </para>
///     <para>
///         ⚠ <b>The declaration count is the vacuity guard</b>, as <c>GeneratedSdkFile.Types</c>
///         is for the .NET SDK: a package whose files parsed to nothing produces no errors, and a
///         row that reported ✔ over an empty compilation would be the defect issue #73 was filed
///         about, one toolchain over.
///     </para>
/// </remarks>
static class GeneratedPackageSurface {
    /// <summary>
    ///     An executable off <c>PATH</c>, or <see langword="null" /> with the reason.
    /// </summary>
    /// <param name="executable">The executable's name — <c>python</c>, <c>go</c>.</param>
    /// <param name="absent">Why it could not be resolved, when it could not.</param>
    /// <remarks>
    ///     ⚠ Resolved rather than probed: "is it installed" is the question the gate's status
    ///     answers, and "does it work" is answered by running it over the package.
    /// </remarks>
    public static Tool? Resolve(string executable, out string absent) {
        try {
            absent = string.Empty;
            return ToolResolver.GetPathTool(executable);
        } catch (Exception exception) {
            absent = $"`{executable}` is not on PATH ({exception.Message.TrimEnd('.')})";
            return null;
        }
    }

    /// <summary>Runs a tool and captures its exit code and output, without logging either.</summary>
    /// <param name="tool">The tool.</param>
    /// <param name="arguments">Its arguments, already quoted.</param>
    /// <param name="workingDirectory">The package root.</param>
    /// <param name="environment">Variables added to the inherited environment — the cache locations.</param>
    public static ToolRun Run(
        Tool tool,
        ArgumentStringHandler arguments,
        AbsolutePath workingDirectory,
        IReadOnlyDictionary<string, string> environment
    ) {
        var exitCode = 0;

        var output = tool(
            arguments,
            workingDirectory: workingDirectory,
            environmentVariables: environment,
            logOutput: false,
            logInvocation: false,
            exitHandler: process => exitCode = process.ExitCode
        );

        return new(exitCode, output.Select(x => x.Text).ToList());
    }

    /// <summary>Every file under a root with one extension, ordered by path.</summary>
    public static IReadOnlyList<AbsolutePath> FilesOf(AbsolutePath root, string extension) =>
        root.DirectoryExists()
            ? root.GlobFiles("**/*" + extension).OrderBy(x => x.ToString(), StringComparer.Ordinal).ToList()
            : [];

    /// <summary>
    ///     How many lines across the files start with a declaration keyword — <c>class </c> at any
    ///     indentation for Python, <c>type </c> at column zero for Go.
    /// </summary>
    public static int Declarations(IEnumerable<AbsolutePath> files, string keyword, bool indented) =>
        files.Sum(file => File.ReadLines(file).Count(line => (indented ? line.TrimStart() : line).StartsWith(keyword, StringComparison.Ordinal)));
}
