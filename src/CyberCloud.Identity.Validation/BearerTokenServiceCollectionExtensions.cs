using CyberCloud.Core.Time;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace CyberCloud.Identity.Validation;

/// <summary>
///     Registers the production identity seam: bearer JWTs validated against the identity host's
///     published key set. docs/plan/11 § Protocol.
/// </summary>
public static class BearerTokenServiceCollectionExtensions {
    /// <summary>
    ///     Registers OpenIddict's validation core over <c>HttpClient</c> and the
    ///     <see cref="JwksBearerTokenValidator" /> that reads claims off what it accepts.
    /// </summary>
    /// <param name="services">The container.</param>
    /// <param name="identity">Which identity host to trust — the issuer and the audience to pin.</param>
    /// <param name="sectionName">
    ///     The configuration section <paramref name="identity" /> was bound from, so the refusal for
    ///     an empty issuer names the key an operator would set.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             Called by a host's composition only when its identity section names an issuer,
    ///             and never with an empty one.
    ///         </b> A host told no issuer has nothing to validate against, and registering a
    ///         validator anyway would mean choosing a default origin for it — see
    ///         <see cref="BearerTokenOptions" /> for why there is none. What a composition does
    ///         instead is refuse to build, naming its section, which is the loud start-up failure
    ///         https://github.com/Rikarin/CyberCloud/issues/68 asked for in place of the silent
    ///         <c>500</c> that a missing validator used to produce at the gateway.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>OpenIddict.Validation.SystemNetHttp</c> and deliberately not
    ///             <c>OpenIddict.Server.*</c> — the package-level expression of docs/plan/11
    ///             § Hosts' boundary.
    ///         </b> A relying party serves bearer tokens and mints none; one that could issue a
    ///         token would be a second authorization server on an origin whose entire job is to
    ///         accept them. Nor is <c>OpenIddict.Validation.AspNetCore</c> here: that package is an
    ///         ASP.NET Core authentication handler, and no caller of this method is one — each
    ///         resolves <see cref="IBearerTokenValidator" /> itself, so the handler would register a
    ///         scheme nothing consults.
    ///     </para>
    ///     <para>
    ///         <c>TryAdd</c> for the validator, so a host that registered a substitute first is
    ///         entitled to keep it; a test that has no key set does exactly that.
    ///     </para>
    /// </remarks>
    public static IServiceCollection AddJwksBearerTokenValidation(
        this IServiceCollection services,
        BearerTokenOptions identity,
        string sectionName
    ) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(sectionName);

        if (!identity.IsConfigured) {
            throw new ArgumentException(
                $"{sectionName}:Issuer is empty. This method registers a validator that validates "
                + "against that host's key set, and there is no default host to validate against.",
                nameof(identity)
            );
        }

        services
            .AddOpenIddict()
            .AddValidation(options => {
                    // ⚠ Pinned, both of them. An unpinned issuer accepts any host's discovery
                    // document; an unpinned audience accepts a token minted for some other relying
                    // party. Item 2 of the gateway's ICallerContextResolver list.
                    options.SetIssuer(new Uri(identity.Issuer, UriKind.Absolute));
                    options.AddAudiences(identity.Audience);

                    // Discovery and the JWKS over HttpClient, cached, refreshed on an unknown kid.
                    // Item 1 of the same list: a key rotation must not need a host deploy.
                    options.UseSystemNetHttp();
                }
            );

        services.TryAddSingleton<IClock, SystemClock>();
        services.TryAddSingleton<IBearerTokenValidator, JwksBearerTokenValidator>();

        return services;
    }
}
