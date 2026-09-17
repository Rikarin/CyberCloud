namespace CyberCloud.Providers.Monitor;

/// <summary>
///     Managed dashboards — a Grafana OSS instance per resource, provisioned with one workspace's
///     datasources.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/16 § Managed Grafana · <b>M2 · 0.8 EM</b>, and docs/plan/01's row
///         <c>CyberCloud.Dashboard/grafanas</c>. Issue #32's third noun of four.
///     </para>
///     <para>
///         ⚠
///         <b>
///             THE SECOND <c>IResourceProvider</c> IN ONE ASSEMBLY, WHICH NO FAMILY HAD DONE, AND
///             docs/plan/03 ASKED FOR IT FIRST.
///         </b> § Providers' tree reads <c>CyberCloud.Providers.Monitor/ # workspaces, collectors,
///         alerts, grafanas</c> — four nouns, one directory — while docs/plan/01 gives Grafana its
///         own namespace. A provider declares exactly one namespace, so the two documents together
///         say: two providers, one family. <c>ProviderDiscovery.FromAssembly</c> instantiates
///         <i>"every provider an assembly declares"</i> and <c>MonitorApplicationModule</c> registers
///         both, so nothing above this family had to learn anything. What the shape buys is the
///         one reference this type cannot do without: <c>MonitorWorkspaces.WorkspaceEnv</c> and the
///         row's key names, which a seventeenth family could not take under rule 2 of § Assembly
///         graph rules.
///     </para>
///     <para>
///         ⚠ <b>WHAT ADR-011 DECIDES HERE, AND WHY THIS PROVIDER CONVERGES RATHER THAN REFUSES.</b>
///         The reading is on <see cref="Grafanas" /> in full. In one line: a managed instance of
///         unmodified upstream Grafana is what the ADR's row permits, the portal's rule is about
///         code the portal never takes, and the licence gate's exception is written beside the image
///         with the reading as its argument. Had the row refused, the type would be declared exactly
///         as below and <see cref="GrafanaReconciler" /> would fail every pass naming the ADR — a
///         published type with a visible refusal beats an absent one — but the row does not refuse.
///     </para>
///     <para>
///         ⚠ <b>Two meters from a preset, and the count.</b> Grafana is a pod, so it draws vCPU and
///         memory the way <c>CyberCloud.Network/…/loadBalancers</c> does — a preset's quantity, parsed
///         by <c>KubeQuantity</c>. No storage: the instance's state is an <c>emptyDir</c>, on purpose,
///         and a resource that reserved disk it does not keep would be billing for a promise it
///         does not make.
///     </para>
/// </remarks>
public sealed class DashboardProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => Grafanas.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(Grafanas.TypePath)
            .ApiVersion(Grafanas.V2026, Grafanas.Schema2026)
            .Reconciler<GrafanaReconciler>()
            .Meter(QuotaMeter.Vcpu, GrafanaVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, GrafanaMemoryDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // ⚠ SYNCHRONOUS, WITH A HANDLER, AND SECRET — the workspace's listKeys shape, because the
            // response carries the admin password. A long-running `url` would answer 202 and re-run
            // the reconciler, which for a read is nothing.
            .Action(
                Grafanas.UrlAction,
                ActionKind.Post,
                Grafanas.UrlPermission,
                secret: true,
                response: Grafanas.UrlResponse,
                handler: typeof(GrafanaUrlHandler)
            )
            .Display(
                "Managed Grafana",
                "Managed Grafanas",
                shortName: "grafana",
                summary: "An unmodified Grafana OSS instance in your cluster, provisioned with one monitor "
                + "workspace's metrics and logs as its datasources and reachable at a URL your pages "
                + "embed rendered dashboards from."
            )
            .Chart(Grafanas.ChartName)
            .SupportsTags()
            .RequiresCluster(Grafanas.ClusterIdPointer);
    }

    /// <summary>vCPU: the sizing preset's cpu, in cores.</summary>
    static MeterDerivation GrafanaVcpuDrawn { get; } =
        MeterDerivation.Of(
            "sizing.preset's cpu, in cores",
            ["/properties/sizing/preset"],
            body => KubeQuantity.TryParse(Grafanas.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(cores)
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "the sizing preset behind '/properties/sizing/preset' carries a cpu quantity that "
                    + "does not parse, so no reservation can be computed"
                )
        );

    /// <summary>Memory: the sizing preset's memory, in GiB.</summary>
    static MeterDerivation GrafanaMemoryDrawn { get; } =
        MeterDerivation.Of(
            "sizing.preset's memory, in GiB",
            ["/properties/sizing/preset"],
            body => KubeQuantity.TryGibibytes(Grafanas.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Result<decimal>.Failure(
                    ErrorCode.InternalError,
                    "the sizing preset behind '/properties/sizing/preset' carries a memory quantity "
                    + "that does not parse, so no reservation can be computed"
                )
        );
}
