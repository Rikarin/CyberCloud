namespace CyberCloud.Providers.Compute;

/// <summary>
///     Virtual machines, the disks they attach and the images they boot from — on KubeVirt and CDI.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ROW THAT MAKES THE PLATFORM A CLOUD RATHER THAN A MANAGED-DATABASE SERVICE</b>, in
///         #28's words, and the core of it rather than the whole: docs/plan/13 § Virtual Machines' three
///         nouns are here, and its scale sets and docs/plan/13 § Container Instances are not. Each
///         absence is a row in <c>charts/managed/virtual-machine/conformance.yaml § owed</c> rather
///         than an implication of the type list.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST FAMILY WITH A POWER STATE, AND THE FIRST WHOSE ACTIONS WRITE THE CLUSTER.</b>
///         Every handler before <see cref="VirtualMachinePowerHandler" /> read — a status, a
///         credential, a constant. <c>start</c>, <c>stop</c> and <c>restart</c> apply and delete
///         objects, under the same field manager the reconciler uses, and
///         <see cref="VirtualMachines" />' class remarks carry what that decides and what it costs.
///     </para>
///     <para>
///         ⚠ <b>Three types and no child</b>, although a disk and a machine look like a pair. A managed
///         disk outlives the machine it is attached to — that is the point of the word <i>managed</i>
///         — and a child type shares its parent's lifetime by construction. The join is a name in the
///         machine's body, resolved to a claim in the same namespace, exactly as a machine's image is.
///     </para>
///     <para>
///         ⚠ <b>No <c>SupportsSoftDelete</c>, and the reason is this type's rather than the
///         platform's.</b> docs/plan/08 § Soft delete is built. A soft-deleted machine would hold its
///         root clone and its cloud-init Secret for the window, which is recoverable — but a disk
///         detached by the machine's teardown and reattached by its restore is a disk another machine
///         may have taken in between, and nothing here can see that. The window lands with the check.
///     </para>
/// </remarks>
public sealed class ComputeProvider : IResourceProvider {
    /// <summary>The machine type's CLI alias — <c>cyc compute vm …</c>.</summary>
    public const string MachineShortName = "vm";

    /// <summary>The disk type's CLI alias.</summary>
    public const string DiskShortName = "disk";

    /// <summary>The image type's CLI alias.</summary>
    public const string ImageShortName = "image";

    /// <inheritdoc />
    public string ProviderNamespace => VirtualMachines.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(VirtualMachines.TypePath)
            .ApiVersion(VirtualMachines.V2026, VirtualMachines.Schema2026)
            .Reconciler<VirtualMachineReconciler>()
            // ⚠ THE SIZE TABLE IS WHAT THE GUEST GETS AND WHAT QUOTA RESERVES, and that is the
            // sentence AgentPools.Resources could not write about its own table. The render puts
            // Sizes[size].Cores into domain.cpu.cores and Sizes[size].Memory into domain.memory.guest,
            // so a body that reserves 2 cores runs 2 cores.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE — the delete path
            // re-derives committed amounts from the stored body through ResourceManagerService
            // .CommittedBy, so a derivation that read a clock or configuration would make a delete
            // return a different number than the create committed.
            //
            // ⚠ THE ROOT DISK IS METERED HERE AND A DATA DISK IS METERED ON ITS OWN TYPE. A machine
            // that summed its data disks would reserve every attached gibibyte twice — once on the disk
            // that owns the claim and once here — which is the double-count seaweedfs-bucket's manifest
            // records as the reason a bucket draws no storage meter.
            .Meter(QuotaMeter.Vcpu, MachineVcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, MachineMemoryDrawn)
            .Meter(QuotaMeter.StorageGb, MachineStorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            // ⚠ THREE SYNCHRONOUS ACTIONS ON ONE HANDLER. IResourceActionHandler.Action is empty on
            // VirtualMachinePowerHandler, which the seam documents as "serves every action on Type" —
            // the ordinary shape for listKeys beside regenerateKeys — and the handler switches on
            // ActionContext.Action. Synchronous rather than long-running because a long-running action
            // re-runs the reconciler and the reconciler cannot see which action was asked for; the
            // handler's own apply is the whole of the work, and a power change that answered 202 and
            // polled would be a caller polling for an object it could have read.
            .Action(
                VirtualMachines.StartAction,
                ActionKind.Post,
                VirtualMachines.PowerPermission,
                response: VirtualMachines.PowerResponse,
                handler: typeof(VirtualMachinePowerHandler)
            )
            .Action(
                VirtualMachines.StopAction,
                ActionKind.Post,
                VirtualMachines.PowerPermission,
                response: VirtualMachines.PowerResponse,
                handler: typeof(VirtualMachinePowerHandler)
            )
            .Action(
                VirtualMachines.RestartAction,
                ActionKind.Post,
                VirtualMachines.PowerPermission,
                response: VirtualMachines.PowerResponse,
                handler: typeof(VirtualMachinePowerHandler)
            )
            // ⚠ `vm`, `disk` AND `image` UNDER THE GROUP `compute`, and CliTokens' rule is that none may
            // equal the group key, a sibling's command name (virtual-machines, disks, images) or a
            // sibling's short name. ComputeDeclarationTests asks the derived question rather than
            // keeping a list.
            .Display(
                "Virtual machine",
                "Virtual machines",
                shortName: MachineShortName,
                summary: "A virtual machine on KubeVirt: a size from the platform catalogue, a root "
                + "disk cloned from an image, managed disks by name, a tenant subnet, and cloud-init "
                + "from a vault handle. Start, stop and restart are actions; stop releases compute "
                + "and keeps every disk."
            )
            .Chart(VirtualMachines.ChartName)
            .SupportsTags()
            .RequiresCluster(VirtualMachines.ClusterIdPointer)
            // ── Managed disks ─────────────────────────────────────────────────────────────────────
            .ResourceType(Disks.TypePath)
            .ApiVersion(Disks.V2026, Disks.Schema2026)
            .Reconciler<DiskReconciler>()
            .Meter(QuotaMeter.StorageGb, DiskStorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Display(
                "Managed disk",
                "Managed disks",
                shortName: DiskShortName,
                summary: "A blank data disk of a size and a storage class, provisioned on its own and "
                + "attached to a virtual machine by name. It outlives the machine."
            )
            .Chart(Disks.ChartName)
            .SupportsTags()
            .RequiresCluster(Disks.ClusterIdPointer)
            // ── Images ────────────────────────────────────────────────────────────────────────────
            .ResourceType(Images.TypePath)
            .ApiVersion(Images.V2026, Images.Schema2026)
            .Reconciler<ImageReconciler>()
            .Meter(QuotaMeter.StorageGb, ImageStorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Display(
                "Image",
                "Images",
                shortName: ImageShortName,
                summary: "A bootable disk image imported once into your resource group — one of the "
                + "platform's Ubuntu and Debian cloud images, pinned by digest, or a container disk "
                + "or HTTP address you supply — and cloned by every machine that boots from it."
            )
            .Chart(Images.ChartName)
            .SupportsTags()
            .RequiresCluster(Images.ClusterIdPointer);
    }

    // ── What a machine draws ───────────────────────────────────────────────────────────────────

    /// <summary>vCPU: the size's cores.</summary>
    static MeterDerivation MachineVcpuDrawn { get; } =
        MeterDerivation.Of(
            "the size's cores",
            ["/properties/size"],
            body => VirtualMachines.Resources(body) is { Cores: > 0 } size
                ? Result<decimal>.Success(size.Cores)
                : Unresolvable("cpu", "the size catalogue")
        );

    /// <summary>Memory: the size's guest memory, in gibibytes.</summary>
    static MeterDerivation MachineMemoryDrawn { get; } =
        MeterDerivation.Of(
            "the size's guest memory, in GiB",
            ["/properties/size"],
            body => KubeQuantity.TryGibibytes(VirtualMachines.Resources(body).Memory, out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Unresolvable("memory", "the size catalogue")
        );

    /// <summary>Storage: the root disk. Data disks are metered on their own type.</summary>
    static MeterDerivation MachineStorageDrawn { get; } =
        MeterDerivation.Of(
            "osDiskSize, in GiB",
            ["/properties/osDiskSize"],
            body => KubeQuantity.TryGibibytes(VirtualMachines.OsDiskSize(body), out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Unresolvable("storage", "osDiskSize")
        );

    /// <summary>Storage: the disk's size.</summary>
    static MeterDerivation DiskStorageDrawn { get; } =
        MeterDerivation.Of(
            "size, in GiB",
            ["/properties/size"],
            body => KubeQuantity.TryGibibytes(Disks.Size(body), out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Unresolvable("storage", "size")
        );

    /// <summary>Storage: the claim the image is imported into.</summary>
    static MeterDerivation ImageStorageDrawn { get; } =
        MeterDerivation.Of(
            "size, in GiB",
            ["/properties/size"],
            body => KubeQuantity.TryGibibytes(Images.Size(body), out var gibibytes)
                ? Result<decimal>.Success(gibibytes)
                : Unresolvable("storage", "size")
        );

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a compute resource draws could not be read from {where}: the value is not a "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
