// Clean, Restore, Compile — docs/plan/23 § Build, row `Restore` `Compile`:
// ".NET, with CPM and deterministic builds".

using System.Collections.Generic;
using System.Linq;
using Nuke.Common.IO;
using Nuke.Common.Tools.DotNet;
using Serilog;

partial class Build
{
    /// <summary>
    ///     Directories deleted by <c>Clean</c>.
    ///     <para>
    ///         <c>references/</c> is not among them and must never be — docs/plan/03 § Top level:
    ///         it is read-only, never built, never restored, and a <c>bin</c> inside a vendored
    ///         reference tree is that tree's business.
    ///     </para>
    ///     <para>
    ///         ⚠ Nor is <c>.nuke/temp/</c>, however tempting: Nuke writes
    ///         <c>.nuke/temp/build-attempt.log</c> during the very run that would be deleting it.
    ///         Observed — <c>./build.sh Clean</c> failed with
    ///         <c>DirectoryNotFoundException: Could not find a part of the path
    ///         '…/.nuke/temp/build-attempt.log'</c> thrown from <c>BuildExecutor.Execute</c>, that
    ///         is, after the target had already succeeded. It is gitignored; leave it alone.
    ///     </para>
    /// </summary>
    IEnumerable<AbsolutePath> CleanableDirectories =>
        SourceRoots
            .Concat([RootDirectory / "build"])
            .SelectMany(root => root.GlobDirectories("**/bin", "**/obj"))
            .Concat([ArtifactsDirectory])
            .Where(x => x.DirectoryExists());

    void CleanOutputs()
    {
        var deleted = 0;

        // Materialised before deleting: GlobDirectories enumerates lazily, and deleting `obj` while
        // walking into it is how a clean target starts throwing IOException on one machine in ten.
        foreach (var directory in CleanableDirectories.ToList())
        {
            Log.Debug("Deleting {Directory}", directory);
            directory.DeleteDirectory();
            deleted++;
        }

        Log.Information("Clean: removed {Count} output director{Suffix}", deleted, deleted == 1 ? "y" : "ies");
    }

    void RestoreSolution()
    {
        if (!SolutionHasProjects)
        {
            SkippingEmptySolution(nameof(Restore));
            return;
        }

        // CPM is on repo-wide (Directory.Packages.props); there is nothing to pass for it here —
        // restore picks the pinned versions up from the props file on its own.
        DotNetTasks.DotNetRestore(s => s
            .SetProjectFile(SolutionFile));
    }

    void CompileSolution()
    {
        if (!SolutionHasProjects)
        {
            SkippingEmptySolution(nameof(Compile));
            return;
        }

        DotNetTasks.DotNetBuild(s => s
            .SetProjectFile(SolutionFile)
            .SetConfiguration(Configuration)
            .EnableNoRestore()
            // docs/plan/23: deterministic builds. ContinuousIntegrationBuild also switches on
            // DeterministicSourcePaths and the PathMap in Directory.Build.props.
            .SetContinuousIntegrationBuild(IsServerBuild));
    }
}
