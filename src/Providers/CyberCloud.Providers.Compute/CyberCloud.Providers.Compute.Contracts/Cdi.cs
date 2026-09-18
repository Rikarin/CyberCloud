using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Contracts;

/// <summary>
///     What the two disk-shaped types share: the CDI <c>DataVolume</c> kind, its phases, and the one
///     annotation that decides when a volume binds.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Spelled once for <see cref="Images" /> and <see cref="Disks" /> both.</b> An image and a
///         managed disk are the same Kubernetes object with a different <c>spec.source</c>, and a
///         phase compared against two spellings of <c>Succeeded</c> is a phase that stops matching in
///         one of them the day CDI renames it.
///     </para>
///     <para>
///         ⚠
///         <b>
///             <c>v1beta1</c>, which is the only version <c>charts/bundle/containerized-data-importer</c>
///             claims to serve
///         </b> — its <c>serves:</c> line names <c>cdi.kubevirt.io/v1beta1</c> alone,
///         and the Bundle gate requires every group/version a managed chart renders to be served by
///         exactly one component. Rendering <c>v1alpha1</c> here would fail that gate before it failed
///         a cluster.
///     </para>
/// </remarks>
public static class Cdi {
    /// <summary>The <c>DataVolume</c> — CDI's request to fill a PersistentVolumeClaim.</summary>
    public static GroupVersionKind DataVolumeKind { get; } =
        new() { Group = "cdi.kubevirt.io", Version = "v1beta1", Kind = "DataVolume", Plural = "datavolumes" };

    /// <summary>
    ///     The annotation that makes CDI bind a claim on a <c>WaitForFirstConsumer</c> class at once,
    ///     instead of waiting for a pod that will never come.
    /// </summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         On an image and never on a disk, and the asymmetry is the whole point of the two
    ///         types.
    ///     </b> An image is imported once and consumed by clones; nothing ever mounts it, so a
    ///     claim on a <c>WaitForFirstConsumer</c> class — which is what <c>charts/bundle/openebs-localpv</c>
    ///     installs — would stay unbound forever and the import would never start. A disk is mounted
    ///     by the VM that attaches it, and binding it early on node-local storage would pin the disk
    ///     to whichever node CDI's helper happened to land on, which is the node the VM then has to
    ///     run on. <see cref="Disks" /> leaves the class's own binding mode alone and calls the
    ///     <c>WaitForFirstConsumer</c> phase provisioned — see <see cref="IsProvisioned" />.
    /// </remarks>
    public const string ImmediateBindAnnotation = "cdi.kubevirt.io/storage.bind.immediate.requested";

    /// <summary>The phase CDI reports once the claim holds the bytes it was asked for.</summary>
    public const string Succeeded = "Succeeded";

    /// <summary>The phase of a claim that is provisioned and waits for a pod before it binds.</summary>
    public const string WaitForFirstConsumer = "WaitForFirstConsumer";

    /// <summary>
    ///     The phase CDI's populator path reports for the same situation — a claim that binds when
    ///     something consumes it.
    /// </summary>
    /// <remarks>
    ///     CDI 1.66 fills a <c>DataVolume</c> through a volume populator when the class allows it, and
    ///     the phase it then reports for "waiting for a consumer" is this one rather than
    ///     <see cref="WaitForFirstConsumer" />. KubeVirt's own VM controller treats the two as one —
    ///     <c>pkg/virt-controller/watch/vm/vm.go</c> lists
    ///     <c>
    /// Succeeded, WaitForFirstConsumer,
    ///     PendingPopulation
    ///     </c> in one case — and so does this platform.
    /// </remarks>
    public const string PendingPopulation = "PendingPopulation";

    /// <summary>The phase CDI reports when an import or a clone gave up.</summary>
    public const string Failed = "Failed";

    /// <summary>The <c>status.phase</c> an object read back carries, or empty when CDI has not written one.</summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    public static string Phase(string objectJson) => ComputeBodies.TextOf(ComputeBodies.Status(objectJson)?["phase"]);

    /// <summary>Whether a phase means the bytes are there.</summary>
    /// <param name="phase">A value of <see cref="Phase" />.</param>
    public static bool IsPopulated(string phase) => phase == Succeeded;

    /// <summary>
    ///     Whether a phase means the claim exists and only wants a consumer, which for a blank disk is
    ///     as far as CDI can take it.
    /// </summary>
    /// <param name="phase">A value of <see cref="Phase" />.</param>
    public static bool IsProvisioned(string phase) => phase is Succeeded or WaitForFirstConsumer or PendingPopulation;

    /// <summary>
    ///     The reason CDI gives for a <c>DataVolume</c> that is not progressing, from its
    ///     <c>Bound</c> and <c>Running</c> conditions, or empty when it gives none.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <remarks>
    ///     ⚠ CDI writes the reason a tenant needs — <c>no storage class</c>, <c>ImportInProgress</c>,
    ///     a pull error's own text — into <c>status.conditions[].message</c> and nowhere else. Reporting
    ///     the phase alone would tell a tenant their disk is <c>Pending</c> and not why.
    /// </remarks>
    public static string Detail(string objectJson) {
        var status = ComputeBodies.Status(objectJson);

        foreach (var type in new[] { "Running", "Bound" }) {
            var message = ComputeBodies.TextOf(ComputeBodies.Condition(status, type)?["message"]);

            if (message.Length > 0) {
                return message;
            }
        }

        return string.Empty;
    }

    /// <summary>
    ///     The <c>spec.storage</c> block a size and a class become — CDI's <c>storage</c> form rather
    ///     than the older <c>pvc</c> one.
    /// </summary>
    /// <param name="size">The requested size, a Kubernetes quantity.</param>
    /// <param name="storageClass">The class, or empty for the cluster's default.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             <c>storage</c> and not <c>pvc</c>, so that the volume mode comes from CDI's
    ///             <c>StorageProfile</c> for the class
    ///         </b> rather than from a guess written here — a
    ///         node-local hostpath class is <c>Filesystem</c> and a replicated block one may not be,
    ///         which are the two stages <c>charts/bundle/openebs-localpv/component.yaml</c> § which
    ///         stage is on describes. <c>storageClassName</c> is written only when the body names a
    ///         class: an empty string here is a real value to the API server ("no class at all"),
    ///         not "the default".
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE ACCESS MODE IS WRITTEN, AND IT WAS NOT UNTIL THE FIRST REAL RUN.</b> This
    ///         method left it to the profile too, and CDI 1.66 answered
    ///         <i>"no accessMode specified in StorageProfile openebs-hostpath"</i> on the first
    ///         <c>DataVolume</c> ever applied to a real CDI — <c>KubeVirtOnAnEmptyCluster</c>,
    ///         2026-09-17. CDI ships profile entries for the provisioners it knows and
    ///         <c>openebs.io/local</c> is not one; with no entry it fills nothing and refuses rather
    ///         than guessing. <see cref="ReadWriteOnce" /> is what a node-local disk can be and what
    ///         every KubeVirt disk works with; what it costs is live migration, which needs
    ///         <c>ReadWriteMany</c> and is recorded at
    ///         <c>charts/managed/disk/conformance.yaml § owed</c>, <c>access-mode-is-read-write-once</c>,
    ///         beside the bundle's half — a <c>StorageProfile</c> for its own class.
    ///     </para>
    /// </remarks>
    public static JsonObject Storage(string size, string storageClass) {
        var storage = new JsonObject {
            ["accessModes"] = new JsonArray(ReadWriteOnce),
            ["resources"] = new JsonObject { ["requests"] = new JsonObject { ["storage"] = size } }
        };

        if (storageClass.Length > 0) {
            storage["storageClassName"] = storageClass;
        }

        return storage;
    }

    /// <summary>The one access mode every claim this family renders asks for — see <see cref="Storage" />.</summary>
    public const string ReadWriteOnce = "ReadWriteOnce";

    /// <summary>The requested size of a <c>DataVolume</c> read back, or empty.</summary>
    /// <param name="spec">The object's <c>spec</c>.</param>
    public static string RequestedSize(JsonObject? spec) =>
        ComputeBodies.TextOf(spec?["storage"]?["resources"]?["requests"]?["storage"]);

    /// <summary>The class a <c>DataVolume</c> read back names, or empty.</summary>
    /// <param name="spec">The object's <c>spec</c>.</param>
    public static string RequestedClass(JsonObject? spec) =>
        ComputeBodies.TextOf(spec?["storage"]?["storageClassName"]);
}
