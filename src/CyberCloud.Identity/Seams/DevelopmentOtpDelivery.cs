using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.Seams;

/// <summary>
///     An <see cref="IOtpDeliverySeam" /> that writes the code to the silo's log and — when the
///     development run has a relay — also mails it through <see cref="CommunicationOtpDelivery" />.
///     For a development run (#93), and for nothing else.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The constructor throws outside Development, and that is the whole safety story.</b>
///         Keyed on <c>IHostEnvironment.IsDevelopment()</c> rather than on a setting, for the
///         reason <c>IdentityHostOpenIddict</c> gives for <c>DisableTransportSecurityRequirement</c>:
///         a value somebody can set is a value somebody will set. A production silo that somehow
///         registered this type fails at the first activation that needs the seam, with this
///         sentence, rather than writing every customer's one-time codes to a log stream.
///     </para>
///     <para>
///         ⚠ <b>The silo's log, not the identity host's console, and the reason is
///         <see cref="OtpPolicy" />'s fourth property.</b> A code is minted and delivered inside a
///         grain activation, so the only process that ever holds the plaintext is the silo that ran
///         the grain — the host never sees it, and could not print it. On the AppHost the person
///         reads it in the Aspire dashboard: <c>silo-1</c>'s or <c>silo-2</c>'s console (the
///         grain lives on one of them), or the structured logs with the filter
///         <c>DEVELOPMENT OTP</c>, which searches both.
///     </para>
///     <para>
///         <b>And, since #93's carrier, in an inbox.</b> The AppHost runs Mailpit and configures the
///         silos to send through it, so <c>SiloIdentityComposition</c> hands this seam a
///         <see cref="CommunicationOtpDelivery" /> pointed at the platform's own communication
///         service and the code lands at <c>http://localhost:8025</c> as well. The log line stays,
///         because the person on the dashboard should still find the code there. ⚠ <b>The mail
///         failing does not fail the delivery.</b> A Mailpit still starting, a relay a laptop's
///         firewall refused — in Development the log line is a delivery in its own right, so the
///         refusal is logged beside the code (<c>IdentityLog.DevelopmentOtpNotMailed</c>) and the
///         sign-up continues. Outside Development this type does not load, so that leniency reaches
///         no production tenant.
///     </para>
///     <para>
///         ⚠ <b>The address is a structured property and never in the message — and neither is
///         the sending module's sentence.</b> docs/plan/11 § Auditing bans an email from a log
///         <i>message</i>; the code is not PII and is in the line, the address is not and rides in
///         a scope. <c>IdentityLog.DevelopmentOtpDelivered</c> carries the template and the
///         argument. The refusal line is held to the same rule: a suppression refusal starts with
///         the address and a relay's <c>550</c> quotes it back, so the module's sentence goes in
///         the scope as <see cref="ReasonProperty" /> and the line carries only its
///         <c>ErrorCode</c>.
///         <c>DevelopmentOtpDeliveryTests.AMailThatIsRefusedIsAWarningBesideTheCodeAndNotAFailedDelivery</c>
///         drives it with a refusal that quotes the address.
///     </para>
/// </remarks>
public sealed class DevelopmentOtpDelivery : IOtpDeliverySeam {
    /// <summary>The scope property the destination address travels in.</summary>
    public const string DestinationProperty = "Destination";

    /// <summary>The scope property a refused mail's reason — the sending module's own sentence — travels in.</summary>
    public const string ReasonProperty = "Reason";

    readonly ILogger<DevelopmentOtpDelivery> logger;
    readonly IOtpDeliverySeam? mail;

    /// <summary>Builds the seam, or refuses to.</summary>
    /// <param name="environment">The host's environment. Must be Development.</param>
    /// <param name="logger">Where the codes go.</param>
    /// <param name="mail">
    ///     The seam the code is also sent through — <see cref="CommunicationOtpDelivery" /> over
    ///     the development relay — or <see langword="null" /> for the log alone.
    /// </param>
    /// <exception cref="InvalidOperationException">The environment is not Development.</exception>
    public DevelopmentOtpDelivery(IHostEnvironment environment, ILogger<DevelopmentOtpDelivery> logger, IOtpDeliverySeam? mail = null) {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        if (!environment.IsDevelopment()) {
            throw new InvalidOperationException(
                $"DevelopmentOtpDelivery refuses to load in the '{environment.EnvironmentName}' "
                + "environment. It writes one-time codes to the log instead of sending them, which is "
                + "acceptable on a developer's laptop (#93) and nowhere else. Configure "
                + "CyberCloud:Identity:OtpDelivery so CommunicationOtpDelivery sends them through "
                + "CyberCloud.Communication instead — docs/plan/11 § Credentials, docs/plan/17."
            );
        }

        this.logger = logger;
        this.mail = mail;
    }

    /// <summary>Whether the code is mailed as well as logged.</summary>
    public bool AlsoMails => mail is not null;

    /// <inheritdoc />
    public async Task<Result> DeliverAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(delivery);

        // ⚠ The address is in the SCOPE and not in the template. A scope property reaches every
        // structured sink as a field and reaches no rendered message, which is the split
        // docs/plan/11 § Auditing asks for.
        using (logger.BeginScope(
                   new Dictionary<string, object>(StringComparer.Ordinal) {
                       [DestinationProperty] = delivery.Destination
                   }
               )) {
            IdentityLog.DevelopmentOtpDelivered(logger, delivery.Code, delivery.UserId, delivery.Purpose, delivery.Kind);
        }

        if (mail is null) {
            return Result.Success;
        }

        // The log line above is already a delivery; the mail is the second copy, and its failure is
        // reported beside the code rather than to the person — see the class remarks.
        var mailed = await mail.DeliverAsync(delivery, cancellationToken);

        if (mailed.TryGetError(out var refused)) {
            // ⚠ The sentence in the SCOPE, the code in the line — the module's sentence can quote
            // the address (a suppression refusal opens with it), and the rule above has no exception
            // for a refusal.
            using (logger.BeginScope(
                       new Dictionary<string, object>(StringComparer.Ordinal) {
                           [DestinationProperty] = delivery.Destination,
                           [ReasonProperty] = refused.Message
                       }
                   )) {
                IdentityLog.DevelopmentOtpNotMailed(logger, delivery.UserId, refused.Code.Value);
            }
        }

        return Result.Success;
    }
}
