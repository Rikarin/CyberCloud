namespace CyberCloud.Identity.Seams;

/// <summary>
///     The default <see cref="IOtpDeliverySeam" />: it fails, and says which module is missing.
/// </summary>
/// <remarks>
///     ⚠ <b>It does not succeed quietly.</b> An OTP factor that reports delivery and sends nothing
///     locks every user who enrols in it out of their own account, and the failure surfaces on the
///     second sign-in rather than at enrolment — which is the worst possible time to discover it.
/// </remarks>
public sealed class UnavailableOtpDelivery : IOtpDeliverySeam {
    /// <inheritdoc />
    public Task<Result> DeliverAsync(CredentialKind kind, string destination, string code) =>
        Task.FromResult(
            Result.Failure(
                ErrorCode.InternalError,
                $"{kind} delivery is not implemented. docs/plan/11 § Credentials routes email, SMS "
                + "and WhatsApp codes through CyberCloud.Communication (docs/plan/17), which does not "
                + "exist. Generating and checking six digits is the small part; provider fan-out, "
                + "per-tenant sender identity, bounce handling and WhatsApp template pre-approval are "
                + "the feature, and they belong to that module."
            )
        );
}
