using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Validation;
using CyberCloud.Providers.ContainerRegistry;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Conformance;
using CyberCloud.ServiceDefaults;
using CyberCloud.Tenancy.Contracts;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;

namespace CyberCloud.Registry.Feeds.Host.Tests;

/// <summary>
///     The real feeds host, started as an Orleans client against an in-process silo that runs the
///     real resource manager, the real ReBAC engine and the ContainerRegistry provider.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The host is composed by <see cref="FeedsComposition.BuildAsync" /> — the identical
///             call <c>Program.cs</c> makes — and started on a real port.
///         </b> Every test in this project sends HTTP to it, which is what makes "the host serves
///         <c>dotnet nuget push</c>" a statement about the host rather than about a handler
///         constructed by hand. What <c>configure</c> substitutes is the deployment's half and
///         nothing more: a token table for the JWKS validator, and the silo's own in-memory object
///         store for the S3 one, so the bytes a push stores are the bytes a teardown lists.
///     </para>
///     <para>
///         ⚠ <b>The silo is a <c>TestCluster</c> the host connects to as any client would</b> — by
///         gateway port, cluster id and service id, all passed as configuration. That is the same
///         path a deployed feeds pod takes to a deployed silo, minus Kubernetes membership, and it
///         is what proves the host's client-side composition can reach <c>IFeedGrain</c> at all.
///     </para>
///     <para>
///         <b>Three subjects, one tenant, two feeds.</b> Alice owns the resource group and creates
///         both feeds through the host's own <see cref="IResourceManager" />; Bob is a reader on the
///         group; Carol holds nothing. A second tenant, Mallory, owns her own group. Their tokens
///         are what the tests carry.
///     </para>
/// </remarks>
public sealed class FeedsHostFixture : IAsyncLifetime {
    /// <summary>The tenant every feed lives in.</summary>
    public static Guid Tenant { get; } = Guid.Parse("aaaaaaaa-0000-4000-8000-000000000001");

    /// <summary>Somebody else's tenant.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("bbbbbbbb-0000-4000-8000-000000000002");

    /// <summary>Alice's subscription.</summary>
    public static Guid Subscription { get; } = Guid.Parse("cccccccc-0000-4000-8000-000000000003");

    /// <summary>Mallory's subscription.</summary>
    public static Guid OtherSubscription { get; } = Guid.Parse("dddddddd-0000-4000-8000-000000000004");

    /// <summary>The resource group in both tenants.</summary>
    public const string Group = "build";

    /// <summary>The NuGet feed's name.</summary>
    public const string NuGetFeed = "packages";

    /// <summary>The npm feed's name.</summary>
    public const string NpmFeed = "modules";

    /// <summary>The Maven feed's name.</summary>
    public const string MavenFeed = "artifacts";

    /// <summary>The cap the host is started with, so an oversized push can be tested with a small file.</summary>
    public const long MaxArtifactBytes = 64 * 1024;

    TestCluster cluster = null!;
    WebApplication host = null!;

    /// <summary>The object store the silo tears down into and the host stores into — one instance.</summary>
    public InMemoryObjectStore Objects { get; } = new();

    /// <summary>The token table the host validates against.</summary>
    public IssuedTokens Tokens { get; } = new();

    /// <summary>Alice's token — owner of the group.</summary>
    public string Alice { get; private set; } = "";

    /// <summary>Bob's token — reader on the group.</summary>
    public string Bob { get; private set; } = "";

    /// <summary>Carol's token — a valid caller in the tenant with no role at all.</summary>
    public string Carol { get; private set; } = "";

    /// <summary>Mallory's token — owner in the other tenant.</summary>
    public string Mallory { get; private set; } = "";

    /// <summary>Where the host listens.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The host's container — the composition under test.</summary>
    public IServiceProvider Services => host.Services;

    /// <summary>The silo's grain factory, for driving operations and reading grains around the host.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>A client with the given token as a Bearer credential, or none.</summary>
    public HttpClient Client(string? token = null) {
        var client = new HttpClient { BaseAddress = BaseAddress };

        if (token is not null) {
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        }

        return client;
    }

    /// <summary>A client with the given token as the password of a Basic credential.</summary>
    public HttpClient BasicClient(string token, string username = "token") {
        var client = new HttpClient { BaseAddress = BaseAddress };
        client.DefaultRequestHeaders.Authorization =
            new("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + token)));
        return client;
    }

    /// <summary>The feed's base URL, relative to the host.</summary>
    public static string Feed(FeedKind kind, string name, Guid? subscription = null) =>
        $"/{ArtifactFeeds.NameOf(kind)}/{(subscription ?? Subscription).ToString("D", CultureInfo.InvariantCulture)}/{Group}/{name}";

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var token = TestContext.Current.CancellationToken;

        // ── The silo ────────────────────────────────────────────────────────────────────────────
        var builder = new TestClusterBuilder(1);
        builder.Options.ClusterId = "cybercloud-feeds-tests";
        builder.Options.ServiceId = "cybercloud-feeds-tests";
        // ⚠ REAL SOCKETS. TestCluster defaults to an in-memory transport that only its own client
        // can reach; the host under test is a separate Orleans client built by CreateClient, and
        // it connects the way a deployed one does — over TCP to the gateway port. Without this line
        // the host retries ConnectionRefused against a port nothing is listening on.
        builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        builder.AddSiloBuilderConfigurator<Configurator>();
        Instance = this;
        cluster = builder.Build();
        await cluster.DeployAsync();

        // ── The host, as a client of it ─────────────────────────────────────────────────────────
        host = await FeedsComposition.BuildAsync(
            [
                "--environment", "Development",
                "--urls", "http://127.0.0.1:0",
                // ⚠ The port the PRIMARY silo's gateway actually bound, not Options.BaseGatewayPort:
                // TestCluster hands each silo a port from the base and the primary's is not the base.
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={cluster.Primary!.GatewayAddress.Endpoint.Port}",
                $"--{CyberCloudClusterOptions.SectionName}:ClusterId={builder.Options.ClusterId}",
                $"--{CyberCloudClusterOptions.SectionName}:ServiceId={builder.Options.ServiceId}",
                $"--{FeedsOptions.SectionName}:MaxArtifactBytes={MaxArtifactBytes}",
                // ⚠ Blanked, because the shipped appsettings.json names the production identity host
                // and this suite registers the validator it can sign for through configure.
                $"--{FeedsIdentityOptions.SectionName}:Issuer="
            ],
            services => {
                services.AddSingleton<IBearerTokenValidator>(Tokens);
                services.AddSingleton<IObjectStore>(Objects);
            }
        );

        await host.Services.GetRequiredService<Volo.Abp.IAbpApplicationWithExternalServiceProvider>()
            .InitializeAsync(host.Services);
        host.MapFeeds();
        await host.StartAsync(token);

        BaseAddress = new(host.Urls.First());

        // ── The tenants, their scopes and their people ──────────────────────────────────────────
        await CreateScopesAsync(Tenant, Subscription);
        await CreateScopesAsync(OtherTenant, OtherSubscription);

        await GrantAsync(Tenant, Subscription, Relations.Owner, "alice");
        await GrantAsync(Tenant, Subscription, Relations.Reader, "bob");
        await GrantAsync(OtherTenant, OtherSubscription, Relations.Owner, "mallory");

        Alice = Tokens.Issue(Tenant, "alice");
        Bob = Tokens.Issue(Tenant, "bob");
        Carol = Tokens.Issue(Tenant, "carol");
        Mallory = Tokens.Issue(OtherTenant, "mallory");

        // ── The feeds, created by Alice through the HOST's resource manager ─────────────────────
        await CreateFeedAsync(Tenant, Subscription, "alice", NuGetFeed, "nuget");
        await CreateFeedAsync(Tenant, Subscription, "alice", NpmFeed, "npm");
        await CreateFeedAsync(Tenant, Subscription, "alice", MavenFeed, "maven");
        await CreateFeedAsync(OtherTenant, OtherSubscription, "mallory", NuGetFeed, "nuget");
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        if (host is not null) {
            await host.StopAsync(CancellationToken.None);
            await host.DisposeAsync();
        }

        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }
    }

    /// <summary>Creates a feed through the host's own manager and drives it to Succeeded.</summary>
    /// <returns>The feed's GUID.</returns>
    public async Task<Guid> CreateFeedAsync(Guid tenant, Guid subscription, string user, string name, string kind) {
        var manager = host.Services.GetRequiredService<IResourceManager>();
        var address = new ResourceId(tenant, subscription, Group, ArtifactFeeds.Type, name, Guid.Empty);

        var accepted = await manager.WriteAsync(
            new() {
                Path = address.Path,
                ApiVersion = ArtifactFeeds.V2026,
                Verb = WriteVerb.Put,
                Body = ArtifactFeeds.Body(kind),
                Caller = new() { TenantId = tenant, SubjectType = "user", SubjectId = user, CorrelationId = "fixture" }
            },
            CancellationToken.None
        );

        accepted.IsSuccess.ShouldBeTrue("the fixture could not create a feed: " + accepted.Error?.Message);

        var operation = For(tenant).GetGrain<IOperationGrain>(
            GrainKeys.Operation(accepted.GetValueOrThrow().OperationId)
        );

        for (var drive = 0; drive < 8; drive++) {
            var status = (await operation.DriveAsync()).GetValueOrThrow();

            if (status.IsTerminal) {
                status.State.ShouldBe(
                    OperationState.Succeeded,
                    $"the feed's create ended {status.State}: {status.Error?.Message}"
                );
                break;
            }
        }

        return accepted.GetValueOrThrow().Resource.Id;
    }

    /// <summary>Deletes a feed through the host's manager and drives the teardown.</summary>
    public async Task DeleteFeedAsync(Guid tenant, Guid subscription, string user, string name) {
        var manager = host.Services.GetRequiredService<IResourceManager>();
        var address = new ResourceId(tenant, subscription, Group, ArtifactFeeds.Type, name, Guid.Empty);

        var accepted = await manager.DeleteAsync(
            new() {
                Path = address.Path,
                ApiVersion = ArtifactFeeds.V2026,
                Verb = WriteVerb.Delete,
                Caller = new() { TenantId = tenant, SubjectType = "user", SubjectId = user, CorrelationId = "fixture" }
            },
            CancellationToken.None
        );

        accepted.IsSuccess.ShouldBeTrue("the fixture could not delete a feed: " + accepted.Error?.Message);

        var operation = For(tenant).GetGrain<IOperationGrain>(
            GrainKeys.Operation(accepted.GetValueOrThrow().OperationId)
        );

        for (var drive = 0; drive < 8; drive++) {
            var status = (await operation.DriveAsync()).GetValueOrThrow();

            if (status.IsTerminal) {
                status.State.ShouldBe(
                    OperationState.Succeeded,
                    $"the feed's delete ended {status.State}: {status.Error?.Message}"
                );
                break;
            }
        }
    }

    TenantGrainFactory For(Guid tenant) => Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    async Task CreateScopesAsync(Guid tenant, Guid subscription) {
        var created = await For(tenant).GetGrain<ISubscriptionGrain>(GrainKeys.Subscription(subscription))
            .CreateAsync("feeds");
        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var group = await For(tenant).GetGrain<IResourceGroupGrain>(GrainKeys.ResourceGroup(subscription, Group))
            .CreateAsync(tenant, "eu-central");
        group.IsSuccess.ShouldBeTrue(group.Error?.Message);
    }

    async Task GrantAsync(Guid tenant, Guid subscription, string relation, string user) {
        var tuple = RelationTuple.Create(
            ObjectRef.Of(
                ObjectTypes.ResourceGroup,
                subscription.ToString("N", CultureInfo.InvariantCulture) + "-" + Group
            ),
            relation,
            SubjectRef.Of(ObjectTypes.User, user)
        );

        tuple.IsSuccess.ShouldBeTrue(tuple.Error?.Message);

        var written = await For(tenant).GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant))
            .WriteAsync(tuple.GetValueOrThrow());
        written.IsSuccess.ShouldBeTrue(written.Error?.Message);
    }

    internal static FeedsHostFixture Instance { get; private set; } = null!;

    /// <summary>The silo, wired as <c>CyberCloud.Silo.Host</c> wires one, plus the doubles the suite owns.</summary>
    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(static services => {
                    services.AddSingleton<IClock, SystemClock>();
                    services.AddSingleton<IResourceProvider, ContainerRegistryProvider>();
                    services.AddSingleton<ArtifactFeedReconciler>();
                    services.AddSingleton<ContainerRegistryReconciler>();
                    services.AddSingleton<ContainerRegistryListCredentialsHandler>();

                    // ⚠ THE SAME STORE THE HOST WRITES INTO. A push stores bytes through the host's
                    // IObjectStore and a teardown lists them through the silo's ReconcileContext
                    // .Objects; two instances would be a teardown that converges over nothing.
                    services.AddSingleton<IObjectStore>(Instance.Objects);

                    var vault = new InMemorySecretVault();
                    services.AddSingleton<ISecretResolver>(vault);
                    services.AddSingleton<ISecretWriter>(vault);

                    services.TryAddSingleton<ILoggerFactory>(static _ => NullLoggerFactory.Instance);
                }
            );

            // The real engine with the real schema, so 404-versus-403 is decided by tuples.
            silo.AddCyberCloudAuthorization();
            silo.AddCyberCloudResourceManager();
        }
    }
}

/// <summary>
///     The validator this suite composes the host with: a table of tokens it issued itself.
/// </summary>
/// <remarks>
///     The lookup is the validation — a value this process did not issue has no claims to read.
///     The production composition registers <c>JwksBearerTokenValidator</c> instead, and
///     <c>CyberCloud.Hosts.Tests</c> asserts that by type name.
/// </remarks>
public sealed class IssuedTokens : IBearerTokenValidator {
    readonly ConcurrentDictionary<string, TokenClaims> issued = new(StringComparer.Ordinal);

    /// <summary>Issues a token for a user in a tenant, valid for an hour.</summary>
    public string Issue(Guid tenant, string user) {
        var token = "cc_" + Guid.NewGuid().ToString("N", CultureInfo.InvariantCulture);
        issued[token] = new(tenant, "user", user, "cyc.api", "", DateTimeOffset.UtcNow.AddHours(1));
        return token;
    }

    /// <inheritdoc />
    public Task<Result<TokenClaims>> ValidateAsync(
        string token,
        Microsoft.AspNetCore.Http.HttpContext http,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            issued.TryGetValue(token, out var claims)
                ? Result<TokenClaims>.Success(claims)
                : Result<TokenClaims>.Failure(
                    BearerTokenErrors.Unauthenticated("the bearer token was not issued by this platform")
                )
        );
}

/// <summary>Every test class shares one running host.</summary>
[CollectionDefinition(Name)]
public sealed class FeedsHostSuite : ICollectionFixture<FeedsHostFixture> {
    /// <summary>The collection's name.</summary>
    public const string Name = "feeds-host";
}

/// <summary>Small helpers every protocol test uses.</summary>
public static class HttpAssertions {
    /// <summary>The body as a string.</summary>
    public static Task<string> BodyAsync(this HttpResponseMessage response) =>
        response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

    /// <summary>The body as bytes.</summary>
    public static Task<byte[]> BytesAsync(this HttpResponseMessage response) =>
        response.Content.ReadAsByteArrayAsync(TestContext.Current.CancellationToken);

    /// <summary>A request with an extra header.</summary>
    public static HttpRequestMessage WithHeader(this HttpRequestMessage request, string name, string value) {
        request.Headers.TryAddWithoutValidation(name, value);
        return request;
    }

    /// <summary>A JSON body.</summary>
    public static StringContent Json(string json) => new(json, new MediaTypeHeaderValue("application/json"));
}
