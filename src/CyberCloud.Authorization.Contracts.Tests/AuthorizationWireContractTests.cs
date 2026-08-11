using System.Globalization;
using System.Reflection;
using CyberCloud.Authorization.Contracts;
using Shouldly;

namespace CyberCloud.Authorization.Contracts.Tests;

/// <summary>
///     docs/plan/05 § Serialization and schema evolution, applied to the authorization wire surface.
/// </summary>
/// <remarks>
///     The same gate <c>TenancyWireContractTests</c> applies to the tenancy contracts. A
///     <c>ConsistencyToken</c> or a <c>CheckResult</c> crosses a version boundary on every request
///     during a rolling upgrade, and an <c>[Id(n)]</c> that moved is an allow that becomes a deny —
///     or worse.
/// </remarks>
public sealed class AuthorizationWireContractTests
{
    static readonly Assembly Contracts = typeof(ObjectRef).Assembly;

    /// <summary>
    ///     ⚠ <b>THE BASELINE. Append-only.</b> <c>[Id(n)]</c> numbers are never reused and never
    ///     reordered; removing a member burns its number.
    /// </summary>
    static readonly (string Type, int Id, string Member)[] Baseline =
    [
        ("ObjectRef", 0, "Type"),
        ("ObjectRef", 1, "Id"),

        ("SubjectRef", 0, "Type"),
        ("SubjectRef", 1, "Id"),
        ("SubjectRef", 2, "Relation"),

        ("RelationTuple", 0, "Object"),
        ("RelationTuple", 1, "Relation"),
        ("RelationTuple", 2, "Subject"),

        ("ConsistencyToken", 0, "TenantId"),
        ("ConsistencyToken", 1, "Version"),

        ("Consistency", 0, "Mode"),
        ("Consistency", 1, "Token"),

        ("CheckResult", 0, "Allowed"),
        ("CheckResult", 1, "Outcome"),
        ("CheckResult", 2, "Token"),
        ("CheckResult", 3, "FromCache"),
        ("CheckResult", 4, "TriplesVisited"),
        ("CheckResult", 5, "MaxDepthReached"),
        ("CheckResult", 6, "CapDetail"),

        ("RoleAssignment", 0, "Scope"),
        ("RoleAssignment", 1, "RoleName"),
        ("RoleAssignment", 2, "Principal"),
        ("RoleAssignment", 3, "Inherited"),
        ("RoleAssignment", 4, "InheritedFrom"),

        ("ObjectRelationsSnapshot", 0, "Object"),
        ("ObjectRelationsSnapshot", 1, "ByRelation"),
        ("ObjectRelationsSnapshot", 2, "Count"),

        ("SubjectIndexEntry", 0, "Object"),
        ("SubjectIndexEntry", 1, "Relation"),
        ("SubjectIndexEntry", 2, "SubjectRelation"),

        ("SweepReport", 0, "Pending"),
        ("SweepReport", 1, "Repaired"),
        ("SweepReport", 2, "Remaining"),
    ];

    static readonly (string Type, string Alias)[] Aliases =
    [
        ("CheckResult", "CyberCloud.Authorization.CheckResult"),
        ("Consistency", "CyberCloud.Authorization.Consistency"),
        ("ConsistencyToken", "CyberCloud.Authorization.ConsistencyToken"),
        ("ObjectRef", "CyberCloud.Authorization.ObjectRef"),
        ("ObjectRelationsSnapshot", "CyberCloud.Authorization.ObjectRelationsSnapshot"),
        ("RelationTuple", "CyberCloud.Authorization.RelationTuple"),
        ("RoleAssignment", "CyberCloud.Authorization.RoleAssignment"),
        ("SubjectIndexEntry", "CyberCloud.Authorization.SubjectIndexEntry"),
        ("SubjectRef", "CyberCloud.Authorization.SubjectRef"),
        ("SweepReport", "CyberCloud.Authorization.SweepReport"),
    ];

    static IEnumerable<Type> WireTypes =>
        Contracts.GetTypes().Where(t => t.GetCustomAttribute<GenerateSerializerAttribute>() is not null);

    [Fact]
    public void EveryWireTypeHasAStableAlias() =>
        WireTypes
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .ShouldBeEmpty(
                "docs/plan/05 § Serialization, rule 5: 'Renaming a type without [Alias] is a "
                + "data-loss bug; the analyzer makes it a compile error.' There is no such analyzer "
                + "(see RequiresPermissionAttribute for the same overclaim elsewhere), so this test "
                + "is the gate.");

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() =>
        WireTypes
            .Select(t => (Type: t.Name, Alias: t.GetCustomAttribute<AliasAttribute>()?.Alias ?? "<none>"))
            .OrderBy(x => x.Type, StringComparer.Ordinal)
            .ToList()
            .ShouldBe(Aliases.OrderBy(x => x.Type, StringComparer.Ordinal).ToList());

    [Fact]
    public void TheIdManifestMatchesTheBaseline()
    {
        var actual = WireTypes
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
            "[Id(n)] numbers are never reused and never reordered — docs/plan/05 § Serialization.");
    }

    [Fact]
    public void EveryEnumHasAnAlias()
    {
        var unaliased = Contracts.GetTypes()
            .Where(t => t.IsEnum && t.IsPublic)
            .Where(t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(t => t.Name)
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unaliased.ShouldBeEmpty(
            "an enum needs no [GenerateSerializer] — Orleans has a built-in enum codec — but it "
            + "does need an [Alias], which is the name a peer looks the type up by across a rolling "
            + "upgrade (docs/plan/04 § Failure and upgrade).");
    }

    [Fact]
    public void CheckOutcomeHasAZeroThatIsNotAnAnswer()
    {
        // The same argument as TenantStatus.Unknown and default(Result): a value type's default
        // must never be mistaken for a decision. `default(CheckResult).Allowed` is false and
        // `Outcome` is Unknown, so an unassigned result denies and says it does not know why.
        ((int)CheckOutcome.Unknown).ShouldBe(0);

        var uninitialized = new CheckResult();
        uninitialized.Allowed.ShouldBeFalse();
        uninitialized.Outcome.ShouldBe(CheckOutcome.Unknown);
        uninitialized.WasTruncated.ShouldBeFalse();
    }

    [Fact]
    public void MinimizeLatencyIsTheZeroOfConsistencyMode()
    {
        // docs/plan/07 § Consistency, row 1, marks it "(default)". A default(Consistency) must
        // therefore be the mode the document says is the default, not an accident of ordering.
        ((int)ConsistencyMode.MinimizeLatency).ShouldBe(0);
        new Consistency().Mode.ShouldBe(ConsistencyMode.MinimizeLatency);
        Consistency.MinimizeLatency.Token.ShouldBeNull();
    }

    [Fact]
    public void ATokenRendersAsAnOpaqueButOrderedString()
    {
        var tenant = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");
        var token = new ConsistencyToken { TenantId = tenant, Version = 42 };

        token.ToString().ShouldBe("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f.42");
        token.ToString().ShouldContain(tenant.ToString("N", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EveryPublicMemberOfEveryWireTypeIsNumbered()
    {
        // A member with no [Id(n)] is silently dropped on the wire. Computed properties are
        // excluded by having no setter at all, which is how IsValid and IsUserset are declared.
        var unnumbered = WireTypes
            .SelectMany(type => type
                .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetCustomAttribute<IdAttribute>() is null)
                .Where(p => p.CanWrite)
                .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}")))
            .OrderBy(x => x, StringComparer.Ordinal)
            .ToList();

        unnumbered.ShouldBeEmpty("a settable member with no [Id(n)] is dropped on the wire.");
    }
}
