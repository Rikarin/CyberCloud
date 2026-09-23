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
///         link, names the person, sets their password and makes them
///         <see cref="UserStatus.Active" />. Every one of those is a grain in the same tenant, so
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
    ///     The invitation, with its status; <see cref="ErrorCode.ResourceNotFound" /> for a secret
    ///     that does not match — the answer an id somebody guessed gets.
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
    ///     <see cref="IUserGrain.SetPasswordAsync" />; nothing here stores or logs it.
    /// </param>
    /// <returns>
    ///     The accepted invitation; <see cref="ErrorCode.Conflict" /> when the link was used already;
    ///     <see cref="ErrorCode.PreconditionFailed" /> when it expired; <see cref="ErrorCode.ResourceNotFound" />
    ///     when the secret does not match; <see cref="ErrorCode.InvalidRequestBody" /> for a password
    ///     the user grain refuses, with the link left unspent so the person can try again.
    /// </returns>
    Task<Result<Invitation>> AcceptAsync(string secret, string displayName, string password);

    /// <summary>The invitation as it stands, for its sender and for a test.</summary>
    Task<Result<Invitation>> GetAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}

/// <summary>The numbers an invitation lives by.</summary>
public static class InvitationPolicy {
    /// <summary>How long an invitation link works — seven days, the span a colleague takes to open their mail.</summary>
    public static TimeSpan Lifetime { get; } = TimeSpan.FromDays(7);

    /// <summary>The page an invitation link opens, under the identity app's base address.</summary>
    public const string PagePath = "/invitation";
}

/// <summary>What an invitation stands at.</summary>
[Alias("CyberCloud.Identity.InvitationStatus")]
public enum InvitationStatus {
    /// <summary>Sent and not yet used.</summary>
    Pending = 0,

    /// <summary>Used: the invitee is a member.</summary>
    Accepted = 1,

    /// <summary>Past its seven days without being used.</summary>
    Expired = 2
}

/// <summary>What <see cref="IInvitationGrain.CreateAsync" /> is asked to create.</summary>
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

    /// <summary>When the link stops working. <see cref="InvitationPolicy.Lifetime" /> from now, unless a caller says otherwise.</summary>
    [Id(3)]
    public DateTimeOffset ExpiresAt { get; init; }
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
}
