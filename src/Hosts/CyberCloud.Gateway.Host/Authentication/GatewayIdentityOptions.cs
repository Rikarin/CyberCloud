using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Validation;

namespace CyberCloud.Gateway.Host.Authentication;

/// <summary>
///     Which identity host this gateway trusts, and what a token it issues must say.
///     docs/plan/11 § Protocol, docs/plan/10 § Request pipeline, stage 2.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No default issuer, on purpose.</b> Every other value in <c>GatewayOptions</c> has
///         one, and this one deliberately does not: a gateway whose issuer defaulted to a production
///         origin would validate tokens against a host it was never pointed at, and the failure —
///         every request <c>401</c> — would look like a client's problem. An unconfigured section
///         leaves no <c>ICallerContextResolver</c> registered, and <c>GatewayComposition.BuildAsync</c>
///         refuses to compose, naming this section. The shipped <c>appsettings.json</c> carries the
///         production origin, so the refusal fires only when a deployment blanks it.
///     </para>
///     <para>
///         ⚠ <b>The issuer is the identity host's origin, exactly as that host spells it in
///         <c>CyberCloud:Identity:Issuer</c>.</b> Validation fetches
///         <c>{Issuer}/.well-known/openid-configuration</c>, refuses a document whose <c>issuer</c>
///         differs from this string, and then reads the key set the document names. A trailing-slash
///         difference is tolerated; a scheme, host or port difference is a gateway that accepts
///         nothing.
///     </para>
/// </remarks>
sealed class GatewayIdentityOptions {
    /// <summary>The configuration section, <c>CyberCloud:Gateway:Identity</c>.</summary>
    public const string SectionName = GatewayOptions.SectionName + ":Identity";

    /// <summary>
    ///     The identity host's origin — the <c>iss</c> every accepted token carries, and where the
    ///     discovery document and the JWKS are fetched from.
    /// </summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>
    ///     The <c>aud</c> an accepted token must name. <see cref="AccessTokenPolicy.Audience" />
    ///     unless a deployment says otherwise, and no deployment should.
    /// </summary>
    /// <remarks>
    ///     ⚠ Configurable so the contract is not a hidden constant a deployment cannot see, not so a
    ///     deployment can widen it. There is one API and one audience; a token minted for anything
    ///     else was minted for somebody else.
    /// </remarks>
    public string Audience { get; init; } = AccessTokenPolicy.Audience;

    /// <summary>Whether the section names an issuer at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer);

    /// <summary>
    ///     The same two values in the shape the shared validator is registered from.
    /// </summary>
    /// <remarks>
    ///     Kept as a separate type rather than replaced by <see cref="BearerTokenOptions" /> because
    ///     this one is bound from the gateway's own section, and the refusal for a blank issuer has to
    ///     name <c>CyberCloud:Gateway:Identity:Issuer</c> — the key an operator would go and set —
    ///     rather than a section every relying party shares.
    /// </remarks>
    public BearerTokenOptions ToBearerTokenOptions() => new() { Issuer = Issuer, Audience = Audience };
}
