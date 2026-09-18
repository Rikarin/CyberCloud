using CyberCloud.Authorization;
using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Resources;
using CyberCloud.Core.Time;
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
using System.Security.Cryptography;
using System.Text;
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
///     <para>
///         Two tenant-registered clients live in the tenant beside the person, created the way a
///         tenant administrator's registration would create them — <c>IApplicationGrain.CreateAsync</c>,
///         which claims the client index: <see cref="TenantPublicClient" />, a public client with
///         PKCE and nothing else, and <see cref="TenantConfidentialClient" />, whose secret lives
///         behind a <c>SecretRef</c> that <see cref="DictionarySecrets" /> — the host's
///         <c>IClientSecretSeam</c> here, registered the way a deployment registers its vault
///         adapter — answers for. Both exist so the consent page, the code store and the secret
///         check can be driven for a client that is not the platform's own.
///     </para>
///     <para>
///         ⚠ The host reads its clock through <see cref="Clock" />, a <see cref="ShiftableClock" />
///         that follows the wall clock plus an offset a test may move forward. It exists for the
///         per-IP rate limit's "recovers" half: the window is minutes long and a test cannot wait
///         it out, so it advances the clock the counters read. Forward only, and shared by every
///         suite in the collection — a test that shifts it leaves it shifted, which nothing here
///         minds because nothing compares the host's clock to the silo's.
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

    /// <summary>A tenant-registered public client — a third party's dashboard, say.</summary>
    public const string TenantPublicClient = "acme-dashboard";

    /// <summary>What the consent page shows for <see cref="TenantPublicClient" />.</summary>
    public const string TenantPublicClientName = "Acme dashboard";

    /// <summary>Where <see cref="TenantPublicClient" /> receives its codes.</summary>
    public const string TenantPublicClientRedirectUri = "https://acme.example/cb";

    /// <summary>A tenant-registered confidential client — a third party's server, say.</summary>
    public const string TenantConfidentialClient = "acme-server";

    /// <summary>Where <see cref="TenantConfidentialClient" /> receives its codes.</summary>
    public const string TenantConfidentialClientRedirectUri = "https://acme.example/server/cb";

    /// <summary>The confidential client's secret, as <see cref="DictionarySecrets" /> holds it.</summary>
    public const string TenantConfidentialClientSecret = "acme-server-secret-7c1e";

    /// <summary>The vault handle the confidential client's registration names.</summary>
    public static SecretRef TenantConfidentialClientSecretRef { get; } = new() { Path = "tenants/grants-tests/apps/acme-server", Field = "client_secret" };

    TestCluster cluster = null!;
    WebApplication host = null!;

    /// <summary>The person.</summary>
    public Guid UserId { get; private set; }

    /// <summary>The host's clock — see the type's remarks.</summary>
    public ShiftableClock Clock { get; } = new();

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
        await CreateTenantClientsAsync();

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
    /// <param name="settings">
    ///     Command-line settings for the new host on top of the fixture's own, as
    ///     <c>--CyberCloud:Identity:Name=value</c> pairs. A test that passes any restarts again
    ///     without them when it is done, because the host is shared by the collection.
    /// </param>
    /// <remarks>
    ///     ⚠ A restart also empties the per-IP buckets: the counters are in process. A test that
    ///     needs an empty bucket advances <see cref="Clock" /> past the window instead, which is
    ///     cheaper and does not disturb the sessions other tests hold.
    /// </remarks>
    public async Task RestartHostAsync(params string[] settings) {
        await host.StopAsync(CancellationToken.None);
        await host.DisposeAsync();

        host = await StartHostAsync(settings);
    }

    async Task<WebApplication> StartHostAsync(params string[] settings) {
        var started = await IdentityComposition.BuildAsync(
            [
                .. settings,
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
            services => {
                services.AddSingleton<IPasswordHasher>(new Argon2idPasswordHasher(CheapArgon2));
                // What a deployment registers here: its vault adapter. This one knows one secret.
                services.AddSingleton<IClientSecretSeam>(new DictionarySecrets(TenantConfidentialClientSecretRef, TenantConfidentialClientSecret));
                services.AddSingleton<IClock>(Clock);
            }
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

    Task<Guid> CreatePersonAsync() => CreatePersonAsync(Email);

    /// <summary>
    ///     Creates another person in the tenant, with <see cref="Password" /> and
    ///     <see cref="DisplayName" />, the way sign-up does — the index claim, the user, the
    ///     confirmation, the credential.
    /// </summary>
    /// <param name="email">A fresh address. Two people cannot share one in a tenant.</param>
    /// <remarks>
    ///     ⚠ For a suite that signs in more than <c>OtpPolicy.MaxIssuesPerWindow</c> times: the grain
    ///     caps delivered codes per person per window, so the fifth sign-in of one person inside
    ///     fifteen minutes gets no fresh code and its second factor fails uniformly. Each sign-in
    ///     that needs a code signs in somebody new.
    /// </remarks>
    public async Task<Guid> CreatePersonAsync(string email) {
        var userId = Guid.NewGuid();
        var index = For(Tenant).GetGrain<IEmailIndexGrain>(GrainKeys.EmailIndex(Tenant, email));

        (await index.TryClaimAsync(email, userId)).IsSuccess.ShouldBeTrue();

        var user = For(Tenant).GetGrain<IUserGrain>(GrainKeys.User(userId));

        (await user.CreateAsync(email, DisplayName, UserStatus.Active)).IsSuccess.ShouldBeTrue();
        (await index.ConfirmAsync(userId)).IsSuccess.ShouldBeTrue();
        (await user.SetPasswordAsync(Password)).IsSuccess.ShouldBeTrue();

        return userId;
    }

    async Task CreateTenantClientsAsync() {
        var created = await For(Tenant)
            .GetGrain<IApplicationGrain>(GrainKeys.Application(Guid.NewGuid()))
            .CreateAsync(
                new ApplicationRegistration {
                    ApplicationId = Guid.NewGuid(),
                    TenantId = Tenant,
                    ClientId = TenantPublicClient,
                    DisplayName = TenantPublicClientName,
                    RedirectUris = [TenantPublicClientRedirectUri],
                    AllowedGrants = [GrantType.AuthorizationCode, GrantType.RefreshToken],
                    AllowedScopes = ["openid", "profile", "offline_access", "cyc.api"],
                    IsPublicClient = true
                }
            );

        created.IsSuccess.ShouldBeTrue(created.Error?.Message);

        var confidential = await For(Tenant)
            .GetGrain<IApplicationGrain>(GrainKeys.Application(Guid.NewGuid()))
            .CreateAsync(
                new ApplicationRegistration {
                    ApplicationId = Guid.NewGuid(),
                    TenantId = Tenant,
                    ClientId = TenantConfidentialClient,
                    DisplayName = "Acme server",
                    RedirectUris = [TenantConfidentialClientRedirectUri],
                    AllowedGrants = [GrantType.AuthorizationCode, GrantType.RefreshToken],
                    AllowedScopes = ["openid", "profile", "offline_access", "cyc.api"],
                    IsPublicClient = false,
                    ClientSecretRef = TenantConfidentialClientSecretRef
                }
            );

        confidential.IsSuccess.ShouldBeTrue(confidential.Error?.Message);
    }

    /// <summary>Cheap Argon2id, for the reason <c>CyberCloud.Identity.Tests</c> gives.</summary>
    static Argon2idOptions CheapArgon2 { get; } = new() { MemoryKibibytes = 8_192, Iterations = 1, Parallelism = 1 };

    /// <summary>The fixture the silo configurator reads its seam from — TestCluster constructs the configurator itself.</summary>
    static IdentityHostFixture? Instance { get; set; }

    sealed class SiloConfigurator : ISiloConfigurator {
        public void Configure(ISiloBuilder silo) {
            silo.AddMemoryGrainStorage(StorageTiers.Durable);
            silo.AddMemoryGrainStorage(StorageTiers.Hot);

            // The code store clears itself through a reminder, and RegisterOrUpdateReminder throws —
            // inside the exchange, as a 500 — on a silo with no reminder service. The production silo
            // has UseRedisReminderService; this is its in-memory stand-in.
            silo.UseInMemoryReminderService();

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

/// <summary>
///     The wall clock plus an offset a test moves forward — what the host reads as <see cref="IClock" />.
/// </summary>
public sealed class ShiftableClock : IClock {
    TimeSpan offset;

    /// <inheritdoc />
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow + offset;

    /// <summary>Moves the host's clock forward by <paramref name="by" />. Forward only.</summary>
    /// <param name="by">How far.</param>
    public void Advance(TimeSpan by) {
        if (by < TimeSpan.Zero) {
            throw new ArgumentOutOfRangeException(nameof(by), "The host's clock moves forward only.");
        }

        offset += by;
    }
}

/// <summary>
///     An <see cref="IClientSecretSeam" /> holding one secret behind one handle — what a deployment's
///     vault adapter answers for a registered confidential client, compared in constant time.
/// </summary>
public sealed class DictionarySecrets(SecretRef known, string secret) : IClientSecretSeam {
    /// <inheritdoc />
    public Task<Result<bool>> VerifyAsync(SecretRef reference, string presented, CancellationToken cancellationToken = default) =>
        Task.FromResult(
            Result<bool>.Success(
                reference == known
                && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(presented ?? string.Empty), Encoding.UTF8.GetBytes(secret))
            )
        );
}

/// <summary>The one collection every suite that needs the host joins.</summary>
[CollectionDefinition(Name)]
public sealed class IdentityHostSuite : ICollectionFixture<IdentityHostFixture> {
    /// <summary>The collection's name.</summary>
    public const string Name = "identity-host";
}
