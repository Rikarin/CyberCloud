using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.ResourceGraph;
using CyberCloud.ResourceGraph.Query;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.Extensions.Logging.Abstractions;
using System.Collections.Immutable;
using System.Text.Json;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     A KQL body typed at the gateway, answered from a real ClickHouse the real projector fed —
///     the query half of #54 end to end. docs/plan/08 § The resource-graph projection.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every piece between the HTTP request and the rows is production code except two
///         resolvers, and the two are the ones that need a cluster.</b> The pipeline is
///         <see cref="GatewayHarness" />'s eight stages; stage 8's <c>IResourceGraphQuery</c> is the
///         real <see cref="ResourceGraphQueryService" /> over the real <see cref="ClickHouseClient" />;
///         the rows were written by the real <see cref="ResourceGraphProjector" />'s
///         <c>ProjectAsync</c> — the same call its consume loop makes — into a
///         <c>clickhouse/clickhouse-server:25.3-alpine</c> container. What is substituted is who may
///         read a resource (<see cref="IResourceAccessResolver" />) and who the caller is
///         (<see cref="ICallerAccessResolver" />), both of which read authorization grains in
///         production and are driven against the real engine in <c>CyberCloud.ResourceGraph.Tests</c>.
///         Here they are a dictionary, so the assertion is about the gateway, the translator, the
///         filter and the store agreeing, not about ReBAC.
///     </para>
///     <para>
///         ⚠ <b>Needs a Docker daemon, and fails without one</b> — the project file says why.
///     </para>
/// </remarks>
public sealed class ResourceGraphQueryEndToEndTests : IAsyncLifetime {
    public const string Image = "clickhouse/clickhouse-server:25.3-alpine";
    const string User = "cybercloud";
    const string Password = "cyber-cloud-test-password";

    static readonly Guid Owned = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    static readonly Guid Shared = Guid.Parse("dddddddd-0000-0000-0000-000000000002");
    static readonly Guid Hidden = Guid.Parse("dddddddd-0000-0000-0000-000000000003");
    static readonly Guid Parked = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    readonly IContainer clickHouse = new ContainerBuilder(Image)
        .WithPortBinding(8123, true)
        .WithEnvironment("CLICKHOUSE_USER", User)
        .WithEnvironment("CLICKHOUSE_PASSWORD", Password)
        .WithEnvironment("CLICKHOUSE_DEFAULT_ACCESS_MANAGEMENT", "1")
        // Not UTC, for the reason ProjectionFixture.ClickHouseTimeZone gives.
        .WithEnvironment("TZ", "Europe/Prague")
        // /ping and not a query: ProjectionFixture's remarks carry the trap.
        .WithWaitStrategy(Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(x => x.ForPort(8123).ForPath("/ping")))
        .Build();

    GatewayHarness gateway = null!;

    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;
        await clickHouse.StartAsync(token);

        var options = new ResourceGraphOptions {
            ClickHouseEndpoint = $"http://{clickHouse.Hostname}:{clickHouse.GetMappedPublicPort(8123)}",
            ClickHouseUser = User,
            ClickHousePassword = Password,
            AllowInsecureTransport = true,
            RequestTimeout = TimeSpan.FromSeconds(30)
        };

        var client = new ClickHouseClient(new HttpClient { Timeout = options.RequestTimeout }, options);
        var store = new ClickHouseResourceGraphStore(client);

        // The grantees each row gets — what ICheckGrain.ListRoleAssignmentsAsync would report.
        var readers = new Dictionary<Guid, ImmutableArray<string>> {
            [Owned] = ["user:alice", "group:ops#member"],
            [Shared] = ["group:eng#member"],
            [Hidden] = ["user:zed"],
            [Parked] = ["user:alice"]
        };

        // ⚠ The projector is constructed and never started: ProjectAsync is the consume loop's one
        // call, made here without the loop, so no NATS is needed and none is named.
        var projector = new ResourceGraphProjector(
            options,
            store,
            new DictionaryAccessResolver(readers),
            new FakeClock(),
            NullLogger<ResourceGraphProjector>.Instance
        );

        foreach (var change in new[] {
                     Created(Owned, "pg-main", "eu-central", ("env", "prod")),
                     Created(Shared, "pg-replica", "eu-west", ("env", "prod")),
                     Created(Hidden, "pg-secret", "eu-west", ("env", "prod")),
                     Created(Parked, "pg-old", "eu-central", ("env", "test")) with { Change = ResourceChangeKind.SoftDeleted }
                 }) {
            var projected = await projector.ProjectAsync(change, token);
            projected.IsSuccess.ShouldBeTrue(projected.Error?.Message);
            projected.GetValueOrThrow().ShouldBe(ProjectionOutcome.Applied);
        }

        // Who the callers are — what the membership index would close them into.
        var callers = new Dictionary<string, ImmutableArray<string>>(StringComparer.Ordinal) {
            ["alice"] = ["user:alice"],
            ["bob"] = ["user:bob", "group:eng#member"],
            ["carol"] = ["user:carol"],
            // Dana is in both groups, so she reads Alice's and Bob's rows: two locations to page.
            ["dana"] = ["user:dana", "group:eng#member", "group:ops#member"]
        };

        gateway = new GatewayHarness(new ResourceGraphQueryService(client, store, new DictionaryCallerResolver(callers), options, NullLogger<ResourceGraphQueryService>.Instance));
    }

    public async ValueTask DisposeAsync() => await clickHouse.DisposeAsync();

    [Fact]
    public async Task AQueryTypedAtTheGatewayIsAnsweredFromClickHouseWithTheCallersAccessApplied() {
        // Alice: the one she owns. The parked one is hers too and is not a resource any more.
        var alice = await QueryAsync("alice", "resources | where type =~ 'cybercloud.testing/widgets' | project name, location, tags | order by name asc");

        alice.Status.ShouldBe(StatusCodes.Status200OK, alice.Body);
        Names(alice.Body).ShouldBe(["pg-main"]);

        using (var document = JsonDocument.Parse(alice.Body)) {
            document.RootElement.GetProperty("columns").EnumerateArray().Select(x => x.GetProperty("name").GetString()).ShouldBe(["name", "location", "tags"]);
            document.RootElement.GetProperty("value")[0].GetProperty("tags").GetProperty("env").GetString().ShouldBe("prod");
            document.RootElement.GetProperty("value")[0].GetProperty("location").GetString().ShouldBe("eu-central");
        }

        // Bob: nothing granted to him by name, everything granted to his group.
        var bob = await QueryAsync("bob", "resources | project name | order by name asc");
        bob.Status.ShouldBe(StatusCodes.Status200OK, bob.Body);
        Names(bob.Body).ShouldBe(["pg-replica"]);

        // Carol: nothing — a 200 with no rows, not a 403 and not a 404.
        var carol = await QueryAsync("carol", "resources | project name");
        carol.Status.ShouldBe(StatusCodes.Status200OK, carol.Body);
        Names(carol.Body).ShouldBeEmpty();

        // ⚠ And the count counts what the caller may read, not what the table holds.
        var count = await QueryAsync("bob", "resources | count");
        count.Status.ShouldBe(StatusCodes.Status200OK, count.Body);
        JsonDocument.Parse(count.Body).RootElement.GetProperty("value")[0].GetProperty("Count").GetInt64().ShouldBe(1);
    }

    [Fact]
    public async Task TheNextLinkPagesTheSameQueryAndARefusedQueryIs400() {
        // Dana is closed into both groups, so she reads pg-main (eu-central) and pg-replica
        // (eu-west): two locations, a page of one, and a second page to follow.
        const string kql = "resources | summarize n = count() by location | order by location asc";
        var summary = await QueryAsync("dana", kql, top: 1);
        summary.Status.ShouldBe(StatusCodes.Status200OK, summary.Body);

        string nextLink;

        using (var first = JsonDocument.Parse(summary.Body)) {
            first.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
            first.RootElement.GetProperty("value")[0].GetProperty("location").GetString().ShouldBe("eu-central");
            first.RootElement.GetProperty("value")[0].GetProperty("n").GetInt64().ShouldBe(1);
            nextLink = first.RootElement.GetProperty("nextLink").GetString()!;
        }

        // ⚠ The link is followed the way a client follows it: the same body POSTed to the link's
        // address, with $top and $skipToken read off the link's query string and not repeated in
        // the body. The first cut of this test asserted the link's ABSENCE for a one-row result and
        // followed nothing (#54 review).
        var link = new Uri(nextLink);
        link.GetLeftPart(UriPartial.Path).ShouldBe("https://api.cybercloud.io" + new ResourceGraphAddress(GatewayHarness.TenantA).Path);
        link.Query.ShouldContain("$skipToken=");
        link.Query.ShouldContain("$top=1");

        var followed = await gateway.SendAsync(
            "POST",
            link.AbsolutePath,
            gateway.Token(GatewayHarness.TenantA, subjectId: "dana"),
            query: link.Query.TrimStart('?'),
            body: JsonSerializer.Serialize(new Dictionary<string, object> { ["query"] = kql })
        );

        followed.Status.ShouldBe(StatusCodes.Status200OK, followed.Body);

        using (var second = JsonDocument.Parse(followed.Body)) {
            second.RootElement.GetProperty("value").GetArrayLength().ShouldBe(1);
            second.RootElement.GetProperty("value")[0].GetProperty("location").GetString().ShouldBe("eu-west");
            second.RootElement.GetProperty("value")[0].GetProperty("n").GetInt64().ShouldBe(1);
            second.RootElement.TryGetProperty("nextLink", out _).ShouldBeFalse("two locations, two pages of one, and the second is the last");
        }

        // The token belongs to the query it was handed out for: the same link with another query
        // is a 400, not a page of the other query at that offset.
        var mismatched = await gateway.SendAsync(
            "POST",
            link.AbsolutePath,
            gateway.Token(GatewayHarness.TenantA, subjectId: "dana"),
            query: link.Query.TrimStart('?'),
            body: JsonSerializer.Serialize(new Dictionary<string, object> { ["query"] = "resources | project name" })
        );
        mismatched.Status.ShouldBe(StatusCodes.Status400BadRequest, mismatched.Body);
        mismatched.Body.ShouldContain("different query");

        var refused = await QueryAsync("alice", "resources | where createdAt > ago(1d)");
        refused.Status.ShouldBe(StatusCodes.Status400BadRequest, refused.Body);
        refused.Body.ShouldContain("ago");
        refused.Body.ShouldContain("InvalidRequestBody");

        var injected = await QueryAsync("alice", "resources | where name == 'x\\' OR 1=1; DROP TABLE tenant_x.resource_graph; --' | project name");
        injected.Status.ShouldBe(StatusCodes.Status200OK, injected.Body);
        Names(injected.Body).ShouldBeEmpty("the payload matched no name and ran nothing");

        // The store still answers after it, which a DROP that ran would have ended.
        Names((await QueryAsync("alice", "resources | project name")).Body).ShouldBe(["pg-main"]);
    }

    Task<GatewayResponse> QueryAsync(string caller, string kql, int? top = null) =>
        gateway.SendAsync(
            "POST",
            new ResourceGraphAddress(GatewayHarness.TenantA).Path,
            gateway.Token(GatewayHarness.TenantA, subjectId: caller),
            body: JsonSerializer.Serialize(top is { } size ? new Dictionary<string, object> { ["query"] = kql, ["$top"] = size } : new Dictionary<string, object> { ["query"] = kql })
        );

    static List<string> Names(string body) {
        using var document = JsonDocument.Parse(body);
        return document.RootElement.GetProperty("value").EnumerateArray().Select(x => x.GetProperty("name").GetString()!).ToList();
    }

    static ResourceChangedEvent Created(Guid resourceId, string name, string location, params (string Key, string Value)[] tags) =>
        new() {
            Change = ResourceChangeKind.Created,
            ResourceId = resourceId,
            TenantId = GatewayHarness.TenantA,
            SubscriptionId = GatewayHarness.Subscription,
            ResourceGroup = "prod",
            Provider = "CyberCloud.Testing",
            Type = "widgets",
            Name = name,
            ApiVersion = OneTypeRegistry.TheVersion,
            ProvisioningState = ProvisioningState.Succeeded,
            Location = location,
            Tags = tags.ToImmutableDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal),
            CreatedAt = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero),
            ModifiedAt = new DateTimeOffset(2026, 9, 17, 10, 0, 0, TimeSpan.Zero),
            DesiredHash = "sha256:0",
            Version = 1
        };

    sealed class DictionaryAccessResolver(IReadOnlyDictionary<Guid, ImmutableArray<string>> readers) : IResourceAccessResolver {
        public Task<Result<ImmutableArray<string>>> ReadersOfAsync(Guid tenantId, Guid resourceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(Result<ImmutableArray<string>>.Success(readers.TryGetValue(resourceId, out var found) ? found : []));
    }

    sealed class DictionaryCallerResolver(IReadOnlyDictionary<string, ImmutableArray<string>> callers) : ICallerAccessResolver {
        public Task<Result<ImmutableArray<string>>> SubjectsOfAsync(CallerContext caller, CancellationToken cancellationToken = default) =>
            Task.FromResult(
                Result<ImmutableArray<string>>.Success(callers.TryGetValue(caller.SubjectId, out var found) ? found : ["user:" + caller.SubjectId])
            );
    }
}
