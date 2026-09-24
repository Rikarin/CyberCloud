using CyberCloud.Core;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     One invitation of an address into a tenant — the member half of the M1 exit story's
///     <i>"invite a colleague and grant them Reader on one resource group"</i>. docs/plan/11 § Sign-up
///     and tenant creation, the invited path; issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>invite/{invitationId:N}</c>,
///         tenant-qualified. Build it with <c>GrainKeys.Invitation</c>.
///     </para>
///     <para>
///         ⚠ <b>The grain does the whole of an invitation, in the tenant it is for.</b>
///         <see cref="CreateAsync" /> claims the address in the tenant's email index, creates the
///         user in <see cref="UserStatus.Invited" />, records the invitation and mails the link
///         through <see cref="IInvitationDeliverySeam" />; <see cref="AcceptAsync" /> spends the
///         link and hands the name and password to <see cref="IUserGrain.AcceptInvitationAsync" />,
///         which makes the user <see cref="UserStatus.Active" />. Every one of those is a grain in the same tenant, so
///         none of them crosses the separation the platform keeps between tenants — and the one
///         step that is not here is the <b>check</b>: who may invite is the resource manager's
///         question (<c>IInvitationManager</c>, <c>assignRole</c> on the tenant), asked before this
///         grain is reached, for the reason docs/plan/10 § Request pipeline keeps exactly one
///         enforcement seam.
///     </para>
///     <para>
///         ⚠ <b>A member, and no role.</b> Accepting writes no tuple: the person can sign in and
///         see nothing until somebody grants them something, through <c>IRoleAssignmentManager</c>
///         like any other principal. <see cref="Invitation" />'s remarks say why the record lost the
///         relation it once carried.
///     </para>
///     <para>
///         ⚠ <b>The link is the proof of the address, and it is one-time.</b> It carries the
///         invitation id and a 256-bit secret whose SHA-256 is the only thing stored; the first
///         <see cref="AcceptAsync" /> spends it, and every later one — the same person clicking
///         again, or whoever the mail was forwarded to — is refused with a sentence that says it
///         was used. Possession of the link is what makes a password here safe to set without a
///         second code: it went to the address and nowhere else, as the enrolment code does at
///         sign-up.
///     </para>
///     <para>
///         ⚠ <b>A link opens an invited user and nothing else.</b> Its secret proves the address,
///         not the account's state: re-inviting reuses a user who is still invited, so two links
///         can name one user, and a pending link outlives a suspension or a deprovision of the
///         person it names. Once that user is anything but <see cref="UserStatus.Invited" /> the
///         invitation reads <see cref="InvitationStatus.Withdrawn" /> and accepting it changes
///         nothing — <see cref="IUserGrain.AcceptInvitationAsync" /> checks the status in the same
///         turn as it writes, so this holds against a race between two links too.
///     </para>
///     <para>
///         ⚠ <b>Resend mints a new link and kills the old one; revoke kills the link and keeps the
///         user.</b> Issue #41. The grain keeps one secret digest, so <see cref="ResendAsync" />
///         replaces it — the mail that went astray, or the one a colleague lost, opens nothing once
///         a new one is sent — and restarts the seven days. <see cref="RevokeAsync" /> makes the
///         invitation <see cref="InvitationStatus.Revoked" /> and leaves the invited user
///         <see cref="UserStatus.Invited" />, so inviting the address again reuses that user, as it
///         does after an expiry. Removing the person is the member API's, not this grain's.
///     </para>
///     <para>
///         ⚠ <b>Durable, because an invitation outlives a hot-tier flush.</b> It lives seven days
///         (<see cref="InvitationPolicy.Lifetime" />), is written a handful of times per colleague, and losing one
///         costs a person a dead link they were told would work — which is the shape docs/plan/05
///         § Choosing a tier sends to the durable tier.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IInvitationGrain")]
public interface IInvitationGrain : IGrainWithStringKey {
    /// <summary>
    ///     Creates the invitation: claims the address, creates the invited user, records the
    ///     invitation and mails the link.
    /// </summary>
    /// <param name="request">Who is invited, by whom, into which tenant's name.</param>
    /// <param name="secret">
    ///     The secret half of the link. ⚠ A parameter, digested here and never stored, as
    ///     <see cref="IDeviceAuthorizationGrain.BeginAsync" /> takes a device secret.
    /// </param>
    /// <returns>
    ///     The invitation; <see cref="ErrorCode.Conflict" /> when the address already belongs to a
    ///     member of the tenant or this id was used for another address;
    ///     <see cref="ErrorCode.InvalidRequestBody" /> for an address that is not one. A repeated call
    ///     with the same address and secret is the same invitation and mails the link again —
    ///     the message is idempotent on the invitation id, so a retry is not a second mail.
    /// </returns>
    /// <remarks>
    ///     ⚠ An address whose user is still <see cref="UserStatus.Invited" /> — a colleague whose last
    ///     link expired — is invited again onto the same user rather than refused, so re-inviting
    ///     is how an expired link is replaced.
    /// </remarks>
    Task<Result<Invitation>> CreateAsync(InvitationRequest request, string secret);

    /// <summary>What the invitation page shows — the address, the tenant, and whether it is still good.</summary>
    /// <param name="secret">The secret from the link.</param>
    /// <returns>
    ///     The invitation, with its status — <see cref="InvitationStatus.Withdrawn" /> once its user
    ///     is no longer invited; <see cref="ErrorCode.ResourceNotFound" /> for a secret that does not
    ///     match — the answer an id somebody guessed gets.
    /// </returns>
    Task<Result<Invitation>> DescribeAsync(string secret);

    /// <summary>
    ///     Accepts the invitation: spends the link, then makes the invited user an active member with
    ///     the name and password the person chose.
    /// </summary>
    /// <param name="secret">The secret from the link.</param>
    /// <param name="displayName">The name the person chose.</param>
    /// <param name="password">
    ///     The password for their user in this tenant. ⚠ A parameter handed straight to
    ///     <see cref="IUserGrain.AcceptInvitationAsync" />; nothing here stores or logs it.
    /// </param>
    /// <returns>
    ///     The accepted invitation; <see cref="ErrorCode.Conflict" /> when the link was used already
    ///     or is <see cref="InvitationStatus.Withdrawn" />, with the user untouched;
    ///     <see cref="ErrorCode.PreconditionFailed" /> when it expired; <see cref="ErrorCode.ResourceNotFound" />
    ///     when the secret does not match; <see cref="ErrorCode.InvalidRequestBody" /> for a password
    ///     the user grain refuses, with the link left unspent so the person can try again.
    /// </returns>
    Task<Result<Invitation>> AcceptAsync(string secret, string displayName, string password);

    /// <summary>The invitation as it stands, for its sender and for a test.</summary>
    Task<Result<Invitation>> GetAsync();

    /// <summary>
    ///     Mails the invitation again under a new link, which replaces the old one and runs a fresh
    ///     seven days. Issue #41.
    /// </summary>
    /// <param name="secret">
    ///     The new link's secret. ⚠ A parameter, digested here and never stored, as
    ///     <see cref="CreateAsync" /> takes one.
    /// </param>
    /// <returns>
    ///     The invitation; <see cref="ErrorCode.ResourceNotFound" /> for one never created;
    ///     <see cref="ErrorCode.Conflict" /> when it was accepted, revoked or withdrawn — nobody is
    ///     waiting for the link. An expired invitation can be resent: that's the point of resending.
    ///     ⚠ An expired one whose user is no longer invited reads withdrawn, not expired, so it is
    ///     refused like any other withdrawn one. <see cref="ErrorCode.QuotaExceeded" /> once it has
    ///     been sent <see cref="InvitationPolicy.MaxSendings" /> times.
    ///     A delivery failure is the seam's refusal, with the new link already in force.
    /// </returns>
    /// <remarks>
    ///     ⚠ The new secret is written before the mail goes. A failed mail leaves a link nobody
    ///     received, and the old one dead — the sender resends again. The other order could mail a
    ///     link the grain then failed to record, which is a mail that opens nothing.
    /// </remarks>
    Task<Result<Invitation>> ResendAsync(string secret);

    /// <summary>
    ///     Withdraws the invitation: its link opens nothing from now on. The invited user is left as
    ///     it is. Issue #41.
    /// </summary>
    /// <returns>
    ///     The invitation, now <see cref="InvitationStatus.Revoked" />, and the same answer for a
    ///     repeat; <see cref="ErrorCode.ResourceNotFound" /> for one never created;
    ///     <see cref="ErrorCode.Conflict" /> for one already accepted — the person is a member, and
    ///     removing a member is a different act.
    /// </returns>
    Task<Result<Invitation>> RevokeAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}

/// <summary>The numbers an invitation lives by.</summary>
public static class InvitationPolicy {
    /// <summary>How long an invitation link works — seven days, the span a colleague takes to open their mail.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(7);

    /// <summary>The page an invitation link opens, under the identity app's base address.</summary>
    public const string PagePath = "/invitation";

    /// <summary>
    ///     How many times one invitation is mailed, the first sending included. Past it,
    ///     <see cref="IInvitationGrain.ResendAsync" /> answers <see cref="ErrorCode.QuotaExceeded" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ A count, not an interval. Each resend mails an address outside the tenant, and before
    ///     #41's review nothing limited how often an owner could have the platform do it. An interval
    ///     needs a clock the suites can move, and the harnesses resend in the instant they invite.
    ///     Revoking and inviting again starts a new count, and that path is bounded by the
    ///     invitation list's <see cref="DirectoryIndexPolicy.MaxEntries" />. A per-tenant rate on
    ///     invitation mail is still owed (docs/plan/11).
    /// </remarks>
    public const int MaxSendings = 5;
}

/// <summary>What an invitation stands at.</summary>
[Alias("CyberCloud.Identity.InvitationStatus")]
public enum InvitationStatus {
    /// <summary>Sent and not yet used.</summary>
    Pending = 0,

    /// <summary>Used: the invitee is a member.</summary>
    Accepted = 1,

    /// <summary>
    ///     Past its seven days without being used, with its user still <see cref="UserStatus.Invited" />.
    ///     Otherwise it reads <see cref="Withdrawn" />, which is the more useful thing to know.
    /// </summary>
    Expired = 2,

    /// <summary>
    ///     Unused, and no longer usable: the user it names is not <see cref="UserStatus.Invited" />
    ///     any more — a member through another link, suspended, or deprovisioned.
    /// </summary>
    Withdrawn = 3,

    /// <summary>
    ///     Unused, and withdrawn by an owner through <see cref="IInvitationGrain.RevokeAsync" />.
    ///     Stored, unlike <see cref="Withdrawn" />, which is read from the user on every call.
    /// </summary>
    Revoked = 4
}

/// <summary>What <see cref="IInvitationGrain.CreateAsync" /> is asked to create.</summary>
/// <remarks>
///     ⚠ No expiry. The grain stamps <see cref="InvitationPolicy.Lifetime" /> from its own clock, as
///     the device authorization does, so the link's life doesn't depend on the skew between the
///     gateway and the silo, and no caller can ask for a link that never expires. The first cut took
///     the instant from the gateway's wall clock and accepted any future one. <c>[Id(3)]</c> was that
///     field, and no release carried it.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.InvitationRequest")]
public sealed record InvitationRequest {
    /// <summary>The address to invite, as typed. Normalized by the grain.</summary>
    [Id(0)]
    public string Email { get; init; } = string.Empty;

    /// <summary>The member who is inviting — the caller the resource manager checked.</summary>
    [Id(1)]
    public Guid InvitedBy { get; init; }

    /// <summary>The tenant's name for the mail and the page — its slug.</summary>
    [Id(2)]
    public string TenantName { get; init; } = string.Empty;
}

/// <summary>
///     Where an invitation's mail goes — the seam the grain hands the link to.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The default refuses, as <see cref="IOtpDeliverySeam" />'s does.</b> An invitation
///         that reported itself sent and mailed nothing would be a colleague told to expect a mail
///         that never comes, so a silo with no route answers the invite with a sentence naming the
///         configuration that is missing.
///     </para>
///     <para>
///         The production seam is <c>CommunicationInvitationDelivery</c>, which renders the
///         message and sends it through the platform's own communication service — docs/plan/17's
///         <i>"every OTP, alert, invitation and invoice goes through it"</i>.
///     </para>
/// </remarks>
public interface IInvitationDeliverySeam {
    /// <summary>Mails one invitation.</summary>
    /// <param name="delivery">What to send, and to whom.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> DeliverAsync(InvitationDelivery delivery, CancellationToken cancellationToken = default);
}

/// <summary>
///     One invitation on its way to one address.
/// </summary>
/// <remarks>
///     ⚠ <b>Not a wire type, for <see cref="OtpDelivery" />'s reason</b>: it carries the link's secret,
///     and a <c>[GenerateSerializer]</c> would make putting it in grain state a thing the compiler
///     helps with. Resolved in-process on the silo and never serialized.
/// </remarks>
public sealed record InvitationDelivery {
    /// <summary>The tenant the address is invited into.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The invitation — the link's first half, and the message's idempotency key.</summary>
    public required Guid InvitationId { get; init; }

    /// <summary>The address, unredacted — this is the delivery path.</summary>
    public required string Email { get; init; }

    /// <summary>The tenant's name, for the subject and the body.</summary>
    public required string TenantName { get; init; }

    /// <summary>The secret — the link's second half. ⚠ In the link and nowhere else.</summary>
    public required string Secret { get; init; }

    /// <summary>When the link stops working, for the body.</summary>
    public required DateTimeOffset ExpiresAt { get; init; }

    /// <summary>
    ///     Which mail of this invitation this is — 1 for the first, one more for every resend.
    /// </summary>
    /// <remarks>
    ///     ⚠ Part of the message's idempotency key from the second mail on. The key was the
    ///     invitation id alone, which is right for a retry of one mail and wrong for a resend: the
    ///     communication service would answer the resend with the first mail's receipt and send
    ///     nothing.
    /// </remarks>
    public int Sending { get; init; } = 1;
}
