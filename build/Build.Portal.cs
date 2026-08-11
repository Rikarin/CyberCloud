// Portal — docs/plan/23 § Build, row `Portal`:
// "pnpm install/lint/test/build, performance budget, axe".

partial class Build
{
    /// <summary>
    ///     ⚠ <c>Portal</c> deliberately has no <c>DependsOn</c>, which looks like an omission next
    ///     to <c>Generate</c> and is not.
    ///     <para>
    ///         The tempting edge is <c>Portal</c> → <c>Generate</c>, because <c>Generate</c> emits
    ///         <c>portal/libs/api</c> and the resource forms (ADR-012). But those are generated
    ///         *and committed* — docs/plan/03 § Assembly graph rules 6, "the generator owns the
    ///         directory" — and <c>Generate</c>'s job in the graph is to fail on drift, not to
    ///         produce inputs for a later target. A worktree the portal cannot build from is
    ///         already a failing <c>Generate</c>.
    ///     </para>
    ///     <para>
    ///         The cost of the edge is what settles it: <c>Generate</c> depends on <c>Compile</c>,
    ///         so <c>./build.sh Portal</c> would restore and build the whole .NET solution before
    ///         running <c>eslint</c>. That makes the .NET SDK a prerequisite for touching Angular,
    ///         and docs/plan/23's 25-minute PR budget is met by parallelism — which requires the
    ///         two toolchains to be independently invocable.
    ///     </para>
    /// </summary>
    void BuildPortal()
        => NotImplementedYet(
            nameof(Portal),
            "run `pnpm install --frozen-lockfile` over the portal/ workspace, then lint, the Jest + "
            + "Angular TestBed suites and the production builds of apps/portal and apps/admin, "
            + "failing on a bundle over its performance budget or on any axe accessibility "
            + "violation",
            "docs/plan/23 § Build (row `Portal`) and § Test layers (row `Portal`). Depends on the "
            + "pnpm workspace existing — docs/plan/03 § portal/ is currently a skeleton, and both "
            + "the budget numbers and the axe ruleset have to be calibrated against a real app "
            + "rather than guessed at now.");
}
