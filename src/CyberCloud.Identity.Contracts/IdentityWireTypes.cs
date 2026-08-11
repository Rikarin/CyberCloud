using CyberCloud.Core;
using System.Diagnostics.CodeAnalysis;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     An address in the vault, never a value. docs/plan/00 § Non-negotiables:
///     <i>"secrets are <c>SecretRef</c> handles resolved at the data plane"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This duplicates <c>CyberCloud.Core.Contracts.SecretRef</c>, and the duplication is
///         now debt rather than a decision.</b> That type is the same idea with the same three
///         fields. The reason given here — that reusing it would drag
///         <c>CyberCloud.ResourceManager.Contracts</c>, and through it the Kubernetes and tenancy
///         contracts, into identity for one record — <b>stopped being true on 2026-08-11</b>, when
///         <c>SecretRef</c> was lifted into <c>CyberCloud.Core.Contracts</c> carrying its published
///         alias <c>CyberCloud.ResourceManager.SecretRef</c> unchanged. Both modules already look
///         there.
///     </para>
///     <para>
///         Switching the three properties over is a one-file change plus a
///         <c>WireContractTests</c> baseline edit, and it is cheapest before the first release tag
///         exists, because <c>CyberCloud.Identity.VaultSecretRef</c> below is itself a published wire
///         identifier. It is not done here only because this file was outside the owning task.
///     </para>
///     <para>
///         ⚠ <b>There is no member a value could ride in, and that is the design.</b> A nullable
///         <c>Value</c> "for convenience" would be populated by the first caller who found resolving
///         inconvenient, and from then on every backup of the durable tier would contain it.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.VaultSecretRef")]
public sealed record VaultSecretRef {
    /// <summary>The vault path — <c>tenants/{tenantId}/users/{userId}/totp</c>.</summary>
    [Id(0)]
    public string Path { get; init; } = string.Empty;

    /// <summary>The field within that path.</summary>
    [Id(1)]
    public string Field { get; init; } = string.Empty;

    /// <summary>The version, or empty for "current". Pinning a version makes rotation observable.</summary>
    [Id(2)]
    public string Version { get; init; } = string.Empty;

    /// <summary>Whether this handle names something.</summary>
    public bool IsEmpty => Path.Length == 0 || Field.Length == 0;

    /// <inheritdoc />
    public override string ToString() =>
        Version.Length == 0 ? $"{Path}#{Field}" : $"{Path}#{Field}@{Version}";
}

/// <summary>
///     A user, as everything outside the identity module sees one. docs/plan/11 § The object model.
/// </summary>
/// <remarks>
///     ⚠ <b>No credential material of any kind appears here.</b> Not a hash, not a salt, not a
///     handle: <see cref="EnrolledCredentials" /> says only <i>which kinds</i> are enrolled, which is
///     what a portal needs to render an account page and is the most an authenticated caller may
///     learn. The hashes live in the grain's own state and never leave it.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.UserProfile")]
public sealed record UserProfile {
    /// <summary>The user's GUID. The <c>sub</c> claim, and the ReBAC subject id.</summary>
    [Id(0)]
    public Guid UserId { get; init; }

    /// <summary>
    ///     The tenant this user belongs to. ⚠ Exactly one, forever — docs/plan/11 § Sign-up and
    ///     tenant creation. The same human in two tenants is two <see cref="UserProfile" />s with two
    ///     <see cref="UserId" />s and, usually, one <see cref="Email" />.
    /// </summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>
    ///     The address, normalized by <c>GrainKeys.NormalizeEmail</c>. An attribute, not the key.
    /// </summary>
    [Id(2)]
    public string Email { get; init; } = string.Empty;

    /// <summary>What to call them. Free text, and never a log message — docs/plan/11 § Auditing.</summary>
    [Id(3)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Where in the lifecycle.</summary>
    [Id(4)]
    public UserStatus Status { get; init; } = UserStatus.Invited;

    /// <summary>When the object was created.</summary>
    [Id(5)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>
    ///     Which credential kinds are enrolled, in <see cref="CredentialKind" /> order.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>{ get; set; }</c> and a concrete <see cref="List{T}" />, not <c>{ get; init; }</c>
    ///     over an interface. System.Text.Json — the grain-storage serializer, docs/plan/02
    ///     § Orleans and hosting — does not populate a get-only collection, so a get-only member
    ///     round-trips as empty and the account page silently reports no credentials.
    /// </remarks>
    [Id(6)]
    public List<CredentialKind> EnrolledCredentials { get; set; } = [];

    /// <summary>How many recovery codes are still unburnt. Zero is worth a prompt.</summary>
    [Id(7)]
    public int RemainingRecoveryCodes { get; init; }
}

/// <summary>
///     One enrolled passkey. docs/plan/11 § Credentials, the default credential.
/// </summary>
/// <remarks>
///     Every field here is public by construction — a WebAuthn credential is a public key, its id and
///     a counter. The private key never leaves the authenticator, which is the property that makes
///     passkeys worth leading with.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.PasskeyCredential")]
public sealed record PasskeyCredential {
    /// <summary>The credential id, base64url. What the browser sends back in an assertion.</summary>
    [Id(0)]
    public string CredentialId { get; init; } = string.Empty;

    /// <summary>The COSE public key, base64url.</summary>
    /// <remarks>
    ///     ⚠ The suppression below is the intended answer to CC1005 rather than a way around it —
    ///     see <c>SecretInGrainStateAnalyzer</c>, which says a legitimate <c>*Key</c> is answered
    ///     "with a <c>[SuppressMessage]</c> carrying the argument". The argument: this is the
    ///     <i>public</i> half of an asymmetric pair, it is useless to anyone who holds it, and
    ///     WebAuthn verification cannot happen without it in grain state.
    /// </remarks>
    [Id(1)]
    [SuppressMessage(
        "CyberCloud.Security",
        "CC1005:A secret must not be a serialized member of grain state",
        Justification =
            "A WebAuthn credential public key is public by construction — the private half never "
            + "leaves the authenticator. Verifying an assertion requires it, so it has to be in "
            + "durable grain state, and holding it grants nothing."
    )]
    public string PublicKey { get; init; } = string.Empty;

    /// <summary>The authenticator model. Useful for "you registered a YubiKey" in the portal.</summary>
    [Id(2)]
    public Guid AaGuid { get; init; }

    /// <summary>
    ///     The authenticator's signature counter as of the last assertion.
    /// </summary>
    /// <remarks>
    ///     ⚠ A counter that goes <i>backwards</i> is the cloned-authenticator signal WebAuthn exists
    ///     to give, and it is only a signal if it is persisted. Many authenticators report a constant
    ///     zero, which is legal and must not be treated as a clone — so the rule is "reject a
    ///     decrease from a non-zero counter", not "require an increase".
    /// </remarks>
    [Id(3)]
    public uint SignCount { get; init; }

    /// <summary>What the user called it.</summary>
    [Id(4)]
    public string Label { get; init; } = string.Empty;

    /// <summary>When it was enrolled.</summary>
    [Id(5)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When it last produced an accepted assertion.</summary>
    [Id(6)]
    public DateTimeOffset LastUsedAt { get; init; }
}

/// <summary>
///     A group. docs/plan/11 § The object model — <b>and it holds no member list</b>.
/// </summary>
/// <remarks>
///     ⚠ The absence is the whole design. docs/plan/11 § The object model: membership is
///     <c>group:X#member@user:Y</c> in ReBAC, "so nesting, inheritance and <c>ListObjects</c> come
///     free". A <c>Members</c> array here would be a second source of truth, a write hot spot for a
///     large group, and a thing to keep in step with the tuples that actually decide access. There is
///     nowhere to put one, which is stronger than a rule saying not to.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.GroupDescriptor")]
public sealed record GroupDescriptor {
    /// <summary>The group's GUID — the ReBAC object id in <c>group:{id}</c>.</summary>
    [Id(0)]
    public Guid GroupId { get; init; }

    /// <summary>The tenant.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>The display name. Renaming is free because the key is the GUID.</summary>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>What it is for.</summary>
    [Id(3)]
    public string Description { get; init; } = string.Empty;

    /// <summary>When it was created.</summary>
    [Id(4)]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     An OAuth client registration. docs/plan/11 § The object model.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ApplicationRegistration")]
public sealed record ApplicationRegistration {
    /// <summary>The application's GUID — the grain key, and not the client id.</summary>
    [Id(0)]
    public Guid ApplicationId { get; init; }

    /// <summary>The tenant that owns the registration.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>The <c>client_id</c> on the wire. Unique within the tenant.</summary>
    [Id(2)]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>What the consent screen calls it.</summary>
    [Id(3)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>
    ///     The exact redirect URIs. ⚠ Compared whole and ordinally — never by prefix, never with a
    ///     wildcard. A prefix match on a redirect URI is the classic open-redirect that turns an
    ///     authorization code into someone else's session.
    /// </summary>
    [Id(4)]
    public List<string> RedirectUris { get; set; } = [];

    /// <summary>Where the client may be sent after sign-out.</summary>
    [Id(5)]
    public List<string> PostLogoutRedirectUris { get; set; } = [];

    /// <summary>The grants this client may use. Anything absent is refused at <c>/token</c>.</summary>
    [Id(6)]
    public List<GrantType> AllowedGrants { get; set; } = [];

    /// <summary>The scopes it may ask for.</summary>
    [Id(7)]
    public List<string> AllowedScopes { get; set; } = [];

    /// <summary>
    ///     Whether the client can keep a secret. ⚠ A public client — a SPA, a CLI — <b>must</b> use
    ///     PKCE and must hold no secret; that is what this flag decides rather than describes.
    /// </summary>
    [Id(8)]
    public bool IsPublicClient { get; init; }

    /// <summary>
    ///     Where the client secret lives, for a confidential client. Empty for a public one.
    /// </summary>
    [Id(9)]
    public VaultSecretRef ClientSecretRef { get; init; } = new();

    /// <summary>When it was registered.</summary>
    [Id(10)]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     A machine identity — the thing a CI job or a background service signs in as.
///     docs/plan/11 § The object model.
/// </summary>
/// <remarks>
///     ⚠ A service principal is a ReBAC subject in its own right (<c>servicePrincipal:{id}</c>), not
///     a user with a funny password. Modelling it as a user would put a machine into every "list the
///     people in this tenant" answer and make an audit trail lie about who did something.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ServicePrincipalDescriptor")]
public sealed record ServicePrincipalDescriptor {
    /// <summary>The GUID. The <c>sub</c> claim for a client-credentials token.</summary>
    [Id(0)]
    public Guid ServicePrincipalId { get; init; }

    /// <summary>The tenant.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>The application registration it authenticates as, when there is one.</summary>
    [Id(2)]
    public Guid ApplicationId { get; init; }

    /// <summary>What to call it in an audit event.</summary>
    [Id(3)]
    public string DisplayName { get; init; } = string.Empty;

    /// <summary>Whether it may authenticate at all.</summary>
    [Id(4)]
    public bool Enabled { get; init; } = true;

    /// <summary>Where its client secret lives.</summary>
    [Id(5)]
    public VaultSecretRef CredentialSecretRef { get; init; } = new();

    /// <summary>
    ///     The SHA-256 thumbprints of certificates it may present, for mTLS. ⚠ M2 — the seam is here
    ///     so the wire type does not change when it lands.
    /// </summary>
    [Id(6)]
    public List<string> CertificateThumbprints { get; set; } = [];

    /// <summary>When it was created.</summary>
    [Id(7)]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     A sign-in session, as an administrator or the user's own device list sees one.
///     docs/plan/11 § Sessions and revocation.
/// </summary>
/// <remarks>
///     ⚠ <b>No refresh material appears here.</b> The session carries the <i>hash</i> of the live
///     refresh handle and this descriptor does not carry even that: a "your devices" page that leaked
///     a refresh handle would turn a read permission into a session takeover.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.SessionDescriptor")]
public sealed record SessionDescriptor {
    /// <summary>The session's GUID. Travels inside refresh tokens.</summary>
    [Id(0)]
    public Guid SessionId { get; init; }

    /// <summary>Whose session it is.</summary>
    [Id(1)]
    public Guid UserId { get; init; }

    /// <summary>The tenant.</summary>
    [Id(2)]
    public Guid TenantId { get; init; }

    /// <summary>The client this session was opened for.</summary>
    [Id(3)]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>Something the user recognises — "Firefox on macOS".</summary>
    [Id(4)]
    public string DeviceLabel { get; init; } = string.Empty;

    /// <summary>
    ///     The truncated SHA-256 of the originating IP, not the IP.
    /// </summary>
    /// <remarks>
    ///     Enough to say "this is a different network from last time" without putting an address in
    ///     the durable tier and therefore in every backup. The address itself goes to the telemetry
    ///     pipeline as a structured field, where the retention and redaction policy reaches it —
    ///     docs/plan/11 § Auditing.
    /// </remarks>
    [Id(5)]
    public string ClientAddressDigest { get; init; } = string.Empty;

    /// <summary>When the user signed in.</summary>
    [Id(6)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>The last successful refresh.</summary>
    [Id(7)]
    public DateTimeOffset LastRefreshedAt { get; init; }

    /// <summary>
    ///     How many rotations this chain has been through. ⚠ Monotonic — a refresh presenting an
    ///     older generation is a replay, and that is exactly how reuse is detected.
    /// </summary>
    [Id(8)]
    public int Generation { get; init; }

    /// <summary>Whether the session still authenticates anything.</summary>
    [Id(9)]
    public bool IsLive { get; init; }

    /// <summary>Why it stopped, when it did.</summary>
    [Id(10)]
    public RevocationReason RevokedBecause { get; init; } = RevocationReason.None;

    /// <summary>How the user proved who they were. The <c>amr</c> claim.</summary>
    [Id(11)]
    public List<AuthenticationMethod> Methods { get; set; } = [];

    /// <summary>
    ///     When the last authentication happened — the <c>auth_time</c> claim, which is what a
    ///     step-up requirement compares against. docs/plan/11 § Protocol.
    /// </summary>
    [Id(12)]
    public DateTimeOffset AuthenticatedAt { get; init; }
}

/// <summary>
///     What a successful refresh hands back: a new handle, and the generation it belongs to.
/// </summary>
/// <remarks>
///     ⚠ <see cref="Handle" /> is the one place in this assembly a live credential value crosses a
///     grain boundary, and it does so <b>outward only</b> — the grain mints it, returns it once, and
///     stores only its hash. A caller that loses it cannot ask for it again.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.RefreshRotation")]
public sealed record RefreshRotation {
    /// <summary>The new refresh handle, base64url. Shown once.</summary>
    [Id(0)]
    public string Handle { get; init; } = string.Empty;

    /// <summary>The session it belongs to.</summary>
    [Id(1)]
    public Guid SessionId { get; init; }

    /// <summary>Which rotation this is. Strictly increasing within a session.</summary>
    [Id(2)]
    public int Generation { get; init; }

    /// <summary>When the handle stops being accepted.</summary>
    [Id(3)]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
///     A batch of recovery codes, at the one moment they exist in plaintext.
/// </summary>
/// <remarks>
///     ⚠ <b>Returned once and never stored.</b> docs/plan/11 § Credentials: "10 × 10 chars,
///     single-use, hashed. Shown once." The grain keeps only the hashes, so regenerating is the only
///     recovery from a lost sheet — which is the correct behaviour and the reason the portal has to
///     say so on the page that shows them.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.RecoveryCodeBatch")]
public sealed record RecoveryCodeBatch {
    /// <summary>The codes, in the order they should be printed.</summary>
    [Id(0)]
    public List<string> Codes { get; set; } = [];

    /// <summary>When they were minted. Generating a new batch invalidates the previous one.</summary>
    [Id(1)]
    public DateTimeOffset CreatedAt { get; init; }
}

/// <summary>
///     What TOTP enrolment produces. docs/plan/11 § Credentials.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.TotpEnrollment")]
public sealed record TotpEnrollment {
    /// <summary>Where the shared secret lives. ⚠ Never the secret — docs/plan/11 § Credentials.</summary>
    [Id(0)]
    public VaultSecretRef SecretRef { get; init; } = new();

    /// <summary>The number of digits a code carries. Six, per RFC 6238's common profile.</summary>
    [Id(1)]
    public int Digits { get; init; } = TotpParameters.Digits;

    /// <summary>The step, in seconds. Thirty.</summary>
    [Id(2)]
    public int PeriodSeconds { get; init; } = TotpParameters.PeriodSeconds;

    /// <summary>When enrolment completed. Empty until a first code is verified.</summary>
    [Id(3)]
    public DateTimeOffset ConfirmedAt { get; init; }
}

/// <summary>RFC 6238's parameters, as docs/plan/11 § Credentials fixes them.</summary>
public static class TotpParameters {
    /// <summary>Six digits.</summary>
    public const int Digits = 6;

    /// <summary>A thirty-second step.</summary>
    public const int PeriodSeconds = 30;

    /// <summary>
    ///     ±1 step of drift, and no more. docs/plan/11 § Credentials.
    /// </summary>
    /// <remarks>
    ///     ⚠ Widening this is not a usability tweak, it is an increase in the online guessing surface:
    ///     at ±1 an attacker gets three live codes out of a million per attempt, at ±5 they get
    ///     eleven. The right answer to a user whose clock is wrong is to fix the clock.
    /// </remarks>
    public const int DriftSteps = 1;

    /// <summary>The shared secret's length in bytes — 160 bits, RFC 4226 § 4's recommendation.</summary>
    public const int SecretBytes = 20;
}

/// <summary>
///     The outcome of an authentication attempt, shaped so that it cannot leak whether the account
///     exists. docs/plan/11 § Credentials, § Enumeration.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>There is one failure shape, deliberately.</b> docs/plan/11 § Credentials: sign-in,
///         password reset and sign-up "return the same response and take the same time whether or not
///         the account exists". A <c>NoSuchUser</c> member would make the distinction representable,
///         and a representable distinction is one somebody eventually returns.
///     </para>
///     <para>
///         The <i>reason</i> for a failure still exists — it goes to the audit pipeline as a
///         structured field, where docs/plan/11 § Auditing wants it. What it never does is reach the
///         caller.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.SignInOutcome")]
public sealed record SignInOutcome {
    /// <summary>The single failure, shared by every reason one can happen.</summary>
    public static SignInOutcome Rejected { get; } = new();

    /// <summary>Whether the caller is now authenticated.</summary>
    [Id(0)]
    public bool Succeeded { get; init; }

    /// <summary>The authenticated user, or <see cref="Guid.Empty" />.</summary>
    [Id(1)]
    public Guid UserId { get; init; }

    /// <summary>The session that was opened, or <see cref="Guid.Empty" />.</summary>
    [Id(2)]
    public Guid SessionId { get; init; }

    /// <summary>How they proved it.</summary>
    [Id(3)]
    public AuthenticationMethod Method { get; init; } = AuthenticationMethod.None;

    /// <summary>
    ///     Whether a second factor is still owed before the session may be used.
    /// </summary>
    /// <remarks>
    ///     ⚠ A passkey sets this <see langword="false" /> on its own: a WebAuthn assertion with user
    ///     verification is already two factors, and asking for a TOTP afterwards is theatre that
    ///     trains users to reach for the weaker credential.
    /// </remarks>
    [Id(4)]
    public bool SecondFactorRequired { get; init; }

    /// <summary>Builds the success.</summary>
    /// <param name="userId">Who authenticated.</param>
    /// <param name="sessionId">The session opened for them.</param>
    /// <param name="method">How.</param>
    /// <param name="secondFactorRequired">Whether the session is not yet fully authenticated.</param>
    public static SignInOutcome Success(
        Guid userId,
        Guid sessionId,
        AuthenticationMethod method,
        bool secondFactorRequired = false
    ) =>
        new() {
            Succeeded = true,
            UserId = userId,
            SessionId = sessionId,
            Method = method,
            SecondFactorRequired = secondFactorRequired
        };
}

/// <summary>
///     An invitation of an email address into a tenant. docs/plan/11 § Sign-up and tenant creation,
///     the invited path.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.Invitation")]
public sealed record Invitation {
    /// <summary>The user object created for the invitee, in <see cref="UserStatus.Invited" />.</summary>
    [Id(0)]
    public Guid UserId { get; init; }

    /// <summary>The tenant they were invited into.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>The address invited, normalized.</summary>
    [Id(2)]
    public string Email { get; init; } = string.Empty;

    /// <summary>The ReBAC relation to grant on acceptance — <c>owner</c>, <c>contributor</c>.</summary>
    [Id(3)]
    public string Relation { get; init; } = string.Empty;

    /// <summary>When the invitation stops being redeemable.</summary>
    [Id(4)]
    public DateTimeOffset ExpiresAt { get; init; }
}

/// <summary>
///     A failure that carries no information about whether the account exists.
/// </summary>
/// <remarks>
///     ⚠ Every enumeration-sensitive endpoint returns <b>this exact message</b>, whatever actually
///     went wrong, and the real reason goes to the audit pipeline instead. It is a constant rather
///     than a string literal per call site because "the same response" is a property that has to
///     survive the next person adding a branch.
/// </remarks>
public static class UniformFailures {
    /// <summary>
    ///     The one answer to a failed sign-in, however it failed. Wrong password, no such user,
    ///     locked out, suspended, no credential enrolled — all of them, identically.
    /// </summary>
    public const string SignIn =
        "The email address or credential is incorrect, or the account cannot sign in right now.";

    /// <summary>
    ///     The one answer to a password-reset request. docs/plan/11 § Credentials: "the reset email
    ///     is the only signal, and it goes to the address typed regardless".
    /// </summary>
    public const string PasswordReset =
        "If that address has an account, a reset link is on its way to it.";

    /// <summary>
    ///     The one answer to a self-serve sign-up. Both a free address and a taken one produce it —
    ///     the mail that follows is what differs, and it goes to the address either way.
    /// </summary>
    public const string SignUp =
        "Check that address for a message telling you what to do next.";

    /// <summary>Builds the uniform sign-in failure.</summary>
    public static Result<SignInOutcome> RejectSignIn() =>
        Result<SignInOutcome>.Failure(ErrorCode.AuthorizationFailed, SignIn);
}
