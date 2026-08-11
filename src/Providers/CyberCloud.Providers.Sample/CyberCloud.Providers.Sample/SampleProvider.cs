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
///         <b>What is deliberately <i>not</i> declared:</b> no
///         <c>SupportsSoftDelete</c> — nothing in the manager reads <c>SoftDeleteDays</c> yet, and
///         declaring a recovery window the platform does not honour is worse than declaring none; no
///         <c>Chart</c> — <c>CyberCloud.Kubernetes.Charts</c> does not exist and a chart name that
///         renders nothing would fail at apply time rather than at silo start.
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
            .Action("ping", ActionKind.Post, "write")
            .SupportsTags()
            .RequiresCluster();
    }
}
