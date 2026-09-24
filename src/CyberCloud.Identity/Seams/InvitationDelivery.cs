using CyberCloud.Communication.Contracts;
using System.Globalization;

namespace CyberCloud.Identity.Seams;

/// <summary>
///     Where an invitation's mail goes: the platform's communication service, and the page its link
///     opens.
/// </summary>
public sealed record InvitationDeliveryRoute {
    /// <summary>The tenant that owns the communication service — the platform's, as for the codes.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The <c>CyberCloud.Communication/services/{name}</c> to send through.</summary>
    public required Guid ServiceId { get; init; }

    /// <summary>
    ///     The identity app's base address — the link is this plus
    ///     <see cref="InvitationPolicy.PagePath" />. <c>CyberCloud:Identity:Invitations:PageBaseUri</c>.
    /// </summary>
    public required Uri PageBaseUri { get; init; }
}

/// <summary>
///     <see cref="IInvitationDeliverySeam" /> over <see cref="IMessageSender" /> — the invitation,
///     rendered from <see cref="Render" />'s template and mailed through the platform's own
///     communication service. docs/plan/17: "every OTP, alert, invitation and invoice goes through
///     it". Issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The template is this type's, not the communication service's, and that is the
///         owed half.</b> <c>IMessageTemplateGrain</c> exists and a send can name a template, but
///         the platform's service has none registered and nothing bootstraps one — so the message
///         is rendered here from one template in code, with the tenant, the link and the expiry
///         substituted, and sent as a body. Moving it into a registered template is a bootstrap
///         change (<c>PlatformBootstrapTask</c> writing the template beside the service) and is
///         recorded in docs/plan/17.
///     </para>
///     <para>
///         ⚠ <b>The idempotency key is the invitation id.</b> <c>CommunicationOtpDelivery</c>'s
///         remarks carry the argument for the key being the one value that changes exactly when the
///         message does; an invitation's link never changes under one id (the grain refuses another
///         secret for it), so the id is that value, and the sender's retry after a relay outage is
///         one mail, not two.
///     </para>
///     <para>
///         ⚠ <b>The link carries the tenant, the invitation and the secret — and the secret only
///         here.</b> The first line is the subject (<c>MailMessages</c> takes it), and the link sits
///         alone on its own line so a mail client that wraps text cannot break it.
///     </para>
/// </remarks>
/// <param name="sender">The module's client-side seam.</param>
/// <param name="route">The service and the page.</param>
public sealed class CommunicationInvitationDelivery(IMessageSender sender, InvitationDeliveryRoute route)
    : IInvitationDeliverySeam {
    /// <inheritdoc />
    public async Task<Result> DeliverAsync(InvitationDelivery delivery, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(delivery);

        var sent = await sender.SendAsync(
            route.TenantId,
            new() {
                ServiceId = route.ServiceId,
                Channel = ChannelKind.Email,
                Destination = delivery.Email,
                Body = Render(delivery, LinkFor(delivery)),
                IdempotencyKey = IdempotencyKeyFor(delivery)
            },
            cancellationToken
        );

        return sent.TryGetError(out var failure) ? Result.Failure(failure) : Result.Success;
    }

    /// <summary>The accept link: the page, with the tenant, the invitation and the secret.</summary>
    /// <param name="delivery">The invitation.</param>
    public Uri LinkFor(InvitationDelivery delivery) {
        ArgumentNullException.ThrowIfNull(delivery);

        return new(
            route.PageBaseUri.GetLeftPart(UriPartial.Path).TrimEnd('/')
            + InvitationPolicy.PagePath
            + "?tenant="
            + delivery.TenantId.ToString("N", CultureInfo.InvariantCulture)
            + "&invitation="
            + delivery.InvitationId.ToString("N", CultureInfo.InvariantCulture)
            + "&token="
            + Uri.EscapeDataString(delivery.Secret)
        );
    }

    /// <summary>The message's key — one per invitation, so a retry is one mail.</summary>
    /// <param name="delivery">The invitation.</param>
    public static string IdempotencyKeyFor(InvitationDelivery delivery) {
        ArgumentNullException.ThrowIfNull(delivery);

        return string.Create(CultureInfo.InvariantCulture, $"invite-{delivery.TenantId:N}-{delivery.InvitationId:N}");
    }

    /// <summary>
    ///     The invitation mail — the template, with the tenant, the link and the expiry substituted.
    /// </summary>
    /// <param name="delivery">The invitation.</param>
    /// <param name="link">The accept link.</param>
    public static string Render(InvitationDelivery delivery, Uri link) {
        ArgumentNullException.ThrowIfNull(delivery);
        ArgumentNullException.ThrowIfNull(link);

        var expires = delivery.ExpiresAt.UtcDateTime.ToString("d MMMM yyyy 'at' HH:mm 'UTC'", CultureInfo.InvariantCulture);

        return string.Join(
            "\r\n",
            $"You're invited to {delivery.TenantName} on Cyber Cloud",
            string.Empty,
            $"You have been invited to join {delivery.TenantName} on Cyber Cloud as {delivery.Email}.",
            "Open this link to accept — you will choose a name and a password for this organisation:",
            string.Empty,
            link.AbsoluteUri,
            string.Empty,
            $"The link works once and expires on {expires}. If you were not expecting this, ignore it:",
            "nothing happens until the link is opened."
        );
    }
}

/// <summary>
///     The default <see cref="IInvitationDeliverySeam" />: it fails, and says what is not wired.
/// </summary>
/// <remarks>
///     ⚠ Not a quiet success, for <see cref="UnavailableOtpDelivery" />'s reason: an invitation that
///     reported itself sent would be a colleague told to expect a mail that never comes.
/// </remarks>
public sealed class UnavailableInvitationDelivery : IInvitationDeliverySeam {
    /// <inheritdoc />
    public Task<Result> DeliverAsync(InvitationDelivery delivery, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(delivery);

        return Task.FromResult(
            Result.Failure(
                ErrorCode.InternalError,
                "Invitation mail is not wired. CommunicationInvitationDelivery sends it through the "
                + "platform's communication service (docs/plan/17), and this silo registered no route: "
                + "call ISiloBuilder.AddCommunicationInvitationDelivery(tenantId, serviceId, pageBaseUri), "
                + "which CyberCloud.Silo.Host does when a relay is configured and "
                + "CyberCloud:Identity:Invitations:PageBaseUri names the identity app."
            )
        );
    }
}
