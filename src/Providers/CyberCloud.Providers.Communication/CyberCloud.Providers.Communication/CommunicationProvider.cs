namespace CyberCloud.Providers.Communication;

/// <summary>
///     The sending product as a tenant-facing provider — four resource types over the grains
///     <c>CyberCloud.Communication</c> already runs, and no cluster anywhere.
/// </summary>
/// <remarks>
///     <para>
///         [17 § <c>CyberCloud.Communication/services</c>](../../../../docs/plan/17-communication-and-email.md)
///         · M2 · 2.0 EM, and docs/plan/24 § Phase 3's <c>Communication</c> row: <i>"Channels,
///         templates, suppression, delivery receipts."</i> The first three are the three child types;
///         delivery receipts are per send and come back on the service's <c>status</c> action, and
///         <see cref="CommunicationServices.SendAction" />'s remarks say why a message is an action
///         rather than a fifth type.
///     </para>
///     <para>
///         ⚠
///         <b>
///             NO <c>RequiresCluster</c>, NO <c>Chart</c>, AND NO <c>ClusterId</c> PROPERTY ON ANY
///             TYPE — the first family in the catalogue for which that is true.
///         </b> docs/plan/08 § What the resource manager deliberately does not do promised a manager
///         that works <i>"for a provider with no cluster at all"</i>; every reconciler here gets a
///         <see langword="null" /> <c>ReconcileContext.Cluster</c> and never looks at it. What it
///         converges is grain state through <see cref="ICommunicationControlPlane" />, and what the
///         four-clause contract's <i>observes, never assumes</i> means here is a second grain read
///         after the write — see any of the reconcilers.
///     </para>
///     <para>
///         ⚠ <b>Every synchronous action here runs on the request path, and four of the five reach
///         a grain.</b> A synchronous action runs inside <c>ResourceManagerService</c>, which in
///         production is the gateway's process, so the gateway registers
///         <c>AddCyberCloudCommunicationClient</c> — the two client-side seams over its cluster
///         client — and <c>HostCompositionTests</c> is what notices if it stops. <c>render</c> is the
///         exception and is pure over the template's own body.
///     </para>
///     <para>
///         ⚠ <b>One meter, the count, on every type.</b> Nothing here draws vCPU, memory, storage or
///         an address — a channel is a row in a grain. The send-side limits docs/plan/17 asks for are
///         <c>ChannelLimits</c> on the channel body and are enforced per window by
///         <c>ISendLimitGrain</c> on the send path, not by quota: <c>CyberCloud.Communication.Contracts</c>'
///         <c>.csproj</c> says why a message is not a thing a subscription <i>holds</i>.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, and on this family the argument is per type.</b> A
///         service's delete retires its grain and keeps its suppression list and its templates'
///         version history regardless — <c>ICommunicationServiceGrain.RetireAsync</c> — so the data a
///         recovery window would protect is protected without one. A channel, a template name and a
///         manual block are each one PUT to restore.
///     </para>
/// </remarks>
public sealed class CommunicationProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => CommunicationServices.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(CommunicationServices.TypePath)
            .ApiVersion(CommunicationServices.V2026, CommunicationServices.Schema2026)
            .Reconciler<CommunicationServiceReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                CommunicationServices.SendAction,
                ActionKind.Post,
                "write",
                request: CommunicationServices.SendRequest,
                response: CommunicationServices.MessageResponse,
                handler: typeof(ServiceSendHandler)
            )
            .Action(
                CommunicationServices.StatusAction,
                ActionKind.Post,
                "read",
                request: CommunicationServices.StatusRequest,
                response: CommunicationServices.MessageResponse,
                handler: typeof(ServiceStatusHandler)
            )
            .Action(
                CommunicationServices.CheckSuppressionAction,
                ActionKind.Post,
                "read",
                request: CommunicationServices.CheckSuppressionRequest,
                response: CommunicationServices.CheckSuppressionResponse,
                handler: typeof(ServiceCheckSuppressionHandler)
            )
            .Action(
                CommunicationServices.ListSuppressionsAction,
                ActionKind.Post,
                "read",
                request: CommunicationServices.ListSuppressionsRequest,
                response: CommunicationServices.ListSuppressionsResponse,
                handler: typeof(ServiceListSuppressionsHandler)
            )
            .Display(
                "Communication service",
                "Communication services",
                // ⚠ NOT `communication`, for the reason CyberCloud.Mail/domains could not be `mail`:
                // CliEmitter.GroupOf derives the group key from the namespace's last segment, and a
                // short name equal to its own group's key gives `cyc communication communication`
                // two meanings.
                shortName: "comms",
                summary: "A sending service — SMS, WhatsApp, email, push and voice through the platform's "
                + "carrier accounts or the tenant's own — with per-channel spend limits, versioned "
                + "templates, a suppression list honoured before every dispatch, and delivery receipts "
                + "per send."
            )
            .SupportsTags()
            .ResourceType(CommunicationChannels.TypePath)
            .ApiVersion(CommunicationServices.V2026, CommunicationChannels.Schema2026)
            .Reconciler<CommunicationChannelReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Display(
                "Communication channel",
                "Communication channels",
                shortName: "channel",
                summary: "One channel a service sends on: which carrier, whose account pays, and what it "
                + "may send and spend per day."
            )
            .SupportsTags()
            .ResourceType(CommunicationTemplates.TypePath)
            .ApiVersion(CommunicationServices.V2026, CommunicationTemplates.Schema2026)
            .Reconciler<CommunicationTemplateReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                CommunicationTemplates.RenderAction,
                ActionKind.Post,
                "read",
                request: CommunicationTemplates.RenderRequest,
                response: CommunicationTemplates.RenderResponse,
                handler: typeof(TemplateRenderHandler)
            )
            .Display(
                "Message template",
                "Message templates",
                shortName: "template",
                summary: "A named, versioned message body with typed variables. Every change appends a "
                + "version; a send names the template and the version it wants."
            )
            .SupportsTags()
            .ResourceType(CommunicationSuppressions.TypePath)
            .ApiVersion(CommunicationServices.V2026, CommunicationSuppressions.Schema2026)
            .Reconciler<CommunicationSuppressionReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Display(
                "Suppression",
                "Suppressions",
                shortName: "suppression",
                summary: "An address a service must never send to, placed by the tenant. Bounces, "
                + "complaints and opt-outs join the same list on their own and are not resources."
            )
            .SupportsTags();
    }
}
