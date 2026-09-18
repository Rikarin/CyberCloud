namespace CyberCloud.Identity;

/// <summary>
///     <c>UserGrain</c>'s durable state. docs/plan/11 § The object model.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             Every collection here is <c>{ get; set; }</c> over a concrete type, and that is a
///             trap rather than a style choice.
///         </b> The grain-storage serializer is System.Text.Json
///         (docs/plan/02 § Orleans and hosting), and STJ
///         <b>
///             does not populate a get-only
///             collection
///         </b>: a <c>{ get; }</c> property with an initializer deserializes as whatever
///         the initializer produced, silently discarding what was stored. For this type that would
///         mean a user whose passkeys and recovery codes vanish on the first reactivation — which
///         looks exactly like "somebody deleted my credentials" and is not reproducible in memory.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately absent:</b> the TOTP shared secret (a
///         <see cref="CyberCloud.Core.Contracts.SecretRef" /> instead — docs/plan/11 § Credentials), the recovery codes in
///         plaintext (hashes), the pepper (resolved at the data plane), and any group membership
///         (ReBAC tuples — docs/plan/11 § The object model).
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.UserGrainState")]
public sealed class UserGrainState {
    /// <summary>The address, normalized. Empty until <c>CreateAsync</c>.</summary>
    [Id(0)]
    public string Email { get; set; } = string.Empty;

    /// <summary>The display name.</summary>
    [Id(1)]
    public string DisplayName { get; set; } = string.Empty;

    /// <summary>Where in the lifecycle.</summary>
    [Id(2)]
    public UserStatus Status { get; set; } = UserStatus.Invited;

    /// <summary>When the object was created.</summary>
    [Id(3)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>
    ///     The Argon2id PHC string, or empty when no password is enrolled.
    /// </summary>
    /// <remarks>
    ///     ⚠ CC1005 does not fire on this and should not: a member called <c>PasswordHash</c> ends in
    ///     <c>Hash</c>, not in <c>Password</c>, and the analyzer's suffix match is a word match. The
    ///     rule is about secrets, and a memory-hard one-way hash with a per-user salt and a vault
    ///     pepper is the storable form of the credential rather than the credential.
    /// </remarks>
    [Id(4)]
    public string PasswordHash { get; set; } = string.Empty;

    /// <summary>Enrolled passkeys. The default credential — docs/plan/11 § Credentials.</summary>
    [Id(5)]
    public List<PasskeyCredential> Passkeys { get; set; } = [];

    /// <summary>Where the TOTP shared secret lives, and the parameters it was enrolled with.</summary>
    [Id(6)]
    public TotpEnrollment? Totp { get; set; }

    /// <summary>
    ///     RFC 6238 steps already spent, so a code cannot be replayed inside its window.
    /// </summary>
    /// <remarks>
    ///     ⚠ Pruned to the few steps that can still be presented, because an unbounded list on a
    ///     durable grain is a row that grows until the write fails. Only <c>±DriftSteps</c> around
    ///     now can ever verify, so anything older can never be replayed and is dropped.
    /// </remarks>
    [Id(7)]
    public List<long> SpentTotpCounters { get; set; } = [];

    /// <summary>SHA-256 of each unburnt recovery code.</summary>
    [Id(8)]
    public List<string> RecoveryCodeHashes { get; set; } = [];

    /// <summary>
    ///     Sessions this user has open. What "sign out everywhere" iterates.
    /// </summary>
    /// <remarks>
    ///     ⚠ This <i>is</i> a list in durable state, unlike group membership, and the difference is
    ///     worth stating. A session list is bounded by the devices one person signs in from, it has
    ///     exactly one writer, and nothing checks it for authorization — the session grain decides
    ///     whether a session is live. A group's membership has none of those properties.
    /// </remarks>
    [Id(9)]
    public List<Guid> Sessions { get; set; } = [];

    /// <summary>
    ///     One outstanding one-time code per <see cref="OtpPurpose" /> — docs/plan/11 § Credentials.
    /// </summary>
    /// <remarks>
    ///     ⚠ A list rather than a dictionary because the collection is at most one entry per member
    ///     of a five-value enum, and a <c>Dictionary</c> keyed by an enum is a serializer question
    ///     nobody needs to have answered. A linear scan over five entries is not a cost.
    /// </remarks>
    [Id(10)]
    public List<OtpChallengeState> OtpChallenges { get; set; } = [];

    /// <summary>
    ///     When codes were sent to this user, for <see cref="OtpPolicy.MaxIssuesPerWindow" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Timestamps rather than a count-and-reset pair, and bounded by pruning.</b> A count
    ///     with a window start is one field cheaper and gets the edge wrong: the window resets
    ///     wholesale, so five codes at 14:59 and five more at 15:00 are ten inside a minute. Sliding
    ///     the window over the actual issue times has no such edge. Entries older than
    ///     <see cref="OtpPolicy.IssueWindow" /> can never be counted again and are dropped on the
    ///     next issue, so this cannot grow — the same argument
    ///     <see cref="SpentTotpCounters" /> makes for the same reason.
    /// </remarks>
    [Id(11)]
    public List<DateTimeOffset> OtpIssuedAt { get; set; } = [];
}

/// <summary>
///     One outstanding one-time code, as the durable tier holds it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE CODE IS NOT HERE AND CC1005 WOULD NOT HAVE STOPPED IT BEING HERE.</b> The
///         analyzer bans <c>[Id]</c> members whose names end in <c>Password</c>, <c>Secret</c>,
///         <c>Token</c> or <c>Key</c> — that is docs/plan/00 § Non-negotiables' list, verbatim and
///         deliberately closed. A member spelled <c>Code</c>, or <c>Otp</c>, or <c>Digits</c> matches
///         none of them, so a future edit that stored the plaintext here would compile clean and
///         ship. Nothing mechanical is guarding this type; what guards it is
///         <see cref="Digest" /> being the only credential-shaped member and
///         <c>OtpIssuanceTests.TheCodeIsNowhereInGrainState</c> asserting the plaintext appears in no
///         serialized member by reflection, which is the same instrument
///         <c>ManagedIdentityTests.NoSecretIsStoredAnywhereInTheFlow</c> uses for the same reason.
///     </para>
///     <para>
///         ⚠ Durable rather than hot, unlike <see cref="SessionGrainState" />, and for the reason
///         <see cref="UserGrainState.SpentTotpCounters" /> gives: losing a session costs a sign-in,
///         and losing a burnt challenge lets an observed code be replayed. Zero loss tolerance puts
///         it in the durable tier.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.OtpChallengeState")]
public sealed class OtpChallengeState {
    /// <summary>Which challenge this is. At most one per purpose is outstanding.</summary>
    [Id(0)]
    public OtpPurpose Purpose { get; set; } = OtpPurpose.Unknown;

    /// <summary>Which channel it went out on. Recorded so a resend cannot silently switch channel.</summary>
    [Id(1)]
    public CredentialKind Kind { get; set; } = CredentialKind.EmailOtp;

    /// <summary>
    ///     <c>OtpCodeProtector.Digest</c> of the code — an HMAC-SHA-256 under the vault pepper.
    /// </summary>
    /// <remarks>
    ///     ⚠ Not a bare SHA-256. Six digits is a million candidates, which is a rounding error to
    ///     anyone holding a database dump; the pepper is what makes the dump insufficient. See
    ///     <c>OtpCodeProtector</c>.
    /// </remarks>
    [Id(2)]
    public string Digest { get; set; } = string.Empty;

    /// <summary>When it was issued. <see cref="OtpPolicy.ResendCooldown" /> is measured from here.</summary>
    [Id(3)]
    public DateTimeOffset IssuedAt { get; set; }

    /// <summary>When it stops being answerable. <see cref="OtpPolicy.Lifetime" /> after issue.</summary>
    [Id(4)]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>How many answers have been tried. At <see cref="OtpPolicy.MaxAttempts" /> it is burnt.</summary>
    [Id(5)]
    public int Attempts { get; set; }
}

/// <summary><c>GroupGrain</c>'s durable state — identity only, no membership.</summary>
/// <remarks>
///     ⚠ <b>There is no members collection and there must not be one.</b> docs/plan/11 § The object
///     model: a member list here "would be a second source of truth and a hot spot for large groups".
///     The membership is <c>group:X#member@user:Y</c> in the tuple store. If a future edit adds a
///     cached list to make some listing faster, the thing to add is the Leopard index of docs/plan/07
///     § Storage, in the authorization module, not a field here.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.GroupGrainState")]
public sealed class GroupGrainState {
    /// <summary>The display name. Empty until created.</summary>
    [Id(0)]
    public string Name { get; set; } = string.Empty;

    /// <summary>What it is for.</summary>
    [Id(1)]
    public string Description { get; set; } = string.Empty;

    /// <summary>When it was created.</summary>
    [Id(2)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Whether the group has been deleted.</summary>
    [Id(3)]
    public bool Deleted { get; set; }
}

/// <summary><c>ApplicationGrain</c>'s durable state.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ApplicationGrainState")]
public sealed class ApplicationGrainState {
    /// <summary>The registration, or <see langword="null" /> before <c>CreateAsync</c>.</summary>
    [Id(0)]
    public ApplicationRegistration? Registration { get; set; }

    /// <summary>
    ///     Whether the client-id index holds a confirmed binding for <see cref="Registration" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>false</c> is the window docs/plan/06 § Two-phase create names — the registration is
    ///     written and the index claim is not yet confirmed — made durable, so the grain can tell a
    ///     create that finished from one whose silo died between the write and the confirm. Nothing
    ///     sweeps an orphaned application by reminder the way a per-subscription reaper sweeps a
    ///     resource; the grain settles itself on its next call instead, and this flag is what it
    ///     reads to decide whether there is anything to settle.
    /// </remarks>
    [Id(1)]
    public bool ClientIdConfirmed { get; set; }
}

/// <summary><c>ServicePrincipalGrain</c>'s durable state.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ServicePrincipalGrainState")]
public sealed class ServicePrincipalGrainState {
    /// <summary>The principal, or <see langword="null" /> before <c>CreateAsync</c>.</summary>
    [Id(0)]
    public ServicePrincipalDescriptor? Descriptor { get; set; }
}

/// <summary>
///     <c>ManagedIdentityGrain</c>'s durable state. docs/plan/11 § Managed identity.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THERE IS NO CREDENTIAL HERE AND THERE IS NOWHERE TO PUT ONE, WHICH IS THE ENTIRE
///             FEATURE.
///         </b> docs/plan/11 § Managed identity calls this "the feature that removes stored
///         secrets" and justifies 1.2 EM with "no secret is ever stored, on either side … it removes
///         an entire incident class". Compare the three states above it: <c>UserGrainState</c> holds
///         hashes and a vault handle, <c>ApplicationGrainState</c> and
///         <c>ServicePrincipalGrainState</c> hold a <see cref="CyberCloud.Core.Contracts.SecretRef" />. This holds a
///         binding — <i>who</i> may present a token — and a public key set. A stolen backup of this
///         row lets an attacker learn which namespace a workload runs in, and nothing else.
///     </para>
///     <para>
///         ⚠
///         <b>
///             CC1005 does not fire on any member here, and that is a result rather than an
///             accident.
///         </b> The analyzer bans <c>[Id]</c> members named <c>*Password</c>, <c>*Secret</c>,
///         <c>*Token</c> or <c>*Key</c> outside the vault assembly, and this is the module most likely
///         to trip it — <c>PasskeyCredential.PublicKey</c> already carries a per-member suppression
///         with its argument. Nothing here needs one: the key set is spelled
///         <c>PublicKeySetJson</c> because that is what it is, and the presented service-account token
///         is a method <i>parameter</i> that is read, verified and discarded rather than a serialized
///         member. <c>NoSecretIsStoredAnywhereInTheFlow</c> asserts the absence by reflection instead
///         of trusting the naming.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ManagedIdentityGrainState")]
public sealed class ManagedIdentityGrainState {
    /// <summary>The identity, or <see langword="null" /> before <c>CreateAsync</c>.</summary>
    [Id(0)]
    public ManagedIdentityDescriptor? Descriptor { get; set; }
}

/// <summary>
///     <c>SessionGrain</c>'s <b>hot</b> state. docs/plan/05 § Hot lists sessions among what it holds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>No refresh handle appears here, only digests.</b> The hot tier is Redis: a database
///         with a wire protocol, a replica and, in a support session, a <c>redis-cli</c>. A stored
///         refresh handle would be a stored bearer credential visible to anyone who could read a key.
///     </para>
///     <para>
///         ⚠ <b><see cref="RetiredHandleDigests" /> is the reuse detector and it must be kept.</b>
///         The obvious optimisation — overwrite the current digest on each rotation and keep nothing
///         — makes a replayed token indistinguishable from a token that was never issued, which
///         turns a compromise signal into a shrug. It is bounded because the session is: at most
///         <c>SessionGrain.MaxRetainedGenerations</c> entries, oldest first.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.SessionGrainState")]
public sealed class SessionGrainState {
    /// <summary>Whose session. <see cref="Guid.Empty" /> before it is opened.</summary>
    [Id(0)]
    public Guid UserId { get; set; }

    /// <summary>Which client it was opened for.</summary>
    [Id(1)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Something the user recognises.</summary>
    [Id(2)]
    public string DeviceLabel { get; set; } = string.Empty;

    /// <summary>The truncated digest of the originating address — never the address.</summary>
    [Id(3)]
    public string ClientAddressDigest { get; set; } = string.Empty;

    /// <summary>When the session opened.</summary>
    [Id(4)]
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>The last successful rotation.</summary>
    [Id(5)]
    public DateTimeOffset LastRefreshedAt { get; set; }

    /// <summary>When the subject last actually authenticated. The <c>auth_time</c> claim.</summary>
    [Id(6)]
    public DateTimeOffset AuthenticatedAt { get; set; }

    /// <summary>When the current handle stops being accepted.</summary>
    [Id(7)]
    public DateTimeOffset RefreshExpiresAt { get; set; }

    /// <summary>The SHA-256 of the handle that is currently valid.</summary>
    [Id(8)]
    public string CurrentHandleDigest { get; set; } = string.Empty;

    /// <summary>The SHA-256 of every handle this chain has retired. Presenting one is a replay.</summary>
    [Id(9)]
    public List<string> RetiredHandleDigests { get; set; } = [];

    /// <summary>Which rotation the current handle is. Strictly increasing.</summary>
    [Id(10)]
    public int Generation { get; set; }

    /// <summary>Whether the session still authenticates anything.</summary>
    [Id(11)]
    public bool IsLive { get; set; }

    /// <summary>Why it stopped.</summary>
    [Id(12)]
    public RevocationReason RevokedBecause { get; set; } = RevocationReason.None;

    /// <summary>How the subject authenticated. The <c>amr</c> claim.</summary>
    [Id(13)]
    public List<AuthenticationMethod> Methods { get; set; } = [];
}

/// <summary>
///     <c>SignUpGrain</c>'s hot-tier state — everything a self-serve sign-up holds before the tenant
///     exists. docs/plan/11 § Sign-up and tenant creation.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The code is not here, for the reason <see cref="OtpChallengeState" /> gives</b> — and
///         CC1005 would not have stopped it being here either, because <c>Code</c> is not one of the
///         four banned suffixes. What is stored is <see cref="Digest" />, the keyed digest
///         <c>OtpCodeProtector</c> produces under the silo's pepper, exactly as it is for a user's
///         challenge. <c>SignUpGrainTests.TheCodeIsNowhereInGrainState</c> is the same reflective
///         assertion <c>OtpIssuanceTests.TheCodeIsNowhereInGrainState</c> makes for a user.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Hot rather than durable, unlike <see cref="OtpChallengeState" />, and the difference
///             is what is lost.
///         </b> A user's burnt challenge must survive a hot-tier loss because a replay of an
///         observed code lets somebody into an account that exists. A sign-up that has not
///         completed owns nothing: losing it means the person starts again at the address step, and
///         a replayed enrolment code against a sign-up the tier has forgotten finds no grain to
///         answer it. So this is the same trade <see cref="SessionGrainState" /> makes for the same
///         reason.
///     </para>
///     <para>
///         ⚠ Every collection is <c>{ get; set; }</c> over a concrete type —
///         <see cref="UserGrainState" />'s remarks say what a get-only collection costs under
///         System.Text.Json.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.SignUpGrainState")]
public sealed class SignUpGrainState {
    /// <summary>The address being enrolled, normalized. Empty until <c>BeginAsync</c>.</summary>
    [Id(0)]
    public string Email { get; set; } = string.Empty;

    /// <summary>The tenant this sign-up will create. Allocated at <c>BeginAsync</c>.</summary>
    [Id(1)]
    public Guid TenantId { get; set; }

    /// <summary>The user that will own it. Allocated at <c>BeginAsync</c>.</summary>
    [Id(2)]
    public Guid UserId { get; set; }

    /// <summary>The default subscription's id. Allocated at <c>BeginAsync</c>.</summary>
    [Id(3)]
    public Guid SubscriptionId { get; set; }

    /// <summary>When the sign-up began.</summary>
    [Id(4)]
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the whole sign-up stops being resumable — <see cref="SignUpPolicy.Lifetime" /> after start.</summary>
    [Id(5)]
    public DateTimeOffset ExpiresAt { get; set; }

    /// <summary>
    ///     <c>OtpCodeProtector.Digest</c> of the outstanding enrolment code, or empty when there is
    ///     none. ⚠ Never the code — see the type's remarks.
    /// </summary>
    [Id(6)]
    public string Digest { get; set; } = string.Empty;

    /// <summary>When the outstanding code was issued. <see cref="OtpPolicy.ResendCooldown" /> is measured from here.</summary>
    [Id(7)]
    public DateTimeOffset ChallengeIssuedAt { get; set; }

    /// <summary>When the outstanding code stops being answerable. <see cref="OtpPolicy.Lifetime" /> after issue.</summary>
    [Id(8)]
    public DateTimeOffset ChallengeExpiresAt { get; set; }

    /// <summary>How many answers the outstanding code has taken. At <see cref="OtpPolicy.MaxAttempts" /> it is burnt.</summary>
    [Id(9)]
    public int Attempts { get; set; }

    /// <summary>When codes were sent, for <see cref="OtpPolicy.MaxIssuesPerWindow" /> — pruned as <see cref="UserGrainState.OtpIssuedAt" /> is.</summary>
    [Id(10)]
    public List<DateTimeOffset> OtpIssuedAt { get; set; } = [];

    /// <summary>Whether the address has been proven.</summary>
    [Id(11)]
    public bool Verified { get; set; }

    /// <summary>The create steps that have completed, in the order they ran.</summary>
    [Id(12)]
    public List<SignUpStep> CompletedSteps { get; set; } = [];
}

/// <summary>
///     <c>AuthorizationCodeGrain</c>'s hot-tier state — whether a code has been exchanged, and by
///     which token session. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     ⚠ <b>Never the code and never its verifier.</b> The grain is reached by the code's id, which
///     the server put inside the encrypted code; the record says only that the id was seen. A stored
///     code would be a stored bearer credential, in a tier that is still a database somebody can
///     read.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.AuthorizationCodeGrainState")]
public sealed class AuthorizationCodeGrainState {
    /// <summary>The token session the first exchange opened. <see cref="Guid.Empty" /> until then.</summary>
    [Id(0)]
    public Guid TokenSessionId { get; set; }

    /// <summary>When the first exchange happened.</summary>
    [Id(1)]
    public DateTimeOffset ConsumedAt { get; set; }

    /// <summary>When the record stops protecting anything — the code's own expiry plus a skew grace.</summary>
    [Id(2)]
    public DateTimeOffset ForgetAt { get; set; }
}

/// <summary>
///     <c>ConsentGrain</c>'s durable state — who consented to which client, for which scopes.
///     docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     The person and the client are here because the key is a digest of them
///     (<c>GrainKeys.ConsentGrant</c>) and cannot be read back; a repair tool or an audit export
///     reads them off the state. <see cref="Scopes" /> is <c>{ get; set; }</c> over a concrete list
///     for the reason <see cref="UserGrainState" /> gives.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ConsentGrainState")]
public sealed class ConsentGrainState {
    /// <summary>The person. <see cref="Guid.Empty" /> before the first grant.</summary>
    [Id(0)]
    public Guid UserId { get; set; }

    /// <summary>The <c>client_id</c>.</summary>
    [Id(1)]
    public string ClientId { get; set; } = string.Empty;

    /// <summary>Every scope allowed so far. Empty after a revocation.</summary>
    [Id(2)]
    public List<string> Scopes { get; set; } = [];

    /// <summary>When consent was first given.</summary>
    [Id(3)]
    public DateTimeOffset GrantedAt { get; set; }

    /// <summary>When the scope set last changed.</summary>
    [Id(4)]
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>Whether a grant is on record — false before the first grant and after a revocation.</summary>
    [Id(5)]
    public bool Granted { get; set; }
}
