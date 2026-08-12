namespace CyberCloud.Providers.Sample;

/// <summary>
///     The sample provider — one resource type, one api-version, one reconciler.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/24 § Phase 1, exit criterion 1: <i>"A trivial provider
///         (<c>CyberCloud.Sample/widgets</c>, a ConfigMap) passes the full conformance suite."</i>
///         docs/plan/25 § R1 is why it exists at all — it is the instrument that measures whether the
///         resource manager is finished, and its leading indicator is <i>"the number of commits to
///         <c>CyberCloud.ResourceManager</c> made by a provider PR"</i>.
///     </para>
///     <para>
///         ⚠ <b>Nothing here is clever, and that is a requirement rather than a stage of
///         development.</b> A capability this provider grows is a capability the platform is not being
///         asked for, and the measurement quietly degrades. If a widget needs something, the right
///         move is to change <c>CyberCloud.ResourceManager</c> and record why.
///     </para>
///     <para>
///         <b>What is deliberately <i>not</i> declared:</b> no <c>SupportsSoftDelete</c> — the manager did
///         not read <c>SoftDeleteDays</c>, and declaring a recovery window the platform does not honour is
///         worse than declaring none; no <c>Chart</c> — <c>CyberCloud.Kubernetes.Charts</c> does not exist
///         and a chart name that renders nothing would fail at apply time rather than at silo start. ⚠
///         <b>THAT REASON HAS EXPIRED AND THE DECLARATION IS NOW A ONE-LINE DECISION RATHER THAN A BLOCKED
///         ONE.</b> docs/plan/08 § Soft delete is built: a <c>DELETE</c> of a type declaring a window parks
///         the resource at <c>IndexEntryState.SoftDeleted</c> so its old address answers the canonical
///         <c>404</c>, holds its name, keeps its committed quota, moves its ReBAC parent edge to the
///         subscription and drops its direct role assignments; a restore reverses it and a purge — under
///         its own permission — ends it. So the question this type still owes an answer to is the
///         provider's own: <i>does the data this type carries deserve a recovery window, and how long</i>,
///         which is a claim about the data and not about the platform.
///     </para>
/// </remarks>
public sealed class SampleProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => SampleWidgets.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(SampleWidgets.TypePath)
            .ApiVersion(SampleWidgets.V2026, SampleWidgets.Schema2026)
            .Reconciler<WidgetReconciler>()
            // One unit of the `resources` family per widget — docs/plan/06 § Quota. A ConfigMap draws
            // no vcpu and no storage, so declaring either would bill a tenant for nothing.
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // ⚠ The action does nothing on purpose. It is here so the conformance suite has a real
            // POST to drive — docs/plan/08 § The write path, end to end: POST "appears only for
            // actions on an existing resource … never for creation", and a suite with no action
            // registered cannot check the second half.
            //
            // Its request and response shapes are declared because an undeclared action is the one
            // part of the API surface with no contract: the emitted document said `schema: {}` and the
            // manager validated nothing. Declaring them costs no behaviour here — the handler is still
            // a no-op — and makes the action's parameters refusable and generable like everything else.
            .Action(
                "ping",
                ActionKind.Post,
                "write",
                request: SampleWidgets.PingRequest,
                response: SampleWidgets.PingResponse
            )
            // What a person sees. docs/plan/21 § Grammar's alias table is generated from this rather
            // than hand-maintained in the CLI, and a portal breadcrumb has a word to draw.
            .Display("Widget", "Widgets", shortName: "widget", summary: "A ConfigMap with two fields in it.")
            .SupportsTags()
            // The pointer is the default and is stated anyway: it is the fact ProviderBuilder checks
            // the schema against, and reading it here is how the next provider learns the flag has a
            // second half.
            .RequiresCluster(ClusterPlacement.DefaultPointer);
    }
}
