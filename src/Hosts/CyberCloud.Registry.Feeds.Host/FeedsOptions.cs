using CyberCloud.Identity.Validation;

namespace CyberCloud.Registry.Feeds.Host;

/// <summary>
///     The feeds host's own configuration — the <c>CyberCloud:Feeds</c> section.
/// </summary>
/// <remarks>
///     Two knobs, one derived limit, and an identity section. Everything else this host needs — the object store, the
///     Orleans cluster — is a section another component owns and this host reads the same way every
///     host does.
/// </remarks>
public sealed class FeedsOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = "CyberCloud:Feeds";

    /// <summary>
    ///     The origin clients reach this host at — what a NuGet service index and an npm tarball URL
    ///     are spelled with. Empty means "the origin the request arrived on", which is right behind
    ///     no proxy and wrong behind one that rewrites the host.
    /// </summary>
    public string PublicBaseUri { get; init; } = string.Empty;

    /// <summary>
    ///     The largest artefact a push may carry, in bytes. 256 MiB unless a deployment says
    ///     otherwise — larger than any package a registry should hold, and small enough that a
    ///     request is buffered whole, hashed and signed rather than streamed unsigned.
    /// </summary>
    public long MaxArtifactBytes { get; init; } = 256L * 1024 * 1024;

    /// <summary>
    ///     The largest request body the server admits — twice <see cref="MaxArtifactBytes" />, and
    ///     what Kestrel's <c>MaxRequestBodySize</c> and the multipart reader's limit are set to.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The cap was unreachable until this existed.</b> Kestrel refuses a body over its own
    ///     default of 30,000,000 bytes with a <c>413</c> before any handler runs, and the multipart
    ///     reader stops at 128 MiB; the shipped 256 MiB cap, and its message naming
    ///     <c>CyberCloud:Feeds:MaxArtifactBytes</c>, were never met by a real client. The review of
    ///     #29 found it because the test fixture lowers the cap to 64 KiB and never crossed Kestrel's
    ///     line. Twice, rather than exactly, because an npm publish carries its tarball base64-encoded
    ///     inside a JSON document — four thirds of the bytes plus the manifest — and a NuGet push
    ///     wraps its file in a multipart envelope; the protocol handlers bound what they read to the
    ///     artefact cap themselves, so the server's limit only needs to let the envelope through.
    /// </remarks>
    public long MaxRequestBodyBytes => MaxArtifactBytes * 2;

    /// <summary>Which identity host this host trusts — <c>CyberCloud:Feeds:Identity</c>.</summary>
    public FeedsIdentityOptions Identity { get; init; } = new();
}

/// <summary>
///     The identity section, bound from <c>CyberCloud:Feeds:Identity</c> and handed to the shared
///     validator as <see cref="BearerTokenOptions" />.
/// </summary>
/// <remarks>
///     Its own type for the reason the gateway's <c>GatewayIdentityOptions</c> is one: the refusal
///     for a blank issuer has to name <c>CyberCloud:Feeds:Identity:Issuer</c>, the key an operator
///     would set. The shipped <c>appsettings.json</c> carries the production origin; the refusal
///     fires when a deployment blanks it — <c>FeedsComposition.BuildAsync</c> says how.
/// </remarks>
public sealed class FeedsIdentityOptions {
    /// <summary>The configuration section.</summary>
    public const string SectionName = FeedsOptions.SectionName + ":Identity";

    /// <summary>The identity host's origin — see <see cref="BearerTokenOptions.Issuer" />.</summary>
    public string Issuer { get; init; } = string.Empty;

    /// <summary>The audience — see <see cref="BearerTokenOptions.Audience" />.</summary>
    public string Audience { get; init; } = new BearerTokenOptions().Audience;

    /// <summary>Whether the section names an issuer at all.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(Issuer);

    /// <summary>The same two values in the shape the shared validator is registered from.</summary>
    public BearerTokenOptions ToBearerTokenOptions() => new() { Issuer = Issuer, Audience = Audience };
}
