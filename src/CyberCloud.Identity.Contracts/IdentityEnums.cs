namespace CyberCloud.Identity.Contracts;

/// <summary>Where a user is in their lifecycle. docs/plan/11 § Sign-up and tenant creation.</summary>
/// <remarks>
///     ⚠ There is no <c>Deleted</c>. A user id appears as the subject of ReBAC tuples, in audit
///     events and in issued tokens, so removing the object would leave those pointing at nothing and
///     make an audit trail unreadable exactly when it is being read.
///     <see cref="Deprovisioned" /> is the terminal state: the credentials are gone, the email index
///     claim is released so the address can be reused, and the object stays.
/// </remarks>
[Alias("CyberCloud.Identity.UserStatus")]
public enum UserStatus {
    /// <summary>Invited into the tenant but has not completed sign-up. Cannot authenticate.</summary>
    Invited = 0,

    /// <summary>Normal. Can authenticate if a credential is enrolled.</summary>
    Active = 1,

    /// <summary>An administrator suspended the account. Authentication fails; the object is intact.</summary>
    Suspended = 2,

    /// <summary>Credentials removed and the email claim released. Terminal.</summary>
    Deprovisioned = 3
}

/// <summary>
///     A way of proving who you are. docs/plan/11 § Credentials, in that table's order.
/// </summary>
/// <remarks>
///     ⚠ <see cref="Passkey" /> is first because it is the <b>default offered credential</b> at
///     sign-up rather than an upsell — docs/plan/11 § Credentials: "a platform starting in 2026 that
///     leads with passwords is choosing the worse security posture on purpose". Ordering an enum by
///     preference is normally a smell; here the order is read by the enrolment endpoint to decide
///     what to offer first, so it carries meaning and is asserted by a test.
/// </remarks>
[Alias("CyberCloud.Identity.CredentialKind")]
public enum CredentialKind {
    /// <summary>WebAuthn. The default, and the only phishing-resistant option in this list.</summary>
    Passkey = 0,

    /// <summary>Argon2id, per docs/plan/11 § Credentials' parameters.</summary>
    Password = 1,

    /// <summary>RFC 6238, in-house.</summary>
    Totp = 2,

    /// <summary>A single-use printed code. The thing that prevents "I lost my phone".</summary>
    RecoveryCode = 3,

    /// <summary>Delivered through <c>CyberCloud.Communication</c> — see <see cref="IOtpDeliverySeam" />.</summary>
    EmailOtp = 4,

    /// <summary>⚠ Weakest factor: SIM swap. Never the only factor for an admin.</summary>
    SmsOtp = 5,

    /// <summary>⚠ M2, and template pre-approval is a business task.</summary>
    WhatsAppOtp = 6,

    /// <summary>⚠ M2. mTLS for service principals.</summary>
    Certificate = 7
}

/// <summary>
///     Why a one-time code is being sent. <see cref="OtpDelivery.Purpose" />.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>An enum rather than a string, and that is a correctness decision rather than a
///         tidiness one.</b> This value is part of the idempotency key
///         <c>CyberCloud.Communication</c> derives a message grain's identity from, and that key is
///         compared <b>ordinally and without case folding</b> — <c>CommunicationGrainKeys.Message</c>
///         says so. An open string therefore lets two call sites spell the same purpose differently
///         and silently stop deduplicating against each other: <c>"signin"</c> and <c>"signIn"</c>
///         would be two grains, so a retry that reached the second one would send a second SMS. That
///         is the same one-character class of defect this platform has already shipped once, with
///         <c>resourcegroup</c> against <c>resourceGroup</c>. A closed set makes the disagreement a
///         compile error.
///     </para>
///     <para>
///         ⚠ <b>The purpose is part of the key precisely so that two <i>different</i> reasons do not
///         collapse.</b> A sign-in code and a password-reset code issued to the same user seconds
///         apart are two messages, and a key that omitted the purpose would make the second one look
///         like a retry of the first.
///     </para>
/// </remarks>
public enum OtpPurpose {
    /// <summary>
    ///     The zero value a default-constructed <see cref="OtpDelivery" /> carries. ⚠ Never a
    ///     purpose, and refused rather than defaulted: a delivery that did not say why it was being
    ///     sent would share one key space with every other reason.
    /// </summary>
    Unknown = 0,

    /// <summary>A second factor during sign-in.</summary>
    SignIn = 1,

    /// <summary>Re-proving a factor in front of a sensitive operation — docs/plan/11 § Step-up.</summary>
    StepUp = 2,

    /// <summary>Proving control of an address or a number while enrolling it as a factor.</summary>
    Enrolment = 3,

    /// <summary>The password-reset code. docs/plan/11 § Credentials.</summary>
    PasswordReset = 4
}

/// <summary>
///     Why a session stopped being usable. docs/plan/11 § Sessions and revocation names all five.
/// </summary>
[Alias("CyberCloud.Identity.RevocationReason")]
public enum RevocationReason {
    /// <summary>Still live.</summary>
    None = 0,

    /// <summary>The user signed out of this session.</summary>
    SignOut = 1,

    /// <summary>The user signed out everywhere, which revokes every session they hold.</summary>
    SignOutEverywhere = 2,

    /// <summary>A credential changed, so every session predating the change dies.</summary>
    CredentialChange = 3,

    /// <summary>An administrator revoked it.</summary>
    AdminAction = 4,

    /// <summary>
    ///     A refresh token was presented twice — docs/plan/11 § Protocol, "rotating, one-time-use,
    ///     with reuse detection → revoke the whole chain".
    /// </summary>
    /// <remarks>
    ///     ⚠ This reason is the one that must revoke <b>the chain and not the request</b>. A replayed
    ///     refresh token means either the client retried or an attacker stole it, and the two are
    ///     indistinguishable at the endpoint. Killing the chain is safe under the first reading (the
    ///     user signs in again) and correct under the second (the thief and the victim both lose the
    ///     session, which is what stops a silent parallel session).
    /// </remarks>
    RefreshReuseDetected = 5,

    /// <summary>The session outlived its absolute lifetime.</summary>
    Expired = 6
}

/// <summary>
///     The OAuth 2.1 grants this authorization server issues. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     ⚠ There is no <c>Password</c> member and there must never be one. docs/plan/11 § Protocol:
///     resource-owner password credentials was "removed in OAuth 2.1 and it defeats MFA". Leaving the
///     name out of the enum is what makes an application registration unable to ask for it.
///     <c>Implicit</c> and <c>Hybrid</c> are absent for the same reason.
/// </remarks>
[Alias("CyberCloud.Identity.GrantType")]
public enum GrantType {
    /// <summary>Authorization code with PKCE. The only interactive flow.</summary>
    AuthorizationCode = 0,

    /// <summary><c>cyc login</c> on a headless box.</summary>
    DeviceAuthorization = 1,

    /// <summary>Service principals and CI.</summary>
    ClientCredentials = 2,

    /// <summary>Rotating, one-time-use, with reuse detection.</summary>
    RefreshToken = 3,

    /// <summary>
    ///     RFC 8693, for workload identity — <see cref="ITokenExchange" /> and
    ///     <see cref="IManagedIdentityGrain" />. ⚠ It is the only grant that authenticates the client
    ///     with somebody else's signature, which is what makes it the grant with no stored secret.
    /// </summary>
    TokenExchange = 4
}

/// <summary>
///     An <c>amr</c> value — how the subject authenticated on this session, so a sensitive action can
///     require step-up. docs/plan/11 § Protocol.
/// </summary>
[Alias("CyberCloud.Identity.AuthenticationMethod")]
public enum AuthenticationMethod {
    /// <summary>Nothing has been proven.</summary>
    None = 0,

    /// <summary>A passkey assertion. Both factors at once, and phishing-resistant.</summary>
    Passkey = 1,

    /// <summary>An Argon2id password verification.</summary>
    Password = 2,

    /// <summary>A TOTP code.</summary>
    Totp = 3,

    /// <summary>A recovery code. ⚠ Single-use, and burning one is an auditable event.</summary>
    RecoveryCode = 4,

    /// <summary>A client secret or certificate — how a service principal authenticates.</summary>
    ClientCredential = 5
}
