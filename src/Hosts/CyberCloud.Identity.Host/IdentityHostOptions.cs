namespace CyberCloud.Identity.Host;

/// <summary>
///     What the interactive sign-in endpoints need that is not in the request. docs/plan/11 § Hosts.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="TenantId" /> exists because there is no way to derive it, and that is a
///         real gap rather than a configuration preference.</b> docs/plan/11 § Sign-up and tenant
///         creation makes email uniqueness <i>per tenant</i> and refuses a global email index —
///         "global email uniqueness would be a global index, the thing we do not have and do not
///         want". <c>IEmailIndexGrain</c> is keyed by <c>hash(tenantId + normalized email)</c>, so an
///         address alone resolves to nothing. The sign-in page posts an address alone.
///     </para>
///     <para>
///         The two answers that do not need a configured tenant both cost more than this milestone
///         has:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>Derive it from the OIDC request.</b> The sign-in page is reached from
///             <c>/authorize</c>, whose <c>client_id</c> belongs to an
///             <c>ApplicationRegistration</c> that names a tenant — but <c>client_id</c> is
///             unique <i>within</i> a tenant (see that record) and no index maps one to its
///             application. Adding that index is the real fix and it is a tenancy change, not a host
///             change.
///         </item>
///         <item>
///             <b>Ask the caller.</b> A tenant hint in the request body would let an unauthenticated
///             caller choose which tenant's lockout counters and email index it probes, which is a
///             worse starting point than a fixed one.
///         </item>
///     </list>
///     <para>
///         So: one host signs into one tenant, named in configuration. A deployment that serves
///         several tenants runs several of these, which is the same shape as the origin-per-tenant
///         arrangement the <c>__Host-</c> cookie prefix already implies.
///     </para>
/// </remarks>
public sealed class IdentityHostOptions {
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "CyberCloud:Identity";

    /// <summary>
    ///     The tenant whose users may sign in here. See the ⚠ block on the type.
    /// </summary>
    /// <remarks>
    ///     <see cref="Guid.Empty" /> is the platform tenant
    ///     (<c>PlatformCrossTenantAuthorizer.PlatformTenantId</c>) and is the default so a
    ///     development run works without configuration. ⚠ A production deployment that leaves it
    ///     unset is signing every user into the platform tenant, which is the one tenant whose
    ///     members can reach across all the others.
    /// </remarks>
    public Guid TenantId { get; set; }

    /// <summary>
    ///     The WebAuthn relying-party id — the registrable domain a passkey is bound to.
    /// </summary>
    /// <remarks>
    ///     ⚠ It must equal the origin's registrable domain or a parent of it. A page served from
    ///     <c>id.cybercloud.io</c> may use <c>cybercloud.io</c> or <c>id.cybercloud.io</c> and never
    ///     anything else; getting it wrong fails inside the authenticator with a <c>SecurityError</c>
    ///     that names nothing useful. ⚠ Widening it later <b>orphans every enrolled passkey</b> — the
    ///     credential is bound to the value in force when it was created — so it is a one-way choice
    ///     rather than a setting.
    /// </remarks>
    public string RelyingPartyId { get; set; } = "localhost";

    /// <summary>What the authenticator's own prompt calls this deployment.</summary>
    public string RelyingPartyName { get; set; } = "Cyber Cloud";

    /// <summary>
    ///     The origins a WebAuthn response may claim to come from.
    /// </summary>
    /// <remarks>
    ///     ⚠ Checked by the library against the origin the authenticator signed over, which is what
    ///     makes a passkey phishing-resistant. An empty set means the library refuses every
    ///     assertion, which is the correct behaviour for a host nobody has configured.
    /// </remarks>
    public IList<string> Origins { get; } = ["https://localhost:5001"];
}
