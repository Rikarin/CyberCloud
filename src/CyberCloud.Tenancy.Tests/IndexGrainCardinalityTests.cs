using CyberCloud.Core.Resources;
using CyberCloud.Tenancy.Contracts;
using CyberCloud.Tenancy.Tests.Infrastructure;
using Shouldly;
using System.Reflection;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/04 § Grain taxonomy's ⚠, aimed at every grain this assembly defines.
/// </summary>
/// <remarks>
///     <para>
///         <i>
///             "An index grain is a hot spot if the indexed value is not high-cardinality.
///             <c>IEmailIndexGrain</c> keyed by email is fine (one activation per email). An index grain
///             keyed by <b>resource type</b> would be a single activation serialising every create in the
///             platform. The review question for any new index grain is 'what is the cardinality of the
///             key', and if the answer is 'small', it is not an index grain."
///         </i>
///     </para>
///     <para>
///         The review question is asked here as a <b>checked-in table</b> — <see cref="Keys" /> —
///         with one row per grain interface in <c>CyberCloud.Tenancy.Contracts</c>, and a test that
///         fails when a grain is added without a row. That is the difference between a review
///         question and a review habit.
///     </para>
/// </remarks>
[Collection(TenancySuite.Name)]
public sealed class IndexGrainCardinalityTests(TenancyCluster cluster) {
    /// <summary>
    ///     Every grain interface in <c>CyberCloud.Tenancy.Contracts</c>, its key, and the
    ///     cardinality of that key. ⚠ <b>Add a row when you add a grain</b> —
    ///     <see cref="EveryGrainInterfaceHasACardinalityRow" /> is the gate.
    /// </summary>
    static readonly (string Grain, string Key, Cardinality Cardinality)[] Keys = [
        ("ITenantGrain", "tenant/{tenantId:N}", Cardinality.PerTenant),
        ("ISubscriptionGrain", "sub/{subscriptionId:N}", Cardinality.PerEntity),
        ("IResourceGroupGrain", "sub/{subscriptionId:N}/rg/{name}", Cardinality.PerEntity),
        ("IResourceIndexGrain", "idx/path/{sha256(canonicalPath)[..16]}", Cardinality.PerEntity),
        ("IEmailIndexGrain", "idx/email/{sha256(tenantId + email)[..16]}", Cardinality.PerEntity),
        ("IQuotaGrain", "sub/{subscriptionId:N}", Cardinality.PerEntity),
        ("IShardMapGrain", "platform/shard-map", Cardinality.Singleton),
        ("ITenantDirectoryGrain", "platform/tenant-directory", Cardinality.Singleton)
    ];

    /// <summary>
    ///     The two singletons, and why each is allowed to be one.
    /// </summary>
    /// <remarks>
    ///     A singleton is only acceptable when its <i>write rate</i> is bounded by something other
    ///     than the platform's create rate. Both of these are bounded by "new tenants per day",
    ///     which docs/plan/05 § The tenant directory sizes at 0.12 writes per second, and both are
    ///     read from an in-process mirror rather than from the grain.
    /// </remarks>
    static readonly string[] AllowedSingletons = ["IShardMapGrain", "ITenantDirectoryGrain"];

    [Fact]
    public void EveryGrainInterfaceHasACardinalityRow() {
        var declared = typeof(ITenantGrain).Assembly.GetTypes()
            .Where(t => t.IsInterface && typeof(IAddressable).IsAssignableFrom(t))
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        declared.ShouldBe(
            Keys.Select(x => x.Grain).OrderBy(x => x, StringComparer.Ordinal).ToList(),
            "a grain interface was added without answering docs/plan/04 § Grain taxonomy's review "
            + "question: what is the cardinality of its key? Add a row to Keys."
        );
    }

    [Fact]
    public void NoIndexGrainIsASingleton() {
        // ⚠ THE ⚠. An index grain keyed by something small is a single activation serialising every
        // create in the platform.
        var indexGrains = Keys.Where(x => x.Key.Contains("idx/", StringComparison.Ordinal)).ToList();

        indexGrains.Count.ShouldBe(2, "there are exactly two index grains.");
        indexGrains.ShouldAllBe(x => x.Cardinality == Cardinality.PerEntity);
    }

    [Fact]
    public void OnlyTheTwoDocumentedPlatformGrainsAreSingletons() =>
        Keys.Where(x => x.Cardinality == Cardinality.Singleton)
            .Select(x => x.Grain)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBe(
                AllowedSingletons.OrderBy(x => x, StringComparer.Ordinal),
                "a new singleton grain needs the argument that its write rate is bounded by "
                + "something other than the platform's create rate."
            );

    [Fact]
    public void NoGrainKeyIsDerivedFromAResourceTypeOrAnythingElseSmall() {
        // The specific example the document gives, and the general rule behind it. A key built from
        // any of these words would be a key with a handful of values.
        string[] forbidden = ["type", "provider", "kind", "region", "status", "meter", "state"];

        foreach (var (grain, key, _) in Keys) {
            foreach (var word in forbidden) {
                key.ShouldNotContain(
                    word,
                    Case.Insensitive,
                    $"{grain}'s key mentions '{word}', which names a low-cardinality value. "
                    + "docs/plan/04 § Grain taxonomy: 'if the answer is small, it is not an index "
                    + "grain'."
                );
            }
        }
    }

    [Fact]
    public void TheQuotaMeterIsAnEnumAndNeverAKey() {
        // The nearest miss in this assembly: QuotaMeter has six values, and a quota grain keyed by
        // meter would be six activations for the whole platform. It is state on a per-subscription
        // grain instead — docs/plan/06 § Quota's "the quota grain is per-subscription".
        Enum.GetValues<QuotaMeter>().Length.ShouldBeLessThan(10);

        Keys.Single(x => x.Grain == "IQuotaGrain").Key.ShouldBe("sub/{subscriptionId:N}");

        typeof(GrainKeys).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .SelectMany(m => m.GetParameters())
            .ShouldNotContain(
                p => p.ParameterType == typeof(QuotaMeter),
                "no key factory takes a meter."
            );
    }

    [Fact]
    public void TenThousandDistinctResourcePathsProduceTenThousandDistinctIndexKeys() {
        // The cardinality claim as a measurement rather than an assertion about a comment. Same
        // tenant, same subscription, same group, same provider AND same resource type — only the
        // name differs, which is the case where a type-keyed index would collapse to one grain.
        var tenant = TenancyCluster.Tenant(41_001);
        var subscription = TenancyCluster.Tenant(41_002);
        var type = new ResourceTypeName("CyberCloud.DBforPostgreSQL", "servers");

        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 10_000; i++) {
            keys.Add(GrainKeys.PathIndex(new(tenant, subscription, "prod", type, "db-" + i, Guid.Empty)));
        }

        keys.Count.ShouldBe(10_000, "one activation per resource address, not per resource type.");
    }

    [Fact]
    public void TenThousandDistinctEmailsProduceTenThousandDistinctIndexKeys() {
        var tenant = TenancyCluster.Tenant(41_003);

        var keys = new HashSet<string>(StringComparer.Ordinal);

        for (var i = 0; i < 10_000; i++) {
            keys.Add(GrainKeys.EmailIndex(tenant, $"user{i}@example.com"));
        }

        keys.Count.ShouldBe(10_000);
    }

    [Fact]
    public async Task TwoResourcesOfTheSameTypeInOneGroupAreTwoIndexActivations() {
        // The same claim, against the real grains in the real cluster rather than against the key
        // function: two creates of the same TYPE do not contend.
        var tenant = TenancyCluster.Tenant(41_004);
        var subscription = Guid.NewGuid();

        var first = Address(tenant, subscription, "web-01");
        var second = Address(tenant, subscription, "web-02");

        first.Type.ShouldBe(second.Type);
        GrainKeys.PathIndex(first).ShouldNotBe(GrainKeys.PathIndex(second));

        var claims = await Task.WhenAll(
            cluster.ResourceIndexGrain(first).TryClaimAsync(first, first.Id),
            cluster.ResourceIndexGrain(second).TryClaimAsync(second, second.Id)
        );

        claims.ShouldAllBe(x => x.IsSuccess);

        cluster.ResourceIndexGrain(first).GetGrainId().ShouldNotBe(cluster.ResourceIndexGrain(second).GetGrainId());
    }

    [Fact]
    public void TheDigestIsSixteenHexCharactersWhichIsSixtyFourBits() =>
        // docs/plan/06 § Grain keys: "[..16] is sixteen hex characters — 64 bits — not sixteen
        // bytes", with the birthday arithmetic scoped to one tenant's entries. Restated here
        // because the cardinality argument depends on the digest being wide enough that two
        // addresses do not share a grain.
        GrainKeys.DigestLength.ShouldBe(16);

    static ResourceId Address(Guid tenant, Guid subscription, string name) =>
        new(
            tenant,
            subscription,
            "prod",
            new("CyberCloud.DBforPostgreSQL", "servers"),
            name,
            Guid.NewGuid()
        );

    /// <summary>How many activations of a grain type can exist.</summary>
    enum Cardinality {
        /// <summary>One worldwide. Only legitimate for a platform singleton with O(day) writes.</summary>
        Singleton,

        /// <summary>One per tenant.</summary>
        PerTenant,

        /// <summary>One per entity — the only acceptable answer for an index grain.</summary>
        PerEntity
    }
}
