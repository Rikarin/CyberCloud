using CyberCloud.Core;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     One RFC 8628 device authorization — the codes <c>cyc login</c> on a headless box holds, and
///     the answer the person gives on another machine. docs/plan/11 § Protocol.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> <b>Hot</b> · <b>Key</b> <c>device/{digest}</c>,
///         qualified by the <b>platform</b> tenant. Build it with <c>GrainKeys.DeviceAuthorization</c>
///         from the normalized user code.
///     </para>
///     <para>
///         ⚠ <b>The store degraded mode takes away, as a grain.</b> A device code and a user code
///         are the two tokens OpenIddict cannot make self-contained — a person types the user code
///         into a page, so something has to map it back — and in degraded mode OpenIddict has no
///         token store to do it. This grain is that store: the identity host's
///         <c>DegradedModeHandlers.StoreDeviceCodes</c> writes it when the codes are generated,
///         the verification page reads and answers it through the user code, and the token
///         endpoint polls and redeems it through the device code.
///     </para>
///     <para>
///         ⚠ <b>The device secret is never stored.</b> Only its SHA-256 is, for the reason
///         <see cref="ISessionGrain" /> keeps only a refresh handle's: a hot tier is still a
///         database somebody can read, and a stored device secret is a stored bearer credential
///         for as long as the flow is live. The user code is not stored either — the key is its
///         digest.
///     </para>
///     <para>
///         ⚠ <b>Everything RFC 8628 § 3.5 answers a poll with is decided here, in one grain turn.</b>
///         <c>authorization_pending</c>, <c>slow_down</c> (and the five seconds it adds to the
///         interval for every later poll), <c>access_denied</c> and <c>expired_token</c> are
///         <see cref="DevicePollOutcome" /> members, and the interval is state rather than a
///         constant so a device that was told to slow down stays slowed.
///     </para>
///     <para>
///         ⚠ <b>Hot, because the whole record is worth ten minutes.</b> Losing the tier mid-flow
///         costs the person one more <c>cyc login</c>; nothing here survives the codes' expiry, and
///         the grain clears itself then. docs/plan/05 § Hot's "session-shaped state, bounded by
///         concurrent activity".
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IDeviceAuthorizationGrain")]
public interface IDeviceAuthorizationGrain : IGrainWithStringKey {
    /// <summary>
    ///     Records a new device authorization under this user code.
    /// </summary>
    /// <param name="request">What the device asked for.</param>
    /// <param name="deviceSecret">
    ///     The secret half of the device code. ⚠ A parameter and not a member of
    ///     <paramref name="request" />: it is digested here and never serialized into anything, as
    ///     <see cref="ISessionGrain.RefreshAsync" /> takes a refresh handle.
    /// </param>
    /// <returns>
    ///     Success once recorded; <see cref="ErrorCode.Conflict" /> when a live authorization already
    ///     holds this user code — the host draws another code and tries again, so two devices never
    ///     share one.
    /// </returns>
    Task<Result> BeginAsync(DeviceAuthorizationRequest request, string deviceSecret);

    /// <summary>What the verification page shows about the request behind a user code.</summary>
    /// <returns>
    ///     The descriptor, including a decided status; <see cref="ErrorCode.ResourceNotFound" /> when
    ///     no authorization holds this code or it has expired — one answer for both, so the page
    ///     cannot tell a guess from a code that timed out.
    /// </returns>
    Task<Result<DeviceAuthorizationDescriptor>> DescribeAsync();

    /// <summary>
    ///     The person's answer: approve for the person described, or deny.
    /// </summary>
    /// <param name="approval">Who approved and how they signed in, or <see langword="null" /> to deny.</param>
    /// <returns>
    ///     The descriptor in its new status; <see cref="ErrorCode.Conflict" /> when the code was
    ///     already answered — a user code is used once — and <see cref="ErrorCode.ResourceNotFound" />
    ///     when it has expired.
    /// </returns>
    Task<Result<DeviceAuthorizationDescriptor>> DecideAsync(DeviceApproval? approval);

    /// <summary>
    ///     A poll from the device — RFC 8628 § 3.4, answered per § 3.5.
    /// </summary>
    /// <param name="deviceSecret">The secret half of the device code, verbatim.</param>
    /// <returns>
    ///     Always success; the answer is <see cref="DevicePoll.Outcome" />. A secret that does not
    ///     match is <see cref="DevicePollOutcome.Unknown" /> and changes nothing — a guess must not
    ///     move the interval of the device it guessed at.
    /// </returns>
    Task<Result<DevicePoll>> PollAsync(string deviceSecret);

    /// <summary>
    ///     Redeems an approved authorization for the token session about to be opened for it.
    /// </summary>
    /// <param name="deviceSecret">The secret half of the device code.</param>
    /// <param name="tokenSessionId">The session the caller is about to open — recorded before it exists.</param>
    /// <returns>
    ///     <see cref="DeviceRedemption.FirstUse" /> <c>true</c> exactly once, with the approval; every
    ///     later call <c>false</c>, carrying the session the first redemption recorded so the caller
    ///     can revoke it, which is <c>IAuthorizationCodeGrain</c>'s rule applied to the device code.
    ///     A failure when the authorization is not approved or the secret does not match.
    /// </returns>
    Task<Result<DeviceRedemption>> RedeemAsync(string deviceSecret, Guid tokenSessionId);

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}

/// <summary>Where a device authorization stands.</summary>
[Alias("CyberCloud.Identity.DeviceAuthorizationStatus")]
public enum DeviceAuthorizationStatus {
    /// <summary>Waiting for the person to answer on the verification page.</summary>
    Pending = 0,

    /// <summary>The person allowed it; the device's next poll gets tokens.</summary>
    Approved = 1,

    /// <summary>The person refused it; the device hears <c>access_denied</c>.</summary>
    Denied = 2,

    /// <summary>The device collected its tokens. The device code is spent.</summary>
    Redeemed = 3
}

/// <summary>
///     What a poll answers — one member per response RFC 8628 § 3.5 defines, plus the two a
///     device code can meet that the RFC leaves to RFC 6749's <c>invalid_grant</c>.
/// </summary>
[Alias("CyberCloud.Identity.DevicePollOutcome")]
public enum DevicePollOutcome {
    /// <summary>No authorization under this code, or the secret does not match. <c>invalid_grant</c>.</summary>
    Unknown = 0,

    /// <summary><c>authorization_pending</c> — nobody has answered yet.</summary>
    Pending = 1,

    /// <summary><c>slow_down</c> — polled inside the interval, which is now five seconds longer.</summary>
    SlowDown = 2,

    /// <summary><c>access_denied</c> — the person said no.</summary>
    Denied = 3,

    /// <summary><c>expired_token</c> — the codes' lifetime is over.</summary>
    Expired = 4,

    /// <summary>The person said yes; tokens may be minted.</summary>
    Approved = 5,

    /// <summary>
    ///     Tokens were already collected. Carried to <see cref="IDeviceAuthorizationGrain.RedeemAsync" />
    ///     anyway, with the approval, so the replay revokes the first redemption's session and is
    ///     answered <c>invalid_grant</c> there.
    /// </summary>
    Redeemed = 6
}

/// <summary>What the device asked for, as the identity host hands it to <see cref="IDeviceAuthorizationGrain.BeginAsync" />.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.DeviceAuthorizationRequest")]
public sealed record DeviceAuthorizationRequest {
    /// <summary>The <c>client_id</c> that asked — <c>cyc-cli</c>, today the only client allowed the grant.</summary>
    [Id(0)]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>The scopes asked for, already cut to what the client may have.</summary>
    [Id(1)]
    public List<string> Scopes { get; init; } = [];

    /// <summary>
    ///     How long both codes work — the <c>expires_in</c> the device was told. ⚠ A duration and
    ///     not an instant, measured from the grain's clock when it begins: the host and the silo are
    ///     two machines, and the grain already measures the polling interval, so it measures this
    ///     too. An instant stamped by the host would make every code's life depend on the skew
    ///     between the two.
    /// </summary>
    [Id(2)]
    public TimeSpan Lifetime { get; init; }

    /// <summary>The polling interval the device was told — RFC 8628 § 3.2's <c>interval</c>.</summary>
    [Id(3)]
    public TimeSpan Interval { get; init; }
}

/// <summary>A device authorization as the verification page sees it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.DeviceAuthorizationDescriptor")]
public sealed record DeviceAuthorizationDescriptor {
    /// <summary>The client that asked. The page renders its <i>registered</i> name, never this string.</summary>
    [Id(0)]
    public string ClientId { get; init; } = string.Empty;

    /// <summary>The scopes it asked for.</summary>
    [Id(1)]
    public List<string> Scopes { get; init; } = [];

    /// <summary>When the codes expire.</summary>
    [Id(2)]
    public DateTimeOffset ExpiresAt { get; init; }

    /// <summary>Where it stands.</summary>
    [Id(3)]
    public DeviceAuthorizationStatus Status { get; init; }
}

/// <summary>
///     Who approved a device, and how they had signed in — everything the token endpoint needs to
///     open a token session for the device without the person's cookie.
/// </summary>
/// <remarks>
///     ⚠ Copied off the interactive session when the person answered, because the device's poll
///     arrives from another machine with no cookie at all. <see cref="AuthenticatedAt" /> and
///     <see cref="Methods" /> are the sign-in's, not the approval's, so a step-up rule downstream reads
///     how the person really authenticated.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.DeviceApproval")]
public sealed record DeviceApproval {
    /// <summary>The tenant the person signed into, which becomes the tokens' <c>tid</c>.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The person.</summary>
    [Id(1)]
    public Guid UserId { get; init; }

    /// <summary>The cookie session they answered from, for the audit trail.</summary>
    [Id(2)]
    public Guid InteractiveSessionId { get; init; }

    /// <summary>When that session authenticated — the tokens' <c>auth_time</c>.</summary>
    [Id(3)]
    public DateTimeOffset AuthenticatedAt { get; init; }

    /// <summary>How it authenticated — the tokens' <c>amr</c>.</summary>
    [Id(4)]
    public List<AuthenticationMethod> Methods { get; init; } = [];

    /// <summary>The person's address, for the id_token.</summary>
    [Id(5)]
    public string Email { get; init; } = string.Empty;

    /// <summary>The person's display name, for the id_token.</summary>
    [Id(6)]
    public string DisplayName { get; init; } = string.Empty;
}

/// <summary><see cref="IDeviceAuthorizationGrain.PollAsync" />'s answer.</summary>
/// <param name="Outcome">What to tell the device.</param>
/// <param name="Interval">The interval now in force — longer after every <see cref="DevicePollOutcome.SlowDown" />.</param>
/// <param name="ClientId">The client the authorization was issued to, when the secret matched.</param>
/// <param name="Scopes">The scopes it carries, when the secret matched.</param>
/// <param name="Approval">
///     Who approved, for <see cref="DevicePollOutcome.Approved" /> and <see cref="DevicePollOutcome.Redeemed" />.
/// </param>
[GenerateSerializer]
[Alias("CyberCloud.Identity.DevicePoll")]
public sealed record DevicePoll(
    [property: Id(0)]
    DevicePollOutcome Outcome,
    [property: Id(1)]
    TimeSpan Interval,
    [property: Id(2)]
    string ClientId,
    [property: Id(3)]
    List<string> Scopes,
    [property: Id(4)]
    DeviceApproval? Approval
);

/// <summary><see cref="IDeviceAuthorizationGrain.RedeemAsync" />'s answer.</summary>
/// <param name="FirstUse">Whether this call redeemed it.</param>
/// <param name="TokenSessionId">The session the first redemption recorded.</param>
/// <param name="Approval">Who approved.</param>
/// <param name="Scopes">The scopes granted.</param>
[GenerateSerializer]
[Alias("CyberCloud.Identity.DeviceRedemption")]
public sealed record DeviceRedemption(
    [property: Id(0)]
    bool FirstUse,
    [property: Id(1)]
    Guid TokenSessionId,
    [property: Id(2)]
    DeviceApproval Approval,
    [property: Id(3)]
    List<string> Scopes
);
