namespace CyberCloud.Providers.ContainerInstance;

/// <summary>
///     Container groups — one or more containers run as one pod in a tenant's namespace.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/13 § Container Instances, M2 · 0.8 EM, and a provider namespace of its own</b>
///         — <c>CyberCloud.ContainerInstance</c>, not a fifth <c>CyberCloud.Compute</c> type — because
///         docs/plan/03's layout gives a provider namespace a family, and #28's review recorded that this
///         noun was a different family from the machines'.
///     </para>
///     <para>
///         ⚠ <b>The meters are the group's CPU and memory, as a pod-level budget.</b> A group's containers
///         share one limit (<see cref="ContainerGroups.PodJson" />'s remarks), so the reservation and the
///         enforcement are the same two numbers: <see cref="QuotaMeter.Vcpu" /> reserves the CPU quantity
///         in cores — <c>500m</c> is half of one — and <see cref="QuotaMeter.MemoryGb" /> the memory in
///         gibibytes. Billing derives vCPU-hours and GiB-hours from those two families, as it does for
///         every workload in the catalogue (<c>MeterCatalog.MetersOf</c>). No storage meter: a group has
///         no volume. No public-address meter: a public address is metered on the address's own type,
///         which the group only names.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c></b>, because a group carries no data — its containers'
///         filesystems go with the pod whatever the platform does — and a recovery window over a pod
///         would be a window over nothing.
///     </para>
/// </remarks>
public sealed class ContainerInstanceProvider : IResourceProvider {
    /// <summary>The type's CLI alias — <c>cyc containerinstance cg …</c>.</summary>
    public const string ShortName = "cg";

    /// <inheritdoc />
    public string ProviderNamespace => ContainerGroups.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(ContainerGroups.TypePath)
            .ApiVersion(ContainerGroups.V2026, ContainerGroups.Schema2026)
            .Reconciler<ContainerGroupReconciler>()
            // ⚠ PURE FUNCTIONS OF THE BODY, for the reason ComputeProvider's first ⚠ gives: the delete path
            // re-derives what the create committed from the stored body.
            .Meter(QuotaMeter.Vcpu, CpuDrawn)
            .Meter(QuotaMeter.MemoryGb, MemoryDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                ContainerGroups.LogsAction,
                ActionKind.Post,
                ContainerGroups.LogsPermission,
                request: ContainerGroups.LogsRequest,
                response: ContainerGroups.LogsResponse,
                handler: typeof(ContainerGroupActionHandler)
            )
            .Action(
                ContainerGroups.RestartAction,
                ActionKind.Post,
                ContainerGroups.RestartPermission,
                response: ContainerGroups.RestartResponse,
                handler: typeof(ContainerGroupActionHandler)
            )
            .Display(
                "Container group",
                "Container groups",
                ShortName,
                "One or more containers run together as a pod in your resource group: public or "
                + "private images, a CPU and memory budget they share, environment from vault handles, "
                + "ports on a subnet and an optional public address, with logs and restart as actions."
            )
            .Chart(ContainerGroups.ChartName)
            .SupportsTags()
            .RequiresCluster();
    }

    /// <summary>vCPU: the group's CPU quantity, in cores.</summary>
    static MeterDerivation CpuDrawn { get; } =
        MeterDerivation.Of(
            "cpu, in cores",
            ["/properties/cpu"],
            static body => KubeQuantity.TryParse(ContainerGroups.Cpu(body), out var cores) && cores > 0
                ? Result<decimal>.Success(cores)
                : Unresolvable("cpu")
        );

    /// <summary>Memory: the group's memory quantity, in gibibytes.</summary>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "memory, in GiB",
            ["/properties/memory"],
            static body => KubeQuantity.TryGibibytes(ContainerGroups.Memory(body), out var gibibytes) && gibibytes > 0
                ? Result<decimal>.Success(gibibytes)
                : Unresolvable("memory")
        );

    static Result<decimal> Unresolvable(string what) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a container group draws could not be read from its body: the value is not a positive "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a group that "
            + "runs against no quota is one nobody is charged for — docs/plan/06 § Quota."
        );
}
