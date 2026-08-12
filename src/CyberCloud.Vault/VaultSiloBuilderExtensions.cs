using CyberCloud.Core.Time;
using CyberCloud.ResourceManager.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Vault;

/// <summary>
///     Wires the OpenBao-backed <see cref="ISecretResolver" /> into a silo that has a vault.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>OPTING IN IS THE ONLY WAY TO GET THIS, AND THE REFUSING DEFAULT STAYS THE DEFAULT FOR
///         EVERYBODY ELSE.</b> <c>AddCyberCloudResourceManager</c> registers
///         <c>UnavailableSecretResolver</c> with <c>TryAdd</c>, and a silo that never calls the
///         method below keeps it — no configuration key flips this on, and nothing here changes what
///         another silo resolves. <c>VaultSeamWiringTests</c> asserts both halves against the real
///         registrations, in both composition orders, the same shape
///         <c>OtpSeamWiringTests</c> uses. That suite exists because of what the OTP work found:
///         three files claimed <c>UnavailableOtpDelivery</c> was "what every host in this repository
///         gets" while no host registered the seam at all, so the refusal it existed to hand an
///         operator could not be produced.
///     </para>
///     <para>
///         ⚠ <b>What is honestly true of this one, today: no host calls it.</b>
///         <c>CyberCloud.Silo.Host</c> composes the resource manager and therefore holds
///         <c>UnavailableSecretResolver</c> — which is reachable, is driven by <c>ReconcileDriver</c>
///         through every provider, and is the refusal an operator reads. What is not yet true is that
///         any deployment resolves a real secret. Wiring the host is a separate change and this
///         paragraph is here so the next reader does not have to establish it by grepping.
///     </para>
///     <para>
///         ⚠ <b><c>Replace</c> rather than <c>Add</c>, and the reason is the count and not the
///         resolution.</b> Both orders resolve the real thing under a plain <c>Add</c> —
///         <c>TryAdd</c> is a no-op once any descriptor exists, and the last descriptor wins a single
///         resolve — so a resolution assertion cannot tell them apart. What <c>Replace</c> buys is
///         exactly one descriptor, which matters to anything taking
///         <c>IEnumerable&lt;ISecretResolver&gt;</c>: with two, <c>GetServices</c> hands out a
///         refusing resolver sitting behind the real one. That arithmetic was established by the OTP
///         work rather than reasoned about here.
///     </para>
/// </remarks>
public static class VaultSiloBuilderExtensions {
    /// <summary>
    ///     Registers the OpenBao secret resolver, reading <c>CyberCloud:Vault</c> from the host's
    ///     configuration.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    ///     <c>CyberCloud:Vault:Address</c> or <c>CyberCloud:Vault:Role</c> is missing, or the address
    ///     is not an absolute <c>https</c> URL and <c>AllowInsecureTransport</c> is not set. The
    ///     message names the key.
    /// </exception>
    public static ISiloBuilder AddOpenBaoSecretResolver(this ISiloBuilder silo) {
        ArgumentNullException.ThrowIfNull(silo);

        var options = new VaultOptions();

        silo.Configuration.GetSection(VaultOptions.SectionName).Bind(options);

        return silo.AddOpenBaoSecretResolver(options);
    }

    /// <summary>
    ///     Registers the OpenBao secret resolver against options supplied in code.
    /// </summary>
    /// <param name="silo">The silo builder.</param>
    /// <param name="options">Where OpenBao is, which role to use, and which mounts to read.</param>
    /// <returns>The same builder, for chaining.</returns>
    /// <exception cref="InvalidOperationException">
    ///     The options are incomplete or the address is plaintext without
    ///     <see cref="VaultOptions.AllowInsecureTransport" />. ⚠ Thrown at composition rather than
    ///     surfaced as a refusal at the first resolve, and that is deliberate: a silo that opted in
    ///     and got the address wrong should not start and then fail every provision at 03:00 with a
    ///     message about a vault. An unwired silo is a supported shape; a misconfigured one is not.
    /// </exception>
    public static ISiloBuilder AddOpenBaoSecretResolver(this ISiloBuilder silo, VaultOptions options) {
        ArgumentNullException.ThrowIfNull(silo);
        ArgumentNullException.ThrowIfNull(options);

        Validate(options);

        silo.Services.TryAddSingleton<IClock, SystemClock>();
        silo.Services.AddSingleton(options);

        silo.Services.TryAddSingleton<IVaultTokenSource>(
            services => new KubernetesVaultTokenSource(
                CreateClient(options),
                options,
                services.GetRequiredService<IClock>()
            )
        );

        // ⚠ Replace, not TryAdd — see the remarks for what the count buys that resolution does not.
        silo.Services.Replace(
            ServiceDescriptor.Singleton<ISecretResolver>(
                services => {
                    if (options.AllowInsecureTransport) {
                        // ⚠ Every start-up, naming the address. A flag that only appears in a values
                        // file is a flag nobody reads; one that appears in the first hundred log
                        // lines of a production silo is one somebody asks about.
                        services
                            .GetService<ILoggerFactory>()
                            ?.CreateLogger(typeof(VaultSiloBuilderExtensions))
                            .LogWarning(
                                "CyberCloud:Vault:AllowInsecureTransport is set and the vault "
                                + "address is {Address}. Every secret this silo reads crosses the "
                                + "network in the clear. docs/plan/18 § Platform security requires "
                                + "TLS 1.3 everywhere; this setting exists for a test against a "
                                + "container.",
                                options.Address
                            );
                    }

                    return new OpenBaoSecretResolver(
                        CreateClient(options),
                        services.GetRequiredService<IVaultTokenSource>(),
                        options,
                        services.GetService<ILogger<OpenBaoSecretResolver>>()
                    );
                }
            )
        );

        return silo;
    }

    /// <summary>
    ///     Builds the client both the token source and the resolver reach OpenBao with.
    /// </summary>
    /// <param name="options">The vault options, for the timeout.</param>
    /// <remarks>
    ///     ⚠ A bare <see cref="HttpClient" /> and no <c>IHttpClientFactory</c>, which follows the
    ///     tree: nothing in this repository calls <c>AddHttpClient</c>, and
    ///     <c>CyberCloud.Identity</c>'s <c>HttpClusterOidcDiscovery</c> is registered the same way
    ///     with the same argument — control-plane traffic to one host, held for the process's life,
    ///     with a short timeout.
    ///     <para>
    ///         ⚠ Two clients rather than one shared instance, because the two have different
    ///         reasons to exist and a shared one would make the login's timeout and the read's
    ///         timeout the same knob forever. They are singletons either way; a socket is pooled
    ///         across both by <c>SocketsHttpHandler</c>'s own pool.
    ///     </para>
    /// </remarks>
    static HttpClient CreateClient(VaultOptions options) => new() { Timeout = options.RequestTimeout };

    static void Validate(VaultOptions options) {
        if (options.Address.Length == 0) {
            throw new InvalidOperationException(
                "CyberCloud:Vault:Address is not set. AddOpenBaoSecretResolver was called, so this "
                + "silo means to read secrets from OpenBao, and it has no address to read them from. "
                + "A silo that does not have a vault should not call it at all — it keeps "
                + "UnavailableSecretResolver, which refuses and says so."
            );
        }

        if (options.Role.Length == 0) {
            throw new InvalidOperationException(
                "CyberCloud:Vault:Role is not set. The silo logs in to OpenBao with its own projected "
                + "service-account token against a named role, and the role is what OpenBao maps that "
                + "service account onto. There is no default worth guessing."
            );
        }

        if (!Uri.TryCreate(options.Address, UriKind.Absolute, out var address)) {
            throw new InvalidOperationException(
                $"CyberCloud:Vault:Address is '{options.Address}', which is not an absolute URL."
            );
        }

        if (address.Scheme == Uri.UriSchemeHttps) {
            return;
        }

        if (address.Scheme == Uri.UriSchemeHttp && options.AllowInsecureTransport) {
            return;
        }

        throw new InvalidOperationException(
            $"CyberCloud:Vault:Address is '{options.Address}', which is not https. docs/plan/18 § "
            + "Platform security requires TLS 1.3 everywhere, and a vault reached over plaintext "
            + "hands every secret it serves to anything on the path. Set "
            + "CyberCloud:Vault:AllowInsecureTransport only for a test against a container."
        );
    }
}
