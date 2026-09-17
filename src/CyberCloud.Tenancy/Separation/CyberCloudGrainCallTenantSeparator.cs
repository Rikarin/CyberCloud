using Microsoft.Extensions.Logging;
using Orleans.Multitenant;

namespace CyberCloud.Tenancy.Separation;

/// <summary>
///     Which incoming grain calls are subject to tenant separation.
/// </summary>
/// <remarks>
///     <para>
///         <c>Orleans.Multitenant</c>'s default is
///         <c>!context.InterfaceName.StartsWith("Orleans.")</c> — everything except Orleans' own
///         system grains. That is kept, and the ⚠ in <c>Orleans.Multitenant</c>'s own documentation
///         is why nothing may be added to the exclusion list casually:
///         <i>
///             "this is called for ALL
///             grain calls in Orleans - including the internal Orleans grains. A registered
///             implementation should include similar logic to exclude internal Orleans grains."
///         </i>
///         Drop
///         that clause and the silo's own management grains start failing authorization at startup.
///     </para>
///     <para>
///         ⚠ <b>One thing else is excluded, and it is named, counted and logged so it cannot be
///         invisible.</b> The tempting exclusion is "let anyone call the tenant directory and the
///         shard map", and it is not needed: those are null-tenant grains and
///         <see cref="PlatformCrossTenantAuthorizer" /> already allows the edge <i>into</i> the null
///         tenant, with a log line. Doing it here instead would remove the log line and make the
///         allowance invisible, and an exclusion list is the thing that grows until the separation
///         is decorative. The one edge that IS here — <see cref="IsPlatformServiceEdge" /> — is
///         here because the authorizer cannot express it: it sees two tenant ids and nothing else,
///         and this edge is about <i>which grain</i>.
///     </para>
///     <para>
///         <b>The platform-service edge.</b> docs/plan/17 opens with
///         <i>"the platform itself is CyberCloud.Communication's first customer — every OTP, alert,
///         invitation and invoice goes through it"</i>, and <c>CommunicationOtpDelivery</c>'s design
///         is that a sign-in code is <i>the platform</i> notifying a person, sent through the
///         platform tenant's own communication service so that a tenant which has never configured a
///         carrier can still receive codes. The code is minted by <c>UserGrain</c> in the user's
///         tenant, so that send is a grain-to-grain call from tenant X into the platform tenant's
///         <c>IMessageGrain</c> — a cross-tenant edge, which the authorizer refused, and nothing in
///         the tree noticed until #93 wired this separation into the OTP suite: every route to the
///         platform's service had been tested without the filter and would have failed on the first
///         real silo with <c>Tenant "X" attempted to access tenant "0000…"</c>. The edge is
///         therefore allowed, as narrowly as the mechanism permits: <b>only</b> a call whose target
///         grain is in the platform tenant <b>and</b> whose interface is the sending module's
///         message grain. Every other grain in the platform tenant — its tuple store above all —
///         stays separated.
///     </para>
///     <para>
///         ⚠ <b>What the edge grants, stated so the cost is visible.</b> Any grain in any tenant can
///         send through the platform's communication service: spend its daily allowance, and send
///         mail from the platform's <c>From</c> address to whoever the message grain's checks allow.
///         Grains are the platform's own code running for a tenant — a tenant authors nothing that
///         runs inside a silo — so the exposure is to a <i>bug</i> in a provider, and the bound on it
///         is the platform service's own <c>ChannelLimits</c>. The same capability was already the
///         production design; this makes it work and names what it costs.
///     </para>
///     <para>
///         ⚠ <b>The interface is named as a string</b>, because the tenancy module reaches
///         <c>CyberCloud.Core</c> and nothing else (module-layering.txt) and a type reference into
///         the sending module would invert that. It is exactly the kind of dependency
///         module-layering.txt says the gate cannot see, so it is written here in full and pinned
///         where the two modules meet:
///         <c>OtpIssuanceTests.ThePlatformServiceEdgeNamesTheRealMessageGrainInterface</c> holds the
///         string to <c>typeof(IMessageGrain).FullName</c>, the rest of that class drives a code
///         through the edge under the real filter, and
///         <c>CrossTenantReachabilityTests.Route21_ThePlatformServiceEdgeIsOpenAndEveryOtherPlatformGrainIsNot</c>
///         holds it narrow.
///     </para>
/// </remarks>
public sealed class CyberCloudGrainCallTenantSeparator(ILogger<CyberCloudGrainCallTenantSeparator> logger) : IGrainCallTenantSeparator {
    /// <summary>
    ///     The one interface any tenant's grain may reach in the platform tenant —
    ///     <c>CyberCloud.Communication.Contracts.IMessageGrain</c>, as Orleans names it.
    /// </summary>
    public const string PlatformMessageGrainInterface = "CyberCloud.Communication.Contracts.IMessageGrain";

    /// <summary>How many calls took the platform-service edge. A number that climbs while nobody signs in is worth a look.</summary>
    public long AllowedPlatformServiceEdges { get; private set; }

    /// <inheritdoc />
    public bool IsTenantSeparatedCall(IIncomingGrainCallContext context) {
        ArgumentNullException.ThrowIfNull(context);

        if (context.InterfaceName.StartsWith("Orleans.", StringComparison.Ordinal)) {
            return false;
        }

        if (IsPlatformServiceEdge(context.InterfaceName, context.TargetId.GetTenantId())) {
            AllowedPlatformServiceEdges++;
            logger.LogDebug(
                "Platform-service edge taken: a {Interface} call into the platform tenant's communication service "
                + "from tenant {Source}. docs/plan/17 — the platform is the sending module's first customer.",
                context.InterfaceName,
                context.SourceId?.GetTenantId() ?? "a client"
            );

            return false;
        }

        return true;
    }

    /// <summary>
    ///     Whether a call is the one cross-tenant edge the platform grants every tenant: into the
    ///     platform tenant's message grain, and nothing else there.
    /// </summary>
    /// <param name="interfaceName">The grain interface the call is for, as Orleans names it.</param>
    /// <param name="targetTenantId">The target grain's tenant, or <see langword="null" /> for a null-tenant grain.</param>
    public static bool IsPlatformServiceEdge(string interfaceName, string? targetTenantId) =>
        string.Equals(interfaceName, PlatformMessageGrainInterface, StringComparison.Ordinal)
        && string.Equals(targetTenantId, PlatformCrossTenantAuthorizer.PlatformTenantId, StringComparison.OrdinalIgnoreCase);
}
