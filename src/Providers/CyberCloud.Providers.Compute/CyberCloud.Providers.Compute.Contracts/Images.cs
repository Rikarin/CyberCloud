using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Compute.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Compute/images</c> — a bootable disk image, imported
///     once into a tenant's resource group and cloned by every virtual machine that boots from it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE ROW #28 SAID THIS FAMILY WOULD GET STUCK ON, AND WHERE IT DID NOT.</b> The issue's
///         evidence is <c>quay.io/capk/ubuntu-2404-container-disk</c>: four tags, no automation, and
///         <i>
///             "a VM catalogue means building and hosting images, which is infrastructure rather than a
///             provider"
///         </i>. That is true of a <b>Kubernetes node</b> image, which has to carry a kubelet
///         at a pinned minor. A plain cloud image does not: the <c>containerdisks</c> project publishes
///         Ubuntu and Debian as container disks, rebuilt from the distributions' own cloud images, and
///         <see cref="Catalogue" /> pins them <b>by digest</b> — so the catalogue is a table of four
///         lines rather than an image pipeline, and a digest is a checksum the puller verifies rather
///         than one this platform would have to compute.
///     </para>
///     <para>
///         ⚠
///         <b>
///             An image is a CDI <c>DataVolume</c> that binds immediately, and that one annotation is
///             what makes it importable at all
///         </b> — <see cref="Cdi.ImmediateBindAnnotation" />. The
///         storage class the bundle installs is <c>WaitForFirstConsumer</c>, and an image has no
///         consumer: it is the <i>source</i> of a clone, never a mounted volume. Without the annotation
///         the claim never binds, the import never starts, and the tenant sees an image that is
///         <c>Pending</c> forever with nothing anywhere saying why. Measured on the first cluster-backed
///         run — <c>KubeVirtOnAnEmptyCluster</c> is where.
///     </para>
///     <para>
///         ⚠ <b>Two sources and one <c>url</c>, whose scheme picks CDI's importer.</b> A body says
///         <c>catalogue</c> and a <see cref="Catalogue" /> name, or <c>url</c> and an address:
///         <c>docker://</c> is CDI's registry importer and <c>http://</c> or <c>https://</c> its HTTP
///         one. A separate <c>kind: http | registry</c> beside the address would be a second spelling
///         of the scheme, and the two would disagree the first time a body carried one of each.
///     </para>
///     <para>
///         ⚠ <b>No checksum property on a <c>url</c> source, because nothing would verify it.</b> CDI's
///         HTTP importer takes a URL, a secret and a certificate bundle and no digest; a
///         <c>sha256</c> the platform recorded and never compared would be the record-not-a-pin shape
///         <c>charts/bundle/bundle.yaml § owed</c>, <c>images-are-not-pinned-by-digest</c>, spends a
///         page on. A registry <c>url</c> can carry its digest inline, and the catalogue does.
///         <c>charts/managed/image/conformance.yaml § owed</c>, <c>http-imports-are-unverified</c>.
///     </para>
/// </remarks>
public static class Images {
    /// <summary>The provider namespace.</summary>
    public const string ProviderNamespace = "CyberCloud.Compute";

    /// <summary>The type path.</summary>
    public const string TypePath = "images";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/image";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The body spelling of a source taken from <see cref="Catalogue" />.</summary>
    public const string CatalogueSource = "catalogue";

    /// <summary>The body spelling of a source the tenant addresses themselves.</summary>
    public const string UrlSource = "url";

    /// <summary>The two source kinds, in the order the schema offers them.</summary>
    public static ImmutableArray<string> SourceKinds { get; } = [CatalogueSource, UrlSource];

    /// <summary>The scheme CDI's registry importer wants in front of an image reference.</summary>
    public const string RegistryScheme = "docker://";

    /// <summary>
    ///     What a <c>url</c> source may look like: a container-disk reference or an HTTP address, or
    ///     empty for a catalogue image.
    /// </summary>
    public const string OptionalSourceUrlPattern = @"((docker|https?)://\S+)?";

    // ── The platform catalogue ────────────────────────────────────────────────────────────────

    /// <summary>One catalogue row.</summary>
    /// <param name="Url">The CDI source, a registry reference pinned by digest.</param>
    /// <param name="Description">What boots from it, in the tenant's words.</param>
    public readonly record struct CatalogueImage(string Url, string Description);

    /// <summary>
    ///     The Linux cloud images the platform offers, each a <c>quay.io/containerdisks</c> reference
    ///     pinned by the digest quay.io served on 2026-09-17.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Linux only, by docs/plan/13 § Images and licensing</b>:
    ///         <i>
    ///             "Windows Server images are
    ///             a licensing arrangement, not a technical task"
    ///         </i>, and the API refuses a name that is
    ///         not here through <see cref="Schema2026" />'s <c>AllowedValues</c> rather than through a
    ///         mysterious absence.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Digests and not tags, and re-resolving them is how the row moves.</b>
    ///         <c>containerdisks</c> rebuilds a tag whenever the distribution publishes a new cloud
    ///         image, so <c>ubuntu:24.04</c> names different bytes from one week to the next; a VM
    ///         booted from the tag would boot an image nobody on this platform had read. The digest is
    ///         what the tenant gets, the tag is what it was read off, and <c>charts/managed/image/SOURCE</c>
    ///         records both with the date. The chart's <c>catalogue</c> block carries the same four
    ///         rows, and <c>ComputeChartDriftTests</c> is what stops the two drifting — and
    ///         <c>ComputeDeclarationTests.TheCatalogueOffersLinuxOnlyAndEveryRowIsADigest</c> pins the
    ///         count, because this sentence said "five" for a day while the table held four.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, CatalogueImage> Catalogue { get; } =
        new Dictionary<string, CatalogueImage>(StringComparer.Ordinal) {
            ["ubuntu-24.04"] = new(
                RegistryScheme
                + "quay.io/containerdisks/ubuntu@sha256:1b49166bd3047c7d818be67cec73f891b12db7e792dbc91809412b6be1c20ec2",
                "Ubuntu 24.04 LTS (Noble Numbat) cloud image"
            ),
            ["ubuntu-22.04"] = new(
                RegistryScheme
                + "quay.io/containerdisks/ubuntu@sha256:27d3bbe1374521aa43fc50b647d712c9c90693f1e8ce9516aa25aeb73f17681d",
                "Ubuntu 22.04 LTS (Jammy Jellyfish) cloud image"
            ),
            ["debian-13"] = new(
                RegistryScheme
                + "quay.io/containerdisks/debian@sha256:518a687c58255e8556906a6f51f45ed9e96b0997eedd6e8a12c36680d82e2956",
                "Debian 13 (trixie) generic cloud image"
            ),
            ["debian-12"] = new(
                RegistryScheme
                + "quay.io/containerdisks/debian@sha256:ba8d5e83785ce239fa1ff9767ee0146019b96421f96543b68b154cf9e1ff4c7f",
                "Debian 12 (bookworm) generic cloud image"
            )
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The catalogue names, ordered, as the schema offers them.</summary>
    public static ImmutableArray<string> CatalogueNames { get; } = [.. Catalogue.Keys.Order(StringComparer.Ordinal)];

    /// <summary>The catalogue image a body gets when it names none.</summary>
    public const string DefaultCatalogueImage = "ubuntu-24.04";

    // ── The object an image IS ────────────────────────────────────────────────────────────────

    /// <summary>The name of the <c>DataVolume</c> — and so of the claim a VM clones — which is the resource's own.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         The resource's name with nothing added, and <see cref="VirtualMachines" /> depends on
    ///         that.
    ///     </b> A VM's body names an image by its resource name and the VM renders
    ///     <c>source.pvc.name</c> from it without reading the image resource; a prefix here would be a
    ///     prefix the VM has to know about, in a second place.
    /// </remarks>
    public static string ObjectNameOf(string name) => name;

    /// <summary>The <c>DataVolume</c> an image owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef DataVolumeRef(string ns, string name) =>
        new() { Kind = Cdi.DataVolumeKind, Namespace = ns, Name = ObjectNameOf(name) };

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     ⚠
    ///     <b>
    ///         Every property but <c>location</c> is immutable, because an imported image cannot be
    ///         re-imported in place.
    ///     </b> A CDI <c>DataVolume</c>'s source and size are fixed at creation;
    ///     changing either here would be a PUT the API server refuses on every pass, with the resource
    ///     stuck <c>InProgress</c>. A different image is a different resource.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the image is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The image's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    true,
                    Description: "The cluster the image is imported into. A virtual machine can boot from "
                    + "it only in the same cluster and the same resource group, because a clone is a "
                    + "claim in one namespace copied from a claim beside it."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new("/properties/source", SchemaKind.Nested, Description: "Where the bytes come from."),
                new(
                    "/properties/source/kind",
                    SchemaKind.Text,
                    true,
                    Description: "catalogue for one of the platform's own Linux cloud images, pinned by "
                    + "digest; url for an address you supply."
                ) { AllowedValues = SourceKinds, Immutable = true, DefaultJson = "\"" + CatalogueSource + "\"" },
                new(
                    "/properties/source/name",
                    SchemaKind.Text,
                    Description: "Which catalogue image, when kind is catalogue. ⚠ Linux only: Windows "
                    + "Server is a licensing arrangement and not in this catalogue — docs/plan/13 "
                    + "§ Images and licensing."
                ) {
                    AllowedValues = CatalogueNames, Immutable = true, DefaultJson = "\"" + DefaultCatalogueImage + "\""
                },
                new(
                    "/properties/source/url",
                    SchemaKind.Text,
                    Description: "Where to import from, when kind is url. docker://registry/repository"
                    + "[:tag|@digest] is a container disk; http:// or https:// is a raw or qcow2 image. "
                    + "⚠ An HTTP address carries no checksum and nothing verifies what arrives — pin a "
                    + "registry reference by digest when you can."
                ) { Pattern = OptionalSourceUrlPattern, MaxLength = 2048, Immutable = true, DefaultJson = "\"\"" },
                new(
                    "/properties/size",
                    SchemaKind.Text,
                    true,
                    Description: "The claim the image is imported into, in Kubernetes quantity form. It "
                    + "must hold the image's virtual size — 10Gi fits every catalogue image — and it is "
                    + "the smallest disk a machine booted from this image can have."
                ) {
                    Pattern = KubeQuantity.Pattern,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultSize + "\"",
                    ExampleJson = "\"10Gi\""
                },
                new(
                    "/properties/storageClass",
                    SchemaKind.Text,
                    Description: "The storage class the imported claim is on. Empty means the cluster's "
                    + "default, which on a bundle-installed cluster is the node-local class."
                ) { Widget = WidgetHint.StorageClass, Immutable = true, MaxLength = 253, DefaultJson = "\"\"" }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>Which kind of source a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string SourceKind(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Member(desired, "source", "kind"), CatalogueSource);

    /// <summary>The catalogue name a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string SourceName(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Member(desired, "source", "name"), DefaultCatalogueImage);

    /// <summary>The address a <c>url</c> body supplies, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string SourceUrl(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Member(desired, "source", "url"), string.Empty);

    /// <summary>The claim size a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Size(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Property(desired, "size"), DefaultSize);

    /// <summary>The storage class a body names, or empty for the cluster's default.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string StorageClass(JsonElement desired) =>
        ComputeBodies.Text(ComputeBodies.Property(desired, "storageClass"), string.Empty);

    /// <summary>The address CDI imports from: the catalogue's pin for a catalogue body, the tenant's own otherwise.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     Empty for a <c>url</c> body that supplied none. The schema cannot say "required when kind
    ///     is url" — that is a relation between two properties — so the reconciler refuses that body
    ///     by name instead of rendering a <c>DataVolume</c> with no source.
    /// </remarks>
    public static string ImportUrl(JsonElement desired) =>
        SourceKind(desired) == CatalogueSource
            ? Catalogue.TryGetValue(SourceName(desired), out var image) ? image.Url : string.Empty
            : SourceUrl(desired);

    /// <summary>Whether an address is one CDI's registry importer takes.</summary>
    /// <param name="url">A value of <see cref="ImportUrl" />.</param>
    public static bool IsRegistry(string url) => url.StartsWith(RegistryScheme, StringComparison.Ordinal);

    // ── The object a desired body becomes ─────────────────────────────────────────────────────

    /// <summary>The <c>DataVolume</c> a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Carries <see cref="Cdi.ImmediateBindAnnotation" /></b>, for the reason the class remarks
    ///     give: an image has no consumer, so on a <c>WaitForFirstConsumer</c> class it would otherwise
    ///     never bind and never import.
    /// </remarks>
    public static string DataVolumeJson(string name, JsonElement desired) {
        var url = ImportUrl(desired);

        var source = IsRegistry(url)
            ? new JsonObject { ["registry"] = new JsonObject { ["url"] = url } }
            : new JsonObject { ["http"] = new JsonObject { ["url"] = url } };

        return new JsonObject {
            ["kind"] = Cdi.DataVolumeKind.Kind,
            ["metadata"] = new JsonObject {
                ["name"] = ObjectNameOf(name),
                ["annotations"] = new JsonObject { [Cdi.ImmediateBindAnnotation] = "true" }
            },
            ["spec"] = new JsonObject {
                ["source"] = source, ["storage"] = Cdi.Storage(Size(desired), StorageClass(desired))
            }
        }.ToJsonString();
    }

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>Whether a <c>DataVolume</c> read back asks for what the desired body asks for.</summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Containment, on the three fields this type owns.</b> CDI's mutating path fills
    ///     <c>spec.storage</c> with the access mode and volume mode it took from the class's
    ///     <c>StorageProfile</c>, so an equality comparison would fail against every real cluster and
    ///     pass in both conformance suites, whose derived stub has no webhook. The class is compared
    ///     only when the body names one, because "the default" is spelled by its absence.
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        if (ComputeBodies.Kind(objectJson) != Cdi.DataVolumeKind.Kind
            || ComputeBodies.Spec(objectJson) is not { } spec) {
            return false;
        }

        var url = ImportUrl(desired);
        var read = IsRegistry(url)
            ? ComputeBodies.TextOf(spec["source"]?["registry"]?["url"])
            : ComputeBodies.TextOf(spec["source"]?["http"]?["url"]);

        var storageClass = StorageClass(desired);

        return read == url
            && Cdi.RequestedSize(spec) == Size(desired)
            && (storageClass.Length == 0 || Cdi.RequestedClass(spec) == storageClass);
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the image is imported into.</param>
    /// <param name="kind">The source kind.</param>
    /// <param name="name">The catalogue name.</param>
    /// <param name="url">The tenant's own address, for a <c>url</c> body.</param>
    /// <param name="size">The claim size.</param>
    /// <param name="storageClass">The storage class, or empty for the default.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        string kind = CatalogueSource,
        string name = DefaultCatalogueImage,
        string url = "",
        string size = DefaultSize,
        string storageClass = "",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["source"] = new JsonObject { ["kind"] = kind, ["name"] = name, ["url"] = url },
                ["size"] = size,
                ["storageClass"] = storageClass
            }
        }.ToJsonString();

    const string DefaultSize = "10Gi";
}
