using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.ManagedIdentity;
using CyberCloud.Identity.Seams;
using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Identity;

/// <summary>
///     Registers the identity module's grains and services on a silo. docs/plan/04 § Silo
///     composition.
/// </summary>
public static class IdentitySiloBuilderExtensions {
    /// <summary>
    ///     Adds the identity services a silo needs to activate the grains in this assembly.
    /// </summary>
    /// <param name="builder">The silo being composed.</param>
    /// <param name="pepper">
    ///     The Argon2id secret input, resolved from the vault at start-up. ⚠ Pass empty only in
    ///     development: without it, a stolen durable-tier backup is enough to start a dictionary
    ///     attack, and with it the attacker also needs the vault.
    /// </param>
    /// <param name="options">Cost parameters. <see cref="Argon2idOptions.Default" /> in production.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The grains are discovered, not registered.</b> Orleans finds them by scanning the
    ///         referenced assemblies, so what this adds is only the services their constructors ask
    ///         for. A silo that references this assembly and forgets this call fails at the first
    ///         activation with a DI resolution error, not at start-up — which is why the identity
    ///         host calls it unconditionally.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><see cref="ILockoutCounter" /> is registered as
    ///         <see cref="InMemoryLockoutCounter" /> here and that is wrong for production.</b> A
    ///         per-process counter gives an attacker spread across N silos N times the free attempts.
    ///         The production registration is <see cref="RedisLockoutCounter" /> over the hot tier's
    ///         multiplexer, which the host wires because the host is what has the connection. This
    ///         default exists so a silo that has not wired one is still functional rather than
    ///         failing to activate, and <c>TryAddSingleton</c> means the host's registration wins.
    ///     </para>
    /// </remarks>
    public static ISiloBuilder AddCyberCloudIdentity(
        this ISiloBuilder builder,
        ReadOnlySpan<byte> pepper = default,
        Argon2idOptions? options = null
    ) {
        ArgumentNullException.ThrowIfNull(builder);

        var hasher = new Argon2idPasswordHasher(options, pepper);

        builder.Services.TryAddSingleton<IClock, SystemClock>();
        builder.Services.TryAddSingleton<IPasswordHasher>(hasher);
        builder.Services.TryAddSingleton<ILockoutCounter, InMemoryLockoutCounter>();
        builder.Services.TryAddSingleton<IOtpDeliverySeam, UnavailableOtpDelivery>();

        // ⚠ Managed identity — docs/plan/11 § Managed identity, "the feature that removes stored
        // secrets". Three registrations and no configuration, because there is nothing to configure:
        // the trust anchor is a fact recorded on each identity's own grain at binding time, not a
        // list of issuers a silo is told about. A per-silo trusted-issuer list would be the thing
        // that drifts between regions.
        //
        // ⚠ HttpClient as a singleton rather than through IHttpClientFactory. This assembly does not
        // reference Microsoft.Extensions.Http, and the traffic here is control-plane — a binding, and
        // an occasional issuer refresh — so the connection-recycling and DNS-staleness arguments for
        // the factory do not bite. The timeout is short on purpose: an unreachable cluster is the
        // EXPECTED answer for a BYO cluster with no public endpoint (docs/plan/11 § Managed identity),
        // and a tenant sitting in front of a binding form should be told so in seconds.
        builder.Services.TryAddSingleton<IProjectedTokenValidator, ProjectedTokenValidator>();
        builder.Services.TryAddSingleton<IClusterOidcDiscovery>(
            services => new HttpClusterOidcDiscovery(
                new() { Timeout = TimeSpan.FromSeconds(10) },
                services.GetRequiredService<IClock>()
            )
        );
        builder.Services.TryAddSingleton<ITokenExchange, GrainTokenExchange>();
        builder.Services.TryAddSingleton<SignInOptions>(_ => SignInOptions.Default);
        builder.Services.TryAddSingleton<SignInService>();

        return builder;
    }
}
