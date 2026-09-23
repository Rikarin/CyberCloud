using Shouldly;
using System.Globalization;
using System.Reflection;

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
public sealed class AuthorizationWireContractTests {
    static readonly Assembly Contracts = typeof(ObjectRef).Assembly;

    /// <summary>
    ///     ⚠ <b>THE BASELINE. Append-only.</b> <c>[Id(n)]</c> numbers are never reused and never
    ///     reordered; removing a member burns its number.
    /// </summary>
    static readonly (string Type, int Id, string Member)[] Baseline = [
        ("ObjectRef", 0, "Type"),
        ("ObjectRef", 1, "Id"),

        ("SubjectRef", 0, "Type"),
        ("SubjectRef", 1, "Id"),
        ("SubjectRef", 2, "Relation"),

        ("RelationTuple", 0, "Object"),
        ("RelationTuple", 1, "Relation"),
        ("RelationTuple", 2, "Subject"),
        // Time-bounded relations, issue #49 — the expiry rides on the tuple through the store's
        // journal and across the gateway-to-silo boundary on every role assignment PUT.
        ("RelationTuple", 3, "ExpiresOn"),

        ("ConsistencyToken", 0, "TenantId"),
        ("ConsistencyToken", 1, "Version"),

        // ListObjects, issue #37 — the page and the request are wire types because the grain is
        // called across silos; the outcome enum is under EveryEnumHasAnAlias.
        ("ListObjectsPage", 0, "Objects"),
        ("ListObjectsPage", 1, "Continuation"),
        ("ListObjectsPage", 2, "Outcome"),
        ("ListObjectsPage", 3, "CapDetail"),
        ("ListObjectsPage", 4, "Token"),
        ("ListObjectsPage", 5, "PairsReached"),
        ("ListObjectsPage", 6, "ReverseReads"),
        ("ListObjectsPage", 7, "ForwardReads"),
        ("ListObjectsPage", 8, "Verified"),
        ("ListObjectsPage", 9, "IndexReads"),

        ("ListObjectsRequest", 0, "ObjectType"),
        ("ListObjectsRequest", 1, "Permission"),
        ("ListObjectsRequest", 2, "SubjectRelation"),
        ("ListObjectsRequest", 3, "Within"),
        ("ListObjectsRequest", 4, "WithinDepth"),
        ("ListObjectsRequest", 5, "PageSize"),
        ("ListObjectsRequest", 6, "Continuation"),

        // The Leopard index, issue #37 — a slice crosses silos on every read the check and the
        // walk make, and a change on every tuple write.
        ("MembershipIndexSnapshot", 0, "Object"),
        ("MembershipIndexSnapshot", 1, "SchemaVersion"),
        ("MembershipIndexSnapshot", 2, "Members"),
        ("MembershipIndexSnapshot", 3, "Usersets"),
        ("MembershipIndexSnapshot", 4, "Unclosed"),

        ("MembershipIndexChange", 0, "SchemaVersion"),
        ("MembershipIndexChange", 1, "AddMembers"),
        ("MembershipIndexChange", 2, "ReplaceMembers"),
        ("MembershipIndexChange", 3, "AddUsersets"),
        ("MembershipIndexChange", 4, "RemoveUsersets"),
        ("MembershipIndexChange", 5, "Reset"),
        ("MembershipIndexChange", 6, "Unclosed"),

        ("Consistency", 0, "Mode"),
        ("Consistency", 1, "Token"),

        ("CheckResult", 0, "Allowed"),
        ("CheckResult", 1, "Outcome"),
        ("CheckResult", 2, "Token"),
        ("CheckResult", 3, "FromCache"),
        ("CheckResult", 4, "TriplesVisited"),
        ("CheckResult", 5, "MaxDepthReached"),
        ("CheckResult", 6, "CapDetail"),
        ("CheckResult", 7, "ValidUntil"),

        ("RoleAssignment", 0, "Scope"),
        ("RoleAssignment", 1, "RoleName"),
        ("RoleAssignment", 2, "Principal"),
        ("RoleAssignment", 3, "Inherited"),
        ("RoleAssignment", 4, "InheritedFrom"),
        ("RoleAssignment", 5, "ExpiresOn"),

        ("ObjectRelationsSnapshot", 0, "Object"),
        ("ObjectRelationsSnapshot", 1, "ByRelation"),
        ("ObjectRelationsSnapshot", 2, "Count"),
        ("ObjectRelationsSnapshot", 3, "Expiries"),

        ("SubjectIndexEntry", 0, "Object"),
        ("SubjectIndexEntry", 1, "Relation"),
        ("SubjectIndexEntry", 2, "SubjectRelation"),
        ("SubjectIndexEntry", 3, "ExpiresOn"),

        ("ExpirySweepReport", 0, "Removed"),
        ("ExpirySweepReport", 1, "Remaining"),
        ("ExpirySweepReport", 2, "Armed"),
        ("ExpirySweepReport", 3, "Failed"),

        ("SweepReport", 0, "Pending"),
        ("SweepReport", 1, "Repaired"),
        ("SweepReport", 2, "Remaining"),
        ("SweepReport", 3, "Superseded")
    ];

    static readonly (string Type, string Alias)[] Aliases = [
        ("CheckResult", "CyberCloud.Authorization.CheckResult"),
        ("Consistency", "CyberCloud.Authorization.Consistency"),
        ("ExpirySweepReport", "CyberCloud.Authorization.ExpirySweepReport"),
        ("ConsistencyToken", "CyberCloud.Authorization.ConsistencyToken"),
        ("ListObjectsPage", "CyberCloud.Authorization.ListObjectsPage"),
        ("ListObjectsRequest", "CyberCloud.Authorization.ListObjectsRequest"),
        ("MembershipIndexChange", "CyberCloud.Authorization.MembershipIndexChange"),
        ("MembershipIndexSnapshot", "CyberCloud.Authorization.MembershipIndexSnapshot"),
        ("ObjectRef", "CyberCloud.Authorization.ObjectRef"),
        ("ObjectRelationsSnapshot", "CyberCloud.Authorization.ObjectRelationsSnapshot"),
        ("RelationTuple", "CyberCloud.Authorization.RelationTuple"),
        ("RoleAssignment", "CyberCloud.Authorization.RoleAssignment"),
        ("SubjectIndexEntry", "CyberCloud.Authorization.SubjectIndexEntry"),
        ("SubjectRef", "CyberCloud.Authorization.SubjectRef"),
        ("SweepReport", "CyberCloud.Authorization.SweepReport")
    ];

    static IEnumerable<Type> WireTypes =>
        Contracts.GetTypes().Where(static t => t.GetCustomAttribute<GenerateSerializerAttribute>() is not null);

    [Fact]
    public void EveryWireTypeHasAStableAlias() =>
        WireTypes
            .Where(static t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(static t => t.Name)
            .ShouldBeEmpty(
                "docs/plan/05 § Serialization, rule 5: 'Renaming a type without [Alias] is a "
                + "data-loss bug; the analyzer makes it a compile error.' There is no such analyzer "
                + "(see RequiresPermissionAttribute for the same overclaim elsewhere), so this test "
                + "is the gate."
            );

    [Fact]
    public void TheAliasesAreTheOnesRecordedHere() =>
        WireTypes
            .Select(static t => (Type: t.Name, Alias: t.GetCustomAttribute<AliasAttribute>()?.Alias ?? "<none>"))
            .OrderBy(static x => x.Type, StringComparer.Ordinal)
            .ToList()
            .ShouldBe(Aliases.OrderBy(static x => x.Type, StringComparer.Ordinal).ToList());

    [Fact]
    public void TheIdManifestMatchesTheBaseline() {
        var actual = WireTypes
            .SelectMany(static type => type
                    .GetMembers(BindingFlags.Public | BindingFlags.Instance)
                    .Select(static member => (member, id: member.GetCustomAttribute<IdAttribute>()))
                    .Where(static x => x.id is not null)
                    .Select(x => (Type: type.Name, Id: (int)x.id!.Id, Member: x.member.Name))
            )
            .OrderBy(static x => x.Type, StringComparer.Ordinal)
            .ThenBy(static x => x.Id)
            .ToList();

        actual.ShouldBe(
            Baseline.OrderBy(static x => x.Type, StringComparer.Ordinal).ThenBy(static x => x.Id).ToList(),
            "[Id(n)] numbers are never reused and never reordered — docs/plan/05 § Serialization."
        );
    }

    [Fact]
    public void EveryEnumHasAnAlias() {
        var unaliased = Contracts.GetTypes()
            .Where(static t => t.IsEnum && t.IsPublic)
            .Where(static t => t.GetCustomAttribute<AliasAttribute>() is null)
            .Select(static t => t.Name)
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        unaliased.ShouldBeEmpty(
            "an enum needs no [GenerateSerializer] — Orleans has a built-in enum codec — but it "
            + "does need an [Alias], which is the name a peer looks the type up by across a rolling "
            + "upgrade (docs/plan/04 § Failure and upgrade)."
        );
    }

    [Fact]
    public void CheckOutcomeHasAZeroThatIsNotAnAnswer() {
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
    public void MinimizeLatencyIsTheZeroOfConsistencyMode() {
        // docs/plan/07 § Consistency, row 1, marks it "(default)". A default(Consistency) must
        // therefore be the mode the document says is the default, not an accident of ordering.
        ((int)ConsistencyMode.MinimizeLatency).ShouldBe(0);
        new Consistency().Mode.ShouldBe(ConsistencyMode.MinimizeLatency);
        Consistency.MinimizeLatency.Token.ShouldBeNull();
    }

    [Fact]
    public void ATokenRendersAsAnOpaqueButOrderedString() {
        var tenant = Guid.Parse("7f2d4e88-1a3b-4c5d-8e9f-0a1b2c3d4e5f");
        var token = new ConsistencyToken { TenantId = tenant, Version = 42 };

        token.ToString().ShouldBe("7f2d4e881a3b4c5d8e9f0a1b2c3d4e5f.42");
        token.ToString().ShouldContain(tenant.ToString("N", CultureInfo.InvariantCulture));
    }

    [Fact]
    public void EveryPublicMemberOfEveryWireTypeIsNumbered() {
        // A member with no [Id(n)] is silently dropped on the wire. Computed properties are
        // excluded by having no setter at all, which is how IsValid and IsUserset are declared.
        var unnumbered = WireTypes
            .SelectMany(static type => type
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(static p => p.GetCustomAttribute<IdAttribute>() is null)
                    .Where(static p => p.CanWrite)
                    .Select(p => string.Create(CultureInfo.InvariantCulture, $"{type.Name}.{p.Name}"))
            )
            .OrderBy(static x => x, StringComparer.Ordinal)
            .ToList();

        unnumbered.ShouldBeEmpty("a settable member with no [Id(n)] is dropped on the wire.");
    }
}
