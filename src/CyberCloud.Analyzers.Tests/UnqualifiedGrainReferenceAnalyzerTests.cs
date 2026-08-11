namespace CyberCloud.Analyzers.Tests;

/// <summary>
///     CC1006 — <c>docs/plan/00 § Non-negotiables</c> and <c>docs/plan/10</c>: outside grain code a
///     grain reference is taken through <c>ForTenant</c>, because
///     <c>Orleans.Multitenant</c>'s call filter never sees a caller that is not a grain.
/// </summary>
public sealed class UnqualifiedGrainReferenceAnalyzerTests
{
    const string Grains = """
        using Orleans;
        using Orleans.Multitenant;
        using CyberCloud.Core.Resources;

        public interface ITenantGrain : IGrainWithStringKey
        {
        }

        public interface ICounterGrain : IGrainWithIntegerKey
        {
        }
        """;

    // ── positive ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The gateway shape. An Orleans client holding an <c>IGrainFactory</c> reaches any tenant's
    ///     grain by naming its key, and the authorizer is never consulted — see
    ///     <c>CrossTenantReachabilityTests.Route7b_FromOutsideAGrainTheRawKeyIsSTILLOPEN</c>.
    /// </summary>
    [Fact]
    public Task ARawGetGrainFromNonGrainCodeIsReported() =>
        AnalyzerHarness.ReportsAsync<UnqualifiedGrainReferenceAnalyzer>(
            Grains
            + """

            public sealed class TenantEndpoint
            {
                public ITenantGrain Get(IGrainFactory grains, System.Guid tenantId) =>
                    {|CC1006:grains.GetGrain<ITenantGrain>(GrainKeys.Tenant(tenantId))|};
            }
            """);

    /// <summary>
    ///     ⚠ Using <c>GrainKeys</c> is <i>not</i> enough on its own — CC1004 and CC1006 answer
    ///     different questions. A correctly formatted key with no tenant qualification is still a
    ///     cross-tenant read.
    /// </summary>
    [Fact]
    public Task AHostedServiceIsNotGrainCodeEither() =>
        AnalyzerHarness.ReportsAsync<UnqualifiedGrainReferenceAnalyzer>(
            Grains
            + """

            public sealed class Reconciler
            {
                readonly IGrainFactory grains;

                public Reconciler(IGrainFactory grains) => this.grains = grains;

                public ITenantGrain Next(string key) => {|CC1006:grains.GetGrain<ITenantGrain>(key)|};
            }
            """);

    // ── negative ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Exemption 1. Inside a grain the filter <i>does</i> run —
    ///     <c>Route7a_FromInsideAGrainTheRawKeyIsNowClosed</c> asserts the
    ///     <c>UnauthorizedAccessException</c>. This is the library's job, not the analyzer's.
    /// </summary>
    [Fact]
    public Task AGrainCallingAGrainIsCoveredByTheCallFilter() =>
        AnalyzerHarness.IsSilentAsync<UnqualifiedGrainReferenceAnalyzer>(
            Grains
            + """

            public sealed class SubscriptionGrain : Grain
            {
                public ITenantGrain Owner(string key) => GrainFactory.GetGrain<ITenantGrain>(key);
            }
            """);

    /// <summary>
    ///     Exemption 2, and the whole point of the rule: <c>ForTenant</c> returns a different type,
    ///     so the tenant-qualified call cannot be confused with the raw one.
    /// </summary>
    [Fact]
    public Task ForTenantIsTheCorrectWay() =>
        AnalyzerHarness.IsSilentAsync<UnqualifiedGrainReferenceAnalyzer>(
            Grains
            + """

            public sealed class TenantEndpoint
            {
                public ITenantGrain Get(IGrainFactory grains, System.Guid tenantId) =>
                    grains.ForTenant(tenantId.ToString("D")).GetGrain<ITenantGrain>(
                        GrainKeys.Tenant(tenantId));
            }
            """);

    /// <summary>
    ///     ⚠ Exemption 3, and the one that was found by running the rule over this repository:
    ///     <c>ClusterHealthCheck</c> and <c>SiloReadinessHealthCheck</c> both call
    ///     <c>GetGrain&lt;IManagementGrain&gt;(0)</c> from an <c>IHealthCheck</c>. An integer-keyed
    ///     grain has nowhere to carry a tenant (docs/plan/00 § Coding standards:
    ///     <c>IGrainWithStringKey</c> is "the only key kind Orleans.Multitenant can carry a tenant
    ///     in"), so reaching one without a tenant cannot cross one.
    /// </summary>
    [Fact]
    public Task AnIntegerKeyedGrainCannotCarryATenant() =>
        AnalyzerHarness.IsSilentAsync<UnqualifiedGrainReferenceAnalyzer>(
            Grains
            + """

            public sealed class ClusterProbe
            {
                public ICounterGrain Get(IGrainFactory grains) => grains.GetGrain<ICounterGrain>(0);
            }
            """);

    /// <summary>
    ///     Exemption 4. A platform singleton is null-tenant, and <c>ITenantDirectoryGrain</c>'s own
    ///     remarks say to reach it "with a plain <c>IGrainFactory.GetGrain</c> — <b>not</b>
    ///     <c>ForTenant</c>". <c>TenantDirectoryCache</c> and <c>ShardMapRefresher</c> are the two
    ///     real sites.
    /// </summary>
    [Fact]
    public Task ANullTenantPlatformGrainIsReachedWithoutATenantOnPurpose() =>
        AnalyzerHarness.IsSilentAsync<UnqualifiedGrainReferenceAnalyzer>(
            Grains
            + """

            public sealed class ShardMapRefresher
            {
                public ITenantGrain Map(IGrainFactory grains) =>
                    grains.GetGrain<ITenantGrain>(GrainKeys.ShardMap());

                public ITenantGrain Directory(IGrainFactory grains) =>
                    grains.GetGrain<ITenantGrain>(GrainKeys.TenantDirectory());

                public ITenantGrain Cluster(IGrainFactory grains, System.Guid clusterId) =>
                    grains.GetGrain<ITenantGrain>(GrainKeys.ClusterConnection(clusterId));
            }
            """);

    /// <summary>
    ///     ⚠ And a <i>tenanted</i> <c>GrainKeys</c> builder is not exempt. The exemption is the set
    ///     of null-tenant key shapes, not "anything that came from GrainKeys" — otherwise the rule
    ///     would be defeated by using the correct helper.
    /// </summary>
    [Fact]
    public Task ATenantedGrainKeysBuilderIsNotAnExemption() =>
        AnalyzerHarness.ReportsAsync<UnqualifiedGrainReferenceAnalyzer>(
            Grains
            + """

            public sealed class Reader
            {
                public ITenantGrain Get(IGrainFactory grains, System.Guid resourceId) =>
                    {|CC1006:grains.GetGrain<ITenantGrain>(GrainKeys.Resource(resourceId))|};
            }
            """);
}
