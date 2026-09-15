using System.Collections;
using System.Globalization;
using System.Reflection;

namespace CyberCloud.Providers.ContainerRegistry.Tests;

/// <summary>
///     docs/plan/05 § Serialization and schema evolution, applied to the feed catalogue — the wire
///     types in Contracts and the <b>durable</b> <see cref="FeedState" /> in the implementation
///     assembly, which is the first provider state in the tree that outlives a deploy.
/// </summary>
/// <remarks>
///     <para>
///         The same baseline <c>CyberCloud.Tenancy.Tests.TenancyStateContractTests</c> and
///         <c>CyberCloud.Authorization.Tests.AuthorizationStateContractTests</c> keep, for the same
///         reason: the Serializer discipline gate pins a number only once a release tag has recorded
///         it, and until then a renumbered <c>[Id]</c> is a renumbered <c>[Id]</c> that nothing
///         notices — and <c>durable-grains.txt</c> says the catalogue cannot be rebuilt from the
///         store, because a dist-tag, an unlisting and a publish timestamp exist nowhere else. The
///         review of #29 asked where this file was.
///     </para>
///     <para>
///         ⚠ Every persisted collection is <c>{ get; set; }</c>, which is the rule
///         <c>FeedState.Entries</c> already carries in its own remarks, and it is asserted here
///         rather than trusted.
///     </para>
/// </remarks>
public sealed class FeedContractTests {
    static readonly Assembly Contracts = typeof(IFeedGrain).Assembly;
    static readonly Assembly Implementation = typeof(FeedState).Assembly;

    static readonly (string Type, int Id, string Member)[] Baseline = [
        ("FeedClosure", 0, "EntriesDropped"),

        ("FeedDescriptor", 0, "Kind"),
        ("FeedDescriptor", 1, "IsOpen"),
        ("FeedDescriptor", 2, "IsClosed"),
        ("FeedDescriptor", 3, "EntryCount"),
        ("FeedDescriptor", 4, "OpenedAt"),

        ("FeedEntry", 0, "Path"),
        ("FeedEntry", 1, "StoredAt"),
        ("FeedEntry", 2, "Size"),
        ("FeedEntry", 3, "Sha256"),
        ("FeedEntry", 4, "ContentType"),
        ("FeedEntry", 5, "Metadata"),
        ("FeedEntry", 6, "Listed"),
        ("FeedEntry", 7, "PublishedAt"),
        ("FeedEntry", 8, "PublishedBy"),

        ("FeedState", 0, "Kind"),
        ("FeedState", 1, "IsOpen"),
        ("FeedState", 2, "IsClosed"),
        ("FeedState", 3, "OpenedAt"),
        ("FeedState", 4, "Entries")
    ];

    static readonly (string Type, string Alias)[] Aliases = [
        ("FeedClosure", "CyberCloud.ContainerRegistry.FeedClosure"),
        ("FeedDescriptor", "CyberCloud.ContainerRegistry.FeedDescriptor"),
        ("FeedEntry", "CyberCloud.ContainerRegistry.FeedEntry"),
        ("FeedKind", "CyberCloud.ContainerRegistry.FeedKind"),
        ("FeedState", "CyberCloud.ContainerRegistry.FeedState"),
        ("IFeedGrain", "CyberCloud.ContainerRegistry.IFeedGrain")
    ];

    /// <summary>
    ///     Every type the two assemblies serialize, plus the grain interface and the kind enum, which
    ///     carry an alias for the same reason and no <c>[GenerateSerializer]</c>.
    /// </summary>
    static IEnumerable<Type> AliasedTypes =>
        Contracts.GetTypes().Concat(Implementation.GetTypes())
            .Where(t => t.GetCustomAttribute<GenerateSerializerAttribute>() is not null || t == typeof(IFeedGrain) || t == typeof(FeedKind));

    static IEnumerable<Type> SerializedTypes => AliasedTypes.Where(t => !t.IsInterface && !t.IsEnum);

    [Fact]
    public void EverySerializedTypeHasAStableAlias() =>
        AliasedTypes
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .ShouldBeEmpty("docs/plan/05 § Serialization, rule 5. For FeedState that means the row is still in PostgreSQL and nothing can read it.");

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() =>
        AliasedTypes
            .Select(t => (Type: t.Name, Alias: t.GetCustomAttribute<AliasAttribute>()?.Alias ?? "<none>"))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ToList()
            .ShouldBe(Aliases.OrderBy(x => x.Type, StringComparer.Ordinal).ToList());

    [Fact]
    public void TheIdManifestMatchesTheBaseline() {
        var actual = SerializedTypes
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
            "[Id(n)] numbers are never reused and never reordered — and for FeedState the old bytes are still in the database."
        );
    }

    [Fact]
    public void EveryPublicMemberOfEverySerializedTypeIsNumbered() =>
        SerializedTypes
            .SelectMany(type => type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => p.GetCustomAttribute<IdAttribute>() is null)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}"))
            )
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList()
            .ShouldBeEmpty("a member with no [Id(n)] is not serialized at all.");

    [Fact]
    public void EveryPersistedCollectionIsGetAndSet() =>
        SerializedTypes
            .SelectMany(type => type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(p => typeof(IEnumerable).IsAssignableFrom(p.PropertyType) && p.PropertyType != typeof(string))
                    .Where(p => p.SetMethod is null)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}"))
            )
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList()
            .ShouldBeEmpty("System.Text.Json does not populate a get-only collection property on read — FeedState.Entries' own remarks.");

    [Fact]
    public void TheEnumValuesAreTheOnesRecordedHere() =>
        // A wire enum is a number on the wire; renaming is free and renumbering is not.
        Enum.GetValues<FeedKind>()
            .Select(x => (Name: x.ToString(), Value: (int)x))
            .ToList()
            .ShouldBe([("Unknown", 0), ("NuGet", 1), ("Npm", 2), ("Maven", 3)]);
}
