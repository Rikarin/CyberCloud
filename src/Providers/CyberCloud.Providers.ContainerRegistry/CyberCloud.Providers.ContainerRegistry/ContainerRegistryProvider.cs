// ⚠ For `Result<decimal>`, which the quota derivations below return. `CyberCloud.Core.Resources` is
// global here and `CyberCloud.Core` itself is not; the `ErrorCode` alias in GlobalUsings still wins
// over the `Orleans.ErrorCode` this import would otherwise put back in play.
using CyberCloud.Core;
using System.Text.Json;

namespace CyberCloud.Providers.ContainerRegistry;

/// <summary>
///     A managed OCI container registry, on Harbor.
/// </summary>
/// <remarks>
///     <para>
///         [13 § Container Registry](../../../../docs/plan/13-compute-vm-containers.md) —
///         <i>"<c>CyberCloud.ContainerRegistry/registries</c> · M1 · 1.5 EM"</i>, <i>"Harbor, one
///         instance per tenant"</i>. The twelfth provider family.
///     </para>
///     <para>
///         ⚠ <b>THE OPERATOR IS ARCHIVED AND THE ROW IS BUILT ANYWAY, WHICH IS THE THIRD TIME THAT
///         SENTENCE HAS BEEN WRITTEN IN THIS TREE.</b> The full account is on
///         <c>ContainerRegistries</c>. What it costs here is the shape:
///         <c>CyberCloud.Messaging/natsClusters</c> established the operator-less shape at five
///         objects, <c>CyberCloud.Search/vectorStores</c> was refused partly over it, and this row
///         renders <b>fifteen</b> — every default a controller would have supplied is a decision taken
///         in this family, and each one is written where it is taken.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST TYPE IN THE TREE TO DECLARE <c>SupportsSoftDelete</c>, AND THE ARGUMENT IS
///         ABOUT THE DATA RATHER THAN ABOUT THE PLATFORM.</b> Eleven families declined it for one
///         shared reason — the manager did not read <c>SoftDeleteDays</c>, so a type advertising a
///         window the platform did not honour would be a promise made to the users most likely to test
///         it. docs/plan/08 § Soft delete is built now, so the question each type owes an answer to is
///         its own: <i>can the deleted thing genuinely be handed back?</i>
///         <list type="bullet">
///             <item>
///                 <b>Here it can, and the mechanism is Kubernetes' rather than this provider's.</b>
///                 A registry's images, its metadata database and its job queue are all on
///                 <c>PersistentVolumeClaim</c>s created by <c>volumeClaimTemplate</c>s, and deleting
///                 a <c>StatefulSet</c> does not delete those claims. So a soft-deleted registry is
///                 fifteen absent objects over three volumes that still hold every byte, and a restore
///                 re-applies the fifteen against the same claims.
///                 <see cref="ContainerRegistries.StatefulSetKind" />'s remarks say why the workloads
///                 are <c>StatefulSet</c>s and not <c>Deployment</c>s with claims beside them, and
///                 this is the reason.
///             </item>
///             <item>
///                 <b>And the credentials survive too</b>, because <c>ISecretWriter</c> has no delete
///                 and the reconciler's teardown leaves the vault document alone. A recovery window
///                 that handed back a registry nobody could log in to would be a window in name only.
///             </item>
///             <item>
///                 ⚠ <b>What is NOT closed: nothing removes the claims on a purge.</b> The window
///                 works; ending it early does not free the disks.
///                 <c>charts/managed/harbor/conformance.yaml § owed</c>,
///                 <c>purge-leaves-the-volumes-behind</c>.
///             </item>
///             <item>
///                 ⚠ <b>And restore and purge have no HTTP route.</b>
///                 <c>ResourceManagerService.RestoreAsync</c> and <c>PurgeAsync</c> exist, are tested
///                 by <c>SoftDeletePathTests</c>, and are reachable from no gateway stage — grepped
///                 rather than assumed. So what this declaration buys today is that a <c>DELETE</c>
///                 <i>parks</i> the resource instead of destroying it, its name is held, its quota
///                 stays committed and its ReBAC parent moves to the subscription. Recovering it needs
///                 a route somebody else owns. That is a smaller thing than the feature reads like,
///                 and it is stated rather than implied.
///             </item>
///         </list>
///     </para>
///     <para>
///         ⚠ <b>PIECE 5 IS BUILT AND THIS IS THE SHARPEST ROW IT HAS MET.</b> The three earlier
///         sightings of an unsafe default are things that are <i>unset</i>; <c>goharbor/harbor-helm</c>
///         ships <c>harborAdminPassword: "Harbor12345"</c> as a live, consumed <c>values.yaml</c>
///         default while randomising every other credential in the same template. See
///         <c>ContainerRegistries</c> for the three-layer account and for what Harbor's own core does
///         instead.
///     </para>
///     <para>
///         ⚠ <b>Piece 6's SECOND branch, third sighting.</b> There is no operator to ask, so the
///         <c>PodMonitor</c> is hand-written — and the hazard piece 6's correction names cannot arise,
///         because the labels the selector matches are written by this family onto pods created by this
///         family. <c>charts/managed/nats</c> proved that, <c>charts/managed/ferretdb</c> confirmed it,
///         and this is the third.
///     </para>
///     <para>
///         ⚠ <b>Piece 7 is declined for a stated reason, which docs/plan/12 § The pattern, once says
///         is worth more than another implementation of it.</b> A registry's images are content
///         addressed and immutable, and Harbor's own answer to losing them is
///         <i>replication to another registry</i> rather than a snapshot. The right backup for this row
///         is therefore the replication sub-resource docs/plan/13 names — which needs a second registry
///         to replicate to and is owed — and a Velero snapshot of the image volume would be a promise
///         about a 100 GiB volume that the platform would be restoring one tenant at a time.
///     </para>
/// </remarks>
public sealed class ContainerRegistryProvider : IResourceProvider {
    /// <summary>The CLI short form this type takes.</summary>
    /// <remarks>
    ///     ⚠ <b>Checked by hand against three dictionaries, for the reason
    ///     <c>charts/managed/seaweedfs/conformance.yaml § owed</c>'s
    ///     <c>short-name-collides-with-the-group</c> records and the sixth type in a row has had to
    ///     satisfy.</b> <c>CliEmitter</c> derives the CLI group key from the provider namespace's last
    ///     segment, lower-cased, so this namespace is already the group <c>containerregistry</c>;
    ///     System.CommandLine's <c>ValidTokens</c> builds ONE dictionary of every command token and
    ///     every alias in the tree, so a group and an alias that share a string throw
    ///     <c>ArgumentException: An item with the same key has already been added</c> on the first
    ///     parse of <i>any</i> command line. <c>registry</c> is not one of the twelve group keys, not
    ///     one of the sixteen declared short names, and not one of <c>CommandTree.ReservedGroups</c>'
    ///     nine — <c>ContainerRegistryDeclarationTests</c> asserts all three against literals.
    ///     <para>
    ///         ⚠ <b>Not <c>acr</c>, which docs/plan/21 § Grammar's example pattern would suggest.</b>
    ///         That paragraph's two examples are <c>aks</c> and <c>postgres</c> — a vendor acronym and
    ///         a product name — and this platform already spells the Kubernetes row <c>aks</c> for
    ///         Azure parity. <c>acr</c> would be a second borrowed acronym for a service whose own name
    ///         is a word people already type, and <c>registry</c> is what a person reaching for this
    ///         type would guess.
    ///     </para>
    /// </remarks>
    public const string ShortName = "registry";

    /// <inheritdoc />
    public string ProviderNamespace => ContainerRegistries.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(ContainerRegistries.TypePath)
            .ApiVersion(ContainerRegistries.V2026, ContainerRegistries.Schema2026)
            .Reconciler<ContainerRegistryReconciler>()
            // ⚠ THREE DERIVED METERS AND THE COUNT, AND EACH OF THE THREE IS A SUM OVER COMPONENTS
            // THAT ARE NOT THE SAME SIZE AS EACH OTHER — the third sighting of that shape, after
            // CyberCloud.Storage/accounts and CyberCloud.Search/services. What this one adds is that
            // ONE population is multiplied by a tenant-set replica count and the other TWO are fixed:
            //
            //     1 registry × preset  +  3 × replicas × 250m  +  2 × 250m
            //
            // A derivation copied from natsClusters — replicas × one figure — is right about nothing
            // here; one copied from Storage's masters-plus-filer shape misses the ×3.
            // ContainerRegistryQuotaTests.ChangingOnlyTheReplicaCountMovesThreeComponentsAndNotFive is
            // the one that fails on either copy, and it was run red against both.
            //
            // ⚠ EACH DERIVATION IS A PURE FUNCTION OF THE BODY AND MUST STAY ONE. The delete path
            // re-derives committed amounts from the resource's stored body through the same step the
            // create reserved with, so a derivation that read a clock or configuration would make a
            // delete return a different number than the create committed. ⚠ On a SOFT-DELETABLE type
            // that argument gets sharper rather than weaker: the amounts are returned on the PURGE
            // rather than on the delete (docs/plan/08 § Soft delete), so the body they are re-derived
            // from may be up to seven days older than the one that reserved them.
            .Meter(QuotaMeter.Vcpu, VcpuDrawn)
            .Meter(QuotaMeter.MemoryGb, MemoryDrawn)
            .Meter(QuotaMeter.StorageGb, StorageDrawn)
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action(
                ContainerRegistries.ListCredentialsAction,
                ActionKind.Post,
                ContainerRegistries.ListCredentialsPermission,
                secret: true,
                response: ContainerRegistries.ListCredentialsResponse,
                // ⚠ SYNCHRONOUS WITH A HANDLER, AND THE THREE-WAY CHOICE WAS DELIBERATE. A
                // long-running action answers 202 and drives the reconciler through the operation
                // grain — right for a node-image roll, wrong for a credential read, because the
                // result of a secret action must not travel on an operation record that anyone
                // holding `read` can poll. A synchronous action with NO handler is now refused by
                // name, which is the honest answer for eleven declarations across the catalogue and
                // would be the wrong one here: this handler is one vault read and there is nothing
                // stopping it existing.
                handler: typeof(ContainerRegistryListCredentialsHandler)
            )
            .Display(
                "Container registry",
                "Container registries",
                shortName: ShortName,
                summary: "A private OCI container registry on Harbor, with a web portal, an image "
                + "store and a seven-day recovery window."
            )
            .Chart(ContainerRegistries.ChartName)
            .SupportsTags()
            // ⚠ THE FIRST SupportsSoftDelete IN THE TREE. The argument for the window is in this
            // class's remarks; the three arguments below are for the three parameters.
            //
            //   • SEVEN DAYS, which is what docs/plan/06 § Tags, locks gives "types carrying data".
            //     It is declared on the TYPE and is therefore immutable by construction — docs/plan/08
            //     asks that retention be "set at creation and immutable afterwards", and a type-level
            //     window is the stronger form of that because there is no per-resource property for a
            //     caller to shorten.
            //
            //   • `purge` AND NOT `delete`. docs/plan/08 § Soft delete follows Azure in keeping
            //     `deletedVaults/purge/action` out of Key Vault Contributor: "may delete" and "may
            //     destroy permanently" are separable rights. Passing the delete permission here would
            //     make the window worth nothing against exactly the caller it protects against.
            //
            //   • A PURGE-PROTECTION FLAG, because a registry is the type where somebody wants one.
            //     Its images are what a tenant's production deployments pull; an accidental purge
            //     during the window is an outage that starts at the next pod restart rather than at
            //     the moment of the mistake. ProviderBuilder refuses this pointer unless every
            //     api-version declares it as a boolean, which is what stops it from being a protection
            //     that silently never engages.
            .SupportsSoftDelete(
                ContainerRegistries.SoftDeleteDays,
                ContainerRegistries.PurgePermission,
                ContainerRegistries.PurgeProtectionPointer
            )
            .RequiresCluster(ContainerRegistries.ClusterIdPointer);
    }

    // ── What a registry draws ──────────────────────────────────────────────────────────────────
    //
    // ⚠ `publicIps` IS NOT DECLARED, AND THE REASON IS THE THIRD DISTINCT ONE IN THE CATALOGUE.
    // CyberCloud.Messaging/natsClusters cannot declare it because QuotaGrain.TryReserveAsync refuses a
    // non-positive amount and an optional listener derives zero on the default path;
    // CyberCloud.Storage/accounts cannot because its operator's ServiceSpec has nowhere to put a CIDR
    // allow-list. Here the Service kind has `loadBalancerSourceRanges` and would carry one — so
    // neither earlier blocker applies, and the axis is still absent because what would be exposed is
    // an OCI registry over plain HTTP whose internal auth this row could not render. See
    // ContainerRegistries.RegistryConfigYaml. Closing it is an ingress and a bcrypt, in that order.

    /// <summary>vCPU: the registry at its preset, plus a fixed share for the other five components.</summary>
    /// <remarks>
    ///     ⚠ Refuses rather than reserving zero when a quantity does not parse. That happens only if
    ///     <c>sizing.preset</c> names a preset <see cref="ContainerRegistries.Presets" /> does not
    ///     carry — which the schema's <c>AllowedValues</c> makes unreachable from a validated body, and
    ///     which is exactly the drift worth failing on when somebody adds a preset to the enum and
    ///     forgets the table.
    /// </remarks>
    static MeterDerivation VcpuDrawn { get; } =
        MeterDerivation.Of(
            "sizing.cpu (from sizing.preset when unset) + (3 × replicas + 2) × 250m, in cores",
            [
                "/properties/replicas",
                "/properties/sizing/preset",
                "/properties/sizing/cpu"
            ],
            body => KubeQuantity.TryParse(ContainerRegistries.Resources(body).Cpu, out var cores)
            && KubeQuantity.TryParse(ContainerRegistries.ControlPlaneCpu, out var share)
                ? Result<decimal>.Success(cores + (ControlPlanePods(body) * share))
                : Unresolvable("cpu", "sizing.cpu or the sizing.preset behind it")
        );

    /// <summary>Memory: the same two populations, in gibibytes.</summary>
    static MeterDerivation MemoryDrawn { get; } =
        MeterDerivation.Of(
            "sizing.memory (from sizing.preset when unset) + (3 × replicas + 2) × 512Mi, in GiB",
            [
                "/properties/replicas",
                "/properties/sizing/preset",
                "/properties/sizing/memory"
            ],
            body =>
                KubeQuantity.TryGibibytes(ContainerRegistries.Resources(body).Memory, out var gibibytes)
                && KubeQuantity.TryGibibytes(ContainerRegistries.ControlPlaneMemory, out var share)
                    ? Result<decimal>.Success(gibibytes + (ControlPlanePods(body) * share))
                    : Unresolvable("memory", "sizing.memory or the sizing.preset behind it")
        );

    /// <summary>Storage: the image volume, the metadata database's and the job queue's.</summary>
    /// <remarks>
    ///     ⚠ <b>All three, because all three are provisioned and all three outlive a delete.</b> The
    ///     database's 10 GiB and Redis' 1 GiB are constants rather than properties — see
    ///     <see cref="ContainerRegistries.DatabaseVolumeSize" /> — and leaving them out would be the
    ///     same under-count as leaving out their pods. ⚠ It is a <b>filesystem</b> figure and not an
    ///     object-storage one: docs/plan/13 asks for the tenant's SeaweedFS bucket, which would have
    ///     moved this draw off <c>storageGb</c> and onto the account's own meters, and the cross-provider
    ///     seam is why it did not.
    /// </remarks>
    static MeterDerivation StorageDrawn { get; } =
        MeterDerivation.Of(
            "storage.size + 10Gi for the metadata database + 1Gi for the job queue, in GiB",
            ["/properties/storage/size"],
            body =>
                KubeQuantity.TryGibibytes(ContainerRegistries.StorageSize(body), out var images)
                && KubeQuantity.TryGibibytes(ContainerRegistries.DatabaseVolumeSize, out var database)
                && KubeQuantity.TryGibibytes(ContainerRegistries.RedisVolumeSize, out var queue)
                    ? Result<decimal>.Success(images + database + queue)
                    : Unresolvable("storage", "storage.size")
        );

    /// <summary>How many pods are sized by the platform rather than by the tenant.</summary>
    /// <remarks>
    ///     ⚠ <b>Three times the replica count plus two, and both terms are the finding.</b> Core, the
    ///     portal and the job service each run <c>/properties/replicas</c> pods; the database and Redis
    ///     each run exactly one whatever the body says, because each owns a <c>ReadWriteOnce</c> volume.
    ///     ⚠ The registry is <i>not</i> in this count — it is the population the preset sizes — and a
    ///     reader adding it here would double-charge the one component the tenant pays for by name.
    /// </remarks>
    static int ControlPlanePods(JsonElement body) => (3 * ContainerRegistries.Replicas(body)) + 2;

    static Result<decimal> Unresolvable(string what, string where) =>
        Result<decimal>.Failure(
            ErrorCode.InternalError,
            $"The {what} a container registry draws could not be read from {where}: the value is not a "
            + "Kubernetes quantity. The write is refused rather than reserved at zero, because a "
            + "resource that provisions against no quota is one nobody is charged for — docs/plan/06 "
            + "§ Quota."
        );
}
