namespace CyberCloud.Analyzers.Tests;

/// <summary>
///     CC1003 — <c>docs/plan/00 § Coding standards</c> and <c>docs/plan/04 § Failure and upgrade</c>:
///     every <c>[GenerateSerializer]</c> type has a stable <c>[Alias]</c>.
/// </summary>
public sealed class GenerateSerializerAliasAnalyzerTests
{
    // ── positive ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AGenerateSerializerTypeWithNoAliasIsReported() =>
        AnalyzerHarness.ReportsAsync<GenerateSerializerAliasAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            public sealed class {|CC1003:TenantState|}
            {
                [Id(0)] public string Slug { get; set; } = "";
            }
            """);

    [Fact]
    public Task AGenerateSerializerRecordStructWithNoAliasIsReported() =>
        AnalyzerHarness.ReportsAsync<GenerateSerializerAliasAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            public readonly record struct {|CC1003:ShardAssignment|}([property: Id(0)] string Shard);
            """);

    [Fact]
    public Task AGenerateSerializerEnumWithNoAliasIsReported() =>
        AnalyzerHarness.ReportsAsync<GenerateSerializerAliasAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            public enum {|CC1003:TenantStatus|}
            {
                Active,
                Suspended,
            }
            """);

    // ── negative ─────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public Task AnAliasedTypeIsCorrect() =>
        AnalyzerHarness.IsSilentAsync<GenerateSerializerAliasAnalyzer>(
            """
            using Orleans;

            [GenerateSerializer]
            [Alias("CyberCloud.Tenancy.State.Tenant")]
            public sealed class TenantState
            {
                [Id(0)] public string Slug { get; set; } = "";
            }
            """);

    /// <summary>
    ///     ⚠ The attribute order must not matter, and a rule written against the syntax list rather
    ///     than the symbol's attributes would make it matter.
    /// </summary>
    [Fact]
    public Task TheAttributeOrderDoesNotMatter() =>
        AnalyzerHarness.IsSilentAsync<GenerateSerializerAliasAnalyzer>(
            """
            using Orleans;

            [Alias("CyberCloud.Tenancy.State.Tenant"), GenerateSerializer]
            public sealed class TenantState
            {
                [Id(0)] public string Slug { get; set; } = "";
            }
            """);

    /// <summary>
    ///     A type that is not a wire type carries neither attribute and is nobody's business.
    ///     <c>[Alias]</c> matters <i>because</i> of <c>[GenerateSerializer]</c>, not on its own.
    /// </summary>
    [Fact]
    public Task AnOrdinaryTypeIsNotAWireType() =>
        AnalyzerHarness.IsSilentAsync<GenerateSerializerAliasAnalyzer>(
            """
            public sealed class Snapshot
            {
                public string Slug { get; set; } = "";
            }
            """);

    /// <summary>
    ///     <c>[Alias]</c> on a grain interface without <c>[GenerateSerializer]</c> — the normal
    ///     spelling for a grain contract, and not a serializer at all.
    /// </summary>
    [Fact]
    public Task AnAliasedGrainInterfaceIsCorrect() =>
        AnalyzerHarness.IsSilentAsync<GenerateSerializerAliasAnalyzer>(
            """
            using Orleans;

            [Alias("CyberCloud.Tenancy.ITenantGrain")]
            public interface ITenantGrain : IGrainWithStringKey
            {
            }
            """);
}
