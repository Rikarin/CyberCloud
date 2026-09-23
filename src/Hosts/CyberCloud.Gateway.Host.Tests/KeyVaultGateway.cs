using CyberCloud.Authorization;
using CyberCloud.Authorization.Contracts;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Gateway.Host.Authentication;
using CyberCloud.Gateway.Host.Http;
using CyberCloud.Gateway.Host.Hubs;
using CyberCloud.Gateway.Host.Operations;
using CyberCloud.Gateway.Host.Pipeline;
using CyberCloud.Gateway.Host.Pipeline.Stages;
using CyberCloud.Gateway.Host.Principals;
using CyberCloud.Gateway.Host.RateLimiting;
using CyberCloud.Gateway.Host.Regions;
using CyberCloud.Gateway.Host.Tests.Infrastructure;
using CyberCloud.Identity;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Validation;
using CyberCloud.Providers.KeyVault;
using CyberCloud.Providers.KeyVault.Contracts;
using CyberCloud.ResourceManager;
using CyberCloud.ResourceManager.Actions;
using CyberCloud.ResourceManager.Contracts.Registry;
using CyberCloud.ResourceManager.Reconcile;
using CyberCloud.ResourceManager.Registry;
using CyberCloud.ServiceDefaults.RateLimiting;
using CyberCloud.Tenancy.Directory;
using CyberCloud.Vault;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Orleans.Multitenant;
using Orleans.TestingHost;
using System.Globalization;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace CyberCloud.Gateway.Host.Tests;

/// <summary>
///     The key vault's world for <see cref="KeyVaultOverTheGatewayTests" />: a real OpenBao, an
///     in-process silo with the real authorization, identity, resource-manager and key-vault grains,
///     and the gateway's nine stages served by Kestrel on a loopback port over the real
///     <c>ResourceManagerService</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>What is real and what is not, stated so the claim cannot grow.</b> Real: every
///         pipeline stage but authentication's token check; the resource manager, its ReBAC
///         authorizer and <c>CyberCloudSchema</c>; role assignment through
///         <c>RoleAssignmentService</c> and <c>GrainPrincipalDirectory</c> over real identity
///         grains; the reconciler minting each vault's root into a real OpenBao
///         (<c>openbao/openbao:2.4.1</c>) through the platform's own <see cref="OpenBaoSecretWriter" />;
///         the grain resolving it through <see cref="OpenBaoSecretResolver" />. Substituted: the
///         bearer token is <see cref="IssuedTokenCallerContextResolver" />'s rather than the identity
///         host's (<c>TenantOverHttpTests</c> covers that join against the AppHost), OpenBao is
///         reached with its dev root token rather than a Kubernetes login (<c>KubernetesLoginTests</c>
///         covers that), and the tenant directory is seeded rather than refreshed.
///     </para>
///     <para>
///         ⚠ <b>One silo, one process</b>, so the gateway-to-silo process boundary isn't crossed here.
///         <c>CyberCloud.AppHost.Tests</c>' <c>KeyVaultOverTheRealHostsTests</c> crosses it, against
///         the AppHost's silo processes.
///     </para>
///     <para>
///         ⚠ <b>The OpenBao pair is registered by hand in <see cref="Configurator" />.</b> This is not
///         what a real silo does. <c>SiloComposition</c> registers it only when
///         <c>CyberCloud:Vault</c> is configured, and no topology configures that yet. So a green run
///         here says the vault works over a vault, not that a deployment has one. docs/plan/18 § What
///         landed, and what is owed, <c>openbao-on-the-platform-topology</c>.
///     </para>
/// </remarks>
public sealed class KeyVaultGateway : IAsyncLifetime {
    /// <summary>The tenant whose vaults the tests use.</summary>
    public static Guid Tenant { get; } = Guid.Parse("30303030-0000-4000-8000-00000000000a");

    /// <summary>A second tenant, whose owner attacks the first tenant's vaults.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("30303030-0000-4000-8000-00000000000b");

    /// <summary>The subscription both tenants' groups sit in — each in its own tenant.</summary>
    public static Guid Subscription { get; } = Guid.Parse("30303030-0000-4000-8000-0000000000c1");

    /// <summary>The resource group.</summary>
    public const string Group = "secrets";

    /// <summary>The region the group and its vaults are in.</summary>
    public const string Region = "eu-west-1";

    /// <summary>The OpenBao image, the one CyberCloud.Vault.Tests pins.</summary>
    public const string Image = "openbao/openbao:2.4.1";

    const string RootToken = "key-vault-gateway-root";

    static KeyVaultGateway instance = null!;

    readonly IContainer openBao = new ContainerBuilder(Image)
        .WithPortBinding(8200, true)
        .WithEnvironment("BAO_DEV_ROOT_TOKEN_ID", RootToken)
        .WithCommand("server", "-dev", "-dev-listen-address=0.0.0.0:8200")
        .WithWaitStrategy(
            Wait.ForUnixContainer().UntilHttpRequestIsSucceeded(static x => x.ForPort(8200).ForPath("/v1/sys/health"))
        )
        .Build();

    readonly IssuedTokenCallerContextResolver tokens = new(new SystemClock());

    TestCluster cluster = null!;
    WebApplication app = null!;
    VaultOptions vaultOptions = null!;

    /// <summary>The tenant's owner: <c>owner</c> on the tenant, and no data-plane role anywhere.</summary>
    public string Owner { get; } = Guid.Parse("30303030-0000-4000-8000-0000000000d1").ToString("N");

    /// <summary>The other tenant's owner.</summary>
    public string Mallory { get; } = Guid.Parse("30303030-0000-4000-8000-0000000000d2").ToString("N");

    /// <summary>A person the owner grants data-plane roles to, and nothing on the control plane.</summary>
    public string Dana { get; } = Guid.Parse("30303030-0000-4000-8000-0000000000d3").ToString("N");

    /// <summary>A workload's managed identity, granted Key Vault Secrets User on one vault.</summary>
    public string Workload { get; } = Guid.Parse("30303030-0000-4000-8000-0000000000d4").ToString("N");

    /// <summary>The client every test sends through.</summary>
    public HttpClient Http { get; private set; } = null!;

    /// <summary>The cluster's grain factory.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>A raw client for OpenBao, with its root token, for reading what the platform wrote there.</summary>
    public HttpClient OpenBao { get; private set; } = null!;

    /// <summary>The path of a vault in <see cref="Tenant" />.</summary>
    /// <param name="name">The vault's name.</param>
    /// <param name="tenant">The tenant, when it is not <see cref="Tenant" />.</param>
    public static string VaultPath(string name, Guid? tenant = null) =>
        $"/tenants/{tenant ?? Tenant:D}/subscriptions/{Subscription:D}/resourceGroups/{Group}/providers/CyberCloud.KeyVault/vaults/{name}";

    /// <summary>A bearer token for a subject of <see cref="Tenant" />.</summary>
    /// <param name="subject">The subject's id.</param>
    /// <param name="subjectType"><c>user</c> or <c>managedIdentity</c>.</param>
    /// <param name="tenant">The tenant, when it is not <see cref="Tenant" />.</param>
    public string Token(string subject, string subjectType = SubjectTypes.User, Guid? tenant = null) =>
        tokens.Issue(new(tenant ?? Tenant, subjectType, subject, "", "", DateTimeOffset.UtcNow.AddHours(1)));

    /// <summary>Sends a request through the gateway.</summary>
    /// <param name="method">The verb.</param>
    /// <param name="path">The path, without a query.</param>
    /// <param name="token">The bearer token.</param>
    /// <param name="body">The JSON body, or <see langword="null" /> for none.</param>
    public async Task<(int Status, JsonElement Body)> SendAsync(HttpMethod method, string path, string token, object? body = null) {
        using var request = new HttpRequestMessage(method, path + "?api-version=" + KeyVaults.V2026);
        request.Headers.Authorization = new("Bearer", token);

        if (body is not null) {
            request.Content = new StringContent(body as string ?? JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        }

        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);

        using var document = JsonDocument.Parse(text.Length == 0 ? "{}" : text);
        return ((int)response.StatusCode, document.RootElement.Clone());
    }

    /// <summary>Posts a data-plane action on a vault.</summary>
    /// <param name="vault">The vault's name.</param>
    /// <param name="action">The action.</param>
    /// <param name="token">The bearer token.</param>
    /// <param name="body">The request.</param>
    public Task<(int Status, JsonElement Body)> ActionAsync(string vault, string action, string token, object? body = null) =>
        SendAsync(HttpMethod.Post, VaultPath(vault) + "/" + action, token, body ?? new { });

    /// <summary>Grants a role on a scope, over the gateway, as the tenant's owner.</summary>
    /// <param name="scopePath">The scope — a vault's path, or the group's.</param>
    /// <param name="role">The relation.</param>
    /// <param name="principalType">The principal's type.</param>
    /// <param name="principalId">The principal's id.</param>
    public async Task GrantAsync(string scopePath, string role, string principalType, string principalId) {
        var (status, body) = await SendAsync(
            HttpMethod.Put,
            $"{scopePath}/providers/CyberCloud.Authorization/roleAssignments/{role}-{principalType}-{principalId}",
            Token(Owner),
            new { }
        );

        status.ShouldBeOneOf([200, 201], $"granting {role} failed: {body}");
    }

    /// <summary>Creates a vault over the gateway as the owner, and drives its operation to the end.</summary>
    /// <param name="name">The vault's name.</param>
    /// <param name="purgeProtection">Whether purge protection is on.</param>
    public async Task CreateVaultAsync(string name, bool purgeProtection = false) {
        using var request = new HttpRequestMessage(HttpMethod.Put, VaultPath(name) + "?api-version=" + KeyVaults.V2026);
        request.Headers.Authorization = new("Bearer", Token(Owner));
        request.Content = new StringContent(KeyVaults.Body(purgeProtection, location: Region), Encoding.UTF8, "application/json");

        using var response = await Http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        ((int)response.StatusCode).ShouldBe(202, text);

        var operationUri = response.Headers.GetValues(GatewayHeaders.AsyncOperation).Single();
        var operationId = Guid.Parse(Regex.Match(operationUri, "[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}").Value, CultureInfo.InvariantCulture);

        var operation = Grains.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IOperationGrain>(GrainKeys.Operation(operationId));

        for (var i = 0; i < 10; i++) {
            var status = (await operation.DriveAsync()).GetValueOrThrow();

            if (status.IsTerminal) {
                status.State.ShouldBe(OperationState.Succeeded, $"creating vault '{name}' ended {status.State}: {status.Error?.Message}");
                return;
            }
        }

        throw new InvalidOperationException($"Creating vault '{name}' did not finish in ten passes.");
    }

    /// <summary>A vault's resource GUID, from the index the write path bound.</summary>
    /// <param name="name">The vault's name.</param>
    public async Task<Guid> ResourceIdAsync(string name) {
        var address = new ResourceId(Tenant, Subscription, Group, KeyVaults.Type, name, Guid.Empty);
        var bound = await Grains.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture))
            .GetGrain<IResourceIndexGrain>(GrainKeys.PathIndex(address))
            .ResolveAsync();

        return bound.GetValueOrThrow();
    }

    /// <summary>Reads what the platform wrote to OpenBao at a path, with the root token.</summary>
    /// <param name="path">The kv-v2 path, relative to the mount.</param>
    public async Task<JsonElement?> ReadOpenBaoAsync(string path) {
        using var response = await OpenBao.GetAsync($"/v1/secret/data/{path}", TestContext.Current.CancellationToken);

        if (!response.IsSuccessStatusCode) {
            return null;
        }

        var document = await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
        return document.GetProperty("data").GetProperty("data");
    }

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        instance = this;
        var token = TestContext.Current.CancellationToken;

        await openBao.StartAsync(token);

        var address = $"http://{openBao.Hostname}:{openBao.GetMappedPublicPort(8200)}";
        vaultOptions = new() {
            Address = address,
            Role = "unused-with-a-fixed-token",
            KvMountPath = "secret",
            AllowInsecureTransport = true,
            RequestTimeout = TimeSpan.FromSeconds(10)
        };

        OpenBao = new() { BaseAddress = new(address) };
        OpenBao.DefaultRequestHeaders.Add("X-Vault-Token", RootToken);

        var builder = new TestClusterBuilder(1);
        builder.AddSiloBuilderConfigurator<Configurator>();
        cluster = builder.Build();
        await cluster.DeployAsync();

        var grains = cluster.GrainFactory;
        var registry = ProviderRegistry.Build([new KeyVaultProvider()]);
        var resolver = Resolver();

        var handlers = new ServiceCollection();
        handlers.AddSingleton(grains);
        handlers.AddSingleton<KeyVaultActionHandler>();

        var authorizer = new ReBacResourceAuthorizer(grains, NullLogger<ReBacResourceAuthorizer>.Instance);

        var manager = new ResourceManagerService(
            registry,
            authorizer,
            new ReBacResourceRelationWriter(grains, NullLogger<ReBacResourceRelationWriter>.Instance),
            new ResourceScopeLockResolver(grains),
            new NotSupportedPolicyEvaluator(),
            new LoggingResourceChangedSink(NullLogger<LoggingResourceChangedSink>.Instance),
            grains,
            new ActionDispatcher(handlers.BuildServiceProvider(), new NoClusterConnectionFactory(), resolver),
            NullLogger<ResourceManagerService>.Instance,
            new ResourceWatchFanout(registry, grains, authorizer, NullLogger<ResourceWatchFanout>.Instance)
        );

        var scopes = new ScopeManagerService(
            new ReBacScopeAuthorizer(grains, NullLogger<ReBacScopeAuthorizer>.Instance),
            new ReBacScopeRelationWriter(grains, NullLogger<ReBacScopeRelationWriter>.Instance),
            grains,
            new ResourceGroupReclaimer(
                grains,
                new NoClusterConnectionFactory(),
                new ConnectionNamespaceInventory(new NoClusterConnectionFactory()),
                new NamespaceEnsurer(new SystemClock()),
                NullLogger<ResourceGroupReclaimer>.Instance
            ),
            NullLogger<ScopeManagerService>.Instance
        );

        var roles = new RoleAssignmentService(
            new ReBacScopeAuthorizer(grains, NullLogger<ReBacScopeAuthorizer>.Instance),
            authorizer,
            new ReBacRoleAssignmentStore(grains, NullLogger<ReBacRoleAssignmentStore>.Instance),
            new GrainPrincipalDirectory(grains),
            grains,
            NullLogger<RoleAssignmentService>.Instance
        );

        var clock = new SystemClock();
        var options = new GatewayOptions { CurrentApiVersion = ApiVersion.Parse(KeyVaults.V2026), PublicBaseUri = "https://api.cybercloud.io" };
        var tickets = new InMemoryHubTicketStore(clock);
        var directory = new TenantDirectoryCache(grains, NullLogger<TenantDirectoryCache>.Instance);

        directory.Apply(
            new() {
                Version = 1,
                IsFullSnapshot = true,
                Entries = [
                    new() { TenantId = Tenant, Slug = "key-vault-tenant", HomeRegion = "", Status = TenantStatus.Active },
                    new() { TenantId = OtherTenant, Slug = "key-vault-other", HomeRegion = "", Status = TenantStatus.Active }
                ]
            }
        );

        IGatewayStage[] stages = [
            new CorrelationStage(),
            new AuthenticateStage(tokens, tickets),
            new ResolveTenantStage(directory, NullLogger<ResolveTenantStage>.Instance),
            new RegionRoutingStage(options, new UnconfiguredRegionProxy()),
            new RateLimitStage(new GatewayRateLimiter(new InMemoryRateLimitCounters(clock))),
            new RouteStage(registry, options),
            new ValidateStage(options),
            new DispatchStage(manager, scopes, roles, new RecordingResourceGraphQuery(), new TenantScopedOperationReader(manager), tickets, options)
        ];

        var web = WebApplication.CreateSlimBuilder();
        web.WebHost.UseUrls("http://127.0.0.1:0");
        web.Logging.SetMinimumLevel(LogLevel.Warning);

        foreach (var stage in stages) {
            web.Services.AddSingleton(stage);
        }

        web.Services.AddSingleton<GatewayPipeline>();
        web.Services.AddSingleton(grains);
        web.Services.AddSingleton<IConcurrencyLimiter>(new ProcessConcurrencyLimiter(new()));
        web.Services.AddSignalR();

        app = web.Build();
        app.MapGateway();
        await app.StartAsync(token);

        var bound = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses.First();
        Http = new() { BaseAddress = new(bound) };

        await SeedAsync(scopes);
    }

    /// <summary>
    ///     Both tenants, their owners, the principals granted to, and the subscription and group —
    ///     the scopes created over the gateway, through the real scope manager, so the parent edges
    ///     the role inheritance walks are the ones the platform writes.
    /// </summary>
    async Task SeedAsync(ScopeManagerService scopes) {
        foreach (var (tenant, slug, owner) in new[] { (Tenant, "key-vault-tenant", Owner), (OtherTenant, "key-vault-other", Mallory) }) {
            var scoped = Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

            (await scoped.GetGrain<ITenantGrain>(GrainKeys.Tenant(tenant)).CreateAsync(slug, slug, Region)).IsSuccess.ShouldBeTrue();

            var tuple = RelationTuple.Create(
                CyberCloud.Authorization.Contracts.ObjectRef.Of(ObjectTypes.Tenant, tenant.ToString("N", CultureInfo.InvariantCulture)),
                Relations.Owner,
                SubjectRef.Of(ObjectTypes.User, owner)
            );

            (await scoped.GetGrain<ITupleStoreGrain>(GrainKeys.TupleStore(tenant)).WriteAsync(tuple.GetValueOrThrow())).IsSuccess.ShouldBeTrue();

            var ownerToken = Token(owner, tenant: tenant);

            var subscription = await SendAsync(HttpMethod.Put, $"/tenants/{tenant:D}/subscriptions/{Subscription:D}", ownerToken, new { displayName = slug });
            subscription.Status.ShouldBeOneOf([200, 201], subscription.Body.ToString());

            var group = await SendAsync(
                HttpMethod.Put,
                $"/tenants/{tenant:D}/subscriptions/{Subscription:D}/resourceGroups/{Group}",
                ownerToken,
                new { location = Region }
            );
            group.Status.ShouldBeOneOf([200, 201], group.Body.ToString());
        }

        var home = Grains.ForTenant(Tenant.ToString("D", CultureInfo.InvariantCulture));

        (await home.GetGrain<IUserGrain>(GrainKeys.User(Guid.ParseExact(Dana, "N")))
            .CreateAsync("dana@key-vault.test", "Dana", UserStatus.Active)).IsSuccess.ShouldBeTrue();
        (await home.GetGrain<IManagedIdentityGrain>(GrainKeys.ManagedIdentity(Guid.ParseExact(Workload, "N")))
            .CreateAsync("build-workload")).IsSuccess.ShouldBeTrue();
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync() {
        Http?.Dispose();
        OpenBao?.Dispose();

        if (app is not null) {
            await app.StopAsync();
            await app.DisposeAsync();
        }

        if (cluster is not null) {
            await cluster.StopAllSilosAsync();
            await cluster.DisposeAsync();
        }

        await openBao.DisposeAsync();
    }

    OpenBaoSecretResolver Resolver() => new(new() { Timeout = vaultOptions.RequestTimeout }, new RootTokenSource(), vaultOptions);

    OpenBaoSecretWriter Writer() => new(new() { Timeout = vaultOptions.RequestTimeout }, new RootTokenSource(), vaultOptions);

    /// <summary>OpenBao's dev root token, handed out as the platform's.</summary>
    /// <remarks>
    ///     ⚠ Test-only, and the one thing this suite does not prove: how the silo logs in.
    ///     <c>IVaultTokenSource</c>'s remarks say registering an implementation of it in a production
    ///     host is the one way to put a static token into the platform.
    /// </remarks>
    sealed class RootTokenSource : IVaultTokenSource {
        public ValueTask<Result<VaultToken>> GetAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Result<VaultToken>.Success(new(RootToken, DateTimeOffset.MaxValue)));

        public void Invalidate(VaultToken stale) { }
    }

    sealed class Configurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);
            silo.UseInMemoryReminderService();

            silo.ConfigureServices(static services => {
                    services.AddSingleton<IClock>(new SystemClock());
                    services.AddSingleton<IClusterConnectionFactory>(new NoClusterConnectionFactory());
                    services.AddSingleton<IResourceProvider, KeyVaultProvider>();
                    services.AddSingleton<KeyVaultReconciler>();
                    services.AddSingleton<KeyVaultActionHandler>();

                    // ⚠ The platform's own OpenBao client on both sides of the seam: the reconciler
                    // mints each vault's root with the writer, the grain reads it with the resolver.
                    services.AddSingleton<ISecretResolver>(instance.Resolver());
                    services.AddSingleton<ISecretWriter>(instance.Writer());

                    services.TryAddSingleton<ILoggerFactory>(static _ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudAuthorization();
            silo.AddCyberCloudResourceManager();
            silo.AddCyberCloudIdentity();
        }
    }
}
