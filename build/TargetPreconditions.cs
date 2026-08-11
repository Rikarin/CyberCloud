// The shape of "this target cannot honestly run here, and here is exactly why".
//
// docs/plan/23 § Build gives four targets — `Images`, `E2E`, `Chaos`, `Load` — inputs that a
// checkout does not contain: a registry, a signing identity, a staging deployment, a cluster big
// enough to break. Each of them therefore has to be able to say "blocked", and the difference
// between a useful block and a useless one is entirely in the message:
//
//   ✘ not implemented yet                            ← says nothing, and is a lie once it is
//   ✘ syft is not on PATH — brew install syft         ← says what and what to do about it
//
// ⚠ THE POINT OF COLLECTING RATHER THAN THROWING ON THE FIRST ONE. A target with four unmet
// preconditions that reports one per run costs four runs to configure, and the fourth run is the
// first time anybody learns what the whole list was. Every check is evaluated, satisfied ones
// included, and the log shows both — "syft: present" is information, not noise, when the next line
// is "cosign: absent".

using System;
using System.Collections.Generic;
using System.Linq;
using Nuke.Common;
using Nuke.Common.Tooling;
using Serilog;

/// <summary>
///     The inputs a target needs that a checkout does not provide, gathered so the failure names all
///     of them at once.
/// </summary>
/// <param name="target">The Nuke target these belong to, for the message.</param>
sealed class TargetPreconditions(string target)
{
    readonly List<(string What, string Unblock)> unmet = [];
    readonly List<string> met = [];

    /// <summary>Whether every precondition holds. Blocked targets are the ones where it does not.</summary>
    public bool Satisfied => unmet.Count == 0;

    /// <summary>The number of checks made, satisfied or not — a target that checked nothing is not blocked, it is empty.</summary>
    public int Checked => met.Count + unmet.Count;

    /// <summary>Records a precondition and whether it holds.</summary>
    /// <param name="satisfied">Whether it holds.</param>
    /// <param name="what">What is needed, in the negative: "no container registry is configured".</param>
    /// <param name="unblock">
    ///     The concrete thing that would make it hold — a command to run, a parameter to pass, a file
    ///     to create. ⚠ Never a restatement of <paramref name="what" />: the reader already knows what
    ///     is missing by the time they read this half.
    /// </param>
    public void Require(bool satisfied, string what, string unblock)
    {
        if (satisfied)
            met.Add(what);
        else
            unmet.Add((what, unblock));
    }

    /// <summary>
    ///     An executable off <c>PATH</c>, or <see langword="null" /> with the absence recorded.
    /// </summary>
    /// <remarks>
    ///     ⚠ Resolves rather than runs. Probing a tool by invoking it costs a process per check and
    ///     turns "is it installed" into "does it work", which is a different question and a slower
    ///     one; the targets that need the second answer ask it after this list is clean.
    /// </remarks>
    public Tool? Tool(string executable, string unblock)
    {
        try
        {
            var tool = ToolResolver.GetPathTool(executable);

            met.Add($"`{executable}` is on PATH");

            return tool;
        }
        catch (Exception exception)
        {
            unmet.Add((
                $"`{executable}` is not on PATH ({exception.Message.TrimEnd('.')})",
                unblock));

            return null;
        }
    }

    /// <summary>Logs every check, satisfied and not, then fails naming the unmet ones.</summary>
    /// <param name="because">
    ///     Why the target cannot proceed without them — the sentence from docs/plan that makes these
    ///     inputs mandatory rather than convenient.
    /// </param>
    public void AssertSatisfied(string because)
    {
        foreach (var satisfied in met)
            Log.Information("  ✔ {What}", satisfied);

        foreach (var (what, _) in unmet)
            Log.Warning("  ✘ {What}", what);

        if (Satisfied)
            return;

        // No "{Target}:" prefix — Nuke already prefixes the target name in the "Errors & Warnings"
        // summary, and two copies of it on one line reads like a bug. Same reasoning as
        // Build.cs § NotImplementedYet.
        foreach (var (what, unblock) in unmet)
            Log.Error("{What}. To unblock: {Unblock}", what, unblock);

        Assert.Fail(
            $"{target} is BLOCKED on {unmet.Count} of {Checked} precondition(s), listed above. "
            + because
            + " ⚠ This target is not unimplemented — it is unrunnable here, and the difference "
            + "matters: every line above is a thing to install or configure, not a thing to write.");
    }
}
