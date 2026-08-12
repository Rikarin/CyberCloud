using CyberCloud.Core.Contracts;

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
///     <para>
///         ⚠ <b>THIS SENTENCE IS REACHABLE, WHICH IT PREVIOUSLY WAS NOT, AND THAT IS WORTH RECORDING
///         BECAUSE THREE PLACES USED TO CLAIM OTHERWISE.</b> The remarks here, on
///         <c>SignInApi.Offered</c> and on <c>IdentityEndpoints</c> all said this type was "what
///         every host gets". The tree said something emptier: no host registered an
///         <c>IOtpDeliverySeam</c> at all, and nothing called one, so this refusal existed and could
///         not be produced. Both halves are now true — <c>UserGrain.IssueOtpAsync</c> is the caller
///         (see <see cref="OtpPolicy" /> for why it is a grain and not the host) and
///         <c>CyberCloud.Silo.Host</c> composes the identity module, so a silo that does not
///         configure <c>CyberCloud:Identity:OtpDelivery</c> answers with exactly this.
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

/// <summary>
///     The default <see cref="ITotpSecretSeam" />: it fails, and says what is not wired.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The same shape as <see cref="UnavailableOtpDelivery" /> and for the same reason, but
///         it fails at a worse moment.</b> An unwired OTP delivery breaks a factor at enrolment. An
///         unwired secret resolver breaks TOTP <i>verification</i>, which is a factor the user has
///         already enrolled and is now relying on — so this is the difference between "I cannot turn
///         this on" and "I am locked out of my account". Both messages have to name the missing
///         wiring rather than the symptom.
///     </para>
///     <para>
///         ⚠ <b>Reaching this type is a wiring gap and not a bug in the sign-in path.</b> Nothing in
///         this repository provisions a vault yet — <c>ISecretResolver</c>'s own default in
///         <c>CyberCloud.ResourceManager</c> is the same refusal — so a host that offers TOTP as a
///         second factor today will refuse every code with a uniform failure and this sentence in
///         its log. The endpoint is built against the seam so that wiring a vault is a registration
///         rather than a change to the sign-in path.
///     </para>
/// </remarks>
public sealed class UnavailableTotpSecrets : ITotpSecretSeam {
    /// <inheritdoc />
    public Task<Result<string>> ResolveAsync(
        SecretRef reference,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(
            Result<string>.Failure(
                ErrorCode.InternalError,
                $"No TOTP secret resolver is wired, so '{reference}' cannot be read and no TOTP code "
                + "can be verified. docs/plan/11 § Credentials keeps the shared secret in the vault "
                + "behind a SecretRef and never in grain state, so verification needs a reader. "
                + "Register an ITotpSecretSeam over the platform vault (docs/plan/18) — an adapter "
                + "over CyberCloud.ResourceManager.Contracts' ISecretResolver is the intended one, "
                + "in a host that already references it."
            )
        );
}
