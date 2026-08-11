namespace CyberCloud.Analyzers.Tests;

/// <summary>
///     CC1007 — <c>docs/plan/00 § Non-negotiables</c>: "no <c>#pragma warning disable</c> without a
///     linked issue".
/// </summary>
public sealed class PragmaWithoutIssueAnalyzerTests {
    // ── positive ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task ABarePragmaIsReported() =>
        AnalyzerHarness.ReportsWithoutSuppressionCheckAsync<PragmaWithoutIssueAnalyzer>(
            """
            {|CC1007:#pragma warning disable CA1822|}

            class Thing
            {
            }
            """
        );

    /// <summary>A justification with no link is a note to nobody.</summary>
    [Fact]
    public Task AJustificationWithoutALinkIsStillReported() =>
        AnalyzerHarness.ReportsWithoutSuppressionCheckAsync<PragmaWithoutIssueAnalyzer>(
            """
            {|CC1007:#pragma warning disable CA1822 // this one is fine, honestly|}

            class Thing
            {
            }
            """
        );

    /// <summary>The blanket form — no codes at all — is the worst one and is still reported.</summary>
    [Fact]
    public Task ABlanketDisableIsReported() =>
        AnalyzerHarness.ReportsWithoutSuppressionCheckAsync<PragmaWithoutIssueAnalyzer>(
            """
            {|CC1007:#pragma warning disable|}

            class Thing
            {
            }
            """
        );

    // ── negative ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task ATrailingUrlIsALink() =>
        AnalyzerHarness.IsSilentAsync<PragmaWithoutIssueAnalyzer>(
            """
            #pragma warning disable CA1822 // https://github.com/Rikarin/CyberCloud/issues/42

            class Thing
            {
            }
            """
        );

    [Fact]
    public Task AHashNumberIsALink() =>
        AnalyzerHarness.IsSilentAsync<PragmaWithoutIssueAnalyzer>(
            """
            #pragma warning disable CA1822 // remove once #42 lands

            class Thing
            {
            }
            """
        );

    [Fact]
    public Task ATrackerKeyIsALink() =>
        AnalyzerHarness.IsSilentAsync<PragmaWithoutIssueAnalyzer>(
            """
            #pragma warning disable CA1822 // blocked on CC-1234

            class Thing
            {
            }
            """
        );

    /// <summary>A justification too long for one line gets the line above.</summary>
    [Fact]
    public Task ALinkOnTheLineAboveCounts() =>
        AnalyzerHarness.IsSilentAsync<PragmaWithoutIssueAnalyzer>(
            """
            // Suppressed while the upstream fix is in flight — https://github.com/dotnet/roslyn/issues/1
            #pragma warning disable CA1822

            class Thing
            {
            }
            """
        );

    /// <summary>⚠ <c>restore</c> is the good half and must never be reported.</summary>
    [Fact]
    public Task RestoreIsNotADisable() =>
        AnalyzerHarness.IsSilentAsync<PragmaWithoutIssueAnalyzer>(
            """
            #pragma warning restore CA1822

            class Thing
            {
            }
            """
        );

    /// <summary>
    ///     ⚠ A file with a link somewhere in it does not license a pragma. The comment has to be
    ///     adjacent to the directive, or the rule degrades into "does this file mention a URL".
    /// </summary>
    [Fact]
    public Task ALinkElsewhereInTheFileDoesNotCount() =>
        AnalyzerHarness.ReportsWithoutSuppressionCheckAsync<PragmaWithoutIssueAnalyzer>(
            """
            // See https://github.com/Rikarin/CyberCloud/issues/7 for the design.

            class Thing
            {
                void Work()
                {
                }
            }

            {|CC1007:#pragma warning disable CA1822|}
            """
        );

    /// <summary>A file with no suppression at all is the common case and must be silent.</summary>
    [Fact]
    public Task AFileWithNoPragmaIsSilent() =>
        AnalyzerHarness.IsSilentAsync<PragmaWithoutIssueAnalyzer>(
            """
            class Thing
            {
            }
            """
        );
}
