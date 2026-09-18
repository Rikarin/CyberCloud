using CyberCloud.Communication.Contracts;
using CyberCloud.Communication.Providers.Smtp;
using CyberCloud.Core.Resources;
using CyberCloud.Identity.Seams;
using CyberCloud.Providers.Communication.Contracts;
using CyberCloud.ResourceManager;

namespace CyberCloud.Silo.Host;

/// <summary>
///     What the platform's own communication service is allowed to send — the knob on the channel
///     <see cref="PlatformCommunicationService" /> configures. <c>CyberCloud:Communication:PlatformService</c>.
/// </summary>
public sealed class PlatformCommunicationServiceOptions {
    /// <summary>The configuration section this binds from.</summary>
    public const string SectionName = "CyberCloud:Communication:PlatformService";

    /// <summary>
    ///     The most emails the platform's own service dispatches in one UTC day — OTPs and anything
    ///     else the platform sends through it. <c>ChannelLimits.MaxMessagesPerWindow</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ A thousand rather than unlimited, for the reason <c>ChannelLimits.None</c> gives — the
    ///         limit is the only thing between a sign-up loop and a relay's abuse desk. The refusal
    ///         names the limit and this section, so raising it is a deliberate act. It is per UTC
    ///         calendar day (<c>ChannelLimits.MaxMessagesPerWindow</c>), not a rolling 24 hours.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Read it as an outage lever as well as a guard, because it is both.</b> This one
    ///         number is shared by every tenant's sign-in, step-up and password-reset codes the
    ///         moment <c>CyberCloud:Identity:OtpDelivery:ServiceId</c> names this service —
    ///         <c>CommunicationOtpDelivery</c>'s remarks own that as the cost of one service for
    ///         every tenant's codes — and its feeder is <c>POST /api/signup/begin</c>, which is
    ///         unauthenticated and issues a code for any address it is handed. The per-sign-up issue
    ///         cap (<c>OtpPolicy.MaxIssuesPerWindow</c>) bounds one address, so a caller who varies
    ///         the address is what exhausts it: at the default, a thousand addresses and nobody on
    ///         the platform gets a code until midnight UTC. Two things bound that, and neither is
    ///         this number. <c>SignUpApi.BeginAsync</c> runs the lockout ladder on the caller's
    ///         address (<c>LockoutKey.ForCaller</c>) — five begins free per window, then doubling
    ///         waits — so one machine cannot spend the day; a caller spread across many addresses
    ///         still can, and this cap is what stops <i>that</i> from becoming a relay bill and a
    ///         blocked sending domain. When it trips, the refusal is loud and names this section:
    ///         raising it for the day is the operator's call, and so is what the sign-up surface
    ///         should have been sitting behind.
    ///     </para>
    /// </remarks>
    public long MaxEmailsPerDay { get; set; } = 1000;
}

/// <summary>
///     The platform's own <c>CyberCloud.Communication/services</c> — the service every OTP, alert
///     and invoice the <i>platform</i> sends goes out through, addressed by a fixed path in the
///     platform tenant and keyed by the id that path derives.
/// </summary>
/// <remarks>
///     <para>
///         <b>docs/plan/17 opens with it:</b>
///         <i>
///             "The platform itself is CyberCloud.Communication's
///             first customer — every OTP, alert, invitation and invoice goes through it"
///         </i>. Until #93
///         nothing created that customer's service: <c>SiloIdentityOptions</c> asked an operator to
///         create a <c>services</c> resource by hand and paste its derived id into configuration,
///         and on a development run nobody did, so the codes went to the console alone.
///         <see cref="PlatformBootstrapTask" /> now writes this service's grain at start whenever the
///         silo has a relay to send through, and <c>SiloIdentityComposition</c> routes the codes
///         through it in Development.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A grain with no resource behind it, and the address is what keeps that honest.
///         </b> The service grain is keyed by the id
///         <see cref="CommunicationGrainKeys.ResourceIdFor" /> derives from a resource's address —
///         docs/plan/17 § Resource model says why — so the grain this task writes is exactly the
///         grain a <c>services/platform</c> resource in the platform tenant's <c>platform</c>
///         resource group would reconcile onto. If an operator later creates that resource through
///         the resource manager, the reconciler finds this grain, adopts it (the create is idempotent
///         and the channel has no owning resource until a <c>channels</c> child claims it), and the
///         listing shows what was always there. What does not exist until then is the listing.
///     </para>
///     <para>
///         ⚠ <b>The platform tenant's subscription is the platform tenant's own id.</b> The platform
///         tenant (<see cref="ReBacScopeAuthorizer.PlatformTenant" />, all zeroes) has no
///         subscription of record anywhere in this repository, and the address needs one. Reusing
///         the tenant id is a choice with no second copy to drift from; the day the platform tenant
///         gets a real subscription this is the one constant to change, and the derived id — and so
///         the suppression list — changes with it, which is the one-time cost the move would have to
///         accept deliberately.
///     </para>
/// </remarks>
public static class PlatformCommunicationService {
    /// <summary>The resource group and the service share the one name that says whose they are.</summary>
    public const string Name = "platform";

    /// <summary>
    ///     The address —
    ///     <c>
    /// /tenants/{platform}/subscriptions/{platform}/resourceGroups/platform/providers/CyberCloud.Communication/services/platform
    ///     </c>.
    /// </summary>
    public static ResourceId Address { get; } = new(
        ReBacScopeAuthorizer.PlatformTenant,
        ReBacScopeAuthorizer.PlatformTenant,
        Name,
        CommunicationServices.Type,
        Name,
        Guid.Empty
    );

    /// <summary>
    ///     The service grain's id, derived from <see cref="Address" /> exactly as the tenant-facing
    ///     provider would derive it — <see cref="CommunicationServices.ServiceIdOf" />.
    /// </summary>
    public static Guid ServiceId { get; } = CommunicationServices.ServiceIdOf(Address);

    /// <summary>The route <c>CommunicationOtpDelivery</c> sends the platform's codes through.</summary>
    public static OtpDeliveryRoute OtpRoute { get; } =
        new() { TenantId = ReBacScopeAuthorizer.PlatformTenant, ServiceId = ServiceId };

    /// <summary>
    ///     The email channel the bootstrap writes: the smtp carrier, the platform's own account, the
    ///     configured daily cap, and no spend cap because a relay the platform runs has no per-message
    ///     price on the wire.
    /// </summary>
    /// <param name="options">The bound <see cref="PlatformCommunicationServiceOptions" />.</param>
    public static ChannelConfiguration EmailChannel(PlatformCommunicationServiceOptions options) {
        ArgumentNullException.ThrowIfNull(options);

        return new() {
            Channel = ChannelKind.Email,
            // ⚠ Named, not left to the registry's default. The registry resolves an unnamed channel
            // to the one real carrier, which today is this one — but a second email carrier landing
            // would turn every platform OTP into an ambiguity refusal, and naming it here costs one
            // string.
            Provider = SmtpChannelProvider.ProviderName,
            Credentials = new() { Mode = CredentialMode.PlatformAccount },
            Limits = new() { MaxMessagesPerWindow = options.MaxEmailsPerDay, MaxSpendPerWindow = 0m, Currency = "EUR" },
            EstimatedUnitCost = 0m,
            Enabled = true
        };
    }
}
