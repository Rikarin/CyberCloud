using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Compute/disks</c> — a managed data disk: a blank CDI
///     <c>DataVolume</c> of a size and a class, attached to a virtual machine by name.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>A disk is provisioned, not populated, and <c>Converged</c> says so.</b> The bundle's
///         default class is <c>WaitForFirstConsumer</c>, so a blank claim with no pod on it sits in
///         CDI's <c>WaitForFirstConsumer</c> phase until a VM attaches it — and that is the correct
///         resting state of a disk nobody has attached, not a failure. <see cref="Cdi.IsProvisioned" />
///         is the readiness rule, and it is deliberately wider than <see cref="Images" />' <c>Succeeded</c>:
///         an image without its bytes is useless, a disk without a consumer is inventory.
///     </para>
///     <para>
///         ⚠ <b>Attaching is the VM's body naming this disk, and it takes effect at the VM's next
///         start.</b> KubeVirt applies a change to a <c>VirtualMachine</c>'s volumes to the running
///         instance only through hotplug, which is a different volume shape
///         (<c>hotpluggable: true</c>, added through the <c>addvolume</c> subresource) that this
///         platform does not render. So a disk added to a running VM is attached when the VM next
///         restarts, and KubeVirt reports <c>RestartRequired</c> until then.
///         <c>charts/managed/disk/conformance.yaml § owed</c>, <c>hot-attach-is-not-rendered</c>.
///     </para>
///     <para>
///         ⚠ <b>Nothing checks that a disk is attached to at most one VM.</b> Two VMs naming one disk
///         render two claims on one <c>ReadWriteOnce</c> volume; the second to start is refused by the
///         scheduler or the CSI driver rather than by this API, which cannot see another resource's
///         body. Same shape, same owed row, as <c>bucket-cluster-may-differ-from-its-accounts</c>.
///     </para>
/// </remarks>
public static class Disks {
    /// <summary>The provider namespace.</summary>
    public const string ProviderNamespace = Images.ProviderNamespace;

    /// <summary>The type path.</summary>
    public const string TypePath = "disks";

    /// <summary>The one api-version.</summary>
    public const string V2026 = Images.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/disk";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The name of the <c>DataVolume</c> — and of the claim a VM mounts — which is the resource's own.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     The resource's name with nothing added, for the reason <see cref="Images.ObjectNameOf" />
    ///     gives: a VM renders <c>persistentVolumeClaim.claimName</c> from the disk's resource name
    ///     without reading the disk.
    /// </remarks>
    public static string ObjectNameOf(string name) => name;

    /// <summary>The <c>DataVolume</c> a disk owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef DataVolumeRef(string ns, string name) =>
        new() { Kind = Cdi.DataVolumeKind, Namespace = ns, Name = ObjectNameOf(name) };

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     ⚠ <b><c>size</c> is immutable, and growing a disk is owed rather than half-offered.</b> CDI
    ///     expands a <c>DataVolume</c> when its claim's size is raised and the class allows expansion,
    ///     and shrinks nothing. A mutable size would be a PUT that works on one class, is refused by the
    ///     API server on another, and can never go down — three behaviours behind one property.
    ///     <c>charts/managed/disk/conformance.yaml § owed</c>, <c>resize-is-not-offered</c>.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the disk is billed in."
                ) {
                    Format = SchemaFormat.Region, Widget = WidgetHint.Region, Immutable = true, ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The disk's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the disk is provisioned in. Only a virtual machine in the "
                    + "same cluster and the same resource group can attach it."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The disk's size, in Kubernetes quantity form. ⚠ Immutable: growing a "
                    + "disk depends on the storage class and shrinking one is never possible, so a "
                    + "bigger disk is a new disk."
                ) { Pattern = KubeQuantity.Pattern, Immutable = true, DefaultJson = "\"" + DefaultSize + "\"", ExampleJson = "\"32Gi\"" },
                new(
                    "/properties/storageClass",
                    SchemaKind.Text,
                    Description: "The storage class the disk is on. Empty means the cluster's default, "
                    + "which on a bundle-installed cluster is node-local: one copy, on one node, and "
                    + "the machine that attaches the disk runs on that node."
                ) { Widget = WidgetHint.StorageClass, Immutable = true, MaxLength = 253, DefaultJson = "\"\"" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The size a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Size(JsonElement desired) => ComputeBodies.Text(ComputeBodies.Property(desired, "size"), DefaultSize);

    /// <summary>The storage class a body names, or empty for the cluster's default.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageClass(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Property(desired, "storageClass"), string.Empty);

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>DataVolume</c> a desired body becomes: a blank source and a claim.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>No <see cref="Cdi.ImmediateBindAnnotation" /></b> — the class's own binding mode
    ///     stands, so on node-local storage the disk lands on the node its first VM is scheduled to
    ///     rather than on the node CDI's helper pod happened to pick. <see cref="Cdi" /> carries the
    ///     argument.
    /// </remarks>
    public static string DataVolumeJson(string name, JsonElement desired) =>
        new JsonObject {
            ["kind"] = Cdi.DataVolumeKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(name) },
            ["spec"] = new JsonObject {
                ["source"] = new JsonObject { ["blank"] = new JsonObject() },
                ["storage"] = Cdi.Storage(Size(desired), StorageClass(desired))
            }
        }.ToJsonString();

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>Whether a <c>DataVolume</c> read back asks for what the desired body asks for.</summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>Containment, for the reason <see cref="Images.Matches" /> gives.</remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        if (ComputeBodies.Kind(objectJson) != Cdi.DataVolumeKind.Kind || ComputeBodies.Spec(objectJson) is not { } spec) {
            return false;
        }

        var storageClass = StorageClass(desired);

        return spec["source"]?["blank"] is JsonObject
            && Cdi.RequestedSize(spec) == Size(desired)
            && (storageClass.Length == 0 || Cdi.RequestedClass(spec) == storageClass);
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the disk is provisioned in.</param>
    /// <param name="size">The disk's size.</param>
    /// <param name="storageClass">The storage class, or empty for the default.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        string size = DefaultSize,
        string storageClass = "",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["size"] = size,
                ["storageClass"] = storageClass
            }
        }.ToJsonString();

    const string DefaultSize = "32Gi";
}
