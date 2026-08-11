using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     A human. docs/plan/11 § The object model.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>user/{userId:N}</c>,
///         tenant-qualified. Build it with <c>GrainKeys.User</c>.
///     </para>
///     <para>
///         ⚠ <b>One user, one tenant, forever.</b> docs/plan/11 § Sign-up and tenant creation: the
///         same human with accounts in two tenants has two user objects with two GUIDs and probably
///         the same email. That is Azure's guest-user problem and Azure's answer (B2B guests) is
///         complicated; M1's answer is the simple one, because "the same user in two tenants" needs a
///         <i>global</i> user index, which is the thing docs/plan/05 § The two tiers is specifically
///         arranged not to have. The portal's account switcher is a client-side list of tokens.
///     </para>
///     <para>
///         ⚠ <b>This grain holds credential material and the wire types do not.</b> Password hash,
///         recovery-code hashes and passkey public keys live in its state; the TOTP secret does not,
///         only a <see cref="VaultSecretRef" /> to it. Nothing on this interface returns a hash — the
///         verify methods take a candidate and answer a Boolean, which keeps the comparison
///         (constant-time, with the pepper) on the one side that knows how to do it.
///     </para>
///     <para>
///         ⚠ <b>Group membership is not here.</b> "Which groups is this user in" is an <c>Expand</c>
///         over ReBAC, not a field — docs/plan/11 § The object model. Adding a convenience
///         <c>GetGroupsAsync</c> that walked the tuples would be harmless once and a cached second
///         source of truth by the third caller.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IUserGrain")]
public interface IUserGrain : IGrainWithStringKey {
    /// <summary>
    ///     Creates the user. Step 2 of the sign-up flow, after the email index claim.
    /// </summary>
    /// <param name="email">The address. Re-normalized here rather than trusted.</param>
    /// <param name="displayName">What to call them.</param>
    /// <param name="status">
    ///     <see cref="UserStatus.Invited" /> for the invited path, <see cref="UserStatus.Active" />
    ///     for self-serve once the address is verified.
    /// </param>
    /// <remarks>
    ///     ⚠ Idempotent for the same address, which is what makes the sign-up flow re-drivable after
    ///     a silo loss between the index claim and this call. A <i>different</i> address on an
    ///     existing user is a <see cref="ErrorCode.Conflict" />, not a rename — renaming an address
    ///     has to move the index claim and is <see cref="ChangeEmailAsync" />.
    /// </remarks>
    Task<Result<UserProfile>> CreateAsync(string email, string displayName, UserStatus status);

    /// <summary>The profile, or <see cref="ErrorCode.ResourceNotFound" />.</summary>
    Task<Result<UserProfile>> GetAsync();

    /// <summary>Moves the user between lifecycle states.</summary>
    /// <param name="status">The new status.</param>
    /// <remarks>
    ///     ⚠ Suspending or deprovisioning revokes every session the user holds, and does so before
    ///     returning. An administrator who suspends an account and sees a success has been told the
    ///     account cannot act, and a session that outlived the call would make that a lie for up to
    ///     the refresh lifetime.
    /// </remarks>
    Task<Result<UserProfile>> SetStatusAsync(UserStatus status);

    /// <summary>Rebinds the user to a new address, after the caller has moved the index claim.</summary>
    /// <param name="email">The new address, normalized.</param>
    Task<Result<UserProfile>> ChangeEmailAsync(string email);

    // ── Credentials ────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Sets the password. The caller passes the plaintext and it is hashed here.
    /// </summary>
    /// <param name="candidate">The new password.</param>
    /// <remarks>
    ///     ⚠ <b>Hashing happens in the grain, not at the caller.</b> The Argon2id parameters, the
    ///     salt and the pepper are one decision; splitting them across a caller and a grain is how
    ///     two call sites end up hashing with different parameters and neither can verify the
    ///     other's. The cost is that the plaintext crosses one grain call, inside the cluster,
    ///     which is the same trust boundary the session cookie already crossed to get here.
    ///     <para>
    ///         Setting a password revokes every existing session — docs/plan/11 § Sessions and
    ///         revocation lists "password change" among the things that invalidate a refresh chain.
    ///     </para>
    /// </remarks>
    Task<Result> SetPasswordAsync(string candidate);

    /// <summary>
    ///     Whether <paramref name="candidate" /> is this user's password.
    /// </summary>
    /// <param name="candidate">What was typed.</param>
    /// <returns>
    ///     <c>true</c> when the password verifies. ⚠ A user with <i>no</i> password enrolled also
    ///     returns <c>false</c>, and takes the same time doing it.
    /// </returns>
    Task<Result<bool>> VerifyPasswordAsync(string candidate);

    /// <summary>Enrols a passkey. docs/plan/11 § Credentials — the default credential.</summary>
    /// <param name="credential">The verified attestation result, already checked by the host.</param>
    Task<Result<UserProfile>> AddPasskeyAsync(PasskeyCredential credential);

    /// <summary>The user's enrolled passkeys, so the host can build an assertion challenge.</summary>
    Task<Result<IReadOnlyList<PasskeyCredential>>> ListPasskeysAsync();

    /// <summary>
    ///     Records a successful assertion against a passkey and advances its signature counter.
    /// </summary>
    /// <param name="credentialId">Which credential asserted, base64url.</param>
    /// <param name="signCount">The counter the authenticator reported.</param>
    /// <returns>
    ///     <c>true</c> when the credential is enrolled and the counter did not go backwards from a
    ///     non-zero value. A <c>false</c> here is the cloned-authenticator signal.
    /// </returns>
    Task<Result<bool>> RecordPasskeyAssertionAsync(string credentialId, uint signCount);

    /// <summary>Removes a passkey.</summary>
    /// <param name="credentialId">Which one, base64url.</param>
    Task<Result<UserProfile>> RemovePasskeyAsync(string credentialId);

    /// <summary>
    ///     Binds a TOTP enrolment. The secret itself is already in the vault at
    ///     <see cref="TotpEnrollment.SecretRef" />.
    /// </summary>
    /// <param name="enrollment">The handle and parameters.</param>
    Task<Result<UserProfile>> EnrollTotpAsync(TotpEnrollment enrollment);

    /// <summary>The TOTP enrolment, so the verifier can resolve the secret at the data plane.</summary>
    Task<Result<TotpEnrollment>> GetTotpAsync();

    /// <summary>
    ///     Claims a TOTP counter as used, so the same code cannot be replayed within its window.
    /// </summary>
    /// <param name="counter">The RFC 6238 step number the accepted code was generated for.</param>
    /// <returns>
    ///     <c>true</c> when this counter had not been used. ⚠ A <c>false</c> means the code was
    ///     valid <i>and</i> already spent — which is a replay, and must fail the sign-in.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>The replay block is per (user, counter) and it lives here rather than in the hot
    ///     tier</b>, unlike the lockout counter. The two look similar and are not: losing a lockout
    ///     count costs an attacker a few extra guesses, and losing a spent-counter record lets a
    ///     code observed over someone's shoulder be replayed for up to 90 seconds. docs/plan/11
    ///     § Credentials asks for "replay-blocked per (user, counter)" without saying where; zero
    ///     loss tolerance puts it in the durable tier.
    /// </remarks>
    Task<Result<bool>> ClaimTotpCounterAsync(long counter);

    /// <summary>
    ///     Mints a fresh batch of recovery codes, invalidating any previous batch.
    /// </summary>
    /// <returns>The plaintext codes — the only time they exist. The grain keeps hashes.</returns>
    Task<Result<RecoveryCodeBatch>> GenerateRecoveryCodesAsync();

    /// <summary>Burns a recovery code.</summary>
    /// <param name="candidate">What was typed.</param>
    /// <returns><c>true</c> when the code matched an unburnt hash, which it now no longer does.</returns>
    Task<Result<bool>> RedeemRecoveryCodeAsync(string candidate);

    // ── Sessions ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Records that a session was opened for this user.</summary>
    /// <param name="sessionId">The session.</param>
    Task<Result> TrackSessionAsync(Guid sessionId);

    /// <summary>Every session id this user has open. What "sign out everywhere" iterates.</summary>
    Task<Result<IReadOnlyList<Guid>>> ListSessionsAsync();

    /// <summary>Forgets a session that is no longer live.</summary>
    /// <param name="sessionId">The session.</param>
    Task<Result> ForgetSessionAsync(Guid sessionId);

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     A group. docs/plan/11 § The object model — <b>and the membership is not in it</b>.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>group/{groupId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>Read the membership methods carefully: they write and read ReBAC tuples, they do not
///         maintain a list.</b> docs/plan/11 § The object model is explicit that a member list in
///         grain state "would be a second source of truth and a hot spot for large groups". What this
///         grain owns is the group's <i>identity</i> — name, description, who created it. Its
///         membership surface is a thin, honest façade over
///         <see cref="ITupleStoreGrain" /> and <see cref="ICheckGrain" />, and it exists so that a
///         caller does not have to know the tuple spelling to add somebody to a group.
///     </para>
///     <para>
///         What the façade buys, and it is the argument for the whole design: <b>nesting is free</b>.
///         <see cref="AddMemberAsync" /> takes a <see cref="SubjectRef" />, so
///         <c>group:platform#member</c> is as valid a member as <c>user:alice</c>, and the evaluator
///         walks it with no code here. <b>Revoking is a tuple delete</b> rather than a rewrite of a
///         list somebody else may be holding. And <b>"who is in this group"</b> is an
///         <c>Expand</c>, which is the operation that stays correct when the answer is a million
///         subjects and a field would not be.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IGroupGrain")]
public interface IGroupGrain : IGrainWithStringKey {
    /// <summary>Creates the group.</summary>
    /// <param name="name">The display name.</param>
    /// <param name="description">What it is for.</param>
    Task<Result<GroupDescriptor>> CreateAsync(string name, string description);

    /// <summary>The group's identity.</summary>
    Task<Result<GroupDescriptor>> GetAsync();

    /// <summary>Renames it. Free, because the key is a GUID.</summary>
    /// <param name="name">The new display name.</param>
    /// <param name="description">The new description.</param>
    Task<Result<GroupDescriptor>> RenameAsync(string name, string description);

    /// <summary>
    ///     Writes <c>group:{this}#member@{subject}</c>.
    /// </summary>
    /// <param name="subject">
    ///     Who joins. A concrete <c>user:{id}</c>, a <c>servicePrincipal:{id}</c>, or the userset
    ///     <c>group:{id}#member</c> — the last of which is what nesting <i>is</i>.
    /// </param>
    Task<Result> AddMemberAsync(SubjectRef subject);

    /// <summary>Deletes the membership tuple. Takes effect at the next check.</summary>
    /// <param name="subject">Who leaves.</param>
    Task<Result> RemoveMemberAsync(SubjectRef subject);

    /// <summary>
    ///     Whether <paramref name="subject" /> is a member, directly or through nesting.
    /// </summary>
    /// <param name="subject">Who to ask about.</param>
    /// <param name="consistency">
    ///     How fresh the answer has to be. Defaults to
    ///     <see cref="ConsistencyMode.MinimizeLatency" /> when null.
    /// </param>
    /// <returns><c>true</c> when the ReBAC check on <c>member</c> allows.</returns>
    /// <remarks>
    ///     ⚠ <b>The parameter is not decoration, and the default will surprise you.</b> The default
    ///     mode takes any cached result however stale (docs/plan/07 § Consistency, row 1), so a
    ///     membership check issued immediately after <see cref="AddMemberAsync" /> can legitimately
    ///     answer <c>false</c> — the tuple is written and the check grain's cache is not yet aware of
    ///     it. That is correct behaviour for a list view and wrong for anything that decides access,
    ///     which is why the mode is the caller's to choose rather than fixed here.
    ///     <para>
    ///         Pass <see cref="Consistency.AtLeastAsFresh" /> with the token
    ///         <see cref="AddMemberAsync" />'s write returned when you need read-your-writes, or
    ///         <see cref="Consistency.FullyConsistent" /> for anything destructive.
    ///     </para>
    /// </remarks>
    Task<Result<bool>> IsMemberAsync(SubjectRef subject, Consistency? consistency);

    /// <summary>Deletes the group and, with it, every tuple that names it as an object.</summary>
    /// <remarks>
    ///     ⚠ Deleting a group whose <c>member</c> userset is a subject of role assignments elsewhere
    ///     silently removes those grants. That is the correct behaviour and it is the reason the
    ///     portal must show what a group grants before offering to delete it.
    /// </remarks>
    Task<Result> DeleteAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     An OAuth client registration. docs/plan/11 § The object model, § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>app/{applicationId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         This is the grain OpenIddict's application store reads through. ADR-015's whole point is
///         that "OpenIddict is a library: it handles the protocol, we own the stores, and the stores
///         are grains" — so the store adapter in the identity host is thin and this is where the data
///         actually lives.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IApplicationGrain")]
public interface IApplicationGrain : IGrainWithStringKey {
    /// <summary>Registers the client.</summary>
    /// <param name="registration">
    ///     The registration. <see cref="ApplicationRegistration.ApplicationId" /> and
    ///     <see cref="ApplicationRegistration.TenantId" /> are taken from the grain key, so a
    ///     mismatch is rejected rather than silently preferred.
    /// </param>
    Task<Result<ApplicationRegistration>> CreateAsync(ApplicationRegistration registration);

    /// <summary>The registration.</summary>
    Task<Result<ApplicationRegistration>> GetAsync();

    /// <summary>Replaces the mutable parts — redirect URIs, scopes, grants, display name.</summary>
    /// <param name="registration">The new values.</param>
    Task<Result<ApplicationRegistration>> UpdateAsync(ApplicationRegistration registration);

    /// <summary>
    ///     Whether this client may use <paramref name="grant" />.
    /// </summary>
    /// <param name="grant">The grant being attempted.</param>
    /// <returns><c>true</c> when the registration allows it.</returns>
    Task<Result<bool>> AllowsGrantAsync(GrantType grant);

    /// <summary>
    ///     Whether <paramref name="redirectUri" /> is one of the registered URIs.
    /// </summary>
    /// <param name="redirectUri">The URI the authorization request asked to be sent back to.</param>
    /// <returns>
    ///     <c>true</c> only on a whole-string ordinal match. ⚠ Never a prefix match — that is the
    ///     open redirect that hands an authorization code to whoever asked.
    /// </returns>
    Task<Result<bool>> IsRegisteredRedirectUriAsync(string redirectUri);

    /// <summary>Deletes the registration.</summary>
    Task<Result> DeleteAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     A machine identity with client credentials or a certificate. docs/plan/11 § The object model.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>sp/{servicePrincipalId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>A service principal is the thing managed identity exists to replace.</b>
///         docs/plan/11 § Managed identity calls a client secret in a Kubernetes <c>Secret</c> "the
///         bad answer". This grain is the good-enough answer for CI and for third-party integrations
///         that cannot present a projected service-account token; a workload inside a tenant cluster
///         should be a managed identity instead — see <see cref="IManagedIdentityGrain" />, which
///         holds no credential at all rather than a handle to one.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IServicePrincipalGrain")]
public interface IServicePrincipalGrain : IGrainWithStringKey {
    /// <summary>Creates the principal.</summary>
    /// <param name="descriptor">The principal. Ids come from the grain key.</param>
    Task<Result<ServicePrincipalDescriptor>> CreateAsync(ServicePrincipalDescriptor descriptor);

    /// <summary>The principal.</summary>
    Task<Result<ServicePrincipalDescriptor>> GetAsync();

    /// <summary>Turns authentication on or off without deleting anything.</summary>
    /// <param name="enabled">Whether it may authenticate.</param>
    Task<Result<ServicePrincipalDescriptor>> SetEnabledAsync(bool enabled);

    /// <summary>Points the principal at a new secret in the vault.</summary>
    /// <param name="credentialSecretRef">Where the new secret lives.</param>
    /// <remarks>
    ///     ⚠ Rotation is two writes and this is the second. The vault write happens first, so a
    ///     crash between them leaves an unused secret in the vault rather than a principal pointing
    ///     at nothing.
    /// </remarks>
    Task<Result<ServicePrincipalDescriptor>> RotateCredentialAsync(VaultSecretRef credentialSecretRef);

    /// <summary>Deletes the principal.</summary>
    Task<Result> DeleteAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     A sign-in session and its refresh chain. docs/plan/11 § Sessions and revocation.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> <b>Hot</b> · <b>Key</b> <c>session/{sessionId:N}</c>,
///         tenant-qualified.
///     </para>
///     <para>
///         ⚠ <b>Hot, and it is the only identity grain that is.</b> docs/plan/05 § Hot lists
///         "sessions" among what the hot tier holds. Losing a session costs a sign-in, which is a
///         warm-up rather than a loss — and putting sessions in the durable tier would put the
///         highest-write-rate object in the module on the tier whose loss tolerance is zero and whose
///         writes are synchronously replicated.
///     </para>
///     <para>
///         ⚠ <b>The refresh handle is never stored.</b> Only its SHA-256 is. A hot tier is still a
///         database somebody can read, and a stored refresh handle is a stored bearer credential.
///         Comparison is over the digest, which is why <see cref="RefreshAsync" /> takes a handle
///         rather than returning one to compare.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.ISessionGrain")]
public interface ISessionGrain : IGrainWithStringKey {
    /// <summary>
    ///     Opens the session and mints generation 1 of its refresh chain.
    /// </summary>
    /// <param name="userId">Who signed in.</param>
    /// <param name="clientId">Which client they signed in to.</param>
    /// <param name="deviceLabel">Something the user will recognise in a device list.</param>
    /// <param name="clientAddressDigest">
    ///     The truncated digest of the originating address — never the address, see
    ///     <see cref="SessionDescriptor.ClientAddressDigest" />.
    /// </param>
    /// <param name="methods">How they authenticated. Becomes the <c>amr</c> claim.</param>
    Task<Result<RefreshRotation>> OpenAsync(
        Guid userId,
        string clientId,
        string deviceLabel,
        string clientAddressDigest,
        IReadOnlyList<AuthenticationMethod> methods
    );

    /// <summary>
    ///     Rotates the refresh chain: retires <paramref name="presented" /> and mints the next
    ///     generation.
    /// </summary>
    /// <param name="presented">The handle the client sent.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE REUSE RULE, AND IT REVOKES THE CHAIN.</b> docs/plan/11 § Protocol: refresh
    ///         tokens are "rotating, one-time-use, with reuse detection → revoke the whole chain".
    ///         Three cases, and only the first succeeds:
    ///     </para>
    ///     <list type="bullet">
    ///         <item>
    ///             <paramref name="presented" /> hashes to the <i>current</i> handle — rotate, return
    ///             the next generation.
    ///         </item>
    ///         <item>
    ///             It hashes to a handle this chain has <i>already retired</i> — that is a replay.
    ///             The session is revoked with
    ///             <see cref="RevocationReason.RefreshReuseDetected" />, every generation dies
    ///             including the one the legitimate client is holding, and the call fails.
    ///         </item>
    ///         <item>It hashes to nothing this chain ever issued — the call fails and nothing else happens.</item>
    ///     </list>
    ///     <para>
    ///         The second case is the one worth being sure about. The replayed token and the live
    ///         token cannot be told apart by who sent them, so the only safe move is to assume the
    ///         chain is compromised. Revoking only the replayed generation would leave the attacker
    ///         holding a working session in exactly the case where they stole one.
    ///     </para>
    /// </remarks>
    Task<Result<RefreshRotation>> RefreshAsync(string presented);

    /// <summary>The session as a device list sees it.</summary>
    Task<Result<SessionDescriptor>> GetAsync();

    /// <summary>Whether the session still authenticates anything, cheaply.</summary>
    /// <returns><c>true</c> while the session is live and inside its absolute lifetime.</returns>
    Task<Result<bool>> IsLiveAsync();

    /// <summary>Revokes the session and its whole chain.</summary>
    /// <param name="reason">Why, for the audit event.</param>
    Task<Result> RevokeAsync(RevocationReason reason);

    /// <summary>
    ///     How many generations this chain has issued, live and retired.
    /// </summary>
    /// <remarks>
    ///     Present so the reuse-detection tests can assert on the chain rather than on one request —
    ///     "the whole chain died" is not observable from a single failed refresh.
    /// </remarks>
    Task<Result<int>> ChainLengthAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}
