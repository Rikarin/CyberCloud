namespace CyberCloud.Identity;

/// <summary>
///     <c>UserGrain</c>'s durable state. docs/plan/11 § The object model.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Every collection here is <c>{ get; set; }</c> over a concrete type, and that is a
///         trap rather than a style choice.</b> The grain-storage serializer is System.Text.Json
///         (docs/plan/02 § Orleans and hosting), and STJ <b>does not populate a get-only
///         collection</b>: a <c>{ get; }</c> property with an initializer deserializes as whatever
///         the initializer produced, silently discarding what was stored. For this type that would
///         mean a user whose passkeys and recovery codes vanish on the first reactivation — which
///         looks exactly like "somebody deleted my credentials" and is not reproducible in memory.
///     </para>
///     <para>
///         ⚠ <b>What is deliberately absent:</b> the TOTP shared secret (a
///         <see cref="VaultSecretRef" /> instead — docs/plan/11 § Credentials), the recovery codes in
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
///         ⚠ <b>THERE IS NO CREDENTIAL HERE AND THERE IS NOWHERE TO PUT ONE, WHICH IS THE ENTIRE
///         FEATURE.</b> docs/plan/11 § Managed identity calls this "the feature that removes stored
///         secrets" and justifies 1.2 EM with "no secret is ever stored, on either side … it removes
///         an entire incident class". Compare the three states above it: <c>UserGrainState</c> holds
///         hashes and a vault handle, <c>ApplicationGrainState</c> and
///         <c>ServicePrincipalGrainState</c> hold a <see cref="VaultSecretRef" />. This holds a
///         binding — <i>who</i> may present a token — and a public key set. A stolen backup of this
///         row lets an attacker learn which namespace a workload runs in, and nothing else.
///     </para>
///     <para>
///         ⚠ <b>CC1005 does not fire on any member here, and that is a result rather than an
///         accident.</b> The analyzer bans <c>[Id]</c> members named <c>*Password</c>, <c>*Secret</c>,
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
