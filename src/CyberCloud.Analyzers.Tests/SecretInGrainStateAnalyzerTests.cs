namespace CyberCloud.Analyzers.Tests;

/// <summary>
///     CC1005 — <c>docs/plan/00 § Non-negotiables</c>: "Analyzer bans <c>[Id]</c>-annotated members
///     named <c>*Password</c>, <c>*Secret</c>, <c>*Token</c>, <c>*Key</c> outside
///     <c>CyberCloud.Vault</c>".
/// </summary>
public sealed class SecretInGrainStateAnalyzerTests {
    // ── positive — all four suffixes, because the list is the non-negotiable ─────────────────────

    [Fact]
    public Task AnIdAnnotatedTokenIsReported() =>
        AnalyzerHarness.ReportsAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ConnectionState
            {
                [Id(0)] public string {|CC1005:ApiToken|} { get; set; } = "";
            }
            """
        );

    [Fact]
    public Task AnIdAnnotatedPasswordFieldIsReported() =>
        AnalyzerHarness.ReportsAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ConnectionState
            {
                [Id(0)] public string {|CC1005:AdminPassword|} = "";
            }
            """
        );

    [Fact]
    public Task AnIdAnnotatedSecretIsReported() =>
        AnalyzerHarness.ReportsAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ConnectionState
            {
                [Id(0)] public string {|CC1005:ClientSecret|} { get; set; } = "";
            }
            """
        );

    [Fact]
    public Task AnIdAnnotatedKeyIsReported() =>
        AnalyzerHarness.ReportsAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ConnectionState
            {
                [Id(0)] public string {|CC1005:SigningKey|} { get; set; } = "";
            }
            """
        );

    /// <summary>The bare noun counts: <c>*Password</c> is a suffix glob, not "must have a prefix".</summary>
    [Fact]
    public Task TheBareNounCountsToo() =>
        AnalyzerHarness.ReportsAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ConnectionState
            {
                [Id(0)] public string {|CC1005:Password|} { get; set; } = "";
            }
            """
        );

    // ── negative ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     ⚠ The trigger is <c>[Id(n)]</c>, not the name. A member without one is not serialized,
    ///     does not reach the durable tier and does not travel between silos — it is not the hazard
    ///     the non-negotiable is about.
    /// </summary>
    [Fact]
    public Task AMemberThatIsNotSerializedIsNotGrainState() =>
        AnalyzerHarness.IsSilentAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ConnectionState
            {
                [Id(0)] public string Slug { get; set; } = "";

                public string ApiToken { get; set; } = "";
            }
            """
        );

    /// <summary>The ordinary members of a real grain-state type, none of which is a secret.</summary>
    [Fact]
    public Task OrdinaryStateMembersAreCorrect() =>
        AnalyzerHarness.IsSilentAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class TenantState
            {
                [Id(0)] public System.Guid TenantId { get; set; }
                [Id(1)] public string Slug { get; set; } = "";
                [Id(2)] public string DisplayName { get; set; } = "";
                [Id(3)] public string HomeRegion { get; set; } = "";
            }
            """
        );

    /// <summary>
    ///     ⚠ The suffix match is <b>ordinal and case-sensitive</b>, which is what stops it firing on
    ///     every English word that happens to end in the letters. <c>.editorconfig</c> makes
    ///     PascalCase an error on anything a caller can name, so the last word of a member name is
    ///     always capitalised and the suffix match is a word match.
    /// </summary>
    [Fact]
    public Task TheSuffixMatchIsOrdinalAndDoesNotFireOnALowercaseTail() =>
        AnalyzerHarness.IsSilentAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ZooState
            {
                [Id(0)] public int Monkey { get; set; }
                [Id(1)] public int Donkey { get; set; }
            }
            """
        );

    /// <summary>
    ///     ⚠ A <c>*SecretRef</c> member is silent, and the <c>Ref</c> is doing the work.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         <b>This is the shape the non-negotiable asks for, not a hole in the rule.</b>
    ///         docs/plan/00 § Non-negotiables bans a member that holds a secret and prescribes the
    ///         replacement in the same sentence: <i>"secrets are <c>SecretRef</c> handles resolved at
    ///         the data plane"</i>. So the analyzer must fire on <c>ClientSecret</c> and must not
    ///         fire on <c>ClientSecretRef</c> — a rule that reported both would be suppressed at
    ///         every correct site, and a rule suppressed everywhere protects nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The consequence is worth stating plainly, because it is easy to assume the
    ///         opposite:</b> CC1005 does not <i>cover</i>
    ///         <c>ApplicationRegistration.ClientSecretRef</c>,
    ///         <c>ServicePrincipalDescriptor.CredentialSecretRef</c> or
    ///         <c>TotpEnrollment.SecretRef</c> — it is silent on all three and always has been. What
    ///         it guards is the edit that turns one of them back into a value: drop the <c>Ref</c>,
    ///         or add a sibling <c>ClientSecret</c>, and the build stops. That is why this pair of
    ///         cases is one test — the silence is only worth having if the report next to it is real.
    ///     </para>
    /// </remarks>
    [Fact]
    public Task AHandleToASecretIsSilentButTheValueBesideItIsNot() =>
        AnalyzerHarness.ReportsAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.SecretRef")]
            public sealed record SecretRef
            {
                [Id(0)] public string Path { get; init; } = "";
                [Id(1)] public string Field { get; init; } = "";
            }

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.Registration")]
            public sealed record ApplicationRegistration
            {
                [Id(0)] public SecretRef ClientSecretRef { get; init; } = new();
                [Id(1)] public SecretRef CredentialSecretRef { get; init; } = new();
                [Id(2)] public SecretRef SecretRef { get; init; } = new();

                [Id(3)] public string {|CC1005:ClientSecret|} { get; init; } = "";
            }
            """
        );

    /// <summary>
    ///     <c>CyberCloud.Vault</c> is the one assembly allowed to hold the value — docs/plan/00
    ///     § Non-negotiables, docs/plan/18. It does not exist yet; the exemption is written so that
    ///     it does not have to be revisited when it lands.
    /// </summary>
    [Fact]
    public Task TheVaultAssemblyIsExempt() =>
        AnalyzerHarness.IsSilentAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Vault.State")]
            public sealed class VaultEntryState
            {
                [Id(0)] public string ClientSecret { get; set; } = "";
            }
            """,
            "CyberCloud.Vault"
        );

    /// <summary>
    ///     ⚠ And an assembly cannot opt out by having a name that merely starts with the same
    ///     letters. Only <c>CyberCloud.Vault</c> and things genuinely under it are exempt.
    /// </summary>
    [Fact]
    public Task AnAssemblyThatMerelyLooksLikeTheVaultIsNotExempt() =>
        AnalyzerHarness.ReportsAsync<SecretInGrainStateAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Probe.State")]
            public sealed class ConnectionState
            {
                [Id(0)] public string {|CC1005:ClientSecret|} { get; set; } = "";
            }
            """,
            "CyberCloud.VaultAdjacent"
        );
}
