using Shouldly;
using System.CodeDom.Compiler;
using System.Globalization;
using System.Reflection;

namespace CyberCloud.Tenancy.Contracts.Tests;

/// <summary>
///     The <c>[Id(n)]</c> and <c>[Alias]</c> gates of docs/plan/05 § Serialization and schema
///     evolution, applied to <c>CyberCloud.Tenancy.Contracts</c>.
/// </summary>
/// <remarks>
///     <para>
///         The sibling of <c>CyberCloud.Core.Contracts.Tests.WireContractTests</c>, and deliberately
///         a <b>second manifest rather than an extension of the first</b>: <c>[Id(n)]</c> numbers are
///         per type and the two assemblies version independently, so one shared list would make a
///         change in either assembly look like a change in both. The rules are identical and are
///         restated here so this file is readable on its own.
///     </para>
///     <para>
///         ⚠ <b>The <see cref="Baseline" /> list is append-only.</b> A failure here is not a test to
///         update — it is the wire-compatibility gate firing. Adding a member adds a line with the
///         next unused number for that type. Removing a member deletes its line and <b>burns</b> the
///         number; it does not free it.
///     </para>
/// </remarks>
public sealed class TenancyWireContractTests {
    static readonly Assembly Contracts = typeof(TenantDescriptor).Assembly;

    static readonly (string Type, int Id, string Member)[] Baseline = [
        ("TenantDescriptor", 0, "Id"),
        ("TenantDescriptor", 1, "Slug"),
        ("TenantDescriptor", 2, "DisplayName"),
        ("TenantDescriptor", 3, "HomeRegion"),
        ("TenantDescriptor", 4, "Status"),
        ("TenantDescriptor", 5, "CreatedAt"),
        ("TenantDescriptor", 6, "ModifiedAt"),
        ("TenantDescriptor", 7, "Version"),

        ("TenantDirectoryEntry", 0, "TenantId"),
        ("TenantDirectoryEntry", 1, "Slug"),
        ("TenantDirectoryEntry", 2, "HomeRegion"),
        ("TenantDirectoryEntry", 3, "HotShard"),
        ("TenantDirectoryEntry", 4, "DurableShard"),
        ("TenantDirectoryEntry", 5, "Status"),
        ("TenantDirectoryEntry", 6, "DirectoryVersion"),

        ("TenantDirectoryDelta", 0, "Version"),
        ("TenantDirectoryDelta", 1, "Entries"),
        ("TenantDirectoryDelta", 2, "IsFullSnapshot"),

        ("ShardAssignment", 0, "TenantId"),
        ("ShardAssignment", 1, "DurableShard"),
        ("ShardAssignment", 2, "HotHashTag"),
        ("ShardAssignment", 3, "Region"),
        ("ShardAssignment", 4, "AssignedAt"),
        ("ShardAssignment", 5, "Version"),

        ("ShardMapSnapshot", 0, "Version"),
        ("ShardMapSnapshot", 1, "DurableShards"),
        ("ShardMapSnapshot", 2, "Assignments"),
        ("ShardMapSnapshot", 3, "IsFullSnapshot"),

        ("SubscriptionDescriptor", 0, "Id"),
        ("SubscriptionDescriptor", 1, "TenantId"),
        ("SubscriptionDescriptor", 2, "DisplayName"),
        ("SubscriptionDescriptor", 3, "State"),
        ("SubscriptionDescriptor", 4, "ResourceGroups"),
        ("SubscriptionDescriptor", 5, "CreatedAt"),
        ("SubscriptionDescriptor", 6, "Version"),

        ("ResourceGroupDescriptor", 0, "Name"),
        ("ResourceGroupDescriptor", 1, "SubscriptionId"),
        ("ResourceGroupDescriptor", 2, "TenantId"),
        ("ResourceGroupDescriptor", 3, "Region"),
        ("ResourceGroupDescriptor", 4, "State"),
        ("ResourceGroupDescriptor", 5, "CreatedAt"),
        ("ResourceGroupDescriptor", 6, "Version"),

        ("ResourceGroupMember", 0, "ResourceId"),
        ("ResourceGroupMember", 1, "CanonicalPath"),
        ("ResourceGroupMember", 2, "State"),
        ("ResourceGroupMember", 3, "LastFailure"),
        ("ResourceGroupMember", 4, "TeardownAttempts"),

        ("IndexEntry", 0, "State"),
        ("IndexEntry", 1, "BoundTo"),
        ("IndexEntry", 2, "IndexedValue"),
        ("IndexEntry", 3, "LeaseExpiresAt"),
        ("IndexEntry", 4, "ModifiedAt"),

        ("QuotaLease", 0, "LeaseId"),
        ("QuotaLease", 1, "SubscriptionId"),
        ("QuotaLease", 2, "Meter"),
        ("QuotaLease", 3, "Amount"),
        ("QuotaLease", 4, "OperationId"),
        ("QuotaLease", 5, "ReservedAt"),
        ("QuotaLease", 6, "ExpiresAt"),

        ("QuotaUsage", 0, "Meter"),
        ("QuotaUsage", 1, "Committed"),
        ("QuotaUsage", 2, "Reserved"),
        ("QuotaUsage", 3, "Limit")
    ];

    /// <summary>The aliases this assembly publishes. Changing one is a wire break.</summary>
    /// <remarks>
    ///     Grain <i>interfaces</i> are in this list too, and that is the half easiest to forget: an
    ///     interface alias is what a silo of version N looks a grain type up by. Rename
    ///     <c>ITenantGrain</c> without one and every persisted grain id stops resolving.
    /// </remarks>
    static readonly (string Type, string Alias)[] Aliases = [
        // Grain interfaces.
        ("IEmailIndexGrain", "CyberCloud.Tenancy.IEmailIndexGrain"),
        ("IQuotaGrain", "CyberCloud.Tenancy.IQuotaGrain"),
        ("IResourceGroupGrain", "CyberCloud.Tenancy.IResourceGroupGrain"),
        ("IResourceIndexGrain", "CyberCloud.Tenancy.IResourceIndexGrain"),
        ("IShardMapGrain", "CyberCloud.Tenancy.IShardMapGrain"),
        ("ISubscriptionGrain", "CyberCloud.Tenancy.ISubscriptionGrain"),
        ("ITenantDirectoryGrain", "CyberCloud.Tenancy.ITenantDirectoryGrain"),
        ("ITenantGrain", "CyberCloud.Tenancy.ITenantGrain"),

        // Enums.
        ("IndexEntryState", "CyberCloud.Tenancy.IndexEntryState"),
        ("ProvisioningState", "CyberCloud.Tenancy.ProvisioningState"),
        ("QuotaMeter", "CyberCloud.Tenancy.QuotaMeter"),
        ("TenantStatus", "CyberCloud.Tenancy.TenantStatus"),

        // Wire records.
        ("IndexEntry", "CyberCloud.Tenancy.IndexEntry"),
        ("QuotaLease", "CyberCloud.Tenancy.QuotaLease"),
        ("QuotaUsage", "CyberCloud.Tenancy.QuotaUsage"),
        ("ResourceGroupDescriptor", "CyberCloud.Tenancy.ResourceGroupDescriptor"),
        ("ResourceGroupMember", "CyberCloud.Tenancy.ResourceGroupMember"),
        ("ShardAssignment", "CyberCloud.Tenancy.ShardAssignment"),
        ("ShardMapSnapshot", "CyberCloud.Tenancy.ShardMapSnapshot"),
        ("SubscriptionDescriptor", "CyberCloud.Tenancy.SubscriptionDescriptor"),
        ("TenantDescriptor", "CyberCloud.Tenancy.TenantDescriptor"),
        ("TenantDirectoryDelta", "CyberCloud.Tenancy.TenantDirectoryDelta"),
        ("TenantDirectoryEntry", "CyberCloud.Tenancy.TenantDirectoryEntry")
    ];

    static IEnumerable<Type> GeneratedSerializerTypes =>
        Contracts.GetTypes().Where(t => t.GetCustomAttribute<GenerateSerializerAttribute>() is not null);

    /// <summary>
    ///     Types this assembly's <i>source</i> declares an <c>[Alias]</c> on.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Orleans' own code generator emits aliased types into this assembly and they must be
    ///         excluded.
    ///     </b>
    ///     Every grain interface gets a <c>Proxy_CyberCloud_Tenancy_IxxxGrain</c> class
    ///     carrying <c>[Alias("GrainRef")]</c> — the same alias on all of them, deliberately, because
    ///     a proxy is identified by the interface it proxies rather than by itself. Without this
    ///     filter <c>EveryAliasIsUnique</c> reports "GrainRef" as a duplicate and the manifest test
    ///     reports eight types nobody wrote. Filtering on <c>[GeneratedCode]</c> rather than on the
    ///     name prefix is what keeps this true if the generator's naming changes.
    /// </remarks>
    static IEnumerable<Type> AliasedTypes =>
        Contracts.GetTypes()
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is not null)
            .Where(t => t.GetCustomAttribute<GeneratedCodeAttribute>() is null);

    [Fact]
    public void EveryGenerateSerializerTypeHasAnAlias() =>
        GeneratedSerializerTypes
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ShouldBeEmpty(
                "docs/plan/04 § Failure and upgrade makes a rolling upgrade depend on every "
                + "[GenerateSerializer] type having a stable [Alias]."
            );

    [Fact]
    public void EveryGrainInterfaceHasAnAlias() {
        // The half the Core.Contracts version of this test could not have: that assembly has no
        // grain interfaces. A grain interface without an [Alias] is identified on the wire by its
        // full CLR name, so moving ITenantGrain to another namespace would orphan every activation.
        var missing = Contracts.GetTypes()
            .Where(t => t.IsInterface && typeof(IAddressable).IsAssignableFrom(t))
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void EveryEnumHasAnAlias() {
        // Enums carry no [GenerateSerializer] — Orleans has a built-in enum codec — so the test
        // above cannot see them, and an un-aliased enum is exactly as renameable-into-a-break as an
        // un-aliased record.
        var missing = Contracts.GetTypes()
            .Where(t => t.IsEnum && t.IsPublic)
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        missing.ShouldBeEmpty();
    }

    [Fact]
    public void EveryAliasIsUnique() {
        var duplicates = AliasedTypes
            .Select(t => t.GetCustomAttribute<AliasAttribute>()!.Alias)
            .GroupBy(a => a, StringComparer.Ordinal)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToList();

        duplicates.ShouldBeEmpty("two types claiming one alias is a coin flip at deserialization.");
    }

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() {
        var actual = AliasedTypes
            .Select(t => (Type: t.Name, t.GetCustomAttribute<AliasAttribute>()!.Alias))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ToList();

        actual.ShouldBe(
            Aliases.OrderBy(x => x.Type, StringComparer.Ordinal).ToList(),
            "an alias changed, or an aliased type was added without recording it. Both are "
            + "wire-contract changes."
        );
    }

    [Fact]
    public void TheIdManifestMatchesTheBaseline() {
        var actual = GeneratedSerializerTypes
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Select(member => (member, id: member.GetCustomAttribute<IdAttribute>()))
                .Where(x => x.id is not null)
                .Select(x => (Type: type.Name, Id: (int)x.id!.Id, Member: x.member.Name))
            )
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Id)
            .ToList();

        actual.ShouldBe(
            Baseline.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Id).ToList(),
            "docs/plan/05 § Serialization: [Id(n)] numbers are never reused and never reordered. If "
            + "this fails because a member was added, append it to Baseline with the next unused "
            + "number for that type. If it fails for any other reason, the wire contract just "
            + "broke."
        );
    }

    [Fact]
    public void NoTypeReusesAnIdNumber() {
        foreach (var group in Baseline.GroupBy(x => x.Type, StringComparer.Ordinal)) {
            var ids = group.Select(x => x.Id).ToList();
            ids.Distinct().Count().ShouldBe(ids.Count, $"{group.Key} declares the same [Id(n)] twice.");
        }
    }

    [Fact]
    public void TheBaselineNamesEveryPublicMemberOfEveryWireType() {
        var unnumbered = GeneratedSerializerTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<IdAttribute>() is null)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}"))
            )
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unnumbered.ShouldBeEmpty("a public property on a [GenerateSerializer] type with no [Id(n)] is not serialised.");
    }

    [Fact]
    public void EveryWireTypeIsPublic() =>
        GeneratedSerializerTypes
            .Where(t => !t.IsPublic && !t.IsNestedPublic)
            .Select(t => t.Name)
            .ShouldBeEmpty("the gateway, the CLI and the SDK reference this assembly.");

    [Fact]
    public void TheAssemblyHasNoOrleansHostingDependency() {
        // The graph rule that makes this assembly referenceable from the CLI and the SDK: it is
        // Microsoft.Orleans.Sdk only, so referencing it does not acquire a silo.
        var references = Contracts.GetReferencedAssemblies().Select(x => x.Name ?? string.Empty).ToList();

        references.ShouldNotContain("Orleans.Runtime");
        references.ShouldNotContain("Orleans.TestingHost");
        references.ShouldNotContain("Npgsql");
        references.ShouldNotContain("StackExchange.Redis");
    }
}
