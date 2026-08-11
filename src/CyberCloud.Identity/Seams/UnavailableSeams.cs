namespace CyberCloud.Identity.Seams;

/// <summary>
///     The default <see cref="IOtpDeliverySeam" />: it fails, and says what is not wired.
/// </summary>
/// <remarks>
///     ⚠ <b>It does not succeed quietly.</b> An OTP factor that reports delivery and sends nothing
///     locks every user who enrols in it out of their own account, and the failure surfaces on the
///     second sign-in rather than at enrolment — which is the worst possible time to discover it.
///     <para>
///         ⚠ <b>This is now a <i>wiring</i> failure rather than a missing feature, and the message
///         says so.</b> <c>CyberCloud.Communication</c> exists and
///         <see cref="CommunicationOtpDelivery" /> adapts onto it; reaching this type means the host
///         did not call <c>AddCommunicationOtpDelivery</c>. An operator reading this at 03:00 needs
///         the name of the call that is missing, not an essay about a module.
///     </para>
/// </remarks>
public sealed class UnavailableOtpDelivery : IOtpDeliverySeam {
    /// <inheritdoc />
    public Task<Result> DeliverAsync(OtpDelivery delivery, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(delivery);

        return Task.FromResult(
            Result.Failure(
                ErrorCode.InternalError,
                $"{delivery.Kind} delivery is not wired. docs/plan/11 § Credentials routes email, SMS "
                + "and WhatsApp codes through CyberCloud.Communication (docs/plan/17), and "
                + "CommunicationOtpDelivery is the adapter onto it — but this host registered "
                + "neither. Call ISiloBuilder.AddCommunicationOtpDelivery(tenantId, serviceId) with "
                + "the communication service the platform sends its own codes through, beside "
                + "AddCyberCloudCommunication()."
            )
        );
    }
}
