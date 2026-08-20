// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;

namespace CyberCloud.Providers.ContainerService;

/// <summary>
///     Managed Kubernetes — a cluster and the node pools inside it, on Cluster API, Kamaji and KubeVirt.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE FIRST PROVIDER WHOSE PRODUCT IS A KUBERNETES API SERVER.</b> The whole argument is
///         on <see cref="ManagedClusters" /> and it is the first thing to read on this row; the short
///         form is that every other family renders objects into a cluster the platform owns, and this
///         one renders objects whose <i>effect</i> is a second cluster the tenant then talks to
///         directly. That changes what <c>Matches</c> can claim, what drift means, and what
///         <c>Converged</c> is allowed to say.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST TYPE IN THE TREE TO DRAW <see cref="QuotaMeter.Clusters" />.</b> That meter
///         has existed since the quota grain was written — <c>QuotaGrain.Defaults</c> gives it 5 — and
///         <c>MeterCatalog</c> already bills <c>BillingMeter.ClusterHours</c> off it, described as
///         <i>"Managed Kubernetes clusters × hours"</i>. Nine families shipped without declaring it,
///         because none of them is a cluster. It is declared through <c>Meters(...)</c> rather than a
///         derivation because a cluster is exactly one cluster and no arithmetic makes that clearer.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST CHILD TYPE THAT CHANGES ITS PARENT'S CAPACITY, AND THEREFORE THE FIRST WHOSE
///         QUOTA IS NOT A CREATE-TIME CONSTANT.</b> <c>CyberCloud.Storage/accounts/buckets</c> draws
///         <c>QuotaMeter.Resources</c> and nothing else, because a bucket is a ceiling inside capacity
///         its account already reserved. An agent pool <i>is</i> the capacity: a cluster with no pool
///         has no worker nodes. And with an autoscaler on, the number of machines is moved by a
///         controller this platform does not observe — see <see cref="AgentPools.EffectiveCount" />,
///         which reserves the ceiling rather than the current count and says what that trade costs.
///     </para>
///     <para>
///         ⚠ <b>Three of the things docs/plan/13 asks this row for are not built, and each is named
///         rather than implied by an absence.</b> <c>listCredentials</c> has a declared response shape
///         and no handler, because <see cref="IResourceTypeBuilder.Action" /> takes no handler on any
///         type in this platform; <c>addons</c> needs a connection to the cluster this provider
///         creates, and nothing in the tree calls <c>IClusterConnectionGrain.AttachAsync</c>; and the
///         version-skew rule docs/plan/13 says <i>"the API enforces"</i> spans two resources, which
///         <see cref="ResourceSchema" /> cannot see. All three are at
///         <c>charts/managed/kubernetes/conformance.yaml § owed</c>.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, for the reason the nine providers before this one give</b>:
///         the manager did not read <c>SoftDeleteDays</c>, and declaring a recovery window the
///         platform does not honour would be a promise made to the users most likely to test it.
///         ⚠ <b>THAT REASON HAS EXPIRED</b> — docs/plan/08 § Soft delete is built and the manager
///         honours the declaration: a <c>DELETE</c> of a type declaring a window parks the resource at
///         <c>IndexEntryState.SoftDeleted</c> so its old address answers the canonical <c>404</c>,
///         holds its name, keeps its committed quota, moves its ReBAC parent edge to the subscription
///         and drops its direct role assignments; a restore reverses it and a purge, under its own
///         permission, ends it. ⚠ <b>And the second sentence below is the one that still decides
///         it, which is the only kind of reason this line should ever carry.</b> It would be a
///         strange promise on this type anyway — a soft-deleted cluster whose worker VMs are gone is
///         not a cluster anybody can be handed back, so the window would hold capacity for a recovery
///         that cannot be delivered. That is a judgement about this type rather than about the
///         platform, and unlike the platform's it has not changed.
///     </para>
/// </remarks>
public sealed class ContainerServiceProvider : IResourceProvider {
    /// <summary>The cluster type's CLI alias, spelled once so a test can name it.</summary>
    /// <remarks>
    ///     ⚠ <b><c>aks</c>, and it is docs/plan/21 § Grammar's own example rather than a choice.</b>
    ///     That section's alias table reads <i>"<c>aks</c> → <c>containerservice managed-cluster</c>,
    ///     <c>postgres</c> → <c>dbforpostgresql server</c>"</i>, so this is the second alias in the
    ///     tree that the document names by name.
    /// </remarks>
    public const string ClusterShortName = "aks";

    /// <summary>The pool type's CLI alias.</summary>
    public const string PoolShortName = "nodepool";

    /// <inheritdoc />
    public string ProviderNamespace => ManagedClusters.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(ManagedClusters.TypePath)
            .ApiVersion(ManagedClusters.V2026, ManagedClusters.Schema2026)
            .Reconciler<ManagedClusterReconciler>()
            // ⚠ TWO DERIVED METERS, THE RESOURCE COUNT, AND — FOR THE FIRST TIME IN THIS TREE —
            // QuotaMeter.Clusters. Each derivation is a PRODUCT OF THREE: replicas × containers per
            // replica × the per-container figure. That is a fifth shape after the four MeterDerivation
            // had already been asked for:
            //
            //   CyberCloud.DBforPostgreSQL/servers  an amount is a quantity STRING, not a number
            //   CyberCloud.Messaging/natsClusters   a PRODUCT of a replica count and one figure
            //   CyberCloud.Storage/accounts         a SUM over HETEROGENEOUS components
            //   CyberCloud.Analytics/clickhouse…    a PRODUCT and a SUM at once
            //   here                                a product whose MIDDLE factor is in NO PROPERTY
            //
            // ⚠ AND THE MIDDLE FACTOR IS WHERE A COPY GOES WRONG AND STAYS PLAUSIBLE. A Kamaji
            // control-plane replica is three containers — kube-apiserver, kube-controller-manager and
            // kube-scheduler, which the CRD takes as three separate component blocks — so a derivation
            // that read `controlPlane.replicas` and multiplied by one figure would reserve a third of
            // what the control plane costs, on every cluster, and the resource would provision, read
            // back and converge. ManagedClusters.ControlPlaneContainersPerReplica is the one place
            // that number is spelled, and ManagedClusterQuotaTests.AControlPlaneReplicaIsThreeContainers
            // is what fails on the copy.
            //
            // ⚠ NO QuotaMeter.StorageGb, AND THAT IS A CONSEQUENCE OF THE DATASTORE BEING SHARED. A
            // Kamaji control plane keeps no volume of its own: its state is in a Kamaji DataStore,
            // which is cluster-scoped, platform-owned and named rather than created here — see
            // ManagedClusters.DataStoreName. The day ADR-009's dedicated-etcd-per-tenant is honoured,
            // this type gains a storage meter and that meter is the evidence the change landed.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with — ResourceManagerService.CommittedBy — so a derivation that read a
            // clock or configuration would make a delete return a different number than the create
            // committed, and quota would drift upward on every create/delete cycle.
            .Meter(QuotaMeter.Vcpu, ControlPlaneVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, ControlPlaneMemoryDrawn)
            .Meters(QuotaMeter.Clusters, QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                ManagedClusters.ListCredentialsAction,
                ActionKind.Post,
                ManagedClusters.ListCredentialsPermission,
                secret: true,
                response: ManagedClusters.ListCredentialsResponse,
                handler: typeof(ManagedClusterListCredentialsHandler)
            )
            // ⚠ `aks` AND `nodepool`, AND WHAT THEY HAVE TO STAY CLEAR OF IS THIS GROUP AND EACH
            // OTHER. CliEmitter derives the CLI GROUP key from the provider namespace's last segment,
            // lower-cased, so this namespace is already the group `containerservice`; a short name
            // equal to its own group's key, to a sibling's command name, or to a sibling's short name
            // gives one `cyc containerservice …` token two meanings. CliTokens carries the rule and
            // CliTokenTests carries the measurements.
            //
            // ⚠ THE THREE LISTS THIS PARAGRAPH HELD ARE GONE, AND TWO OF THEM ASKED THE WRONG
            // QUESTION. Measured against System.CommandLine 2.0.10 the token dictionary is per PARENT
            // command, so neither the nine other group keys nor CommandTree.ReservedGroups' nine can
            // collide with an alias that sits under `containerservice` — a reserved group is a ROOT
            // command, and CommandTree throws on a generated GROUP taking one of the nine while the
            // root command is built, which cyc.Tests.ReservedGroupTests asserts over the whole tree.
            // ProviderRegistry.Build derives the real question from what is registered;
            // ManagedClusterDeclarationTests.NoShortNameHereGivesACycTokenTwoMeanings asks it here.
            .Display(
                "Managed Kubernetes cluster",
                "Managed Kubernetes clusters",
                shortName: ClusterShortName,
                summary: "A Kubernetes cluster whose control plane runs as pods in the management "
                + "cluster and whose workers are isolated virtual machines. Node pools are a child "
                + "resource."
            )
            .Chart(ManagedClusters.ChartName)
            .SupportsTags()
            .RequiresCluster(ManagedClusters.ClusterIdPointer)
            // ── The child, docs/plan/13 § Managed Kubernetes' first sub-resource ────────────────
            //
            // ⚠ A CHILD THAT DRAWS REAL QUOTA, WHICH THE ONLY OTHER SHIPPING CHILD DOES NOT. A bucket
            // declares Meters(Resources) and argues — correctly — that a derived storage meter there
            // would reserve the same gibibyte twice. A node pool is the opposite case: the cluster
            // reserves its control plane and NOTHING ELSE, so every worker VM's vCPU, memory and disk
            // is reserved here or nowhere.
            //
            // ⚠ AND THE AMOUNT IS AutoscaleEnabled ? maxCount : count, WHICH IS THE ONLY METER IN THE
            // CATALOGUE WHOSE INPUT IS A CEILING RATHER THAN A SIZE. AgentPools.EffectiveCount carries
            // the argument; AgentPoolQuotaTests.EnablingAutoscalingReservesTheCeilingRatherThanTheCount
            // is what fails on the obvious implementation.
            .ResourceType(AgentPools.TypePath)
            .ApiVersion(AgentPools.V2026, AgentPools.Schema2026)
            .Reconciler<AgentPoolReconciler>()
            .Meter(QuotaMeter.Vcpu, PoolVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, PoolMemoryDrawn)
            .Meter(QuotaMeter.StorageGb, PoolStorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                AgentPools.UpgradeNodeImageAction,
                ActionKind.Post,
                AgentPools.UpgradeNodeImagePermission,
                longRunning: true
            )
            .Display(
                "Node pool",
                "Node pools",
                shortName: PoolShortName,
                summary: "A group of identically sized worker virtual machines in a managed Kubernetes "
                + "cluster, optionally under a cluster-autoscaler."
            )
            .Chart(AgentPools.ChartName)
            .SupportsTags()
            .RequiresCluster(AgentPools.ClusterIdPointer);
    }

    // ── What a cluster draws ───────────────────────────────────────────────────────────────────
    //
    // ⚠ `publicIps` IS NOT DECLARED, AND THE REASON IS A THIRD ONE — NEITHER natsClusters' NOR
    // CyberCloud.Storage/accounts'. Their blockers are QuotaGrain.TryReserveAsync refusing a
    // non-positive amount (a conditional external listener) and an operator with no
    // loadBalancerSourceRanges field to render an allow-list into. Here the type declares no external
    // exposure AT ALL — ManagedClusters.Schema2026's remarks carry the argument — so there is no
    // address to charge for on any body. The axis is absent because the product is deliberately
    // unreachable from outside, which is a decision rather than a gap in the meter.

    /// <summary>vCPU: every control-plane container of every replica.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when the quantity does not parse. That happens only if
    ///     <see cref="ManagedClusters.ControlPlaneCpu" /> stops being a Kubernetes quantity, which is
    ///     exactly the drift worth failing on.
    /// </remarks>
    static MeterDerivation ControlPlaneVcpuDrawn { get; } =
        MeterDerivation.Of(
            "controlPlane.replicas × 3 containers × 500m, in cores",
            ["/properties/controlPlane/replicas"],
            body => KubeQuantity.TryParse(ManagedClusters.ControlPlaneCpu, out var cores)
                ? Result<decimal>.Success(
                    ManagedClusters.ControlPlaneReplicas(body)
                    * ManagedClusters.ControlPlaneContainersPerReplica
                    * cores
                )
                : Unresolvable("cpu", "the platform's per-container control-plane figure")
        );

    /// <summary>Memory: the same population, in gibibytes.</summary>
    static MeterDerivation ControlPlaneMemoryDrawn { get; } =
        MeterDerivation.Of(
            "controlPlane.replicas × 3 containers × 1Gi, in GiB",
            ["/properties/controlPlane/replicas"],
            body => KubeQuantity.TryGibibytes(ManagedClusters.ControlPlaneMemory, out var gibibytes)
                ? Result<decimal>.Success(
                    ManagedClusters.ControlPlaneReplicas(body)
                    * ManagedClusters.ControlPlaneContainersPerReplica
                    * gibibytes
                )
                : Unresolvable("memory", "the platform's per-container control-plane figure")
        );

    // ── What a node pool draws ─────────────────────────────────────────────────────────────────

    /// <summary>vCPU: every machine the pool may run, at its instancetype's believed size.</summary>
    /// <remarks>
    ///     ⚠ <b>Every <c>Reads</c> pointer below includes the autoscale block, and it has to.</b>
    ///     <see cref="MeterDerivation.Reads" /> is what the generated document publishes as the meter's
    ///     inputs; a derivation whose amount depends on <c>autoscale.maxCount</c> and whose declared
    ///     reads did not mention it would publish a lie that no test in the platform compares.
    /// </remarks>
    static MeterDerivation PoolVcpuDrawn { get; } =
        MeterDerivation.Of(
            "(autoscale.enabled ? autoscale.maxCount : count) × the size preset's cpu, in cores",
            [
                "/properties/count",
                "/properties/size",
                "/properties/autoscale/enabled",
                "/properties/autoscale/maxCount"
            ],
            body => KubeQuantity.TryParse(AgentPools.Resources(body).Cpu, out var cores)
                ? Result<decimal>.Success(AgentPools.EffectiveCount(body) * cores)
                : Unresolvable("cpu", "the size preset")
        );

    /// <summary>Memory: the same population, in gibibytes.</summary>
    static MeterDerivation PoolMemoryDrawn { get; } =
        MeterDerivation.Of(
            "(autoscale.enabled ? autoscale.maxCount : count) × the size preset's memory, in GiB",
            [
                "/properties/count",
                "/properties/size",
                "/properties/autoscale/enabled",
                "/properties/autoscale/maxCount"
            ],
            body => KubeQuantity.TryGibibytes(AgentPools.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(AgentPools.EffectiveCount(body) * gibibytes)
                : Unresolvable("memory", "the size preset")
        );

    /// <summary>Storage: every machine's root volume.</summary>
    /// <remarks>
    ///     ⚠ <b>The root volume only, and a tenant's own PersistentVolumeClaims are not counted
    ///     here.</b> A workload inside the produced cluster provisions storage through that cluster's
    ///     own CSI driver, against volumes in the management cluster, and this platform sees none of
    ///     it — docs/plan/09 § Kubernetes in Kubernetes puts that on <c>kubevirt-csi</c>. So the
    ///     figure below is the floor a pool costs, not the ceiling, and the difference is real:
    ///     <c>conformance.yaml § owed</c>, <c>tenant-pvcs-are-not-metered</c>.
    /// </remarks>
    static MeterDerivation PoolStorageDrawn { get; } =
        MeterDerivation.Of(
            "(autoscale.enabled ? autoscale.maxCount : count) × osDiskSize, in GiB",
            [
                "/properties/count",
                "/properties/osDiskSize",
                "/properties/autoscale/enabled",
                "/properties/autoscale/maxCount"
            ],
            body => KubeQuantity.TryGibibytes(AgentPools.OsDiskSize(body), out var gibibytes)
                ? Result<decimal>.Success(AgentPools.EffectiveCount(body) * gibibytes)
                : Unresolvable("storage", "osDiskSize")
        );

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a managed Kubernetes resource draws could not be read from {where}: the value "
            + "is not a Kubernetes quantity. The write is refused rather than reserved at zero, because "
            + "a resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
