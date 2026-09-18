using CyberCloud.ResourceGraph.Query;
using CyberCloud.ResourceGraph.Tests.Infrastructure;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;

namespace CyberCloud.ResourceGraph.Tests.Query;

/// <summary>
///     The query API over the real projection: rows fed by the sink → JetStream → the silo's
///     projector → ClickHouse, then read back through <see cref="ResourceGraphQueryService" /> with
///     the caller's access resolved from the real membership index. docs/plan/08 § The resource-graph
///     projection, the query half of #54.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is where the translator's SQL meets ClickHouse 25.3 for the first time, and
///         every shape the golden files pin is run here at least once.</b> The golden suite proves
///         the translator says what it means; this proves ClickHouse accepts it — the alias that
///         shadows a column (<c>extend name = toupper(name)</c>), the sort key over a map, the
///         <c>Array(String)</c> parameter, a <c>DateTime64</c> parameter, <c>FINAL</c> under
///         <c>readonly=2</c>.
///     </para>
///     <para>
///         The tenant is shared with the projection tests in the same collection, so every query
///         narrows to a prefix minted per test; a stray row from another test is filtered and never
///         counted.
///     </para>
/// </remarks>
[Collection(ProjectionSuite.Name)]
public sealed class ResourceGraphQueryServiceTests(ProjectionFixture fixture) {
    static string N(Guid id) => id.ToString("N", CultureInfo.InvariantCulture);

    ResourceGraphQueryService Service() =>
        new(fixture.ClickHouse, fixture.Reader, new MembershipIndexCallerAccessResolver(fixture.Grains), fixture.Options);

    static CallerContext Caller(string user) => new() { TenantId = ProjectionFixture.Tenant, SubjectType = "user", SubjectId = user };

    /// <summary>
    ///     Three resources under one prefix: one Alice owns through the group, one the engineering
    ///     group reads directly, one nobody was granted. Bob is in engineering; Carol is in nothing.
    /// </summary>
    async Task<(string Prefix, Guid Owned, Guid Shared, Guid Orphan)> SeedAsync() {
        var token = TestContext.Current.CancellationToken;
        var prefix = "q-" + Guid.NewGuid().ToString("N")[..8];
        var group = Guid.NewGuid();
        var owned = Guid.NewGuid();
        var shared = Guid.NewGuid();
        var orphan = Guid.NewGuid();
        var engineering = "eng-" + prefix;

        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resource:{N(owned)}#parent@resourceGroup:{N(group)}");
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resourceGroup:{N(group)}#owner@user:alice-{prefix}");
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"resource:{N(shared)}#reader@group:{engineering}#member");
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"group:{engineering}#member@user:bob-{prefix}");

        var events = new[] {
            ProjectionFixture.Created(owned, prefix + "-owned") with { Location = "eu-central", Tags = Tags(("env", "prod"), ("tier", "db")) },
            ProjectionFixture.Created(shared, prefix + "-shared") with { Location = "eu-west", Tags = Tags(("env", "test")) },
            ProjectionFixture.Created(orphan, prefix + "-orphan") with { Location = "eu-west" }
        };

        foreach (var change in events) {
            (await fixture.Sink.PublishAsync(change, token)).IsSuccess.ShouldBeTrue();
        }

        foreach (var id in new[] { owned, shared, orphan }) {
            await fixture.WaitForVersionAsync(ProjectionFixture.Tenant, id, 1);
        }

        return (prefix, owned, shared, orphan);
    }

    static ImmutableDictionary<string, string> Tags(params (string Key, string Value)[] pairs) =>
        pairs.ToImmutableDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);

    [Fact]
    public async Task ACallerSeesWhatTheyMayReadAndNothingElseAndAUsersetReachesItsMembers() {
        var token = TestContext.Current.CancellationToken;
        var (prefix, owned, shared, orphan) = await SeedAsync();
        var service = Service();
        var query = $"resources | where name startswith '{prefix}-' | project name, resourceId | order by name asc";

        // Alice owns the group and inherits the owned resource; nothing else.
        var alice = await service.QueryAsync(new() { Query = query, Caller = Caller("alice-" + prefix) }, token);
        alice.IsSuccess.ShouldBeTrue(alice.Error?.Message);
        Names(alice.GetValueOrThrow()).ShouldBe([prefix + "-owned"]);
        alice.GetValueOrThrow().Columns.Select(x => x.Name).ShouldBe(["name", "resourceId"]);
        Column(alice.GetValueOrThrow(), 0, "resourceId").ShouldBe(owned.ToString("D"));

        // ⚠ Bob was granted nothing on any resource. He reads the shared one because the row's
        // access column holds `group:eng…#member` and the membership index closes him into it — the
        // two halves docs/plan/08 puts on the row and on the query, meeting for the first time.
        var bob = await service.QueryAsync(new() { Query = query, Caller = Caller("bob-" + prefix) }, token);
        bob.IsSuccess.ShouldBeTrue(bob.Error?.Message);
        Names(bob.GetValueOrThrow()).ShouldBe([prefix + "-shared"]);
        Column(bob.GetValueOrThrow(), 0, "resourceId").ShouldBe(shared.ToString("D"));

        // Carol is in no group and holds no role: an empty page, and not an error.
        var carol = await service.QueryAsync(new() { Query = query, Caller = Caller("carol-" + prefix) }, token);
        carol.IsSuccess.ShouldBeTrue(carol.Error?.Message);
        carol.GetValueOrThrow().Rows.ShouldBeEmpty();
        carol.GetValueOrThrow().HasMore.ShouldBeFalse();

        // And the orphan — a resource nobody was granted — is in nobody's result, count included.
        var count = await service.QueryAsync(new() { Query = $"resources | where name startswith '{prefix}-' | count", Caller = Caller("alice-" + prefix) }, token);
        count.IsSuccess.ShouldBeTrue(count.Error?.Message);
        Column(count.GetValueOrThrow(), 0, "Count").ShouldBe("1");
        _ = orphan;
    }

    [Fact]
    public async Task ThePageIsSlicedByTheContinuationAndTheTokenIsTiedToTheQuery() {
        var token = TestContext.Current.CancellationToken;
        var (prefix, _, _, _) = await SeedAsync();
        var service = Service();

        // Alice reads the owned one; give her the shared one too so there are two rows to page.
        await fixture.GrantAsync(ProjectionFixture.Tenant, $"group:eng-{prefix}#member@user:alice-{prefix}");

        var query = $"resources | where name startswith '{prefix}-' | project name | order by name asc";
        var first = await service.QueryAsync(new() { Query = query, Caller = Caller("alice-" + prefix), Top = 1 }, token);

        first.IsSuccess.ShouldBeTrue(first.Error?.Message);
        Names(first.GetValueOrThrow()).ShouldBe([prefix + "-owned"]);
        first.GetValueOrThrow().HasMore.ShouldBeTrue("two rows and a page of one");

        var second = await service.QueryAsync(
            new() { Query = query, Caller = Caller("alice-" + prefix), Top = 1, Continuation = first.GetValueOrThrow().Continuation },
            token
        );

        second.IsSuccess.ShouldBeTrue(second.Error?.Message);
        Names(second.GetValueOrThrow()).ShouldBe([prefix + "-shared"]);
        second.GetValueOrThrow().HasMore.ShouldBeFalse("the second row was the last");

        // ⚠ The token belongs to the query that handed it out.
        var other = await service.QueryAsync(
            new() { Query = query + " | take 10", Caller = Caller("alice-" + prefix), Top = 1, Continuation = first.GetValueOrThrow().Continuation },
            token
        );

        other.IsFailure.ShouldBeTrue();
        other.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        other.Error.Message.ShouldContain("different query");
    }

    [Fact]
    public async Task EveryShapeTheTranslatorEmitsIsAcceptedByClickHouse() {
        var token = TestContext.Current.CancellationToken;
        var (prefix, owned, _, _) = await SeedAsync();
        var service = Service();
        var caller = Caller("alice-" + prefix);

        // The alias that shadows a column, and the map's sort key — the two shapes that fail without
        // prefer_column_name_to_alias and toJSONString respectively.
        var shadow = await service.QueryAsync(
            new() { Query = $"resources | where name startswith '{prefix}-' | extend name = toupper(name), tolower(provider) | project name, tags, Column1", Caller = caller },
            token
        );
        shadow.IsSuccess.ShouldBeTrue(shadow.Error?.Message);
        Column(shadow.GetValueOrThrow(), 0, "name").ShouldBe((prefix + "-owned").ToUpperInvariant());
        Column(shadow.GetValueOrThrow(), 0, "Column1").ShouldBe("cybercloud.testing");
        JsonDocument.Parse(shadow.GetValueOrThrow().Rows[0]).RootElement.GetProperty("tags").GetProperty("tier").GetString().ShouldBe("db");

        // A summarize with every aggregate, a datetime parameter, `has`, `in`, split and strcat.
        var summary = await service.QueryAsync(
            new() {
                Query = $"resources | where name startswith '{prefix}-' and createdAt >= datetime(2026-09-17T09:00:00Z) and tags.env in ('prod', 'test') and name has 'owned' "
                    + "| summarize n = count(), names = dcount(name), min(version), max(version), sum(version), avg(version) by type, region = tolower(location)",
                Caller = caller
            },
            token
        );
        summary.IsSuccess.ShouldBeTrue(summary.Error?.Message);
        summary.GetValueOrThrow().Rows.Length.ShouldBe(1);
        Column(summary.GetValueOrThrow(), 0, "type").ShouldBe("CyberCloud.Testing/widgets");
        Column(summary.GetValueOrThrow(), 0, "region").ShouldBe("eu-central");
        Column(summary.GetValueOrThrow(), 0, "n").ShouldBe("1");
        Column(summary.GetValueOrThrow(), 0, "avg_version").ShouldBe("1");

        var strings = await service.QueryAsync(
            new() {
                Query = $"resources | where resourceId == '{owned:D}' | project family = split(name, '-', 0), parts = split(name, '-'), label = strcat(name, '#', version, ' ', isnotempty(tags.env)), empty = isempty(tags['absent'])",
                Caller = caller
            },
            token
        );
        strings.IsSuccess.ShouldBeTrue(strings.Error?.Message);
        Column(strings.GetValueOrThrow(), 0, "family").ShouldBe("q");
        Column(strings.GetValueOrThrow(), 0, "label").ShouldBe(prefix + "-owned#1 true");
        Column(strings.GetValueOrThrow(), 0, "empty").ShouldBe("true");

        // A take, then a where, then paging — three SELECTs deep, each re-stating the order.
        var deep = await service.QueryAsync(
            new() { Query = $"resources | where name startswith '{prefix}-' | order by name asc | take 5 | where location == 'eu-central' | distinct name", Caller = caller, Top = 2 },
            token
        );
        deep.IsSuccess.ShouldBeTrue(deep.Error?.Message);
        Names(deep.GetValueOrThrow()).ShouldBe([prefix + "-owned"]);
    }

    [Fact]
    public async Task ARefusedQueryNeverReachesClickHouseAndATenantWithNoTableGetsAnEmptyPage() {
        var token = TestContext.Current.CancellationToken;
        var service = Service();

        var refused = await service.QueryAsync(new() { Query = "resources | mv-expand tags", Caller = Caller("alice") }, token);
        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Code.ShouldBe(ErrorCode.InvalidRequestBody);
        refused.Error.Message.ShouldContain("'mv-expand'");

        // ⚠ A tenant nothing has projected yet has no database. The service ensures it, so the
        // answer is "nothing here" and not ClickHouse's UNKNOWN_DATABASE as a 500.
        var fresh = Guid.NewGuid();
        var empty = await service.QueryAsync(
            new() { Query = "resources | project name", Caller = new() { TenantId = fresh, SubjectType = "user", SubjectId = "nobody" } },
            token
        );
        empty.IsSuccess.ShouldBeTrue(empty.Error?.Message);
        empty.GetValueOrThrow().Rows.ShouldBeEmpty();
    }

    static List<string> Names(ResourceGraphQueryPage page) =>
        page.Rows.Select(row => JsonDocument.Parse(row).RootElement.GetProperty("name").GetString()!).ToList();

    static string Column(ResourceGraphQueryPage page, int row, string column) {
        var value = JsonDocument.Parse(page.Rows[row]).RootElement.GetProperty(column);

        return value.ValueKind switch {
            JsonValueKind.String => value.GetString()!,
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => value.GetRawText()
        };
    }
}
