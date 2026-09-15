using CyberCloud.Authorization;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
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
using Volo.Abp;

namespace CyberCloud.Identity.Host.Tests.Infrastructure;

/// <summary>
///     The real identity host, started as a client of an in-process Orleans cluster with memory
///     storage, with one tenant in the directory and one person in it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The composition under test is <c>IdentityComposition.BuildAsync</c> — the call
///         <c>Program.cs</c> makes — and nothing about the OIDC pipeline is substituted.</b> The
///         same OpenIddict server in the same degraded mode with the same handlers, the same
///         cookie scheme, the same <c>TokenApi</c> over the same grain interfaces; what the cluster
///         behind it stores is in memory rather than in Redis and PostgreSQL, and its password
///         hasher is cheap. Everything else a person's browser would meet, an <see cref="HttpClient" />
///         meets here, which is what makes the handler order, the cookie attributes and the CORS
///         headers assertable at all.
///     </para>
///     <para>
///         ⚠ <b>Real sockets.</b> <c>TestCluster</c> defaults to an in-memory transport only its own
///         client can reach; the host is a separate Orleans client built by
///         <c>OrleansApplication.CreateClient</c>, and it connects the way a deployed one does — over
///         TCP to the primary silo's gateway port, which is not <c>Options.BaseGatewayPort</c> but
///         the port that silo actually bound. <c>CyberCloud.Registry.Feeds.Host.Tests</c>'
///         <c>FeedsHostFixture</c> found both.
///     </para>
///     <para>
///         The tenant is registered in the platform directory the way <c>CreateTenantAsync</c>
///         would register it, because <c>TenantHint</c> resolves every hint through the directory
///         and a tenant that is not in it does not exist over HTTP. The person is created by grain
///         — the email index claim, the user, the password — which is what the sign-up flow does
///         over the same interfaces.
///     </para>
///     <para>
///         The OTP delivery seam on the silo is <see cref="CapturingOtpDelivery" />, so a test can
///         read the code a sign-in's second factor sent — the same seam a deployment wires over
///         Communication and the development run wires to the silo console.
///     </para>
/// </remarks>
public sealed class IdentityHostFixture : IAsyncLifetime {
    /// <summary>The one tenant in the directory.</summary>
    public static Guid Tenant { get; } = Guid.Parse("7a11e0aa-0000-4000-8000-00000000a001");

    /// <summary>A second tenant, also in the directory, with nobody in it.</summary>
    public static Guid OtherTenant { get; } = Guid.Parse("7a11e0aa-0000-4000-8000-00000000a002");

    /// <summary>The tenant's slug — what a person types.</summary>
    public const string Slug = "grants-tests";

    /// <summary>The other tenant's slug.</summary>
    public const string OtherSlug = "grants-tests-other";

    /// <summary>The person's address.</summary>
    public const string Email = "person@grants.example";

    /// <summary>The person's display name, for the id_token.</summary>
    public const string DisplayName = "Grants Person";

    /// <summary>The person's password.</summary>
    public const string Password = "correct-horse-battery-staple-9";

    /// <summary>Where the sign-in page is, as the AppHost configures it.</summary>
    public const string SignInPageBaseUri = "http://localhost:4201";

    /// <summary>The portal's origin — the first-party browser client's, from the development default.</summary>
    public const string PortalOrigin = "http://localhost:4200";

    /// <summary>The portal's redirect URI, from the development default.</summary>
    public const string PortalRedirectUri = "http://localhost:4200/auth/callback";

    TestCluster cluster = null!;
    WebApplication host = null!;

    /// <summary>The person.</summary>
    public Guid UserId { get; private set; }

    /// <summary>Every code the silo delivered, newest last.</summary>
    public CapturingOtpDelivery Otp { get; } = new();

    /// <summary>Where the host listens.</summary>
    public Uri BaseAddress { get; private set; } = null!;

    /// <summary>The key directory the host was started with, and will be restarted with.</summary>
    public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "cyc-identity-host-tests", Guid.NewGuid().ToString("N"));

    /// <summary>The host's container — <c>TokenApi</c>, <c>AuthorizeApi</c>, the handlers.</summary>
    public IServiceProvider Services => host.Services;

    /// <summary>The cluster's own client, for seeding and for asserting on grain state.</summary>
    public IGrainFactory Grains => cluster.GrainFactory;

    /// <summary>A tenant-qualified factory over <see cref="Grains" />.</summary>
    public TenantGrainFactory For(Guid tenant) => Grains.ForTenant(tenant.ToString("D", CultureInfo.InvariantCulture));

    /// <inheritdoc />
    public async ValueTask InitializeAsync() {
        var builder = new TestClusterBuilder(1);
        builder.Options.ClusterId = "cybercloud-identity-host-tests";
        builder.Options.ServiceId = "cybercloud-identity-host-tests";
        builder.Options.ConnectionTransport = ConnectionTransportType.TcpSocket;
        builder.AddSiloBuilderConfigurator<SiloConfigurator>();
        Instance = this;
        cluster = builder.Build();
        await cluster.DeployAsync();

        await RegisterTenantAsync(Tenant, Slug);
        await RegisterTenantAsync(OtherTenant, OtherSlug);
        UserId = await CreatePersonAsync();

        host = await StartHostAsync();
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

        try {
            Directory.Delete(KeyDirectory, recursive: true);
        } catch (IOException) {
            // A key ring file still open on Windows. The directory is under the temp path.
        }
    }

    /// <summary>
    ///     Stops the host and starts a new one on the same key directory — a process restart, as
    ///     far as every issued token and cookie is concerned.
    /// </summary>
    public async Task RestartHostAsync() {
        await host.StopAsync(CancellationToken.None);
        await host.DisposeAsync();

        host = await StartHostAsync();
    }

    async Task<WebApplication> StartHostAsync() {
        var started = await IdentityComposition.BuildAsync(
            [
                "--environment", "Development",
                // ⚠ The same port on a restart. The issuer is inferred from the request (no Issuer is
                // configured here, as on a developer's 127.0.0.1:port), and a refresh token names the
                // issuer that minted it — so a restart onto a fresh random port would refuse every
                // token for the wrong reason. The AppHost pins 5101 for the same reason.
                "--urls", BaseAddress?.GetLeftPart(UriPartial.Authority) ?? "http://127.0.0.1:0",
                $"--{CyberCloudClusterOptions.SectionName}:LocalhostGatewayPort={cluster.Primary!.GatewayAddress.Endpoint.Port}",
                $"--{CyberCloudClusterOptions.SectionName}:ClusterId={cluster.Options.ClusterId}",
                $"--{CyberCloudClusterOptions.SectionName}:ServiceId={cluster.Options.ServiceId}",
                $"--{IdentityHostOptions.SectionName}:DevelopmentKeyDirectory={KeyDirectory}",
                $"--{IdentityHostOptions.SectionName}:SignInPageBaseUri={SignInPageBaseUri}"
            ],
            // ⚠ The host's own hasher only computes the dummy hash for the timing floor; the silo's
            // is what verifies. Both are cheapened for the reason CyberCloud.Identity.Tests cheapens
            // theirs: the cost parameters are not what these suites are about, and Argon2id's defaults
            // would add seconds to every sign-in.
            services => services.AddSingleton<IPasswordHasher>(new Argon2idPasswordHasher(CheapArgon2))
        );

        await started.Services.GetRequiredService<IAbpApplicationWithExternalServiceProvider>().InitializeAsync(started.Services);
        started.MapIdentityHost();
        await started.StartAsync(CancellationToken.None);

        BaseAddress = new(started.Urls.First());

        return started;
    }

    async Task RegisterTenantAsync(Guid tenant, string slug) {
        var registered = await Grains
            .GetGrain<ITenantDirectoryGrain>(GrainKeys.TenantDirectory())
            .RegisterAsync(new() { TenantId = tenant, Slug = slug, HomeRegion = "local", Status = TenantStatus.Active });

        registered.IsSuccess.ShouldBeTrue(registered.Error?.Message);
    }

    async Task<Guid> CreatePersonAsync() {
        var userId = Guid.NewGuid();
        var index = For(Tenant).GetGrain<IEmailIndexGrain>(GrainKeys.EmailIndex(Tenant, Email));

        (await index.TryClaimAsync(Email, userId)).IsSuccess.ShouldBeTrue();

        var user = For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(userId));

        (await user.CreateAsync(Email, DisplayName, UserStatus.Active)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(userId)).IsSuccess.ShouldBeTrue();
        (await user.SetPasswordAsync(Password)).IsSuccess.ShouldBeTrue();

        return userId;
    }

    /// <summary>Cheap Argon2id, for the reason <c>CyberCloud.Identity.Tests</c> gives.</summary>
    static Argon2idOptions CheapArgon2 { get; } = new() { MemoryKibibytes = 8_192, Iterations = 1, Parallelism = 1 };

    /// <summary>The fixture the silo configurator reads its seam from — TestCluster constructs the configurator itself.</summary>
    static IdentityHostFixture? Instance { get; set; }

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            silo.ConfigureServices(services => {
                    // FIRST, so the module's TryAdd keeps them.
                    services.AddSingleton<IOtpDeliverySeam>(Instance!.Otp);
                    services.AddSingleton<IPasswordHasher>(new Argon2idPasswordHasher(CheapArgon2));
                    services.TryAddSingleton<ILoggerFactory>(_ => NullLoggerFactory.Instance);
                }
            );

            silo.AddCyberCloudIdentity(options: CheapArgon2);
            silo.AddCyberCloudAuthorization();
        }
    }
}

/// <summary>
///     An <see cref="IOtpDeliverySeam" /> that keeps every code it is handed, so a test can type it.
/// </summary>
public sealed class CapturingOtpDelivery : IOtpDeliverySeam {
    /// <summary>Every delivery, oldest first.</summary>
    public ConcurrentQueue<OtpDelivery> Deliveries { get; } = new();

    /// <summary>The most recent code, or <see langword="null" />.</summary>
    public string? LastCode => Deliveries.LastOrDefault()?.Code;

    /// <inheritdoc />
    public Task<Result> DeliverAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) {
        Deliveries.Enqueue(delivery);

        return Task.FromResult(Result.Success);
    }
}

/// <summary>The one collection every suite that needs the host joins.</summary>
[CollectionDefinition(Name)]
public sealed class IdentityHostSuite : ICollectionFixture<IdentityHostFixture> {
    /// <summary>The collection's name.</summary>
    public const string Name = "identity-host";
}
