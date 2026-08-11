using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Pipeline.Stages;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Regions;
using CyberCloud.ResourceManager.Contracts.Registry;
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
sealed record GatewayResponse(
    int Status,
    string Body,
    IHeaderDictionary Headers,
    IReadOnlyList<string> Trace
) {
    /// <summary>One header, or empty.</summary>
    public string Header(string name) => Headers.TryGetValue(name, out var value) ? value.ToString() : "";
}

/// <summary>
///     The whole gateway pipeline, in one process, with no server.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This composes the same eight stages <c>GatewayServiceCollectionExtensions</c>
///         registers, through the same <see cref="GatewayPipeline" />, in the same order.</b> The
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

    /// <summary>The operation reader, scripted so an LRO poll needs no cluster.</summary>
    public ScriptedOperationReader Operations { get; } = new();

    /// <summary>The rate-limit counters, in memory and driven by <see cref="Clock" />.</summary>
    public InMemoryRateLimitCounters Counters { get; }

    /// <summary>The concurrency limiter the hubs share.</summary>
    public ProcessConcurrencyLimiter Concurrency { get; } = new(new());

    /// <summary>The region this gateway claims to be in.</summary>
    public GatewayOptions Options { get; }

    /// <summary>Composes the pipeline.</summary>
    /// <param name="region">This pod's region. Empty means "serve everything here".</param>
    /// <param name="tenantARegion">Tenant A's home region in the seeded directory.</param>
    /// <param name="status">Tenant A's lifecycle status.</param>
    public GatewayHarness(
        string region = "",
        string tenantARegion = "",
        TenantStatus status = TenantStatus.Active
    ) {
        Counters = new InMemoryRateLimitCounters(Clock);
        tokens = new(Clock);

        Options = new() {
            Region = region,
            CurrentApiVersion = ApiVersion.Parse(OneTypeRegistry.TheVersion),
            PublicBaseUri = "https://api.cybercloud.io"
        };

        var directory = new TenantDirectoryCache(Grains, NullLogger<TenantDirectoryCache>.Instance);

        // Seeded, not fetched. A resident tenant is a dictionary read that cannot do I/O —
        // docs/plan/05 § The tenant directory — which is why Grains stays untouched.
        directory.Apply(new() {
            Version = 1,
            IsFullSnapshot = true,
            Entries = [
                new() { TenantId = TenantA, Slug = "tenant-a", HomeRegion = tenantARegion, Status = status },
                new() { TenantId = TenantB, Slug = "tenant-b", HomeRegion = tenantARegion, Status = TenantStatus.Active }
            ]
        });

        pipeline = new(
            [
                new CorrelationStage(),
                new AuthenticateStage(tokens),
                new ResolveTenantStage(directory, NullLogger<ResolveTenantStage>.Instance),
                new RegionRoutingStage(Options, new UnconfiguredRegionProxy()),
                new RateLimitStage(new GatewayRateLimiter(Counters)),
                new RouteStage(new OneTypeRegistry(), Options),
                new ValidateStage(Options),
                new DispatchStage(Manager, Operations, Options)
            ],
            NullLogger<GatewayPipeline>.Instance
        );
    }

    /// <summary>Issues a token. The only way a caller gets a tenant.</summary>
    /// <param name="tenantId">The <c>tid</c> claim.</param>
    /// <param name="subjectId">The <c>sub</c> claim.</param>
    public string Token(Guid tenantId, string subjectId = "user-1") =>
        tokens.Issue(new(tenantId, "user", subjectId, "", "", Clock.UtcNow.AddMinutes(10)));

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
            [.. context.Snapshot().Reached.Select(x => x.ToString())]
        );
    }
}
