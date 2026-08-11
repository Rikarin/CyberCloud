using System.Collections;
using System.Globalization;
using System.Reflection;
using Shouldly;

namespace CyberCloud.Authorization.Tests;

/// <summary>
///     docs/plan/05 § Serialization and schema evolution, applied to <b>grain state</b>.
/// </summary>
/// <remarks>
///     The same gate <c>CyberCloud.Tenancy.Tests.TenancyStateContractTests</c> applies, plus one
///     more that assembly learned the hard way and this one inherits: <b>every persisted collection
///     is <c>{ get; set; }</c></b>. <c>System.Text.Json</c> writes a get-only collection property
///     and does not populate it on read, so the row in PostgreSQL is right and the grain comes back
///     empty. For an authorization store that is every grant in a tenant disappearing across a
///     deactivation, which looks exactly like a permissions bug and is not.
/// </remarks>
public sealed class AuthorizationStateContractTests
{
    static readonly Assembly Authorization = typeof(ObjectRelationsState).Assembly;

    static readonly (string Type, int Id, string Member)[] Baseline =
    [
        ("ObjectRelationsState", 0, "ByRelation"),

        ("SubjectRelationsState", 0, "Entries"),

        ("PendingWrite", 0, "Tuple"),
        ("PendingWrite", 1, "IsDelete"),
        ("PendingWrite", 2, "Sequence"),

        ("TupleStoreState", 0, "Version"),
        ("TupleStoreState", 1, "Pending"),
        ("TupleStoreState", 2, "NextSequence"),

        ("CheckCacheEntry", 0, "Allowed"),
        ("CheckCacheEntry", 1, "Version"),
        ("CheckCacheEntry", 2, "SchemaVersion"),

        ("CheckCacheState", 0, "Entries"),
    ];

    static readonly (string Type, string Alias)[] Aliases =
    [
        ("CheckCacheEntry", "CyberCloud.Authorization.State.CheckCacheEntry"),
        ("CheckCacheState", "CyberCloud.Authorization.State.CheckCache"),
        ("ObjectRelationsState", "CyberCloud.Authorization.State.ObjectRelations"),
        ("PendingWrite", "CyberCloud.Authorization.State.PendingWrite"),
        ("SubjectRelationsState", "CyberCloud.Authorization.State.SubjectRelations"),
        ("TupleStoreState", "CyberCloud.Authorization.State.TupleStore"),
    ];

    static IEnumerable<Type> StateTypes =>
        Authorization.GetTypes()
            .Where(t => t.GetCustomAttribute<GenerateSerializerAttribute>() is not null);

    [Fact]
    public void EveryStateTypeHasAStableAlias() =>
        StateTypes
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .ShouldBeEmpty(
                "docs/plan/05 § Serialization, rule 5. For state that means the row is still in "
                + "PostgreSQL and nothing can read it.");

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() =>
        StateTypes
            .Select(t => (Type: t.Name, Alias: t.GetCustomAttribute<AliasAttribute>()?.Alias ?? "<none>"))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ToList()
            .ShouldBe(Aliases.OrderBy(x => x.Type, StringComparer.Ordinal).ToList());

    [Fact]
    public void TheIdManifestMatchesTheBaseline()
    {
        var actual = StateTypes
            .SelectMany(type => type
                .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                .Select(member => (member, id: member.GetCustomAttribute<IdAttribute>()))
                .Where(x => x.id is not null)
                .Select(x => (Type: type.Name, Id: (int)x.id!.Id, Member: x.member.Name)))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ThenBy(x => x.Id)
            .ToList();

        actual.ShouldBe(
            Baseline.OrderBy(x => x.Type, StringComparer.Ordinal).ThenBy(x => x.Id).ToList(),
            "[Id(n)] numbers are never reused and never reordered — and unlike a wire payload, the "
            + "old bytes are still in the database.");
    }

    [Fact]
    public void EveryPersistedCollectionIsGetAndSet()
    {
        // ⚠ THE ONE THAT COST CyberCloud.Tenancy A DATA-LOSS BUG. System.Text.Json writes a
        // get-only collection and does not populate it on read: the payload in PostgreSQL is
        // correct and the grain comes back empty, silently.
        var getOnly = StateTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => typeof(IEnumerable).IsAssignableFrom(p.PropertyType)
                            && p.PropertyType != typeof(string))
                .Where(p => p.SetMethod is null)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}")))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        getOnly.ShouldBeEmpty(
            "System.Text.Json does not populate a get-only collection property on read. Every "
            + "persisted collection must be { get; set; } — docs/plan/05 § Serialization and "
            + "CyberCloud.Tenancy's TenancyState remarks.");
    }

    [Fact]
    public void NoPersistedCollectionCarriesAComparerJsonWillNotReconstruct()
    {
        // The same trap in its second costume: System.Text.Json rebuilds a HashSet or a SortedSet
        // with the DEFAULT comparer, so an ordinal set silently becomes a culture-sensitive one
        // across a restart. Dictionary<string, …> is exempt because its default comparer already
        // IS ordinal.
        var risky = StateTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.PropertyType.IsGenericType)
                .Where(p => p.PropertyType.GetGenericTypeDefinition() == typeof(HashSet<>)
                            || p.PropertyType.GetGenericTypeDefinition() == typeof(SortedSet<>)
                            || p.PropertyType.GetGenericTypeDefinition() == typeof(SortedDictionary<,>))
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}")))
            .ToList();

        risky.ShouldBeEmpty(
            "System.Text.Json reconstructs these with the default comparer; use a List or a "
            + "Dictionary<string, …> — see CyberCloud.Tenancy's TenancyState remarks.");
    }

    [Fact]
    public void EveryPublicMemberOfEveryStateTypeIsNumbered()
    {
        var unnumbered = StateTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<IdAttribute>() is null)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}")))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unnumbered.ShouldBeEmpty("a member with no [Id(n)] is not persisted at all.");
    }
}
