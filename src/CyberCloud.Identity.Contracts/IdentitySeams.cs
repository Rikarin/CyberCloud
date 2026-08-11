using CyberCloud.Core;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     WebAuthn, with the library kept on the other side of it. docs/plan/02 § Data, transport,
///     Kubernetes: <c>Fido2.AspNet</c> "wrapped behind <c>IPasskeyService</c> so replacing it is one
///     file".
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every type on this interface is ours.</b> That is what makes the wrapper a wrapper:
///         an <c>IPasskeyService</c> whose methods returned <c>Fido2NetLib</c> types would put the
///         library in the signature of everything that touched a passkey, and replacing it would then
///         be every file rather than one. The one file is
///         <c>CyberCloud.Identity.Host/Credentials/Fido2PasskeyService.cs</c>.
///     </para>
///     <para>
///         ⚠ <b>The pin is <c>Fido2.AspNet</c> 4.0.1, not the beta docs/plan/02's register names.</b>
///         That register says 4.0.0-beta9 and justifies it with "the only maintained .NET WebAuthn
///         library, and it is a beta with no stable successor". The justification is refuted — 4.0.0
///         and 4.0.1 are both published stable — and the correction is recorded in
///         <c>Directory.Packages.props</c>. The wrapper stays regardless: the argument for it was
///         never only that the library was a beta.
///     </para>
/// </remarks>
public interface IPasskeyService {
    /// <summary>
    ///     Builds a registration challenge for a user who is enrolling their first or next passkey.
    /// </summary>
    /// <param name="request">Who is enrolling and what they already have.</param>
    Task<Result<PasskeyRegistrationChallenge>> BeginRegistrationAsync(PasskeyRegistrationRequest request);

    /// <summary>
    ///     Verifies the authenticator's attestation and produces the credential to store.
    /// </summary>
    /// <param name="challenge">The challenge issued by <see cref="BeginRegistrationAsync" />.</param>
    /// <param name="attestationJson">The browser's response, verbatim.</param>
    Task<Result<PasskeyCredential>> CompleteRegistrationAsync(
        PasskeyRegistrationChallenge challenge,
        string attestationJson
    );

    /// <summary>
    ///     Builds an assertion challenge — the sign-in half.
    /// </summary>
    /// <param name="credentials">
    ///     The user's enrolled credentials, or empty for a discoverable-credential ("usernameless")
    ///     flow. ⚠ Passing an empty list for a user who <i>does</i> exist and one for a user who does
    ///     not must produce challenges of the same shape, or the challenge itself enumerates.
    /// </param>
    Task<Result<PasskeyAssertionChallenge>> BeginAssertionAsync(IReadOnlyList<PasskeyCredential> credentials);

    /// <summary>
    ///     Verifies an assertion against a stored credential.
    /// </summary>
    /// <param name="challenge">The challenge issued by <see cref="BeginAssertionAsync" />.</param>
    /// <param name="assertionJson">The browser's response, verbatim.</param>
    /// <param name="credential">The stored credential the assertion names.</param>
    /// <returns>The authenticator's new signature counter.</returns>
    Task<Result<uint>> CompleteAssertionAsync(
        PasskeyAssertionChallenge challenge,
        string assertionJson,
        PasskeyCredential credential
    );
}

/// <summary>Who is enrolling a passkey, in our vocabulary rather than the library's.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.PasskeyRegistrationRequest")]
public sealed record PasskeyRegistrationRequest {
    /// <summary>The user's GUID. Becomes the WebAuthn user handle.</summary>
    [Id(0)]
    public Guid UserId { get; init; }

    /// <summary>The address, shown by the authenticator so the user knows which account this is.</summary>
    [Id(1)]
    public string Email { get; init; } = string.Empty;

    /// <summary>What to call them in the authenticator's own UI.</summary>
    [Id(2)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    ///     Credentials the user already has, so the authenticator does not offer to enrol a duplicate.
    /// </summary>
    [Id(3)]
    public List<PasskeyCredential> Existing { get; set; } = [];
}

/// <summary>A registration challenge, in flight.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.PasskeyRegistrationChallenge")]
public sealed record PasskeyRegistrationChallenge {
    /// <summary>The <c>PublicKeyCredentialCreationOptions</c> JSON the browser is handed.</summary>
    [Id(0)]
    public string OptionsJson { get; init; } = string.Empty;

    /// <summary>The user this challenge belongs to.</summary>
    [Id(1)]
    public Guid UserId { get; init; }

    /// <summary>When it stops being accepted. Short — a challenge is a nonce, not a session.</summary>
    [Id(2)]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>An assertion challenge, in flight.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.PasskeyAssertionChallenge")]
public sealed record PasskeyAssertionChallenge {
    /// <summary>The <c>PublicKeyCredentialRequestOptions</c> JSON the browser is handed.</summary>
    [Id(0)]
    public string OptionsJson { get; init; } = string.Empty;

    /// <summary>When it stops being accepted.</summary>
    [Id(1)]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
///     RFC 8693 token exchange for workload identity — docs/plan/11 § Managed identity. The
///     <c>/token</c> endpoint's one entry point into it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This was a seam and is no longer one, and the thing that changed is worth stating.</b>
///         The earlier note said the exchange could not be implemented because steps 3 and 5 of
///         docs/plan/11 § Managed identity need a <i>tenant cluster's</i> OIDC issuer to validate
///         against, and nothing in this repository provisions a cluster whose discovery document is
///         reachable — so a validator would have been written against a fabricated issuer and never
///         seen a real token. That reasoning confused two things: <b>reading</b> an issuer, which does
///         need a cluster, and <b>trusting</b> one, which does not. The issuer is now a recorded fact
///         on <see cref="IManagedIdentityGrain" />, read through <see cref="IClusterOidcDiscovery" />
///         at binding time and refused there when the cluster does not publish one. Everything after
///         that is arithmetic over a JWS and a stored key set.
///     </para>
///     <para>
///         ⚠ <b>The reachability constraint did not go away; it moved to where the document puts
///         it.</b> docs/plan/11 § Managed identity: the flow "requires the tenant's cluster to expose
///         a <b>publicly reachable</b> OIDC discovery document, or that we fetch the JWKS through the
///         <c>AgentInitiated</c> tunnel", and "for BYO clusters that is not automatic, and the portal
///         must say so <b>at binding time</b> rather than failing at token exchange". The tunnel is
///         M2. So a BYO cluster that publishes nothing fails
///         <see cref="IManagedIdentityGrain.BindAsync" /> with
///         <see cref="ManagedIdentityFailures.Unreachable" />, in front of an administrator who can
///         fix it — not at 3am in front of a workload that deployed cleanly.
///     </para>
///     <para>
///         What this returns is an <see cref="ExchangedSubject" /> and never a token: minting the
///         token is OpenIddict's job (ADR-015), and a second token factory in this module would be a
///         second opinion about the claim set.
///     </para>
/// </remarks>
public interface ITokenExchange {
    /// <summary>
    ///     Exchanges a workload's projected service-account token for a platform identity.
    /// </summary>
    /// <param name="tenantId">The tenant that owns the managed identity.</param>
    /// <param name="managedIdentityId">Which managed identity the workload claims to be.</param>
    /// <param name="subjectToken">The projected service-account token, verbatim.</param>
    /// <param name="subjectTokenType">
    ///     The RFC 8693 token type URI. Only <see cref="TokenExchange.JwtSubjectTokenType" /> is
    ///     meaningful for a projected service-account token.
    /// </param>
    /// <returns>
    ///     The identity to mint a token for, or the uniform
    ///     <see cref="ManagedIdentityFailures.Exchange" /> refusal. ⚠ One message for every reason,
    ///     because the caller is unauthenticated and a distinguishable refusal enumerates a tenant's
    ///     identities and their bindings from the token endpoint.
    /// </returns>
    Task<Result<ExchangedSubject>> ExchangeAsync(
        Guid tenantId,
        Guid managedIdentityId,
        string subjectToken,
        string subjectTokenType
    );
}

/// <summary>
///     ⚠ <b>A seam, not an implementation.</b> Email, SMS and WhatsApp one-time codes —
///     docs/plan/11 § Credentials.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why it is only a seam.</b> All three rows in docs/plan/11 § Credentials route through
///         <c>CyberCloud.Communication</c> (docs/plan/17), which does not exist. Delivery is the
///         entire feature: generating six digits and checking them back is twenty lines, and every
///         hard part — provider fan-out, per-tenant sender identity, bounce handling, WhatsApp
///         template pre-approval — belongs to the module that is not written.
///     </para>
///     <para>
///         ⚠ <b>SMS is the weakest factor and the seam should not hide that.</b> docs/plan/11
///         § Credentials: "SIM swap. Offered, never the only factor for an admin." That rule belongs
///         at enrolment, in whatever implements this — an interface cannot enforce it.
///     </para>
/// </remarks>
public interface IOtpDeliverySeam {
    /// <summary>
    ///     Delivers a one-time code by whichever channel <paramref name="kind" /> names.
    /// </summary>
    /// <param name="kind">
    ///     <see cref="CredentialKind.EmailOtp" />, <see cref="CredentialKind.SmsOtp" /> or
    ///     <see cref="CredentialKind.WhatsAppOtp" />.
    /// </param>
    /// <param name="destination">The address or number, unredacted — this is the delivery path.</param>
    /// <param name="code">The six digits.</param>
    /// <returns>
    ///     ⚠ The default implementation fails and says <c>CyberCloud.Communication</c> is missing. It
    ///     does <b>not</b> succeed quietly: an OTP factor that reports delivery and sends nothing
    ///     locks every user who enrols in it out of their account.
    /// </returns>
    Task<Result> DeliverAsync(CredentialKind kind, string destination, string code);
}
