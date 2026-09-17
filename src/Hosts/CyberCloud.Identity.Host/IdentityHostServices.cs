using CyberCloud.Core.Time;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.Host.Api;
using CyberCloud.Identity.Host.Credentials;
using CyberCloud.Identity.Host.RateLimiting;
using CyberCloud.Identity.Host.SignUp;
using CyberCloud.Identity.Host.Tokens;
using CyberCloud.Identity.Seams;
using CyberCloud.Identity.SignIn;
using CyberCloud.ResourceManager;
using CyberCloud.ServiceDefaults.RateLimiting;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace CyberCloud.Identity.Host;

/// <summary>
///     What the interactive sign-in endpoints need in the container.
/// </summary>
/// <remarks>
///     ⚠ <b>These services exist on a silo already, and this is not that registration.</b>
///     <c>IdentitySiloBuilderExtensions.AddCyberCloudIdentity</c> composes them onto an
///     <c>ISiloBuilder</c> so grains can be activated. This host is an Orleans <i>client</i> — it has
///     no silo builder, and the services it needs are a strict subset: the sign-in path, not the
///     grains. Sharing one extension method would mean giving a client an
///     <c>ISiloBuilder</c> it does not have.
/// </remarks>
public static class IdentityHostServices {
    /// <summary>
    ///     Registers the sign-in endpoints' dependencies.
    /// </summary>
    /// <param name="services">The host's services.</param>
    /// <param name="configuration">Where <see cref="IdentityHostOptions" /> is bound from.</param>
    public static IServiceCollection AddIdentityHostApi(
        this IServiceCollection services,
        IConfiguration configuration
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.Configure<IdentityHostOptions>(configuration.GetSection(IdentityHostOptions.SectionName));

        var options = new IdentityHostOptions();
        configuration.GetSection(IdentityHostOptions.SectionName).Bind(options);

        services.TryAddSingleton<IClock, SystemClock>();

        // ⚠ THE PEPPER IS EMPTY HERE AND THAT IS CORRECT, WHICH IS NOT OBVIOUS.
        //
        // The only thing this host's hasher ever computes is IPasswordHasher.DummyHash — the value
        // SignInService verifies against on the no-such-user branch so that a missing account costs
        // exactly what a wrong password costs. Real verification happens in IUserGrain, on a silo,
        // against that silo's hasher and its vault-supplied pepper.
        //
        // So what has to match here is the COST PARAMETERS, not the secret: Argon2idOptions.Default
        // is what makes the dummy verification take the same time as the real one. A pepper would
        // change nothing about the timing and would mean this host needed a vault to start.
        //
        // ⚠ It follows that this registration must never be reused for anything that hashes a
        // password for storage or verifies one for real. Both would silently produce values no silo
        // can verify.
        services.TryAddSingleton<IPasswordHasher>(new Argon2idPasswordHasher(Argon2idOptions.Default));

        // ⚠ In-process, and wrong for production for the reason AddCyberCloudIdentity states: an
        // attacker spread across N replicas gets N times the free attempts. docs/plan/11
        // § Credentials puts the counter in the hot tier as a Redis INCR. RedisLockoutCounter is the
        // production registration and it needs an IDatabase this host does not configure — wiring
        // one is a host change, and TryAdd means it wins without touching this line.
        services.TryAddSingleton<ILockoutCounter, InMemoryLockoutCounter>();

        services.TryAddSingleton<SignInOptions>(_ => SignInOptions.Default);
        services.TryAddSingleton<SignInService>();

        // ⚠ Still UnavailableTotpSecrets — no vault is wired anywhere in this repository, so every
        // TOTP code is refused with a uniform failure and a log line naming the missing
        // registration. See that type. Recovery codes are unaffected: they are hashed in the grain.
        services.TryAddSingleton<ITotpSecretSeam, UnavailableTotpSecrets>();

        // ⚠ The same gap, at the token endpoint: a service principal's secret is a SecretRef into a
        // vault nothing wires, so every client-credentials grant is refused with invalid_client and
        // UnavailableClientSecrets' sentence in the log until a host registers a verifier. TryAdd,
        // so a host — or a test supplying what its deployment would — wins without touching this
        // line.
        services.TryAddSingleton<IClientSecretSeam, UnavailableClientSecrets>();
        services.TryAddSingleton<TokenApi>();

        // ── The interactive grants ─────────────────────────────────────────────────────────────
        //
        // The tenant a request names, resolved through the platform directory; the two static
        // first-party registrations and the resolver that consults them before a tenant's index;
        // the /authorize decision; and the development key file that IdentityHostKeys reads. Each
        // is TryAdd so a test can hand the handlers a double at the seam — a client resolver over a
        // dictionary, say — without a cluster behind it.
        services.TryAddSingleton<TenantHint>();
        services.TryAddSingleton<FirstPartyClients>();
        services.TryAddSingleton<IClientResolver, ClientResolver>();
        services.TryAddSingleton<AuthorizeApi>();
        services.TryAddSingleton<ConsentApi>();
        services.TryAddSingleton<DevelopmentKeyFile>();

        // ── The per-IP buckets — docs/plan/11 § Credentials' "global per-IP limit" ───────────────
        //
        // ⚠ The same pair the gateway registers, on the same rule: Redis when a deployment has put an
        // IConnectionMultiplexer in the container, in process otherwise. Shared through
        // CyberCloud.ServiceDefaults.RateLimiting so the window arithmetic is written once; what is
        // this host's is the buckets (IdentityRateLimits) and the 429 they answer. TryAdd, so a test
        // can hand the limiter counters over a clock it drives.
        if (services.Any(x => x.ServiceType == typeof(IConnectionMultiplexer))) {
            services.TryAddSingleton<IRateLimitCounters, RedisRateLimitCounters>();
        } else {
            services.TryAddSingleton<IRateLimitCounters, InMemoryRateLimitCounters>();
        }

        services.TryAddSingleton<IdentityRateLimiter>();

        // ⚠ PostConfigure, not Configure: AddServiceDefaults' Configure clears both known lists, and
        // entries a delegate put there before it ran would be cleared with them. Nothing here decides
        // whether the middleware runs — TrustedProxies.AreConfigured does, in MapIdentityHost — this
        // only makes the options right for when it does. Read through IOptions rather than from the
        // `options` bound above, so a registration made through IdentityComposition's configure seam
        // is seen. A bad entry throws when the middleware is constructed, which is start-up.
        services.AddOptions<ForwardedHeadersOptions>()
            .PostConfigure<IOptions<IdentityHostOptions>>((forwarded, identity) => TrustedProxies.Apply(forwarded, identity.Value));

        // ⚠ The data-protection key ring follows the signing keys onto disk when a development key
        // directory is configured, and for the same reason: it protects the session cookie and the
        // passkey-challenge cookie, and a ring that dies with the process signs everybody out on
        // restart just as an ephemeral signing key does. DevelopmentKeyFile's constructor is what
        // refuses the directory outside Development, so this configure cannot run there.
        services.AddDataProtection();
        services.AddOptions<KeyManagementOptions>()
            .Configure<DevelopmentKeyFile, ILoggerFactory>((keys, file, loggers) => {
                    if (file.IsConfigured) {
                        keys.XmlRepository = new FileSystemXmlRepository(new DirectoryInfo(file.DataProtectionDirectory), loggers);
                    }
                }
            );

        // ── WebAuthn ───────────────────────────────────────────────────────────────────────────
        //
        // ⚠ The relying-party id and the origin list are the whole of a passkey's phishing
        // resistance, and they are configuration rather than constants because they differ between a
        // developer's localhost and a cluster. Getting either wrong fails inside the authenticator
        // with a SecurityError naming nothing useful — IdentityHostOptions says which values are
        // legal.
        services.AddFido2(fido2 => {
                fido2.ServerDomain = options.RelyingPartyId;
                fido2.ServerName = options.RelyingPartyName;
                fido2.Origins = new HashSet<string>(options.Origins, StringComparer.Ordinal);
            }
        );

        services.TryAddSingleton<IPasskeyService, Fido2PasskeyService>();
        services.TryAddSingleton<PasskeyChallengeCookie>();
        services.TryAddSingleton<SignInApi>();

        // ── Self-serve sign-up — docs/plan/11 § Sign-up and tenant creation ──────────────────────
        //
        // ⚠ THE RESOURCE MANAGER, IN THE IDENTITY HOST, AND AFTER THE IDENTITY REGISTRATIONS. Sign-up
        // ends by creating a tenant, a subscription and a resource group, and the seam that creates
        // them is IScopeManager — docs/plan/06 § The hierarchy — whose registration lives in one
        // place for the reason ResourceManagerSiloBuilderExtensions gives: it names the authorization
        // engine, and a second copy of that list is a second place to forget the IScopeAuthorizer. The
        // feeds host is the precedent for a client host making this one call. It is a TryAdd list, so
        // it comes AFTER the identity registrations above: IClock and IPasswordHasher are ours and
        // must stay ours, and the manager's own IClock default would otherwise win by ordering.
        //
        // What it registers and never resolves here — DriftScanner, ReconcileDriver, the provider
        // registry — is the same arrangement the gateway lives with, and an IProviderRegistry with no
        // provider is never asked for by anything on the scope path.
        services.AddCyberCloudResourceManager();

        services.TryAddSingleton<SignUpTicketCookie>();
        services.TryAddSingleton<SignUpOrchestrator>();
        services.TryAddSingleton<SignUpApi>();

        return services;
    }
}
