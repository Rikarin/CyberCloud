using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace CyberCloud.Providers.ContainerInstance.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.ContainerInstance/containerGroups</c> — one or more
///     containers run together as one <c>Pod</c> in a tenant's namespace.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>docs/plan/13 § Container Instances: "the cheapest real provider", and the one that proves
///         the log-streaming path.</b> A container group is a <c>Pod</c> with resource limits, env from
///         vault handles, an optional address on a tenant subnet and an optional public address, and
///         its <c>logs</c> action is the first thing in this tree that reads a line a tenant's
///         workload wrote — through <c>IKubeClusterConnection.ReadLogsAsync</c>, which this type added.
///         The same document says "a <c>Pod</c> (or a <c>Job</c> for <c>restartPolicy: Never</c>)"; it is
///         a <c>Pod</c> for every policy, because a pod's own <c>restartPolicy</c> already means
///         <c>Never</c>, and a <c>Job</c> would add a second object whose retries and deadline the
///         tenant did not ask for.
///     </para>
///     <para>
///         ⚠
///         <b>
///             ONE OR MORE CONTAINERS IS AN ARRAY OF STRINGS, <c>name=image</c>, AND THE REASON IS THE
///             SCHEMA RATHER THAN TASTE.
///         </b> An array of objects is refused outright by
///         <see cref="SchemaKind.Array" /> — its element is a scalar kind — so the per-container half
///         of a group is what one string can carry: a name and an image. Everything a pod shares across
///         its containers is a property of the group, which is where Kubernetes puts it too: the ports
///         (one network namespace), the environment, the restart policy, and — through the pod-level
///         <c>resources</c> Kubernetes 1.34 turned on by default — the CPU and memory, so quota reserves
///         the group's total and the kubelet enforces exactly that total. The command is the first
///         container's; a sidecar runs its image's own entrypoint. <c>conformance.yaml § owed</c>,
///         <c>one-command-and-one-environment</c>.
///     </para>
///     <para>
///         ⚠ <b>Every relation the schema cannot state is refused on the first reconcile pass</b> — the
///         element grammar of the four arrays, the uniqueness of container names, and the tenancy of
///         every vault handle — because the write path has no per-type validator and <c>./build.sh
///         Charts</c> refuses <c>@pattern</c> on an <c>{array}</c> parameter. <see cref="BodyProblem" />
///         is the check, and it runs before anything is read or resolved.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A POD IS ALMOST ENTIRELY IMMUTABLE, SO A CHANGED BODY IS A REPLACED POD.
///         </b> Server-side apply
///         of a changed container list, environment or resource block onto a live pod is refused by the
///         API server — "pod updates may not change fields other than …" — so the reconciler reads the
///         pod before it applies and deletes one that does not carry the desired spec, and the next
///         pass creates the new one. A group's update is therefore a restart, which is what a tenant
///         changing a container's image expects anyway.
///     </para>
/// </remarks>
public static partial class ContainerGroups {
    /// <summary>The provider namespace.</summary>
    public const string ProviderNamespace = "CyberCloud.ContainerInstance";

    /// <summary>The type path.</summary>
    public const string TypePath = "containerGroups";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/container-group";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The objects a group IS ────────────────────────────────────────────────────────────────

    /// <summary>The pod — the group.</summary>
    public static GroupVersionKind PodKind { get; } = new() { Group = "", Version = "v1", Kind = "Pod", Plural = "pods" };

    /// <summary>
    ///     Kube-OVN's <c>OvnFip</c>: one public address translated one-to-one onto one private one.
    /// </summary>
    /// <remarks>
    ///     ⚠ Cluster-scoped, like the <c>OvnEip</c> it names, so its name folds in the namespace —
    ///     <see cref="FloatingIpName" />.
    /// </remarks>
    public static GroupVersionKind OvnFipKind { get; } =
        new() { Group = "kubeovn.io", Version = "v1", Kind = "OvnFip", Plural = "ovn-fips" };

    /// <summary>The pod's name — the resource's own.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string PodName(string name) => name;

    /// <summary>The Secret the secure environment is resolved into.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string EnvironmentSecretName(string name) => name + "-env";

    /// <summary>The <c>kubernetes.io/dockerconfigjson</c> Secret the pull credential is resolved into.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string PullSecretName(string name) => name + "-pull";

    /// <summary>The <c>OvnFip</c>'s name: the namespace and the resource's own, joined.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static string FloatingIpName(string ns, string name) => ns + "-" + name;

    /// <summary>The pod a resource owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef PodRef(string ns, string name) => new() { Kind = PodKind, Namespace = ns, Name = PodName(name) };

    /// <summary>The secure-environment Secret a resource owns when its body names one.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef EnvironmentSecretRef(string ns, string name) => KubeSecret.Ref(ns, EnvironmentSecretName(name));

    /// <summary>The pull Secret a resource owns when its body names a registry credential.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef PullSecretRef(string ns, string name) => KubeSecret.Ref(ns, PullSecretName(name));

    /// <summary>The <c>OvnFip</c> a resource owns when its body names a public address.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>⚠ Cluster-scoped: <c>Namespace</c> is empty.</remarks>
    public static ObjectRef FloatingIpRef(string ns, string name) =>
        new() { Kind = OvnFipKind, Namespace = string.Empty, Name = FloatingIpName(ns, name) };

    /// <summary>The annotation that puts the pod on a Kube-OVN subnet — the one <c>LoadBalancers</c> uses.</summary>
    public const string LogicalSwitchAnnotation = "ovn.kubernetes.io/logical_switch";

    /// <summary>The annotation that pins the pod's address on that subnet — the one a load balancer's frontend uses.</summary>
    public const string IpAddressAnnotation = "ovn.kubernetes.io/ip_address";

    /// <summary>The label every group's pod carries, beside the platform's.</summary>
    public const string NameLabel = "app.kubernetes.io/name";

    /// <summary>The value of <see cref="NameLabel" />.</summary>
    public const string NameLabelValue = "container-group";

    /// <summary>The label that names the group a pod is.</summary>
    public const string InstanceLabel = "app.kubernetes.io/instance";

    // ── The actions ───────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the last lines one container wrote.</summary>
    public const string LogsAction = "logs";

    /// <summary>Replaces the pod: every container starts again from its image.</summary>
    public const string RestartAction = "restart";

    /// <summary>The permission <see cref="LogsAction" /> checks.</summary>
    /// <remarks>
    ///     <c>read</c>: a group's output is part of reading the group, as Azure's Reader role reads a
    ///     container group's logs. ⚠ Not <c>secret: true</c>: a log is the tenant's workload speaking,
    ///     and a workload that prints its credentials does so to whoever may read the group anyway.
    /// </remarks>
    public const string LogsPermission = "read";

    /// <summary>The permission <see cref="RestartAction" /> checks: stopping a workload is the authority a PUT carries.</summary>
    public const string RestartPermission = "write";

    /// <summary>How many lines <see cref="LogsAction" /> answers when the request names none.</summary>
    public const int DefaultTailLines = 100;

    /// <summary>The most lines one <see cref="LogsAction" /> answers.</summary>
    public const int MaxTailLines = 5000;

    /// <summary>What <see cref="LogsAction" /> takes.</summary>
    public static ResourceSchema LogsRequest { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/container",
                    SchemaKind.Text,
                    Description: "The container whose output to read, by the name the containers "
                    + "property gives it. Empty means the first container."
                ) { Pattern = "(" + ContainerNamePattern + ")?", MaxLength = 63 },
                new(
                    "/tailLines",
                    SchemaKind.WholeNumber,
                    Description: "How many lines from the end. At most 5000; 100 when not given."
                ) { Minimum = 1, Maximum = MaxTailLines }
            ]
        );

    /// <summary>What <see cref="LogsAction" /> answers.</summary>
    public static ResourceSchema LogsResponse { get; } =
        ResourceSchema.Of(
            [
                new("/container", SchemaKind.Text, true, Description: "The container the lines are from."),
                new(
                    "/log",
                    SchemaKind.Text,
                    true,
                    Description: "The lines, newline-separated, as the kubelet kept them. ⚠ A tail and "
                    + "not a stream: the last tailLines lines, capped at a mebibyte."
                ),
                new(
                    "/readAt",
                    SchemaKind.Text,
                    true,
                    Description: "When the platform read the log, RFC 3339."
                ) { Format = SchemaFormat.DateTime }
            ]
        );

    /// <summary>What <see cref="RestartAction" /> answers.</summary>
    public static ResourceSchema RestartResponse { get; } =
        ResourceSchema.Of(
            [
                new("/action", SchemaKind.Text, true, Description: "restart.") { AllowedValues = [RestartAction] },
                new(
                    "/podUidBefore",
                    SchemaKind.Text,
                    true,
                    Description: "The uid of the pod that was replaced."
                ),
                new(
                    "/podUid",
                    SchemaKind.Text,
                    true,
                    Description: "The uid of the pod that replaced it."
                )
            ]
        );

    // ── The grammar the schema cannot carry ───────────────────────────────────────────────────

    /// <summary>A container's name: a DNS label, as Kubernetes requires of one.</summary>
    public const string ContainerNamePattern = "[a-z0-9]([-a-z0-9]*[a-z0-9])?";

    /// <summary>An environment variable's name — what the kubelet accepts: <c>C_IDENTIFIER</c>, plus dots and dashes.</summary>
    public const string VariableNamePattern = "[-._a-zA-Z][-._a-zA-Z0-9]*";

    /// <summary>A vault handle as one string: <c>path#field</c>, optionally <c>@version</c>.</summary>
    /// <remarks>The spelling <c>SecretRef</c> prints, and <c>VirtualMachines.OptionalSecretRefPattern</c>'s.</remarks>
    public const string SecretRefPattern = @"[^#@\s]+#[^#@\s]+(@[^#@\s]+)?";

    /// <summary>A vault handle or nothing.</summary>
    public const string OptionalSecretRefPattern = "(" + SecretRefPattern + ")?";

    /// <summary>A registry host, with an optional port, or nothing.</summary>
    public const string OptionalServerPattern = @"([a-z0-9]([-a-z0-9.]*[a-z0-9])?(:\d{1,5})?)?";

    /// <summary>A bare IPv4 address or nothing.</summary>
    public const string OptionalV4Pattern = @"((\d{1,3}\.){3}\d{1,3})?";

    /// <summary>The most containers one group may hold.</summary>
    public const int MaxContainers = 10;

    /// <summary>The restart policies a group may have — the pod's own three.</summary>
    public static ImmutableArray<string> RestartPolicies { get; } = ["Always", "OnFailure", "Never"];

    /// <summary>The restart policy a body gets when it names none.</summary>
    public const string DefaultRestartPolicy = "Always";

    /// <summary>The protocols a port may carry.</summary>
    public static ImmutableArray<string> Protocols { get; } = ["TCP", "UDP", "SCTP"];

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The container <see cref="Body" /> writes when a caller names none — the tests' and the fixtures'.
    ///     ⚠ Not the schema's default, which is no container at all: see the <c>containers</c> property.
    /// </summary>
    public const string DefaultContainer = "app=" + DefaultImage;

    /// <summary>The image the default container runs.</summary>
    /// <remarks>
    ///     ⚠ busybox, pinned to the tag <c>OpenEbsLocalPvOnAnEmptyCluster</c> already pulls, so the
    ///     cluster-backed lane pulls one image for both. A tag and not a digest — this is a default a
    ///     tenant replaces, not a platform image.
    /// </remarks>
    public const string DefaultImage = "busybox:1.37";

    /// <summary>The group's CPU when a body names none.</summary>
    public const string DefaultCpu = "500m";

    /// <summary>The group's memory when a body names none.</summary>
    public const string DefaultMemory = "256Mi";

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The four arrays declare no per-element pattern</b>, for the reason
    ///         <c>VirtualMachines.Schema2026</c>'s <c>dataDisks</c> gives — the chart surface cannot carry
    ///         one — and <see cref="BodyProblem" /> checks each element on the first pass instead.
    ///         <c>conformance.yaml § owed</c>, <c>array-elements-are-checked-at-reconcile</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Values in <c>environment</c> are plaintext in the body, and that is the property's
    ///         whole meaning.</b> Anything that must not sit in durable grain state goes in
    ///         <c>secureEnvironment</c> as a vault handle, which docs/plan/13 § Container Instances calls
    ///         "env from <c>SecretRef</c>s": resolved at render into a Secret in the tenant's namespace,
    ///         never into a body, and a path outside the tenant's own vault prefix is refused before
    ///         the resolver is asked — <see cref="TenantVaultPrefix" />.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the group is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The group's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    true,
                    Description: "The cluster the group runs in."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/containers",
                    SchemaKind.Array,
                    true,
                    Description: "The containers, each name=image — for example web=nginx:1.27 or "
                    + "app=myregistry.example/team/app:2.1. One to ten; names are lower-case DNS labels "
                    + "and unique in the group. The first is the main container: the command and the "
                    + "ports are its."
                ) {
                    // ⚠ NO DEFAULT CONTAINER, and the portal is why. A list control that starts with a
                    // value hides its placeholder, and the placeholder is the only accessible name
                    // xui-tag-input's inner input has (form-field-node.ts) — the generated form failed
                    // resource-form.spec.ts' WCAG 2.2 AA check with an unlabelled input the day this
                    // default was one busybox. A group names its containers, which is the honest form
                    // anyway; the example shows the grammar.
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[]",
                    ExampleJson = """["web=nginx:1.27", "log=busybox:1.37"]"""
                },
                new(
                    "/properties/command",
                    SchemaKind.Array,
                    Description: "The main container's command, replacing its image's entrypoint — for "
                    + "example [\"sh\", \"-c\", \"echo hello; sleep 3600\"]. Empty runs the image as built."
                ) { ElementKind = SchemaKind.Text, DefaultJson = "[]", ExampleJson = """["sh", "-c", "echo hello; sleep 3600"]""" },
                new(
                    "/properties/cpu",
                    SchemaKind.Text,
                    true,
                    Description: "The CPU the whole group may use, as a Kubernetes quantity — 500m is half "
                    + "a core. Reserved against your vCPU quota and enforced on the pod as one limit its "
                    + "containers share."
                ) { Pattern = KubeQuantity.Pattern, DefaultJson = "\"" + DefaultCpu + "\"", ExampleJson = "\"1\"" },
                new(
                    "/properties/memory",
                    SchemaKind.Text,
                    true,
                    Description: "The memory the whole group may use, as a Kubernetes quantity. Reserved "
                    + "against your memory quota and enforced on the pod as one limit."
                ) {
                    Pattern = KubeQuantity.Pattern, DefaultJson = "\"" + DefaultMemory + "\"", ExampleJson = "\"1Gi\""
                },
                new(
                    "/properties/restartPolicy",
                    SchemaKind.Text,
                    Description: "Always restarts a container whenever it exits; OnFailure only when it "
                    + "exits non-zero; Never lets the group run once — a batch job — and the group stays "
                    + "Succeeded with its logs readable."
                ) { AllowedValues = RestartPolicies, DefaultJson = "\"" + DefaultRestartPolicy + "\"" },
                new(
                    "/properties/environment",
                    SchemaKind.Array,
                    Description: "Environment variables every container sees, each NAME=value. ⚠ The "
                    + "value is stored in this body in plaintext — anything secret goes in "
                    + "secureEnvironment."
                ) { ElementKind = SchemaKind.Text, DefaultJson = "[]", ExampleJson = """["LOG_LEVEL=info"]""" },
                new(
                    "/properties/secureEnvironment",
                    SchemaKind.Array,
                    Description: "Environment variables whose values are vault handles, each "
                    + "NAME=path#field, optionally @version. Resolved when the group is rendered into a "
                    + "Secret the containers read; the values never enter this body. ⚠ Every path must be "
                    + "under your own tenant's vault prefix, tenants/<tenantId>/; any other is refused."
                ) {
                    // ⚠ No WidgetHint.SecretRef, although every element is a handle: `@widget` renders one
                    // scalar field and ./build.sh Charts refuses it on an array. The form draws a list of
                    // strings, and the description says what each one is.
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[]",
                    ExampleJson = """["DB_PASSWORD=tenants/00000000-0000-0000-0000-000000000000/db#password"]"""
                },
                new(
                    "/properties/ports",
                    SchemaKind.Array,
                    Description: "The ports the main container listens on, each a number with an "
                    + "optional /TCP, /UDP or /SCTP — for example 80 or 53/UDP. Reached at the group's "
                    + "address on its subnet, and at its public address when it has one."
                ) { ElementKind = SchemaKind.Text, DefaultJson = "[]", ExampleJson = """["80", "53/UDP"]""" },
                new(
                    "/properties/registry",
                    SchemaKind.Nested,
                    Description: "The credential a private registry's images are pulled with. All empty "
                    + "means every image is public."
                ),
                new(
                    "/properties/registry/server",
                    SchemaKind.Text,
                    Description: "The registry's host, with a port when it is not 443 — for example "
                    + "registry.example.com. ⚠ The kubelet resolves it from the node, not from inside the "
                    + "cluster."
                ) { Pattern = OptionalServerPattern, MaxLength = 253, DefaultJson = "\"\"" },
                new(
                    "/properties/registry/username",
                    SchemaKind.Text,
                    Description: "The user the registry knows — admin for a CyberCloud.ContainerRegistry, "
                    + "as its listCredentials answers."
                ) { MaxLength = 256, DefaultJson = "\"\"" },
                new(
                    "/properties/registry/password",
                    SchemaKind.Text,
                    Description: "A vault handle — path#field, optionally @version — whose value is the "
                    + "password or token. For a CyberCloud.ContainerRegistry that is its own credential: "
                    + "tenants/<tenantId>/CyberCloud.ContainerRegistry/registries/<registryId>#adminPassword. "
                    + "⚠ Under your own tenant's prefix, or refused."
                ) {
                    Pattern = OptionalSecretRefPattern, MaxLength = 512, Widget = WidgetHint.SecretRef, DefaultJson = "\"\""
                },
                new(
                    "/properties/network",
                    SchemaKind.Nested,
                    Description: "Where the group's ports are reached. All empty means the cluster's pod "
                    + "network, reachable from nothing a tenant owns."
                ),
                new(
                    "/properties/network/virtualNetwork",
                    SchemaKind.Text,
                    Description: "The CyberCloud.Network/virtualNetworks resource in this resource group, "
                    + "by name, or empty."
                ) { Pattern = OptionalNamePattern, MaxLength = ResourceNaming.MaxLength, Immutable = true, DefaultJson = "\"\"" },
                new(
                    "/properties/network/subnet",
                    SchemaKind.Text,
                    Description: "The subnet of that network the group takes its address from, by name, "
                    + "or empty. ⚠ A name that is not a subnet of the network is refused by the fabric "
                    + "rather than by this API, and the group never starts."
                ) {
                    Pattern = OptionalNamePattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Widget = WidgetHint.Subnet,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/network/ipAddress",
                    SchemaKind.Text,
                    Description: "The IPv4 address the group answers on, inside the subnet's range, or "
                    + "empty for one the fabric picks. ⚠ Only with a subnet."
                ) { Pattern = OptionalV4Pattern, Widget = WidgetHint.Cidr, Immutable = true, DefaultJson = "\"\"" },
                new(
                    "/properties/network/publicIpAddress",
                    SchemaKind.Text,
                    Description: "A CyberCloud.Network/publicIpAddresses resource in this resource group, "
                    + "by name, translated one-to-one onto the group's address — every port the group "
                    + "listens on is reachable at it. Empty for none. ⚠ Only with a subnet."
                ) { Pattern = OptionalNamePattern, MaxLength = ResourceNaming.MaxLength, Immutable = true, DefaultJson = "\"\"" }
            ]
        );

    /// <summary>A resource name or nothing.</summary>
    const string OptionalNamePattern = "(" + ResourceNaming.Pattern + ")?";

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>One container of a group: its name and its image.</summary>
    /// <param name="Name">The container's name.</param>
    /// <param name="Image">The image it runs.</param>
    public readonly record struct Container(string Name, string Image);

    /// <summary>One port the main container listens on.</summary>
    /// <param name="Number">The port.</param>
    /// <param name="Protocol">TCP, UDP or SCTP.</param>
    public readonly record struct Port(int Number, string Protocol);

    /// <summary>The containers a body names, in body order, each element split at its first <c>=</c>.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>An element without <c>=</c> is kept with an empty image, for <see cref="BodyProblem" /> to name.</remarks>
    public static ImmutableArray<Container> Containers(JsonElement desired) => [
        .. Strings(Property(desired, "containers"))
            .Select(static x => x.IndexOf('=', StringComparison.Ordinal) is var at and > 0
                ? new Container(x[..at], x[(at + 1)..])
                : new Container(x, string.Empty))
    ];

    /// <summary>The main container's command.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> Command(JsonElement desired) => [.. Strings(Property(desired, "command"), keepEmpty: true)];

    /// <summary>The group's CPU quantity.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Cpu(JsonElement desired) => Text(Property(desired, "cpu"), DefaultCpu);

    /// <summary>The group's memory quantity.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Memory(JsonElement desired) => Text(Property(desired, "memory"), DefaultMemory);

    /// <summary>The pod's restart policy.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RestartPolicy(JsonElement desired) => Text(Property(desired, "restartPolicy"), DefaultRestartPolicy);

    /// <summary>The plain environment, as <c>(name, value)</c> pairs in body order.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<(string Name, string Value)> Environment(JsonElement desired) => Pairs(desired, "environment");

    /// <summary>The secure environment, as <c>(name, handle)</c> pairs in body order.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<(string Name, string Value)> SecureEnvironment(JsonElement desired) =>
        Pairs(desired, "secureEnvironment");

    /// <summary>The ports the main container listens on.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>An element that does not parse is left out, for <see cref="BodyProblem" /> to name.</remarks>
    public static ImmutableArray<Port> Ports(JsonElement desired) => [
        .. Strings(Property(desired, "ports")).Select(ParsePort).Where(static x => x is not null).Select(static x => x!.Value)
    ];

    /// <summary>The registry host a pull credential is for, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RegistryServer(JsonElement desired) => Text(Member(desired, "registry", "server"), string.Empty);

    /// <summary>The registry user, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RegistryUsername(JsonElement desired) => Text(Member(desired, "registry", "username"), string.Empty);

    /// <summary>The registry password's vault handle, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string RegistryPassword(JsonElement desired) => Text(Member(desired, "registry", "password"), string.Empty);

    /// <summary>Whether a body names a pull credential at all.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool HasPullSecret(JsonElement desired) => RegistryPassword(desired).Length > 0;

    /// <summary>Whether a body names a secure environment at all.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool HasSecureEnvironment(JsonElement desired) => SecureEnvironment(desired).Length > 0;

    /// <summary>The virtual network a body names, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string VirtualNetwork(JsonElement desired) => Text(Member(desired, "network", "virtualNetwork"), string.Empty);

    /// <summary>The subnet a body names, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Subnet(JsonElement desired) => Text(Member(desired, "network", "subnet"), string.Empty);

    /// <summary>The pinned address a body names, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string IpAddress(JsonElement desired) => Text(Member(desired, "network", "ipAddress"), string.Empty);

    /// <summary>The public address resource a body names, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string PublicIpAddress(JsonElement desired) => Text(Member(desired, "network", "publicIpAddress"), string.Empty);

    /// <summary>Whether a body names a public address.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool HasPublicIpAddress(JsonElement desired) => PublicIpAddress(desired).Length > 0;

    /// <summary>The Kube-OVN <c>Subnet</c> object the pod joins, or empty for the pod network.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <c>{namespace}-{network}-{subnet}</c>, <c>NetworkSubnets.ObjectNameOf</c>'s rule spelled a
    ///     third time — rule 2 of docs/plan/03 § Assembly graph rules forbids the reference that would
    ///     spell it once, and <c>ContainerGroupSpellingTests</c> holds this spelling to the Network
    ///     family's from a test project the rule does not reach.
    /// </remarks>
    public static string LogicalSwitchOf(string ns, JsonElement desired) {
        var network = VirtualNetwork(desired);
        var subnet = Subnet(desired);

        return network.Length == 0 || subnet.Length == 0 ? string.Empty : ns + "-" + network + "-" + subnet;
    }

    /// <summary>The <c>OvnEip</c> a body's public address renders — <c>PublicIpAddresses.ObjectNameOf</c>, spelled again.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string OvnEipOf(string ns, JsonElement desired) => ns + "-" + PublicIpAddress(desired);

    /// <summary>The name Kube-OVN gives the <c>IP</c> object it allocates for a pod: <c>{pod}.{namespace}</c>.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static string PodIpObjectName(string ns, string name) => PodName(name) + "." + ns;

    // ── The vault ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The vault prefix every path a tenant's body names must start with.</summary>
    /// <param name="tenantId">The tenant whose resource carries the handle.</param>
    /// <remarks>
    ///     ⚠ <c>VirtualMachines.TenantVaultPrefix</c>' rule, spelled a second time because rule 2 keeps
    ///     one family's <c>.Contracts</c> out of another's; <c>ContainerGroupSpellingTests</c> holds the two
    ///     together. That remark carries the whole argument: the resolver holds one platform-wide token,
    ///     so the path is the only thing that scopes a read, and a value a tenant spelled the path of
    ///     reaches something the tenant can read — here, a container's environment or its pull secret.
    /// </remarks>
    public static string TenantVaultPrefix(Guid tenantId) => string.Create(CultureInfo.InvariantCulture, $"tenants/{tenantId:D}/");

    /// <summary>
    ///     Parses <c>path#field[@version]</c>, refusing a path outside the tenant's own prefix.
    /// </summary>
    /// <param name="spelled">The handle as a body spells it.</param>
    /// <param name="tenantId">The only tenant whose paths it may name.</param>
    /// <param name="target">The pointer a refusal names.</param>
    public static Result<SecretRef> ParseSecretRef(string spelled, Guid tenantId, string target) {
        var hash = spelled.IndexOf('#', StringComparison.Ordinal);

        if (hash <= 0 || hash == spelled.Length - 1) {
            return Result<SecretRef>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{spelled}' is not a vault handle. Write path#field, optionally @version.",
                target
            );
        }

        var path = spelled[..hash];
        var prefix = TenantVaultPrefix(tenantId);

        if (!path.StartsWith(prefix, StringComparison.Ordinal) || path.Length == prefix.Length) {
            return Result<SecretRef>.Failure(
                ErrorCode.AuthorizationFailed,
                $"{target} names '{path}', which is not under your tenant's vault prefix '{prefix}'. A "
                + "container group can only be given a value your own tenant holds.",
                target
            );
        }

        var rest = spelled[(hash + 1)..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);

        return Result<SecretRef>.Success(
            new() { Path = path, Field = at < 0 ? rest : rest[..at], Version = at < 0 ? string.Empty : rest[(at + 1)..] }
        );
    }

    // ── What the schema cannot say ────────────────────────────────────────────────────────────

    /// <summary>
    ///     Why the body cannot be rendered, with the pointer to name, or <see langword="null" /> when it can.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="tenantId">The tenant whose resource this is — the only one whose vault paths it may name.</param>
    /// <remarks>
    ///     ⚠ <b>Everything here is a relation the schema cannot state</b>, checked on the first reconcile
    ///     pass before anything is read or resolved, terminally, because a PUT is what changes it — the
    ///     same shape as <c>VirtualMachines.DataDiskProblem</c>. A vault path outside the tenant's prefix
    ///     is <see cref="ErrorCode.AuthorizationFailed" />; everything else is
    ///     <see cref="ErrorCode.InvalidRequestBody" />.
    /// </remarks>
    public static Error? BodyProblem(JsonElement desired, Guid tenantId) {
        var containers = Containers(desired);

        if (containers.Length == 0) {
            return Invalid("/properties/containers", "A group needs at least one container, written name=image.");
        }

        if (containers.Length > MaxContainers) {
            return Invalid(
                "/properties/containers",
                $"A group holds at most {MaxContainers.ToString(CultureInfo.InvariantCulture)} containers."
            );
        }

        var names = new HashSet<string>(StringComparer.Ordinal);

        foreach (var (name, image) in containers) {
            if (!ContainerName().IsMatch(name) || name.Length > 63) {
                return Invalid(
                    "/properties/containers",
                    $"'{name}' is not a container name: lower-case letters, digits and dashes, starting and "
                    + "ending with a letter or digit, at most 63. Write each container as name=image."
                );
            }

            if (image.Length == 0 || image.Any(char.IsWhiteSpace)) {
                return Invalid("/properties/containers", $"The container '{name}' names no image. Write name=image.");
            }

            if (!names.Add(name)) {
                return Invalid("/properties/containers", $"Two containers are called '{name}'; a pod refuses that.");
            }
        }

        foreach (var pointer in new[] { "environment", "secureEnvironment" }) {
            var seen = new HashSet<string>(StringComparer.Ordinal);

            foreach (var element in Strings(Property(desired, pointer))) {
                var at = element.IndexOf('=', StringComparison.Ordinal);
                var variable = at > 0 ? element[..at] : element;

                if (at <= 0 || !VariableName().IsMatch(variable)) {
                    return Invalid(
                        "/properties/" + pointer,
                        $"'{element}' is not NAME=value with a variable name the kubelet accepts."
                    );
                }

                if (!seen.Add(variable)) {
                    return Invalid("/properties/" + pointer, $"'{variable}' is set twice.");
                }
            }
        }

        var plain = Environment(desired).Select(static x => x.Name).ToHashSet(StringComparer.Ordinal);

        foreach (var (variable, handle) in SecureEnvironment(desired)) {
            if (plain.Contains(variable)) {
                return Invalid(
                    "/properties/secureEnvironment",
                    $"'{variable}' is in both environment and secureEnvironment; one value per name."
                );
            }

            if (ParseSecretRef(handle, tenantId, "/properties/secureEnvironment").TryGetError(out var refused)) {
                return refused;
            }
        }

        var ports = new HashSet<Port>();

        foreach (var element in Strings(Property(desired, "ports"))) {
            if (ParsePort(element) is not { } port) {
                return Invalid(
                    "/properties/ports",
                    $"'{element}' is not a port: a number from 1 to 65535, optionally /TCP, /UDP or /SCTP."
                );
            }

            if (!ports.Add(port)) {
                return Invalid("/properties/ports", $"'{element}' is listed twice.");
            }
        }

        if (HasPullSecret(desired)) {
            if (RegistryServer(desired).Length == 0 || RegistryUsername(desired).Length == 0) {
                return Invalid(
                    "/properties/registry",
                    "A registry password needs the server it is for and the user it belongs to."
                );
            }

            if (ParseSecretRef(RegistryPassword(desired), tenantId, "/properties/registry/password").TryGetError(out var refused)) {
                return refused;
            }
        }

        if ((VirtualNetwork(desired).Length == 0) != (Subnet(desired).Length == 0)) {
            return Invalid(
                "/properties/network",
                "A group joins a subnet of a virtual network: name both, or neither for the pod network."
            );
        }

        if ((IpAddress(desired).Length > 0 || HasPublicIpAddress(desired)) && Subnet(desired).Length == 0) {
            return Invalid(
                "/properties/network",
                "An address — a pinned one, or a public one translated onto it — is an address on a subnet, "
                + "and this group names none."
            );
        }

        return KubeQuantity.TryParse(Cpu(desired), out var cores) && cores > 0
            && KubeQuantity.TryParse(Memory(desired), out var bytes) && bytes > 0
                ? null
                : Invalid("/properties/cpu", "A group's cpu and memory must both be more than nothing.");

        static Error Invalid(string target, string message) => new(ErrorCode.InvalidRequestBody, message, target);
    }

    /// <summary>A port element, parsed, or <see langword="null" />.</summary>
    /// <param name="element">For example <c>80</c> or <c>53/UDP</c>.</param>
    public static Port? ParsePort(string element) {
        var slash = element.IndexOf('/', StringComparison.Ordinal);
        var number = slash < 0 ? element : element[..slash];
        var protocol = slash < 0 ? "TCP" : element[(slash + 1)..];

        return int.TryParse(number, NumberStyles.None, CultureInfo.InvariantCulture, out var port)
            && port is >= 1 and <= 65535
            && Protocols.Contains(protocol)
                ? new Port(port, protocol)
                : null;
    }

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>The pod a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body, which <see cref="BodyProblem" /> passed.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Pod-level <c>resources</c>, requests equal to limits.</b> The group's CPU and memory
    ///         are one budget its containers share, which is <c>spec.resources</c> on the pod — beta and
    ///         on by default since Kubernetes 1.34, and read back from the k3s the cluster-backed lane
    ///         runs (1.35.7). Equal requests and limits make the pod <c>Guaranteed</c>, so what quota
    ///         reserved is exactly what the scheduler set aside. ⚠ A cluster with the gate off drops the
    ///         field at admission; <see cref="Matches" /> reads it back and the group never converges,
    ///         which is the honest answer to a limit that is not enforced.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What the security context decides, and what it leaves to the image.</b> No service
    ///         account token is mounted — a tenant's workload holds no credential to this platform's
    ///         cluster — privilege escalation is off and seccomp is the runtime's default. The user and
    ///         the capabilities are the image's, because a group runs images a tenant chose and
    ///         <c>nginx</c> binding port 80 as root is the ordinary case; the namespace's Pod Security
    ///         level is the limit. <c>conformance.yaml § owed</c>, <c>runs-as-the-image-says</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>No service links and no host anything.</b> <c>enableServiceLinks</c> would inject
    ///         an environment variable for every Service in the tenant's namespace into every container
    ///         — other resources' addresses, into a workload that did not ask for them.
    ///     </para>
    /// </remarks>
    public static string PodJson(string ns, string name, JsonElement desired) {
        var containers = Containers(desired);
        var environment = new JsonArray();

        foreach (var (variable, value) in Environment(desired)) {
            environment.Add(new JsonObject { ["name"] = variable, ["value"] = value });
        }

        foreach (var (variable, _) in SecureEnvironment(desired)) {
            environment.Add(
                new JsonObject {
                    ["name"] = variable,
                    ["valueFrom"] = new JsonObject {
                        ["secretKeyRef"] = new JsonObject { ["name"] = EnvironmentSecretName(name), ["key"] = variable }
                    }
                }
            );
        }

        var rendered = new JsonArray();

        for (var i = 0; i < containers.Length; i++) {
            var container = new JsonObject {
                ["name"] = containers[i].Name,
                ["image"] = containers[i].Image,
                ["securityContext"] = new JsonObject { ["allowPrivilegeEscalation"] = false }
            };

            if (i == 0 && Command(desired) is { Length: > 0 } command) {
                container["command"] = new JsonArray([.. command.Select(static x => (JsonNode)JsonValue.Create(x))]);
            }

            if (environment.Count > 0) {
                container["env"] = environment.DeepClone();
            }

            if (i == 0 && Ports(desired) is { Length: > 0 } ports) {
                container["ports"] = new JsonArray(
                    [
                        .. ports.Select(static x => (JsonNode)new JsonObject {
                            ["containerPort"] = x.Number, ["protocol"] = x.Protocol
                        })
                    ]
                );
            }

            rendered.Add(container);
        }

        var metadata = new JsonObject {
            ["name"] = PodName(name),
            ["labels"] = new JsonObject { [NameLabel] = NameLabelValue, [InstanceLabel] = PodName(name) }
        };

        var annotations = new JsonObject();

        if (LogicalSwitchOf(ns, desired) is { Length: > 0 } logicalSwitch) {
            annotations[LogicalSwitchAnnotation] = logicalSwitch;
        }

        if (IpAddress(desired) is { Length: > 0 } address) {
            annotations[IpAddressAnnotation] = address;
        }

        if (annotations.Count > 0) {
            metadata["annotations"] = annotations;
        }

        var spec = new JsonObject {
            ["restartPolicy"] = RestartPolicy(desired),
            ["automountServiceAccountToken"] = false,
            ["enableServiceLinks"] = false,
            ["terminationGracePeriodSeconds"] = TerminationGracePeriodSeconds,
            ["securityContext"] = new JsonObject { ["seccompProfile"] = new JsonObject { ["type"] = "RuntimeDefault" } },
            ["resources"] = new JsonObject {
                ["requests"] = new JsonObject { ["cpu"] = Cpu(desired), ["memory"] = Memory(desired) },
                ["limits"] = new JsonObject { ["cpu"] = Cpu(desired), ["memory"] = Memory(desired) }
            },
            ["containers"] = rendered
        };

        if (HasPullSecret(desired)) {
            spec["imagePullSecrets"] = new JsonArray(new JsonObject { ["name"] = PullSecretName(name) });
        }

        return new JsonObject { ["kind"] = PodKind.Kind, ["metadata"] = metadata, ["spec"] = spec }.ToJsonString();
    }

    /// <summary>The seconds a container is given between <c>SIGTERM</c> and <c>SIGKILL</c>.</summary>
    /// <remarks>
    ///     ⚠ Ten, not Kubernetes' thirty, because a <c>restart</c> waits for the old pod to be gone before
    ///     it creates the new one — the two share a name — and the action is one request.
    /// </remarks>
    public const int TerminationGracePeriodSeconds = 10;

    /// <summary>The Secret the resolved secure environment becomes: one key per variable.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="values">The resolved values by variable name — the values, which go here and nowhere else.</param>
    /// <remarks><c>data</c> and not <c>stringData</c>, for the reason <c>VirtualMachines.CloudInitSecretJson</c> gives.</remarks>
    public static string EnvironmentSecretJson(string name, IReadOnlyDictionary<string, string> values) {
        ArgumentNullException.ThrowIfNull(values);

        var data = new JsonObject();

        foreach (var (variable, value) in values.OrderBy(static x => x.Key, StringComparer.Ordinal)) {
            data[variable] = Convert.ToBase64String(Encoding.UTF8.GetBytes(value));
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = EnvironmentSecretName(name) }, ["type"] = "Opaque", ["data"] = data
        }.ToJsonString();
    }

    /// <summary>The key a pull Secret carries its document under.</summary>
    public const string DockerConfigKey = ".dockerconfigjson";

    /// <summary>The pull Secret a resolved registry credential becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="server">The registry host.</param>
    /// <param name="username">The registry user.</param>
    /// <param name="password">The resolved password — the value, which goes here and nowhere else.</param>
    /// <remarks>
    ///     ⚠ The document the kubelet reads, <c>{"auths": {server: {username, password, auth}}}</c>, with
    ///     <c>auth</c> the base64 of <c>user:password</c> as <c>docker login</c> writes it — containerd's
    ///     CRI plugin reads <c>auth</c> when both are present.
    /// </remarks>
    public static string PullSecretJson(string name, string server, string username, string password) =>
        new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = PullSecretName(name) },
            ["type"] = "kubernetes.io/dockerconfigjson",
            ["data"] = new JsonObject {
                [DockerConfigKey] = Convert.ToBase64String(Encoding.UTF8.GetBytes(DockerConfig(server, username, password)))
            }
        }.ToJsonString();

    /// <summary>The <c>.dockerconfigjson</c> document one credential makes.</summary>
    /// <param name="server">The registry host.</param>
    /// <param name="username">The registry user.</param>
    /// <param name="password">The password.</param>
    public static string DockerConfig(string server, string username, string password) =>
        new JsonObject {
            ["auths"] = new JsonObject {
                [server] = new JsonObject {
                    ["username"] = username,
                    ["password"] = password,
                    ["auth"] = Convert.ToBase64String(Encoding.UTF8.GetBytes(username + ":" + password))
                }
            }
        }.ToJsonString();

    /// <summary>The <c>OvnFip</c> a body's public address becomes.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>One-to-one NAT onto the pod's own <c>IP</c> object, which is how a load balancer's
    ///     frontend would be given one</b> — <c>LoadBalancers</c>' remarks name an <c>OvnFip</c> as the
    ///     object and decline it for their own reason (a DNAT per port). A group has one address and
    ///     every port it listens on, so one floating IP carries all of them, and <c>ipName</c> names the
    ///     <c>IP</c> Kube-OVN allocates for the pod (<see cref="PodIpObjectName" />) rather than an address
    ///     the body may leave to the fabric. <c>ovnEip</c> is <see cref="OvnEipOf" />: the join is by name
    ///     inside one resource group, as a NAT gateway's is.
    /// </remarks>
    public static string FloatingIpJson(string ns, string name, JsonElement desired) =>
        new JsonObject {
            ["kind"] = OvnFipKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = FloatingIpName(ns, name) },
            ["spec"] = new JsonObject {
                ["ovnEip"] = OvnEipOf(ns, desired), ["ipName"] = PodIpObjectName(ns, name)
            }
        }.ToJsonString();

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Whether a pod read back carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Containment over what a tenant chose</b> — every container's name and image in order, the
    ///     main command, every variable's name and value or Secret key, the ports, the restart policy,
    ///     the pod-level CPU and memory, the pull secret, and the two network annotations. The API server
    ///     defaults a pod heavily (<c>dnsPolicy</c>, <c>terminationMessagePath</c>,
    ///     <c>imagePullPolicy</c>, a <c>nodeName</c>), so an equality comparison would report drift on
    ///     the pass after the first apply, forever — <c>CloudConsoles.Matches</c>' finding.
    /// </remarks>
    public static bool Matches(string objectJson, string ns, JsonElement desired) {
        if (Document(objectJson) is not { } document
            || TextOf(document["kind"]) != PodKind.Kind
            || document["spec"] is not JsonObject spec) {
            return false;
        }

        var wanted = JsonNode.Parse(PodJson(ns, TextOf(document["metadata"]?["name"]), desired))!["spec"]!;

        if (TextOf(spec["restartPolicy"]) != TextOf(wanted["restartPolicy"])) {
            return false;
        }

        foreach (var side in new[] { "requests", "limits" }) {
            foreach (var resource in new[] { "cpu", "memory" }) {
                if (!SameQuantity(spec["resources"]?[side]?[resource], wanted["resources"]![side]![resource])) {
                    return false;
                }
            }
        }

        var pullSecrets = (spec["imagePullSecrets"] as JsonArray)?.Select(static x => TextOf(x?["name"])).ToList() ?? [];

        if (!pullSecrets.SequenceEqual((wanted["imagePullSecrets"] as JsonArray)?.Select(static x => TextOf(x?["name"])) ?? [])) {
            return false;
        }

        var have = spec["containers"] as JsonArray ?? [];
        var want = wanted["containers"]!.AsArray();

        if (have.Count != want.Count) {
            return false;
        }

        for (var i = 0; i < want.Count; i++) {
            if (!ContainerMatches(have[i] as JsonObject, want[i]!.AsObject())) {
                return false;
            }
        }

        var annotations = document["metadata"]?["annotations"] as JsonObject;

        foreach (var (key, expected) in new[] {
                     (LogicalSwitchAnnotation, LogicalSwitchOf(ns, desired)), (IpAddressAnnotation, IpAddress(desired))
                 }) {
            if (expected.Length > 0 && TextOf(annotations?[key]) != expected) {
                return false;
            }
        }

        return true;
    }

    static bool ContainerMatches(JsonObject? have, JsonObject want) {
        if (have is null
            || TextOf(have["name"]) != TextOf(want["name"])
            || TextOf(have["image"]) != TextOf(want["image"])) {
            return false;
        }

        if (!Texts(have["command"]).SequenceEqual(Texts(want["command"]))) {
            return false;
        }

        var env = (have["env"] as JsonArray ?? []).OfType<JsonObject>().Select(EnvironmentEntry).ToList();

        if (!env.SequenceEqual((want["env"] as JsonArray ?? []).OfType<JsonObject>().Select(EnvironmentEntry))) {
            return false;
        }

        var ports = (have["ports"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(static x => (WholeOf(x["containerPort"]), TextOf(x["protocol"]) is { Length: > 0 } p ? p : "TCP"))
            .ToList();

        return ports.SequenceEqual(
            (want["ports"] as JsonArray ?? []).OfType<JsonObject>()
            .Select(static x => (WholeOf(x["containerPort"]), TextOf(x["protocol"])))
        );
    }

    static string EnvironmentEntry(JsonObject entry) =>
        TextOf(entry["name"])
        + "="
        + (entry["valueFrom"]?["secretKeyRef"] is JsonObject reference
            ? "secret:" + TextOf(reference["name"]) + "/" + TextOf(reference["key"])
            : "value:" + TextOf(entry["value"]));

    /// <summary>Two quantities that mean one amount — <c>0.5</c> and <c>500m</c> — are the same.</summary>
    /// <remarks>
    ///     ⚠ The API server canonicalises a quantity it stores: <c>0.5</c> comes back <c>500m</c> and
    ///     <c>1024Mi</c> comes back <c>1Gi</c>. A string comparison would call every such body drifted.
    /// </remarks>
    static bool SameQuantity(JsonNode? have, JsonNode? want) =>
        KubeQuantity.TryParse(TextOf(have), out var a) && KubeQuantity.TryParse(TextOf(want), out var b) && a == b;

    /// <summary>What a group's pod is doing, reduced to the three answers a reconciler acts on.</summary>
    /// <param name="objectJson">The pod's JSON, exactly as the API server returned it.</param>
    /// <remarks>
    ///     <para>
    ///         <see cref="ReadinessKind.Ready" /> for a pod that is <c>Running</c>, and for one that has
    ///         <c>Succeeded</c> or <c>Failed</c> — a group whose containers ran to an end, which with
    ///         <c>restartPolicy: Never</c> or <c>OnFailure</c> is where it is meant to stop, and whose logs
    ///         stay readable. <see cref="ReadinessKind.NotReported" /> for a pod with no status, which is
    ///         a harness with no kubelet. <see cref="ReadinessKind.NotReady" /> for a <c>Pending</c> pod,
    ///         with the reason the kubelet or the scheduler gave — <c>ErrImagePull</c> and its message,
    ///         <c>Unschedulable</c> and its sentence.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>Running</c> is converged even when a container keeps crashing.</b> A crash loop is
    ///         the workload's failure and not the platform's — the pod exists, is placed and holds its
    ///         budget — and an operation that stayed <c>InProgress</c> until its budget ran out would
    ///         report a provisioning failure for a tenant's bug. The restart count is in the summary
    ///         <c>ObserveAsync</c> writes, and the logs say why.
    ///     </para>
    /// </remarks>
    public static Readiness ReadinessOf(string objectJson) {
        var status = Document(objectJson)?["status"] as JsonObject;
        var phase = TextOf(status?["phase"]);

        if (phase.Length == 0) {
            return new(ReadinessKind.NotReported, "the kubelet has not reported on the group yet");
        }

        switch (phase) {
            case "Running":
                return new(ReadinessKind.Ready, "the group is running");
            case "Succeeded":
                return new(ReadinessKind.Ready, "the group's containers ran to completion");
            case "Failed":
                return new(ReadinessKind.Ready, "the group's containers exited and will not be restarted: " + Waiting(status!));
        }

        return new(ReadinessKind.NotReady, $"the group is {phase}: {Waiting(status!)}");
    }

    /// <summary>The first reason a pod gives for not running: a container's waiting or terminated state, else the scheduler's.</summary>
    static string Waiting(JsonObject status) {
        foreach (var container in (status["containerStatuses"] as JsonArray ?? []).OfType<JsonObject>()) {
            foreach (var state in new[] { "waiting", "terminated" }) {
                if (container["state"]?[state] is JsonObject reason && TextOf(reason["reason"]) is { Length: > 0 } word) {
                    var message = TextOf(reason["message"]);
                    return $"{TextOf(container["name"])} is {word}" + (message.Length > 0 ? $" ({message})" : string.Empty);
                }
            }
        }

        var scheduled = (status["conditions"] as JsonArray ?? []).OfType<JsonObject>()
            .FirstOrDefault(static x => TextOf(x["type"]) == "PodScheduled" && TextOf(x["status"]) == "False");

        return scheduled is null
            ? "no container has reported why"
            : "the scheduler says " + TextOf(scheduled["message"]);
    }

    /// <summary>How many times the pod's containers have restarted, summed.</summary>
    /// <param name="objectJson">The pod's JSON.</param>
    public static int RestartCount(string objectJson) =>
        ((Document(objectJson)?["status"]?["containerStatuses"] as JsonArray) ?? [])
        .OfType<JsonObject>()
        .Sum(static x => Math.Max(0, WholeOf(x["restartCount"])));

    /// <summary>The pod's <c>metadata.uid</c>, or empty.</summary>
    /// <param name="objectJson">The pod's JSON.</param>
    public static string UidOf(string objectJson) => TextOf(Document(objectJson)?["metadata"]?["uid"]);

    /// <summary>Whether the pod is already being deleted.</summary>
    /// <param name="objectJson">The pod's JSON.</param>
    public static bool IsTerminating(string objectJson) => TextOf(Document(objectJson)?["metadata"]?["deletionTimestamp"]).Length > 0;

    /// <summary>What the kubelet says about a group, as the reconciler reads it.</summary>
    /// <param name="Kind">Which of the three.</param>
    /// <param name="Detail">The words to report.</param>
    public readonly record struct Readiness(ReadinessKind Kind, string Detail);

    /// <summary>The three answers <see cref="ReadinessOf" /> gives.</summary>
    public enum ReadinessKind {
        /// <summary>The pod carries no status; nothing has reported on it.</summary>
        NotReported,

        /// <summary>The pod is pending, with the reason the kubelet or the scheduler gave.</summary>
        NotReady,

        /// <summary>Running, or ran to an end.</summary>
        Ready
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the group runs in.</param>
    /// <param name="containers">The containers, each <c>name=image</c>.</param>
    /// <param name="command">The main container's command.</param>
    /// <param name="cpu">The group's CPU.</param>
    /// <param name="memory">The group's memory.</param>
    /// <param name="restartPolicy">The restart policy.</param>
    /// <param name="environment">Plain variables, each <c>NAME=value</c>.</param>
    /// <param name="secureEnvironment">Vault-backed variables, each <c>NAME=handle</c>.</param>
    /// <param name="ports">Ports, each <c>80</c> or <c>53/UDP</c>.</param>
    /// <param name="registryServer">The registry a credential is for, or empty.</param>
    /// <param name="registryUsername">Its user, or empty.</param>
    /// <param name="registryPassword">Its password's vault handle, or empty.</param>
    /// <param name="virtualNetwork">The virtual network, or empty.</param>
    /// <param name="subnet">The subnet, or empty.</param>
    /// <param name="ipAddress">The pinned address, or empty.</param>
    /// <param name="publicIpAddress">The public address resource, or empty.</param>
    /// <param name="location">The region.</param>
    /// <remarks>⚠ Every property it writes is a <b>leaf</b>, for the reason <c>StorageAccounts.Body</c> gives.</remarks>
    public static string Body(
        Guid clusterId,
        IEnumerable<string>? containers = null,
        IEnumerable<string>? command = null,
        string cpu = DefaultCpu,
        string memory = DefaultMemory,
        string restartPolicy = DefaultRestartPolicy,
        IEnumerable<string>? environment = null,
        IEnumerable<string>? secureEnvironment = null,
        IEnumerable<string>? ports = null,
        string registryServer = "",
        string registryUsername = "",
        string registryPassword = "",
        string virtualNetwork = "",
        string subnet = "",
        string ipAddress = "",
        string publicIpAddress = "",
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["containers"] = Array(containers ?? [DefaultContainer]),
                ["command"] = Array(command ?? []),
                ["cpu"] = cpu,
                ["memory"] = memory,
                ["restartPolicy"] = restartPolicy,
                ["environment"] = Array(environment ?? []),
                ["secureEnvironment"] = Array(secureEnvironment ?? []),
                ["ports"] = Array(ports ?? []),
                ["registry"] = new JsonObject {
                    ["server"] = registryServer, ["username"] = registryUsername, ["password"] = registryPassword
                },
                ["network"] = new JsonObject {
                    ["virtualNetwork"] = virtualNetwork,
                    ["subnet"] = subnet,
                    ["ipAddress"] = ipAddress,
                    ["publicIpAddress"] = publicIpAddress
                }
            }
        }.ToJsonString();

    static JsonArray Array(IEnumerable<string> values) => new([.. values.Select(static x => (JsonNode)JsonValue.Create(x))]);

    // ── Reading ───────────────────────────────────────────────────────────────────────────────

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section && section.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement? element, string fallback) =>
        element is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? fallback : fallback;

    static IEnumerable<string> Strings(JsonElement? element, bool keepEmpty = false) {
        if (element is not { ValueKind: JsonValueKind.Array } array) {
            yield break;
        }

        foreach (var item in array.EnumerateArray()) {
            if (item.ValueKind is JsonValueKind.String && item.GetString() is { } text && (keepEmpty || text.Length > 0)) {
                yield return text;
            }
        }
    }

    static ImmutableArray<(string Name, string Value)> Pairs(JsonElement desired, string property) => [
        .. Strings(Property(desired, property))
            .Select(static x => x.IndexOf('=', StringComparison.Ordinal) is var at and > 0 ? (x[..at], x[(at + 1)..]) : (x, string.Empty))
    ];

    static JsonObject? Document(string? objectJson) {
        if (string.IsNullOrEmpty(objectJson)) {
            return null;
        }

        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }

    static string TextOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : string.Empty;

    static int WholeOf(JsonNode? node) => node is JsonValue value && value.TryGetValue<int>(out var number) ? number : -1;

    static IEnumerable<string> Texts(JsonNode? node) => (node as JsonArray ?? []).Select(TextOf);

    [GeneratedRegex("^" + ContainerNamePattern + "$")]
    private static partial Regex ContainerName();

    [GeneratedRegex("^" + VariableNamePattern + "$")]
    private static partial Regex VariableName();
}
