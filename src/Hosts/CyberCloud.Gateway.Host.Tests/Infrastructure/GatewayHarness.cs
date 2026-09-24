using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Pipeline.Stages;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Regions;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ServiceDefaults.RateLimiting;
using CyberCloud.Tenancy.Directory;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using System.Net;
using System.Text;

namespace CyberCloud.Gateway.Host.Tests.Infrastructure;

/// <summary>What one request produced.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Body">The response body, as text.</param>
/// <param name="Headers">Every response header.</param>
/// <param name="Trace">The stages the pipeline entered, in order.</param>
/// <param name="Caller">
///     The caller stage 3 built — the object <c>GatewayComposition.MapGateway</c> parks for a hub to
///     read, so on a hub route this <i>is</i> what the hub sees. A request refused before stage 3
///     has the empty default: <c>Guid.Empty</c> for the tenant and no subject.
/// </param>
sealed record GatewayResponse(
    int Status,
    string Body,
    IHeaderDictionary Headers,
    IReadOnlyList<string> Trace,
    CallerContext Caller
) {
    /// <summary>One header, or empty.</summary>
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value.ToString() : "";
}

/// <summary>
///     The whole gateway pipeline, in one process, with no server.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             This composes the same eight stages <c>GatewayServiceCollectionExtensions</c>
///             registers, through the same <see cref="GatewayPipeline" />, in the same order.
///         </b> The
///         pipeline validates the set at construction, so a test that composed a different pipeline
///         would fail here rather than quietly assert something about a shape production does not
///         have.
///     </para>
///     <para>
///         ⚠ <b><see cref="Grains" /> is a substitute that must never be called.</b> The tenant
///         directory is seeded through <see cref="TenantDirectoryCache.Apply" />, so a resident
///         tenant is a dictionary hit and reaches no grain — which is what makes
///         "<c>Grains.ReceivedCalls()</c> is empty" a meaningful assertion about a flood, a smuggled
///         tenant, or anything else that must not reach the cluster.
///     </para>
/// </remarks>
sealed class GatewayHarness {
    /// <summary>The tenant every test calls as.</summary>
    public static Guid TenantA { get; } = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");

    /// <summary>The tenant every test tries to reach and must not.</summary>
    public static Guid TenantB { get; } = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");

    /// <summary>The subscription in the happy path.</summary>
    public static Guid Subscription { get; } = Guid.Parse("cccccccc-0000-0000-0000-000000000003");

    readonly GatewayPipeline pipeline;
    readonly IssuedTokenCallerContextResolver tokens;

    /// <summary>The clock the rate limiter and the token expiry read.</summary>
    public FakeClock Clock { get; } = new();

    /// <summary>The recording manager stage 8 dispatches to.</summary>
    public RecordingResourceManager Manager { get; } = new();

    /// <summary>The grain factory that must never be touched.</summary>
    public IGrainFactory Grains { get; } = Substitute.For<IGrainFactory>();

    /// <summary>The recording scope manager stage 8 dispatches a scope route to.</summary>
    public RecordingScopeManager Scopes { get; } = new();

    /// <summary>The recording role assignment manager stage 8 dispatches an assignment route to.</summary>
    public RecordingRoleAssignmentManager Roles { get; } = new();

    /// <summary>
    ///     The recording resource graph query stage 8 dispatches a query route to — unless the
    ///     harness was built over a real one (<see cref="GatewayHarness(IResourceGraphQuery)" />).
    /// </summary>
    public RecordingResourceGraphQuery Graph { get; } = new();

    /// <summary>The recording deployment entry point stage 8 routes a deployment's what-if to.</summary>
    public RecordingDeploymentManager Deployments { get; } = new();

    /// <summary>The recording invitation manager stage 8 dispatches an invitation to (#43).</summary>
    public RecordingInvitationManager Invitations { get; } = new();

    /// <summary>The recording identity administration stage 8 dispatches every other identity address to (#41).</summary>
    public RecordingIdentityAdministration Identity { get; } = new();

    /// <summary>The recording cost query stage 8 dispatches a cost route to.</summary>
    public RecordingCostQuery Costs { get; } = new();

    /// <summary>The recording invoice reader stage 8 dispatches an invoice route to.</summary>
    public RecordingInvoiceReader Invoices { get; } = new();

    /// <summary>The operation reader, scripted so an LRO poll needs no cluster.</summary>
    public ScriptedOperationReader Operations { get; } = new();

    /// <summary>The rate-limit counters, in memory and driven by <see cref="Clock" />.</summary>
    public InMemoryRateLimitCounters Counters { get; }

    /// <summary>The concurrency limiter the hubs share.</summary>
    public ProcessConcurrencyLimiter Concurrency { get; } = new(new());

    /// <summary>The hub tickets stage 8 mints and stage 2 redeems, in memory and driven by <see cref="Clock" />.</summary>
    public InMemoryHubTicketStore Tickets { get; }

    /// <summary>The region this gateway claims to be in.</summary>
    public GatewayOptions Options { get; }

    /// <summary>
    ///     Composes the pipeline over a real <see cref="IResourceGraphQuery" /> — the one
    ///     substitution the end-to-end query test undoes, so a KQL body typed at the gateway reaches
    ///     the real translator, the real access filter and a real ClickHouse.
    /// </summary>
    /// <param name="graph">The real query service.</param>
    public GatewayHarness(IResourceGraphQuery graph) : this(graph, "", "", TenantStatus.Active) { }

    /// <summary>Composes the pipeline.</summary>
    /// <param name="region">This pod's region. Empty means "serve everything here".</param>
    /// <param name="tenantARegion">Tenant A's home region in the seeded directory.</param>
    /// <param name="status">Tenant A's lifecycle status.</param>
    public GatewayHarness(
        string region = "",
        string tenantARegion = "",
        TenantStatus status = TenantStatus.Active
    ) : this(null, region, tenantARegion, status) { }

    GatewayHarness(
        IResourceGraphQuery? graph,
        string region,
        string tenantARegion,
        TenantStatus status
    ) {
        Counters = new(Clock);
        Tickets = new(Clock);
        tokens = new(Clock);

        Options = new() {
            Region = region,
            CurrentApiVersion = ApiVersion.Parse(OneTypeRegistry.TheVersion),
            PublicBaseUri = "https://api.cybercloud.io"
        };

        var directory = new TenantDirectoryCache(Grains, NullLogger<TenantDirectoryCache>.Instance);

        // Seeded, not fetched. A resident tenant is a dictionary read that cannot do I/O —
        // docs/plan/05 § The tenant directory — which is why Grains stays untouched.
        directory.Apply(
            new() {
                Version = 1,
                IsFullSnapshot = true,
                Entries = [
                    new() { TenantId = TenantA, Slug = "tenant-a", HomeRegion = tenantARegion, Status = status },
                    new() {
                        TenantId = TenantB, Slug = "tenant-b", HomeRegion = tenantARegion, Status = TenantStatus.Active
                    }
                ]
            }
        );

        Stages = [
            new CorrelationStage(),
            new AuthenticateStage(tokens, Tickets),
            new ResolveTenantStage(directory, NullLogger<ResolveTenantStage>.Instance),
            new RegionRoutingStage(Options, new UnconfiguredRegionProxy()),
            new RateLimitStage(new GatewayRateLimiter(Counters)),
            new RouteStage(new OneTypeRegistry(), Options),
            new ValidateStage(Options),
            new DispatchStage(Manager, Scopes, Roles, graph ?? Graph, Deployments, Invitations, Identity, Costs, Invoices, Operations, Tickets, Options)
        ];

        pipeline = new(Stages, NullLogger<GatewayPipeline>.Instance);
    }

    /// <summary>
    ///     The eight stage objects, in document order — the same instances <see cref="SendAsync" /> runs,
    ///     so a suite that puts them behind a real listener (<see cref="OverHttpGateway" />) drives the
    ///     same fakes this harness seeded.
    /// </summary>
    public IReadOnlyList<IGatewayStage> Stages { get; }

    /// <summary>Issues a token. The only way a caller gets a tenant.</summary>
    /// <param name="tenantId">The <c>tid</c> claim.</param>
    /// <param name="subjectId">The <c>sub</c> claim.</param>
    /// <param name="subjectType">
    ///     The <c>sub_typ</c> claim — <c>user</c>, <c>servicePrincipal</c> or <c>managedIdentity</c>.
    ///     ⚠ Its own claim rather than a prefix on <paramref name="subjectId" />, because
    ///     docs/plan/07 § The model makes <c>user:abc</c> and <c>servicePrincipal:abc</c> two
    ///     different subjects.
    /// </param>
    /// <param name="impersonatedBy">
    ///     The <c>act_sub</c> claim — the operator behind an impersonated request, or empty.
    ///     ⚠ It is a parameter of <b>issuing</b> a token and there is no other way to set it, which
    ///     is the shape docs/plan/06 § Platform administration needs and the reason no request header
    ///     can supply one.
    /// </param>
    /// <param name="sessionId">The <c>sid</c> claim — the token session, or empty (#41).</param>
    public string Token(
        Guid tenantId,
        string subjectId = "user-1",
        string subjectType = "user",
        string impersonatedBy = "",
        string sessionId = ""
    ) =>
        tokens.Issue(new(tenantId, subjectType, subjectId, "", impersonatedBy, Clock.UtcNow.AddMinutes(10), sessionId));

    /// <summary>The scope path of a tenant's <c>prod</c> group — the parent of <see cref="ResourcePath" />.</summary>
    /// <param name="tenantId">Which tenant's path to spell.</param>
    /// <param name="group">The group name.</param>
    public static string GroupPath(Guid tenantId, string group = "prod") =>
        $"/tenants/{tenantId:D}/subscriptions/{Subscription:D}/resourceGroups/{group}";

    /// <summary>The scope path of a tenant's subscription.</summary>
    /// <param name="tenantId">Which tenant's path to spell.</param>
    public static string SubscriptionPath(Guid tenantId) => $"/tenants/{tenantId:D}/subscriptions/{Subscription:D}";

    /// <summary>The scope path of a tenant's <c>platform</c> management group — issue #39.</summary>
    /// <param name="tenantId">Which tenant's path to spell.</param>
    /// <param name="group">The group name.</param>
    public static string ManagementGroupPath(Guid tenantId, string group = "platform") =>
        $"/tenants/{tenantId:D}/managementGroups/{group}";

    /// <summary>The happy-path resource path for a tenant.</summary>
    /// <param name="tenantId">Which tenant's path to spell.</param>
    /// <param name="name">The resource name.</param>
    public static string ResourcePath(Guid tenantId, string name = "main") =>
        $"/tenants/{tenantId:D}/subscriptions/{Subscription:D}/resourceGroups/prod"
        + $"/providers/CyberCloud.DBforPostgreSQL/servers/{name}";

    /// <summary>Runs one request through all nine stages.</summary>
    /// <param name="method">The HTTP method.</param>
    /// <param name="path">The URL path.</param>
    /// <param name="token">The bearer token, or <see langword="null" /> for no Authorization header.</param>
    /// <param name="query">The query string, without the leading <c>?</c>.</param>
    /// <param name="body">The request body, as text.</param>
    /// <param name="headers">Extra request headers.</param>
    public async Task<GatewayResponse> SendAsync(
        string method,
        string path,
        string? token,
        string query = "api-version=" + OneTypeRegistry.TheVersion,
        string body = "",
        params (string Name, string Value)[] headers
    ) {
        var http = new DefaultHttpContext();
        http.Request.Method = method;
        http.Request.Path = path;
        http.Request.QueryString = query.Length == 0 ? QueryString.Empty : new("?" + query);
        http.Connection.RemoteIpAddress = IPAddress.Loopback;

        if (token is not null) {
            http.Request.Headers.Authorization = "Bearer " + token;
        }

        if (body.Length > 0) {
            var bytes = Encoding.UTF8.GetBytes(body);
            http.Request.Body = new MemoryStream(bytes);
            http.Request.ContentLength = bytes.Length;
            http.Request.ContentType = "application/json";
        }

        foreach (var (name, value) in headers ?? []) {
            http.Request.Headers[name] = value;
        }

        var response = new MemoryStream();
        http.Response.Body = response;

        var context = await pipeline.RunAsync(http);

        return new(
            http.Response.StatusCode,
            Encoding.UTF8.GetString(response.ToArray()),
            http.Response.Headers,
            [.. context.Snapshot().Reached.Select(static x => x.ToString())],
            context.Caller
        );
    }
}
