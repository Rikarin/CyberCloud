using CyberCloud.Identity.Contracts;

namespace CyberCloud.Identity.Validation;

/// <summary>
///     Which identity host a relying party trusts, and what a token it issues must say.
///     docs/plan/11 § Protocol, docs/plan/10 § Request pipeline, stage 2.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No default issuer, on purpose.</b> A host whose issuer defaulted to a production
///         origin would validate tokens against a host it was never pointed at, and the failure —
///         every request <c>401</c> — would look like a client's problem. An unconfigured section
///         leaves no <see cref="IBearerTokenValidator" /> registered, and each host's composition
///         refuses to build, naming its own section. The shipped <c>appsettings.json</c> of each host
///         carries the production origin, so the refusal fires only when a deployment blanks it.
///     </para>
///     <para>
///         ⚠ <b>The issuer is the identity host's origin, exactly as that host spells it in
///         <c>CyberCloud:Identity:Issuer</c>.</b> Validation fetches
///         <c>{Issuer}/.well-known/openid-configuration</c>, refuses a document whose <c>issuer</c>
///         differs from this string, and then reads the key set the document names. A trailing-slash
///         difference is tolerated; a scheme, host or port difference is a host that accepts
///         nothing.
///     </para>
///     <para>
///         The configuration section is the host's to name — <c>CyberCloud:Gateway:Identity</c> at
///         the gateway, <c>CyberCloud:Feeds:Identity</c> at the feeds host — because each host's
///         refusal has to name the section an operator would go and set.
///     </para>
/// </remarks>
public sealed class BearerTokenOptions {
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
}
