namespace CyberCloud.Sdk;

/// <summary>Settings shared by every credential.</summary>
public class CyberCloudCredentialOptions {
    /// <summary>
    ///     The identity host. Defaults to <see cref="CyberCloudAuthorityHosts.Default" />, which reads
    ///     <c>CYC_AUTHORITY_HOST</c>.
    /// </summary>
    public Uri AuthorityHost { get; set; } = CyberCloudAuthorityHosts.Default;

    /// <summary>
    ///     The tenant to authenticate against. docs/plan/11 § Sign-up and tenant creation makes a user
    ///     belong to exactly one tenant, so this is normally left unset and comes from the credential
    ///     itself.
    /// </summary>
    public string? TenantId { get; set; }

    /// <summary>
    ///     A transport for the identity host — the test seam. Nothing in the SDK's test suite opens a
    ///     socket.
    /// </summary>
    public HttpMessageHandler? Transport { get; set; }

    /// <summary>
    ///     Where refresh tokens are kept between processes. Defaults to
    ///     <see cref="TokenCache.CreatePersistent" />, which is the OS keychain and never a file.
    /// </summary>
    /// <remarks>
    ///     ⚠ Set to <see cref="TokenCache.None" /> for a credential that must leave no trace — a CI
    ///     runner using client credentials has nothing worth caching across processes and everything
    ///     to lose by writing it somewhere.
    /// </remarks>
    public ITokenCache TokenCache { get; set; } = Sdk.TokenCache.CreatePersistent();
}

/// <summary>Settings for <see cref="DefaultCyberCloudCredential" />.</summary>
public sealed class DefaultCyberCloudCredentialOptions : CyberCloudCredentialOptions {
    /// <summary>Skip the environment-variable credential.</summary>
    public bool ExcludeEnvironmentCredential { get; set; }

    /// <summary>Skip the workload-identity credential.</summary>
    public bool ExcludeWorkloadIdentityCredential { get; set; }

    /// <summary>Skip the <c>cyc</c> CLI credential.</summary>
    public bool ExcludeCliCredential { get; set; }

    /// <summary>
    ///     Include an interactive credential as the last resort. ⚠ Off by default: a chain that opens a
    ///     browser on a build agent turns a misconfiguration into a hung job, and the failure a
    ///     developer wants at that point is "no credential found", immediately.
    /// </summary>
    public bool IncludeInteractiveCredential { get; set; }

    /// <summary>Opens the browser for the interactive credential. Required when <see cref="IncludeInteractiveCredential" /> is set.</summary>
    public OpenBrowserCallback? OpenBrowser { get; set; }

    /// <summary>The OAuth client id the interactive and CLI credentials present.</summary>
    public string? ClientId { get; set; }
}
