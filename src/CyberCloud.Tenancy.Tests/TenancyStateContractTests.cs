using CyberCloud.Core.Contracts;
using Shouldly;
using System.Globalization;
using System.Reflection;

namespace CyberCloud.Tenancy.Tests;

/// <summary>
///     docs/plan/05 § Serialization and schema evolution, applied to <b>grain state</b> rather than
///     to wire types.
/// </summary>
/// <remarks>
///     <para>
///         The evolution rules are written for "persisted types", and grain state is the most
///         persisted thing there is: a row in PostgreSQL that outlives every deploy and appears in
///         every backup. <c>TenancyWireContractTests</c> guards <c>CyberCloud.Tenancy.Contracts</c>;
///         this guards <c>CyberCloud.Tenancy</c>'s state types, which are never on the wire and are
///         exactly as unforgiving of a rename.
///     </para>
///     <para>
///         ⚠ <b>The <see cref="Baseline" /> list is append-only</b>, for the same reason as the wire
///         one: <c>[Id(n)]</c> numbers are never reused and never reordered.
///     </para>
/// </remarks>
public sealed class TenancyStateContractTests {
    static readonly Assembly Tenancy = typeof(TenantState).Assembly;

    static readonly (string Type, int Id, string Member)[] Baseline = [
        ("TenantState", 0, "Descriptor"),
        ("TenantState", 1, "Subscriptions"),
        ("TenantState", 2, "LastStatusReason"),

        ("SubscriptionState", 0, "Descriptor"),
        ("SubscriptionState", 1, "ResourceGroups"),

        ("ResourceGroupState", 0, "Descriptor"),
        ("ResourceGroupState", 1, "Members"),
        ("ResourceGroupState", 2, "CreatingSince"),
        // Added with the group delete — the clusters a group placed objects on, which is the only
        // record of which namespaces its own delete has to reclaim. It cannot be derived from the
        // members, because by then there are none. IResourceGroupGrain.RecordClusterAsync.
        ("ResourceGroupState", 3, "Clusters"),

        ("IndexState", 0, "Entry"),
        // Added with the per-parent child counter — docs/plan/08 § Deleting a parent resource that
        // has children. Only ResourceIndexGrain populates it; EmailIndexGrain shares the state type
        // and leaves it empty.
        ("IndexState", 1, "Children"),

        ("TenantDirectoryState", 0, "Entries"),
        ("TenantDirectoryState", 1, "BySlug"),
        ("TenantDirectoryState", 2, "Version"),
        ("TenantDirectoryState", 3, "TombstonedSlugs"),

        ("ShardMapState", 0, "Assignments"),
        ("ShardMapState", 1, "Shards"),
        ("ShardMapState", 2, "Version"),

        ("QuotaState", 0, "Committed"),
        ("QuotaState", 1, "Limits"),
        ("QuotaState", 2, "Leases")
    ];

    static readonly (string Type, string Alias)[] Aliases = [
        ("IndexState", "CyberCloud.Tenancy.State.Index"),
        ("QuotaState", "CyberCloud.Tenancy.State.Quota"),
        ("ResourceGroupState", "CyberCloud.Tenancy.State.ResourceGroup"),
        ("ShardMapState", "CyberCloud.Tenancy.State.ShardMap"),
        ("SubscriptionState", "CyberCloud.Tenancy.State.Subscription"),
        ("TenantDirectoryState", "CyberCloud.Tenancy.State.TenantDirectory"),
        ("TenantState", "CyberCloud.Tenancy.State.Tenant")
    ];

    static IEnumerable<Type> StateTypes =>
        Tenancy.GetTypes().Where(t => t.GetCustomAttribute<GenerateSerializerAttribute>() is not null);

    [Fact]
    public void EveryStateTypeHasAStableAlias() =>
        StateTypes
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .ShouldBeEmpty(
                "docs/plan/05 § Serialization, rule 5: 'Renaming a type without [Alias] is a "
                + "data-loss bug.' For state that means the row is still in PostgreSQL and nothing "
                + "can read it."
            );

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() =>
        StateTypes
            .Select(t => (Type: t.Name, Alias: t.GetCustomAttribute<AliasAttribute>()?.Alias ?? "<none>"))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ToList()
            .ShouldBe(Aliases.OrderBy(x => x.Type, StringComparer.Ordinal).ToList());

    [Fact]
    public void TheIdManifestMatchesTheBaseline() {
        var actual = StateTypes
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
            "[Id(n)] numbers are never reused and never reordered — and unlike a wire payload, the "
            + "old bytes are still in the database."
        );
    }

    [Fact]
    public void EveryPublicMemberOfEveryStateTypeIsNumbered() {
        var unnumbered = StateTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<IdAttribute>() is null)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}"))
            )
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unnumbered.ShouldBeEmpty(
            "a property with no [Id(n)] is not persisted at all — it reads back as its default on "
            + "the next activation, silently."
        );
    }

    [Fact]
    public void EveryGrainBindsItsPrimaryStateToTheDurableTier() {
        // docs/plan/05 § Choosing a tier and the enforcement paragraph beneath it: "every grain type
        // in durable-grains.txt must bind its primary state to Durable". That checked-in list does
        // not exist yet (docs/plan/23's architecture gates are unimplemented — build/Build.
        // Architecture.cs says so). Every grain in THIS assembly is an Entity, an Index, a
        // Coordinator or a Platform grain, and docs/plan/04 § Grain taxonomy puts all four in
        // Durable — so the list for this assembly is "all of them", and that is checkable now.
        var grains = Tenancy.GetTypes()
            .Where(t => typeof(Grain).IsAssignableFrom(t) && !t.IsAbstract)
            .ToList();

        grains.Count.ShouldBe(
            8,
            "the tenancy grains: tenant, subscription, resource group, resource index, email "
            + "index, tenant directory, shard map, quota."
        );

        foreach (var grain in grains) {
            var bindings = grain.GetConstructors()
                .SelectMany(c => c.GetParameters())
                .Select(p => p.GetCustomAttribute<PersistentStateAttribute>())
                .Where(a => a is not null)
                .Select(a => a!.StorageName)
                .ToList();

            bindings.ShouldNotBeEmpty($"{grain.Name} persists nothing.");
            bindings.ShouldAllBe(
                x => x == StorageTiers.Durable,
                $"{grain.Name} binds state to a tier other than Durable. docs/plan/04 § Grain "
                + "taxonomy puts every kind in this assembly in the Durable column; a Hot binding "
                + "here needs an argument, and docs/plan/05's checked-in durable-grains.txt is "
                + "where it would go."
            );
        }
    }

    [Fact]
    public void NoStateMemberLooksLikeASecret() =>
        // docs/plan/23 § The architecture gates, the Secrets row: "no [Id] member named
        // *Password/*Secret/*Token/*Key outside CyberCloud.Vault". Grain state is JSON in PostgreSQL
        // and in every backup; a secret there is a secret in every backup forever.
        StateTypes
            .SelectMany(t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Select(p => $"{t.Name}.{p.Name}")
            )
            .Where(name =>
                name.EndsWith("Password", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Secret", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Token", StringComparison.OrdinalIgnoreCase)
                || name.EndsWith("Key", StringComparison.OrdinalIgnoreCase)
            )
            .ShouldBeEmpty();
}
