using CyberCloud.Communication.Contracts;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace CyberCloud.Identity.Seams;

/// <summary>
///     Where the platform's own one-time codes are sent from — docs/plan/17's <i>"the platform
///     itself is <c>CyberCloud.Communication</c>'s first customer"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>One service for every tenant's codes, and that is a decision with consequences worth
///         stating.</b> A sign-in OTP is the <i>platform</i> notifying a person, not a tenant
///         notifying their customer, so it goes through the platform's own communication service and
///         not through whatever the user's tenant happens to have configured. Three things follow,
///         and each of them is the right way round:
///     </para>
///     <list type="bullet">
///         <item>
///             a tenant that has never configured a communication service can still receive codes —
///             the alternative is that sign-in breaks for every tenant that has not onboarded a
///             carrier;
///         </item>
///         <item>
///             a tenant's BYO carrier account is not billed for the platform's authentication
///             traffic, which is not theirs;
///         </item>
///         <item>
///             ⚠ <b>the spend cap is shared.</b> <c>ISendLimitGrain</c> counts per service, so one
///             runaway loop of platform codes exhausts the window for every tenant's codes at once.
///             That is the cost, and it is deliberate: a single global cap on authentication spend is
///             the thing an operator can actually reason about at 03:00, and the failure it produces
///             — codes refused, loudly, with the limit and the window named — is better than an
///             unbounded carrier bill.
///         </item>
///     </list>
///     <para>
///         Both ids are the host's to supply because they name a resource, and this assembly has no
///         way to discover one — see <c>IdentitySiloBuilderExtensions.AddCommunicationOtpDelivery</c>.
///     </para>
/// </remarks>
public sealed record OtpDeliveryRoute {
    /// <summary>The tenant that owns the communication service. ⚠ Not the user's tenant.</summary>
    public required Guid TenantId { get; init; }

    /// <summary>The <c>CyberCloud.Communication/services/{name}</c> resource to send through.</summary>
    public required Guid ServiceId { get; init; }

    /// <summary>
    ///     The template within that service to render, or empty for a free-text body.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Empty does not work for WhatsApp and the refusal comes from the module, not from
    ///     here.</b> docs/plan/17 § The channel abstraction: business-initiated WhatsApp messages
    ///     require a pre-approved template, so <c>IMessageGrain</c> refuses a free-text send on that
    ///     channel. Duplicating that rule here would be a second copy of it that can drift.
    /// </remarks>
    public string TemplateName { get; init; } = string.Empty;

    /// <summary>The template parameter the code is substituted into.</summary>
    public string CodeParameter { get; init; } = "code";
}

/// <summary>
///     <see cref="IOtpDeliverySeam" /> over <see cref="IMessageSender" /> — docs/plan/11
///     § Credentials' three OTP rows, delivered by docs/plan/17's module.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE IDEMPOTENCY KEY IS THE WHOLE OF THIS CLASS.</b> Everything else is a field
///         copy. <c>IMessageGrain</c> is keyed on a hash of <c>(serviceId, idempotencyKey)</c> and
///         exists so that a retry after a timeout addresses the same activation and finds the send
///         already recorded. A key that varied per attempt — <c>Guid.NewGuid()</c> being the obvious
///         way to write one — turns every retry into a second SMS, which is exactly the harm the
///         grain was built to prevent, and it would pass every test that only ever sends once.
///     </para>
///     <para>
///         <b>What the key is derived from, and why each part is in it.</b>
///         <c>otp-{tenant:N}-{user:N}-{purpose}-{digest}</c>:
///     </para>
///     <list type="bullet">
///         <item>
///             <b>tenant and user</b> — who. A user id is only unique <i>within</i> a tenant (the
///             user grain is tenant-qualified), and every tenant's codes land in one service's key
///             space here, so the tenant id is what keeps two tenants' users apart.
///         </item>
///         <item>
///             <b>purpose</b> — a closed <see cref="OtpPurpose" />, so a sign-in code and a
///             password-reset code seconds apart are two messages rather than one, and so two call
///             sites cannot disagree by spelling. That last part matters because
///             <c>CommunicationGrainKeys.Message</c> compares ordinally and does not case-fold.
///         </item>
///         <item>
///             <b>digest</b> — a truncated SHA-256 over the same fields <i>plus the code itself</i>.
///             This is the part that makes a retry a retry: a redelivery of the same code computes
///             the same key and sends once, and a genuinely new code computes a different one and
///             sends again.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>There is deliberately no clock in it, and the shape that would have had one is
///         wrong in both directions.</b> A key of the form <c>otp-{user}-{purpose}-{window}</c> fails
///         whichever way the window is cut. Too coarse and two genuinely distinct codes inside one
///         window collide: the second carries different content under the first one's key, so
///         <c>IMessageGrain</c> answers <c>Conflict</c> and the user's "resend" never arrives. Too
///         fine — or unrounded, which <c>{window:O}</c> would be, since round-trip format prints
///         sub-second precision — and a retry that lands on the far side of a boundary computes a
///         new key and sends a second message. Retries arrive seconds to minutes after the attempt
///         they repeat, which is precisely the interval that straddles boundaries. The code is the
///         only value available that changes when, and only when, the notification is a different
///         one.
///     </para>
///     <para>
///         ⚠ <b>What putting a digest of a live code in a grain key costs, stated rather than
///         hidden.</b> <see cref="SendRequest.IdempotencyKey" />'s own remarks say the value reaches
///         "every log line and trace that prints a grain id", and a six-digit code has a million
///         candidates — so somebody holding both the logs and the destination can grind the digest
///         back to the code within its validity window. It is here anyway, on the same reasoning
///         <c>CredentialDigest.AddressDigest</c> records: the alternative shapes are worse (a random
///         key sends twice; a clock window breaks resend), the module already keeps an unkeyed
///         SHA-256 of the message body — which <i>is</i> the code — in hot-tier state for thirty
///         days, and the exposure needs log access that this platform treats as privileged.
///         <b>The honest follow-up is an HMAC under the same vault-resolved pepper
///         <c>AddCyberCloudIdentity</c> already takes for Argon2id</b>, which costs the caller
///         nothing and makes the digest useless without the vault. It is not done here because
///         plumbing a second use of the pepper through the host is a change to the host's start-up
///         contract, and this change is already one.
///     </para>
///     <para>
///         ⚠ <b>Every grain reference is <c>ForTenant</c>-qualified, and none of them is made
///         here.</b> This type holds <see cref="IMessageSender" /> rather than an
///         <c>IGrainFactory</c> for exactly the reason <c>IMessageSender</c>'s own remarks give:
///         identity is not a grain, so <c>Orleans.Multitenant</c>'s call filter never sees it, and
///         CC1006 means the qualification has to be written by hand. Writing it once, in
///         <c>GrainMessageSender</c>, is what keeps this class unable to get it wrong.
///     </para>
/// </remarks>
/// <param name="sender">The module's client-side seam.</param>
/// <param name="route">Which service the platform's codes go through.</param>
public sealed class CommunicationOtpDelivery(IMessageSender sender, OtpDeliveryRoute route) : IOtpDeliverySeam {
    /// <inheritdoc />
    public async Task<Result> DeliverAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(delivery);

        var channel = ChannelFor(delivery.Kind);

        if (channel.TryGetError(out var wrongKind)) {
            return Result.Failure(wrongKind);
        }

        if (delivery.Purpose is OtpPurpose.Unknown || !Enum.IsDefined(delivery.Purpose)) {
            // ⚠ Refused rather than defaulted. A delivery with no purpose would share one key space
            // with every other reason a code is sent to that user, so the first sign-in code of the
            // day would swallow the password-reset code that followed it.
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"An OTP delivery says why it is being sent. '{delivery.Purpose}' is not an "
                + $"{nameof(OtpPurpose)}, and the purpose is part of the idempotency key that keeps "
                + "a sign-in code and a password-reset code from looking like one message."
            );
        }

        if (string.IsNullOrWhiteSpace(delivery.Code)) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                "An OTP delivery with no code would send an empty message, and — because the code is "
                + "what makes one delivery distinguishable from the next — every such delivery to "
                + "one user would collapse onto one message grain."
            );
        }

        var sent = await sender.SendAsync(route.TenantId, RequestFor(delivery, channel.GetValueOrThrow()), cancellationToken);

        return sent.TryGetError(out var failure) ? Result.Failure(failure) : Result.Success;
    }

    /// <summary>
    ///     The idempotency key for one delivery.
    /// </summary>
    /// <param name="delivery">The delivery.</param>
    /// <returns>
    ///     A value that is identical for a redelivery of the same code and different for any other
    ///     code, user, tenant or purpose. See this type's remarks for why it is derived this way.
    /// </returns>
    /// <exception cref="ArgumentNullException"><paramref name="delivery" /> is null.</exception>
    /// <remarks>
    ///     ⚠ Public so it can be asserted directly rather than inferred from a provider's call
    ///     count. A test that only counted sends would pass against a key that happened to be stable
    ///     for the wrong reason.
    /// </remarks>
    public static string IdempotencyKeyFor(OtpDelivery delivery) {
        ArgumentNullException.ThrowIfNull(delivery);

        // ⚠ Prefix-free: every variable-length part is preceded by its own length, so no two
        // different deliveries can produce the same material by re-cutting it at a different place.
        // The same argument CommunicationGrainKeys.Derive and GrainKeys.EmailIndex make.
        var material = new StringBuilder(192)
            .Append("cybercloud.identity.otp\n")
            .Append(delivery.TenantId.ToString("N", CultureInfo.InvariantCulture))
            .Append('\n')
            .Append(delivery.UserId.ToString("N", CultureInfo.InvariantCulture))
            .Append('\n')
            .Append((int)delivery.Purpose)
            .Append('\n')
            .Append((int)delivery.Kind)
            .Append('\n')
            .Append(delivery.Destination.Length)
            .Append('\n')
            .Append(delivery.Destination)
            .Append('\n')
            .Append(delivery.Code.Length)
            .Append('\n')
            .Append(delivery.Code)
            .ToString();

        // 16 hexadecimal characters — 64 bits, matching CredentialDigest.AddressDigest. The space
        // being collided against is one user's codes inside IMessageGrain.Retention, so this is not
        // where the margin runs out.
        var digest = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(material))[..8]);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"otp-{delivery.TenantId:N}-{delivery.UserId:N}-{delivery.Purpose}-{digest}"
        );
    }

    /// <summary>
    ///     The channel a credential kind is delivered over.
    /// </summary>
    /// <param name="kind">The credential kind.</param>
    /// <remarks>
    ///     ⚠ No default case that guesses. <see cref="CredentialKind" /> has eight members and five
    ///     of them are not delivered at all — a passkey is not "sent" anywhere — so mapping the
    ///     remainder onto some channel would turn a caller's bug into a message to a stranger.
    /// </remarks>
    static Result<ChannelKind> ChannelFor(CredentialKind kind) =>
        kind switch {
            CredentialKind.EmailOtp => Result<ChannelKind>.Success(ChannelKind.Email),
            CredentialKind.SmsOtp => Result<ChannelKind>.Success(ChannelKind.Sms),
            CredentialKind.WhatsAppOtp => Result<ChannelKind>.Success(ChannelKind.WhatsApp),
            _ => Result<ChannelKind>.Failure(
                ErrorCode.InvalidRequestBody,
                $"{kind} is not delivered as a one-time code. docs/plan/11 § Credentials routes "
                + $"{CredentialKind.EmailOtp}, {CredentialKind.SmsOtp} and "
                + $"{CredentialKind.WhatsAppOtp} through CyberCloud.Communication, and nothing else."
            )
        };

    SendRequest RequestFor(OtpDelivery delivery, ChannelKind channel) {
        var request = new SendRequest {
            ServiceId = route.ServiceId,
            Channel = channel,
            Destination = delivery.Destination,
            IdempotencyKey = IdempotencyKeyFor(delivery)
        };

        return string.IsNullOrWhiteSpace(route.TemplateName)
            // ⚠ The code goes in the body, and IMessageGrain never stores a body — only a digest of
            // one — precisely because the body of an OTP message IS the credential.
            ? request with {
                Body = string.Create(CultureInfo.InvariantCulture, $"{delivery.Code} is your code.")
            }
            : request with {
                TemplateName = route.TemplateName,
                Arguments = ImmutableArray.Create(
                    new TemplateArgument { Name = route.CodeParameter, Value = delivery.Code }
                )
            };
    }
}
