using CyberCloud.Identity.SignIn;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CyberCloud.Identity.Seams;

/// <summary>
///     An <see cref="IOtpDeliverySeam" /> that writes the code to the silo's log instead of sending
///     it — for a development run with no MTA (#93), and for nothing else.
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
///         reads it in the Aspire dashboard: <c>silo-one</c>'s or <c>silo-two</c>'s console (the
///         grain lives on one of them), or the structured logs with the filter
///         <c>DEVELOPMENT OTP</c>, which searches both.
///     </para>
///     <para>
///         ⚠ <b>The address is a structured property and never in the message.</b> docs/plan/11
///         § Auditing bans an email from a log <i>message</i>; the code is not PII and is in the
///         line, the address is not and rides in a scope. <c>IdentityLog.DevelopmentOtpDelivered</c>
///         carries the template and the argument.
///     </para>
/// </remarks>
public sealed class DevelopmentOtpDelivery : IOtpDeliverySeam {
    /// <summary>The scope property the destination address travels in.</summary>
    public const string DestinationProperty = "Destination";

    readonly ILogger<DevelopmentOtpDelivery> logger;

    /// <summary>Builds the seam, or refuses to.</summary>
    /// <param name="environment">The host's environment. Must be Development.</param>
    /// <param name="logger">Where the codes go.</param>
    /// <exception cref="InvalidOperationException">The environment is not Development.</exception>
    public DevelopmentOtpDelivery(IHostEnvironment environment, ILogger<DevelopmentOtpDelivery> logger) {
        ArgumentNullException.ThrowIfNull(environment);
        ArgumentNullException.ThrowIfNull(logger);

        if (!environment.IsDevelopment()) {
            throw new InvalidOperationException(
                $"DevelopmentOtpDelivery refuses to load in the '{environment.EnvironmentName}' "
                + "environment. It writes one-time codes to the log instead of sending them, which is "
                + "acceptable on a developer's laptop with no MTA (#93) and nowhere else. Configure "
                + "CyberCloud:Identity:OtpDelivery so CommunicationOtpDelivery sends them through "
                + "CyberCloud.Communication instead — docs/plan/11 § Credentials, docs/plan/17."
            );
        }

        this.logger = logger;
    }

    /// <inheritdoc />
    public Task<Result> DeliverAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) {
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

        return Task.FromResult(Result.Success);
    }
}
