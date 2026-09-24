using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/virtualNetworks/applicationGateways</c> — one
///     L7 gateway with a web application firewall inside a tenant's virtual network, as an HAProxy
///     proxy and a Coraza agent in one pod, the <c>ConfigMap</c> both read, and the <c>Secret</c> that
///     holds the HTTPS certificate when there is one.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Application gateway, M2 · 2.0 EM</b>: listeners,
///         host and path routes, TLS, and a WAF with a rule-set selection. That section named Envoy
///         Gateway and Coraza as an Envoy filter; this type ships on <b>HAProxy and Coraza SPOA</b>, and
///         the decision is written into docs/plan/14 § Application gateway with the four readings behind
///         it. The short form: the bundle carries no Gateway API controller, so Envoy Gateway was a bundle
///         component, a CRD family and a controller owning every object before it was a chart; a
///         controller-managed proxy is a pod this platform does not template, which is exactly what
///         putting it on a tenant's subnet needs; and Envoy Gateway reaches a bare address only through
///         its <c>Backend</c> extension, which is off by default <i>"due to security considerations"</i>.
///         HAProxy is already the precedent (<see cref="LoadBalancers" />), its own WAF is an
///         Enterprise product, and <c>corazawaf/coraza-spoa</c> is the OWASP Coraza project's agent for
///         exactly this pairing — Apache-2.0, with the OWASP CRS embedded, and run against HAProxy 2.8,
///         3.0 and 3.2 by the CRS regression suite in its own CI (<c>.github/workflows/ftw.yaml</c> at
///         <c>v0.7.3</c>).
///     </para>
///     <para>
///         ⚠ <b>A CHILD OF <c>virtualNetworks</c>, ON <see cref="LoadBalancers" />' ARGUMENT</b>: the
///         pod is annotated onto one subnet of one VPC, so the network comes from the address and cannot
///         be wrong. The objects are namespaced like that type's, and ⚠ <b>named
///         <c>{network}.{name}</c> and not its <c>{network}-{name}</c></b> — both types render a
///         <c>ConfigMap</c> and a <c>Deployment</c> into one namespace, and <see cref="ObjectNameOf" />
///         says what one name for both did.
///     </para>
///     <para>
///         ⚠
///         <b>
///             EVERY LIST IN THE BODY IS AN ARRAY OF PATTERNED STRINGS, AND THAT IS THE SCHEMA'S LIMIT
///             TURNED INTO A GRAMMAR RATHER THAN A WORKAROUND.
///         </b> A route, a pool member, a firewall exclusion and a custom rule are each an object in
///         Azure's shape, and <c>SchemaProperty.ElementKind</c> refuses an object element. Each is
///         written as one string with a small grammar instead — <c>host/path=pool</c>,
///         <c>pool=target:port</c> — which <see cref="BodyProblem" /> checks against a closed pattern
///         before anything is rendered. ⚠ <b>The grammars are closed on purpose</b>: no
///         element reaches HAProxy's configuration or Coraza's SecLang as text the tenant wrote, only as
///         values out of a character class that cannot close a quote, open a directive or start a new
///         line. A custom rule that accepted raw SecLang would be a tenant writing
///         <c>Include /etc/…</c> into an agent that runs beside their proxy.
///     </para>
///     <para>
///         ⚠
///         <b>
///             ROUTES ARE A LIST ON THE GATEWAY AND NOT A CHILD TYPE, WHICH REVERSES WHAT
///             <c>application-gateway-is-not-an-http-mode-of-this-proxy</c> PREDICTED.
///         </b> That measurement made routes one child each because an Envoy Gateway <c>HTTPRoute</c> is
///         its own object. On HAProxy the routing table is lines in one file, so a route child would
///         write into its parent's file — the <c>routeTables</c> refusal. The list is on the parent, the
///         order is the tenant's, and the first rule that matches wins.
///     </para>
///     <para>
///         ⚠
///         <b>
///             A POOL MEMBER MAY BE A RESOURCE ID, AND ONE KIND OF RESOURCE IS ACCEPTED.
///         </b> <c>CyberCloud.Compute/virtualMachines</c> resolves through
///         <c>ReconcileContext.View</c> (#90) to the address KubeVirt reports on the machine's
///         <c>VirtualMachineInstance</c>. docs/plan/14 also names container groups, and
///         <c>CyberCloud.ContainerInstance/containerGroups</c> is not a published type — a member naming
///         one is refused by name. The resolved addresses are written into the <c>ConfigMap</c> beside
///         the configuration (<see cref="ResolvedKey" />), so a drift scan, which has no view, can still
///         re-render what the reconciler rendered and compare.
///     </para>
///     <para>
///         ⚠ <b>LICENCE.</b> HAProxy is GPL-2.0-or-later, run unmodified in its own container —
///         <see cref="LoadBalancers" />' reading of ADR-011's ClamAV row. Coraza SPOA, Coraza and the
///         OWASP CRS it embeds are Apache-2.0. Both images are pinned by digest.
///     </para>
/// </remarks>
public static class ApplicationGateways {
    /// <summary>The provider namespace — the family's.</summary>
    public const string ProviderNamespace = VirtualNetworks.ProviderNamespace;

    /// <summary>The type path, a child of <c>virtualNetworks</c>.</summary>
    public const string TypePath = "virtualNetworks/applicationGateways";

    /// <summary>The one api-version. ⚠ Equal to the rest of the family's.</summary>
    public const string V2026 = VirtualNetworks.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/application-gateway";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The images ────────────────────────────────────────────────────────────────────────────

    /// <summary>The proxy image, by tag and digest.</summary>
    /// <remarks>
    ///     ⚠ <b>A digest, which is the opposite of <see cref="LoadBalancers" />' choice</b> and is
    ///     right here for a reason that type does not have: the proxy and the agent are a <i>pair</i>,
    ///     and the SPOE protocol between them is what coraza-spoa's CI tests against named HAProxy
    ///     lines. A moving tag on either side is an untested pair on the next pod restart. Resolved
    ///     against <c>registry-1.docker.io</c> on 2026-09-23: <c>3.2.23-alpine</c> and
    ///     <c>3.2-alpine</c> both answered with this index digest.
    /// </remarks>
    public const string ProxyImage =
        "docker.io/library/haproxy:3.2.23-alpine@sha256:5961c68bc8a81c5124d0a98ab20f81b74717d6afe596e805977d9cf84c126222";

    /// <summary>The WAF agent image, by tag and digest.</summary>
    /// <remarks>
    ///     ⚠ Resolved against <c>ghcr.io</c> on 2026-09-23 — the multi-architecture index of
    ///     <c>ghcr.io/corazawaf/coraza-spoa:0.7.3</c>. Its image config reads <c>User: 0</c> and
    ///     <c>Cmd: ["/coraza-spoa", "--config", "/config.yaml"]</c> on a distroless static base, so
    ///     the pod sets a numeric non-root user and passes its own <c>--config</c>. Built from
    ///     <c>go.mod</c> requiring <c>coraza/v3 v3.7.0</c> and <c>coraza-coreruleset/v4 v4.25.0</c>,
    ///     which is the CRS <see cref="CrsVersions" /> offers.
    /// </remarks>
    public const string WafImage =
        "ghcr.io/corazawaf/coraza-spoa:0.7.3@sha256:2d262d2eb34eb3efa1d2e4a164903c446beeee69877b7e693d46040b3fee38c2";

    /// <summary>The OWASP CRS versions a tenant may ask for — the ones a pinned agent image embeds.</summary>
    /// <remarks>
    ///     ⚠ <b>One, because a CRS version is an agent image</b>: coraza-spoa compiles the rule set in
    ///     (<c>@owasp_crs/*.conf</c> is an embedded file system), so offering a second version means
    ///     pinning a second agent digest, not changing a setting.
    /// </remarks>
    public static ImmutableArray<string> CrsVersions { get; } = ["4.25"];

    /// <summary>The CRS version a body gets when it names none.</summary>
    public const string DefaultCrsVersion = "4.25";

    // ── Sizing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The default preset.</summary>
    public const string DefaultPreset = "c1.small";

    /// <summary>What each preset gives the proxy and the agent, each.</summary>
    /// <remarks>
    ///     ⚠ <b>The same figure for both containers</b>, because on an HTTP gateway with the CRS on the
    ///     agent is where the CPU goes: every request is parsed by HAProxy once and evaluated by a few
    ///     hundred rules once. ⚠ <b>The same table is in the chart</b> and
    ///     <c>NetworkApplicationGatewayTests</c> compares them row for row.
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal) {
            ["c1.small"] = ("250m", "256Mi"), ["c1.medium"] = ("500m", "512Mi"), ["c1.large"] = ("1", "1Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>What one container of a body's preset costs.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static (string Cpu, string Memory) Resources(JsonElement desired) =>
        Presets.TryGetValue(SizingPreset(desired), out var chosen) ? chosen : Presets[DefaultPreset];

    /// <summary>How many containers the pod runs: the proxy, and the agent unless the WAF is off.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int Containers(JsonElement desired) => WafMode(desired) == WafOff ? 1 : 2;

    // ── The action ────────────────────────────────────────────────────────────────────────────

    /// <summary>The action that reports what the gateway routes, to what, and whether it is up.</summary>
    public const string RoutingAction = "showRouting";

    /// <summary>The permission <see cref="RoutingAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <c>read</c>. The certificate is reported by its vault handle and never by value.
    /// </remarks>
    public const string RoutingPermission = "read";

    // ── The objects an application gateway IS ────────────────────────────────────────────────

    /// <summary>The configuration of both processes, as a <c>ConfigMap</c>.</summary>
    public static GroupVersionKind ConfigMapKind { get; } = LoadBalancers.ConfigMapKind;

    /// <summary>The pod.</summary>
    public static GroupVersionKind DeploymentKind { get; } = LoadBalancers.DeploymentKind;

    /// <summary>
    ///     The object name: the parent network's name and this resource's, joined by a dot — a
    ///     character no resource name contains, so no load balancer's name can ever equal it.
    /// </summary>
    /// <param name="id">The gateway's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             NOT <see cref="LoadBalancers.ObjectNameOf" />' HYPHEN, BECAUSE BOTH TYPES RENDER A
    ///             <c>ConfigMap</c> AND A <c>Deployment</c> INTO ONE NAMESPACE UNDER ONE FIELD MANAGER.
    ///         </b> The manager is <c>cybercloud/</c> plus the provider namespace, and neither the
    ///         apply nor the delete path checks the <c>cybercloud.io/resource-id</c> label on what it
    ///         touches. With the hyphen, a gateway and a load balancer both called <c>web</c> in the
    ///         network <c>net</c> were both <c>net-web</c>: the gateway's configuration overwrote the
    ///         balancer's without a conflict, its Deployment then failed on the immutable selector,
    ///         and deleting that failed gateway deleted the balancer's pod. Measured by #31's review on
    ///         a real k3s, where a gateway left behind by one lifecycle class broke the load balancer's
    ///         claims test with <i>"spec.selector: Invalid value … field is immutable"</i>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A dot, because a hyphen cannot be made injective at all.</b> Resource names are
    ///         <see cref="ResourceNaming.Pattern" />, which admits interior hyphens, so any
    ///         hyphen-joined scheme collides with some other network-and-name pair — a prefix or a
    ///         suffix only moves the collision. A <c>ConfigMap</c>, a <c>Deployment</c> and a
    ///         <c>Secret</c> take DNS-1123 <i>subdomain</i> names, which admit dots, and the one place
    ///         the name becomes a label value (<see cref="LoadBalancers.InstanceLabel" />) admits them
    ///         too. The pods are then named <c>net.web-…</c>; the pod template names its volumes itself
    ///         rather than after the object, since a volume name is a DNS label.
    ///     </para>
    /// </remarks>
    public static string ObjectNameOf(ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the Deployment it renders would collide with "
                + "every other network's gateway of the same name in the same resource group.",
                nameof(id)
            )
            : id.ParentNames.Replace('/', '.') + "." + id.Name;

    /// <summary>The name of the <c>Secret</c> holding the HTTPS certificate.</summary>
    /// <param name="id">The gateway's address.</param>
    public static string TlsSecretNameOf(ResourceId id) => ObjectNameOf(id) + "-tls";

    /// <summary>The parent network's name.</summary>
    /// <param name="id">The gateway's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> has no parent.</exception>
    public static string NetworkOf(ResourceId id) => LoadBalancers.NetworkOf(id);

    /// <summary>The Kube-OVN <c>Subnet</c> the pod is placed on.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ Composed through <see cref="NetworkSubnets.ObjectNameOf(string, string, string)" />, and
    ///     the network half comes from the address — <see cref="LoadBalancers.LogicalSwitchOf" />' rule.
    /// </remarks>
    public static string LogicalSwitchOf(string ns, ResourceId id, JsonElement desired) =>
        NetworkSubnets.ObjectNameOf(ns, NetworkOf(id), Subnet(desired));

    /// <summary>The <c>ConfigMap</c> a gateway owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    public static ObjectRef ConfigMapRef(string ns, ResourceId id) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The <c>Deployment</c> a gateway owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    public static ObjectRef DeploymentRef(string ns, ResourceId id) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The certificate <c>Secret</c> a gateway with an HTTPS listener owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    public static ObjectRef TlsSecretRef(string ns, ResourceId id) => KubeSecret.Ref(ns, TlsSecretNameOf(id));

    /// <summary>The two objects every gateway owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    /// <remarks>
    ///     ⚠ <b>Two, and a third only with a certificate.</b> The <c>Secret</c> exists when the body
    ///     names a certificate and not otherwise, so it is not in this list — which is what the shared
    ///     conformance suite asserts ownership over — and it is in <see cref="AllObjects" />, which is
    ///     what a teardown removes.
    /// </remarks>
    public static ImmutableArray<ObjectRef> Objects(string ns, ResourceId id) =>
        [ConfigMapRef(ns, id), DeploymentRef(ns, id)];

    /// <summary>Every object a gateway may own, in teardown order: the pod first.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    public static ImmutableArray<ObjectRef> AllObjects(string ns, ResourceId id) =>
        [DeploymentRef(ns, id), ConfigMapRef(ns, id), TlsSecretRef(ns, id)];

    /// <summary>The annotation carrying the hash of every file both processes read.</summary>
    /// <remarks>
    ///     ⚠ <see cref="LoadBalancers.ConfigChecksumAnnotation" />' reason, twice over: neither HAProxy
    ///     nor coraza-spoa's rule set picks up an edited <c>ConfigMap</c> by itself in the shape this
    ///     platform needs — coraza-spoa does watch its file, and HAProxy does not, and a gateway half
    ///     updated is worse than one not updated.
    /// </remarks>
    public const string ConfigChecksumAnnotation = "cybercloud.io/gateway-config";

    /// <summary>The annotation carrying the hash of the certificate, when there is one.</summary>
    /// <remarks>
    ///     ⚠ HAProxy reads <c>crt</c> once at start, so a rotated certificate is a rollout — the same
    ///     argument as the configuration's. The value is a SHA-256 of the PEM, which reveals nothing a
    ///     reader of the pod template could use.
    /// </remarks>
    public const string TlsChecksumAnnotation = "cybercloud.io/gateway-tls";

    /// <summary>The <c>ConfigMap</c> key holding HAProxy's configuration.</summary>
    public const string ProxyConfigFile = "haproxy.cfg";

    /// <summary>The <c>ConfigMap</c> key holding the SPOE configuration HAProxy's filter reads.</summary>
    public const string SpoeConfigFile = "coraza.cfg";

    /// <summary>The <c>ConfigMap</c> key holding coraza-spoa's own configuration.</summary>
    public const string WafConfigFile = "coraza-spoa.yaml";

    /// <summary>The <c>ConfigMap</c> key recording what each resource-id pool member resolved to.</summary>
    /// <remarks>
    ///     ⚠ <b>Neither process reads it; the platform does.</b> <c>ObserveContext</c> carries no
    ///     cross-resource view, so a drift scan cannot resolve a machine's address itself — it
    ///     re-renders the configuration from the body and from this record, and compares.
    /// </remarks>
    public const string ResolvedKey = "resolved-members.json";

    /// <summary>The <c>Secret</c> key and file name of the PEM bundle.</summary>
    public const string TlsFile = "tls.pem";

    /// <summary>Where the certificate is mounted.</summary>
    public const string TlsDirectory = "/etc/cybercloud/tls";

    /// <summary>Where the WAF agent's configuration is mounted.</summary>
    public const string WafConfigDirectory = "/etc/coraza-spoa";

    /// <summary>The port the WAF agent listens on, on the pod's loopback only.</summary>
    /// <remarks>
    ///     ⚠ <b>Loopback</b>, so nothing on the tenant's network can speak SPOP to the agent. And it is
    ///     reserved: a listener on this port would collide with the agent's socket inside the pod's
    ///     one network namespace and HAProxy would not start.
    /// </remarks>
    public const int WafPort = 9000;

    /// <summary>The port the proxy answers its readiness probe on.</summary>
    /// <remarks>
    ///     ⚠ <b>A monitor frontend rather than a TCP probe on the listener</b>, because a TCP probe
    ///     says HAProxy is up and not that the WAF behind it is: <c>monitor fail</c> reports the pod
    ///     unready while the agent's backend has no healthy server, which in prevention mode is a
    ///     gateway answering 503 to everything.
    /// </remarks>
    public const int ReadinessPort = 8405;

    /// <summary>The path the readiness probe asks for.</summary>
    public const string ReadinessPath = "/healthz";

    /// <summary>The uid the WAF agent runs as — the distroless <c>nonroot</c> user.</summary>
    /// <remarks>
    ///     ⚠ The image's own <c>User</c> is <c>0</c>, so without this the agent runs as root. 65532 is
    ///     distroless's conventional non-root uid and the binary needs nothing a uid owns.
    /// </remarks>
    public const int WafUid = 65532;

    /// <summary>The agent's binary, as the image's <c>Cmd</c> names it.</summary>
    public const string WafBinary = "/coraza-spoa";

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>WAF off: no agent container, no filter.</summary>
    public const string WafOff = "off";

    /// <summary>WAF in detection mode: every rule is evaluated and logged, nothing is blocked.</summary>
    public const string WafDetection = "detection";

    /// <summary>WAF in prevention mode: a request the rule set scores as an attack gets a 403.</summary>
    public const string WafPrevention = "prevention";

    /// <summary>The three WAF modes.</summary>
    public static ImmutableArray<string> WafModes { get; } = [WafOff, WafDetection, WafPrevention];

    /// <summary>The shape of one routing rule: <c>host/path=pool</c>.</summary>
    /// <remarks>
    ///     The host is <c>*</c>, an exact name or <c>*.</c> and a suffix; the path is a prefix starting
    ///     with <c>/</c>. ⚠ Lower-case names only, because HAProxy compares the <c>Host</c> header after
    ///     lower-casing it and a rule with an upper-case letter could never match.
    /// </remarks>
    public const string RulePattern =
        @"(\*|(\*\.)?[a-z0-9]([-a-z0-9]*[a-z0-9])?(\.[a-z0-9]([-a-z0-9]*[a-z0-9])?)*)(/[A-Za-z0-9._~-]*)+=[a-z0-9]([-a-z0-9]*[a-z0-9])?";

    /// <summary>The shape of one pool member: <c>pool=target:port</c>.</summary>
    /// <remarks>
    ///     ⚠ Loose on the target on purpose — an IPv4 address, a bracketed IPv6 address or a resource id
    ///     path — for <see cref="LoadBalancers.AddressListPattern" />' reason: the exact reading is
    ///     <see cref="BodyProblem" />'s.
    /// </remarks>
    public const string MemberPattern = @"[a-z0-9]([-a-z0-9]*[a-z0-9])?=[^\s=]+:[0-9]{1,5}";

    /// <summary>The shape of one WAF exclusion.</summary>
    /// <remarks>
    ///     A rule id, a range of them, or a rule id and one request field that rule stops inspecting —
    ///     <c>942100:ARGS:password</c>. The field names are the three CRS exclusions are written against.
    /// </remarks>
    public const string ExclusionPattern =
        "[0-9]{3,7}(-[0-9]{3,7}|:(ARGS|REQUEST_HEADERS|REQUEST_COOKIES):[A-Za-z0-9_.-]{1,64})?";

    /// <summary>The shape of one custom WAF rule.</summary>
    /// <remarks>
    ///     <c>deny</c> or <c>allow</c>, then one of five matches. ⚠ Every value is a character class
    ///     that cannot hold a quote, a comma or a space — which is what keeps it inside the SecLang
    ///     string it is rendered into.
    /// </remarks>
    public const string CustomRulePattern =
        "(deny|allow) (ip [0-9A-Fa-f:./]{2,49}|path /[A-Za-z0-9._~/-]{0,200}|host [a-z0-9.-]{1,253}"
        + "|useragent [A-Za-z0-9._/-]{1,64}|method [A-Z]{3,7})";

    /// <summary>How many routing rules, pool members, exclusions or custom rules one body may carry.</summary>
    /// <remarks>
    ///     ⚠ Checked in <see cref="BodyProblem" />, because <c>SchemaProperty</c> bounds a length and
    ///     not an arity. A product decision rather than HAProxy's limit: past this, the rendered
    ///     configuration stops fitting comfortably in one <c>ConfigMap</c> and the gateway is a mesh.
    /// </remarks>
    public const int MaxEntries = 64;

    /// <summary>The default frontend address. ⚠ Inside the family's own example subnet.</summary>
    public const string DefaultFrontendV4 = "10.20.1.20";

    /// <summary>The default subnet.</summary>
    public const string DefaultSubnet = "web";

    /// <summary>The default routing rule: everything to the pool called <c>web</c>.</summary>
    public const string DefaultRule = "*/=web";

    /// <summary>The default pool member.</summary>
    public const string DefaultMember = "web=10.20.1.11:8080";

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     ⚠ <b>No <c>version</c> property</b>, unlike <see cref="LoadBalancers" />: the proxy and the
    ///     agent are a tested pair pinned by digest, and a tenant-selectable HAProxy line would be an
    ///     untested pair.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the gateway is billed in. ⚠ It must be its virtual "
                    + "network's region."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The gateway's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the gateway runs in. ⚠ It must be the cluster the "
                    + "virtual network was created in."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    "/properties/subnet",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The subnet of this virtual network the gateway sits on. The "
                    + "frontend address below must be inside its range."
                ) {
                    Pattern = ResourceNaming.Pattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Widget = WidgetHint.Subnet,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultSubnet + "\"",
                    ExampleJson = "\"web\""
                },

                // ── The frontend ───────────────────────────────────────────────────────────────
                new("/properties/frontend", SchemaKind.Nested, Description: "The address clients connect to."),
                new(
                    "/properties/frontend/v4",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The IPv4 address the gateway answers on, inside the subnet's range. "
                    + "⚠ Required: there is no DNS inside a virtual network, so an address nobody "
                    + "picked is an address nothing can be pointed at."
                ) {
                    Pattern = IpAddresses.V4Pattern,
                    Widget = WidgetHint.Cidr,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultFrontendV4 + "\"",
                    ExampleJson = "\"10.20.1.20\""
                },
                new(
                    "/properties/frontend/v6",
                    SchemaKind.Text,
                    Description: "The IPv6 address the gateway also answers on, or empty. Lower case only."
                ) {
                    Pattern = IpAddresses.OptionalV6Pattern,
                    Widget = WidgetHint.Cidr,
                    Immutable = true,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"fd00:20:1::20\""
                },

                // ── Listeners ──────────────────────────────────────────────────────────────────
                new(
                    "/properties/listeners",
                    SchemaKind.Nested,
                    Description: "The HTTP listener, and the HTTPS listener when a certificate is given."
                ),
                new(
                    "/properties/listeners/httpPort",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "The port plain HTTP is served on."
                ) { Minimum = 1, Maximum = 65535, DefaultJson = "80" },
                new(
                    "/properties/listeners/httpsPort",
                    SchemaKind.WholeNumber,
                    Description: "The port HTTPS is served on. Used only when a certificate is given."
                ) { Minimum = 1, Maximum = 65535, DefaultJson = "443" },
                new(
                    "/properties/listeners/certificate",
                    SchemaKind.Text,
                    Description: "A vault handle — path#field, optionally @version — whose value is a "
                    + "PEM bundle: the certificate chain followed by its private key. Empty means no "
                    + "HTTPS listener. ⚠ The path must be under your own tenant's vault prefix, "
                    + "tenants/<tenantId>/. The value is written into a Secret the proxy mounts and "
                    + "never into this body."
                ) {
                    Pattern = VaultHandlePattern,
                    MaxLength = 512,
                    Widget = WidgetHint.SecretRef,
                    DefaultJson = "\"\""
                },

                // ── Routing ────────────────────────────────────────────────────────────────────
                new(
                    "/properties/routingRules",
                    SchemaKind.Array,
                    Required: true,
                    Description: "Where each request goes, as host/path=pool — for example "
                    + "shop.example.com/api=api, *.example.com/=web or */=web. The host is *, a name, "
                    + "or *. and a suffix; the path is a prefix, matched on whole segments. ⚠ The first "
                    + "rule that matches wins, in the order written. A request no rule matches gets "
                    + "404."
                ) {
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[\"" + DefaultRule + "\"]",
                    ExampleJson = """["shop.example.com/api=api", "*/=web"]"""
                },
                new(
                    "/properties/backendPools",
                    SchemaKind.Array,
                    Required: true,
                    Description: "The pool members, one per entry, as pool=target:port. The target is "
                    + "an IPv4 address, an IPv6 address in brackets, or the resource id of a virtual "
                    + "machine in this network — for example web=10.20.1.11:8080 or "
                    + "api=/tenants/…/providers/CyberCloud.Compute/virtualMachines/api-1:8080. An address "
                    + "is written in its usual form (10.0.0.1, not 10.1) and may not be loopback, "
                    + "link-local, multicast or the gateway's own. ⚠ A "
                    + "machine is resolved to its address only once this gateway has been granted "
                    + "read on it."
                ) {
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[\"" + DefaultMember + "\"]",
                    ExampleJson = """["web=10.20.1.11:8080", "web=10.20.1.12:8080"]"""
                },

                // ── Health probes ──────────────────────────────────────────────────────────────
                new(
                    "/properties/health",
                    SchemaKind.Nested,
                    Description: "How a pool member is decided to be up: an HTTP GET that answers "
                    + "2xx or 3xx. ⚠ Probing cannot be turned off."
                ),
                new(
                    "/properties/health/path",
                    SchemaKind.Text,
                    Description: "The path every member is probed on."
                ) { Pattern = "/[A-Za-z0-9._~/-]{0,200}", DefaultJson = "\"/\"" },
                new(
                    "/properties/health/intervalSeconds",
                    SchemaKind.WholeNumber,
                    Description: "How often each member is probed."
                ) { Minimum = 2, Maximum = 60, DefaultJson = "5" },
                new(
                    "/properties/health/unhealthyAfter",
                    SchemaKind.WholeNumber,
                    Description: "How many failed probes take a member out of its pool."
                ) { Minimum = 1, Maximum = 10, DefaultJson = "3" },
                new(
                    "/properties/health/healthyAfter",
                    SchemaKind.WholeNumber,
                    Description: "How many successful probes put a member back."
                ) { Minimum = 1, Maximum = 10, DefaultJson = "2" },

                // ── The WAF policy ─────────────────────────────────────────────────────────────
                new(
                    "/properties/waf",
                    SchemaKind.Nested,
                    Description: "The web application firewall: the OWASP Core Rule Set on Coraza."
                ),
                new(
                    "/properties/waf/mode",
                    SchemaKind.Text,
                    Description: "prevention answers 403 to a request the rule set scores as an "
                    + "attack; detection evaluates and logs every rule and blocks nothing; off runs "
                    + "no firewall at all. ⚠ In prevention mode a firewall that does not answer in "
                    + "time is a 503, never a pass."
                ) {
                    AllowedValues = [.. WafModes.Order(StringComparer.Ordinal)],
                    DefaultJson = "\"" + WafPrevention + "\""
                },
                new(
                    "/properties/waf/crsVersion",
                    SchemaKind.Text,
                    Description: "The OWASP Core Rule Set version. ⚠ One is offered: the rule set is "
                    + "compiled into the firewall image, so a second version is a second image."
                ) {
                    AllowedValues = [.. CrsVersions],
                    DefaultJson = "\"" + DefaultCrsVersion + "\""
                },
                new(
                    "/properties/waf/paranoiaLevel",
                    SchemaKind.WholeNumber,
                    Description: "The CRS paranoia level. 1 is the default and blocks little that is "
                    + "legitimate; each level above adds rules and false positives."
                ) { Minimum = 1, Maximum = 4, DefaultJson = "1" },
                new(
                    "/properties/waf/exclusions",
                    SchemaKind.Array,
                    Description: "Rules to turn off, as a rule id (942100), a range (942100-942199), or "
                    + "a rule id and one request field it stops inspecting (942100:ARGS:password)."
                ) {
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[]",
                    ExampleJson = """["942100:ARGS:password"]"""
                },
                new(
                    "/properties/waf/customRules",
                    SchemaKind.Array,
                    Description: "Rules evaluated before the rule set, in order: deny or allow, then "
                    + "ip <address or range>, path <prefix>, host <name>, useragent <text> or method "
                    + "<METHOD> — for example deny ip 203.0.113.0/24 or allow path /healthz. A host is "
                    + "matched in any case and with any port; a path after percent-decoding and resolving "
                    + "//, . and .. segments. ⚠ allow skips the rule set for that request entirely."
                ) {
                    ElementKind = SchemaKind.Text,
                    DefaultJson = "[]",
                    ExampleJson = """["deny ip 203.0.113.0/24"]"""
                },

                // ── Limits and sizing ──────────────────────────────────────────────────────────
                new(
                    "/properties/limits",
                    SchemaKind.Nested,
                    Description: "What the gateway refuses rather than passes on."
                ),
                new(
                    "/properties/limits/maxConnections",
                    SchemaKind.WholeNumber,
                    Description: "How many client connections the gateway accepts at once."
                ) { Minimum = 10, Maximum = 100000, DefaultJson = "2000" },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory for the proxy and the firewall, each."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "How much the proxy and the firewall each get. With the firewall on, "
                    + "the firewall is where the CPU goes."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"" + DefaultPreset + "\""
                }
            ]
        );

    /// <summary>A vault handle as one string, or empty. The spelling <c>SecretRef.ToString</c> prints.</summary>
    public const string VaultHandlePattern = @"([^#@\s]+#[^#@\s]+(@[^#@\s]+)?)?";

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    /// <summary>What a <c>POST …/showRouting</c> returns.</summary>
    /// <remarks>
    ///     ⚠ <b><c>readyReplicas</c> and <c>unresolved</c> are the two facts the body cannot carry.</b>
    ///     The rules and pools are the body expanded; which machine members did not resolve, and whether
    ///     the pod is serving at all, are not.
    /// </remarks>
    public static ResourceSchema RoutingResponse { get; } =
        ResourceSchema.Of(
            [
                new("/listeners", SchemaKind.Array, Required: true, Description: "Each listener, as address:port and protocol.") {
                    ElementKind = SchemaKind.Text
                },
                new("/rules", SchemaKind.Array, Required: true, Description: "The routing rules in evaluation order.") {
                    ElementKind = SchemaKind.Text
                },
                new(
                    "/members",
                    SchemaKind.Array,
                    Required: true,
                    Description: "Every pool member as the gateway was configured with it, a machine's "
                    + "resolved address beside its resource id."
                ) { ElementKind = SchemaKind.Text },
                new(
                    "/unresolved",
                    SchemaKind.Array,
                    Required: true,
                    Description: "Machine members that did not resolve to an address and are not in "
                    + "the configuration."
                ) { ElementKind = SchemaKind.Text },
                new("/waf", SchemaKind.Text, Required: true, Description: "The firewall's mode, rule set and paranoia level."),
                new(
                    "/readyReplicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many gateway pods are ready. ⚠ 0 is a gateway configured and "
                    + "carrying no traffic."
                ),
                new("/note", SchemaKind.Text, Required: true, Description: "What the answer is and is not."),
                new(
                    "/sampledAt",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When the platform read the cluster, RFC 3339."
                ) { Format = SchemaFormat.DateTime }
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>One routing rule, as parsed.</summary>
    /// <param name="Host">The host as written: <c>*</c>, a name, or <c>*.</c> and a suffix.</param>
    /// <param name="Path">The path prefix, <c>/</c> for everything.</param>
    /// <param name="Pool">The pool the rule sends to.</param>
    public sealed record Rule(string Host, string Path, string Pool) {
        /// <inheritdoc />
        public override string ToString() => Host + Path + "=" + Pool;
    }

    /// <summary>One pool member, as parsed.</summary>
    /// <param name="Pool">The pool it belongs to.</param>
    /// <param name="Target">An address — a bare one, IPv6 without brackets — or a resource id path.</param>
    /// <param name="Port">The port the member serves on.</param>
    public sealed record Member(string Pool, string Target, int Port) {
        /// <summary>Whether the target is a resource id rather than an address.</summary>
        public bool IsResource => Target.StartsWith('/');

        /// <inheritdoc />
        public override string ToString() =>
            Pool + "=" + (Target.Contains(':', StringComparison.Ordinal) ? "[" + Target + "]" : Target) + ":"
            + Port.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>The region a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Location(JsonElement desired) => VirtualNetworks.Location(desired);

    /// <summary>The subnet the gateway sits on.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Subnet(JsonElement desired) => Text(Properties(desired), "subnet", DefaultSubnet);

    /// <summary>The IPv4 address the gateway answers on.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string FrontendV4(JsonElement desired) => Text(Section(desired, "frontend"), "v4", DefaultFrontendV4);

    /// <summary>The IPv6 address the gateway also answers on, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string FrontendV6(JsonElement desired) => Text(Section(desired, "frontend"), "v6", "");

    /// <summary>The HTTP port.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int HttpPort(JsonElement desired) => Whole(Section(desired, "listeners"), "httpPort", 80);

    /// <summary>The HTTPS port.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int HttpsPort(JsonElement desired) => Whole(Section(desired, "listeners"), "httpsPort", 443);

    /// <summary>The certificate's vault handle as the body spells it, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string CertificateHandle(JsonElement desired) => Text(Section(desired, "listeners"), "certificate", "");

    /// <summary>Whether the body asks for an HTTPS listener.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool HasHttps(JsonElement desired) => CertificateHandle(desired).Length > 0;

    /// <summary>The routing rules, parsed, in body order.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>An element that does not parse is skipped here and refused by <see cref="BodyProblem" />.</remarks>
    public static ImmutableArray<Rule> Rules(JsonElement desired) => [
        .. Strings(Properties(desired), "routingRules").Select(ParseRule).OfType<Rule>()
    ];

    /// <summary>The pool members, parsed, in body order.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<Member> Members(JsonElement desired) => [
        .. Strings(Properties(desired), "backendPools").Select(ParseMember).OfType<Member>()
    ];

    /// <summary>The pool names, in the order they are first mentioned by a member.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> Pools(JsonElement desired) => [
        .. Members(desired).Select(static x => x.Pool).Distinct(StringComparer.Ordinal)
    ];

    /// <summary>The path every member is probed on.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string HealthPath(JsonElement desired) => Text(Section(desired, "health"), "path", "/");

    /// <summary>How often each member is probed, in seconds.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int HealthIntervalSeconds(JsonElement desired) => Whole(Section(desired, "health"), "intervalSeconds", 5);

    /// <summary>How many failed probes take a member out.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int UnhealthyAfter(JsonElement desired) => Whole(Section(desired, "health"), "unhealthyAfter", 3);

    /// <summary>How many successful probes put a member back.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int HealthyAfter(JsonElement desired) => Whole(Section(desired, "health"), "healthyAfter", 2);

    /// <summary>The WAF mode.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string WafMode(JsonElement desired) =>
        Text(Section(desired, "waf"), "mode", WafPrevention) is var mode && WafModes.Contains(mode) ? mode : WafPrevention;

    /// <summary>The CRS version.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string CrsVersion(JsonElement desired) =>
        Text(Section(desired, "waf"), "crsVersion", DefaultCrsVersion) is var version && CrsVersions.Contains(version)
            ? version
            : DefaultCrsVersion;

    /// <summary>The paranoia level.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int ParanoiaLevel(JsonElement desired) =>
        Math.Clamp(Whole(Section(desired, "waf"), "paranoiaLevel", 1), 1, 4);

    /// <summary>The exclusions, as written.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> Exclusions(JsonElement desired) => Strings(Section(desired, "waf"), "exclusions");

    /// <summary>The custom rules, as written.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> CustomRules(JsonElement desired) => Strings(Section(desired, "waf"), "customRules");

    /// <summary>How many client connections the gateway accepts at once.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MaxConnections(JsonElement desired) => Whole(Section(desired, "limits"), "maxConnections", 2000);

    /// <summary>The sizing preset.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string SizingPreset(JsonElement desired) =>
        Text(Section(desired, "sizing"), "preset", DefaultPreset) is var preset && Presets.ContainsKey(preset)
            ? preset
            : DefaultPreset;

    // ── Parsing one element ───────────────────────────────────────────────────────────────────

    /// <summary>Parses <c>host/path=pool</c>, or returns <see langword="null" />.</summary>
    /// <param name="text">One element of <c>/properties/routingRules</c>.</param>
    public static Rule? ParseRule(string text) {
        var equals = text.LastIndexOf('=');
        var slash = text.IndexOf('/', StringComparison.Ordinal);

        if (equals <= 0 || slash <= 0 || slash > equals || equals == text.Length - 1) {
            return null;
        }

        return new(text[..slash], text[slash..equals], text[(equals + 1)..]);
    }

    /// <summary>Parses <c>pool=target:port</c>, or returns <see langword="null" />.</summary>
    /// <param name="text">One element of <c>/properties/backendPools</c>.</param>
    /// <remarks>
    ///     ⚠ The port is after the <b>last</b> colon and the pool before the <b>first</b> equals sign,
    ///     so neither a bracketed IPv6 address nor a resource id path — which carries neither character
    ///     — can shift the split.
    /// </remarks>
    public static Member? ParseMember(string text) {
        var equals = text.IndexOf('=', StringComparison.Ordinal);
        var colon = text.LastIndexOf(':');

        if (equals <= 0 || colon <= equals + 1 || colon == text.Length - 1) {
            return null;
        }

        if (!int.TryParse(text.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var port)) {
            return null;
        }

        var target = text[(equals + 1)..colon];

        if (target.StartsWith('[') && target.EndsWith(']')) {
            target = target[1..^1];
        }

        return new(text[..equals], target, port);
    }

    // ── What is wrong with a body ─────────────────────────────────────────────────────────────

    /// <summary>
    ///     What is wrong with a body that its schema could not say, or <see langword="null" />.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A pure function of its argument</b>, for <see cref="LoadBalancers.BackendProblem" />'
    ///         reason: it runs in a reconciler that serves every tenant in the process.
    ///     </para>
    ///     <para>
    ///         Each element's grammar first — <c>Grammar</c> says why that is checked here and not by
    ///         the schema — and then what no pattern can say, each of which would otherwise be a proxy
    ///         that refuses to start or a gateway that answers 503 to every request: a rule naming a pool no
    ///         member is in; a member whose address does not parse, is not spelled the way the address
    ///         is written, is the gateway's own, is loopback, link-local, unspecified or multicast
    ///         (<see cref="MemberAddressProblem" />), or is in the wrong family for its brackets; a resource id that is not a virtual machine; a port of 0;
    ///         two listeners on one port, or a listener on a port the pod reserves; an exclusion range
    ///         that runs backwards; and too many entries. ⚠ <b>A machine that is not in this network is
    ///         not refused here</b> — its network is in another resource's body and needs the view, so
    ///         the reconciler refuses it after reading.
    ///     </para>
    /// </remarks>
    public static string? BodyProblem(JsonElement desired) {
        if (IpAddresses.ProblemWith(FrontendV4(desired), false, "/properties/frontend/v4") is { } v4) {
            return v4;
        }

        if (IpAddresses.ProblemWith(FrontendV6(desired), true, "/properties/frontend/v6") is { } v6) {
            return v6;
        }

        var http = HttpPort(desired);
        var https = HttpsPort(desired);

        foreach (var (port, pointer) in (ReadOnlySpan<(int, string)>)[
                     (http, "/properties/listeners/httpPort"),
                     (HasHttps(desired) ? https : 0, "/properties/listeners/httpsPort")
                 ]) {
            if (port is WafPort or ReadinessPort) {
                return $"'{pointer}' is {port.ToString(CultureInfo.InvariantCulture)}, which the gateway's "
                    + $"pod reserves — {WafPort.ToString(CultureInfo.InvariantCulture)} for the firewall "
                    + $"agent and {ReadinessPort.ToString(CultureInfo.InvariantCulture)} for its readiness "
                    + "probe. Choose another port.";
            }
        }

        if (HasHttps(desired) && http == https) {
            return "'/properties/listeners/httpsPort' is the same port as '/properties/listeners/httpPort'. "
                + "One port cannot serve both plain HTTP and HTTPS.";
        }

        var ruleTexts = Strings(Properties(desired), "routingRules");
        var memberTexts = Strings(Properties(desired), "backendPools");

        foreach (var (elements, grammar) in (ReadOnlySpan<(ImmutableArray<string>, Grammar)>)[
                     (ruleTexts, RuleGrammar),
                     (memberTexts, MemberGrammar),
                     (Exclusions(desired), ExclusionGrammar),
                     (CustomRules(desired), CustomRuleGrammar)
                 ]) {
            if (grammar.ProblemWith(elements) is { } malformed) {
                return malformed;
            }
        }

        if (ruleTexts.IsEmpty) {
            return "'/properties/routingRules' is empty, so every request would get 404. Give at least "
                + "one rule — */=pool sends everything to one pool.";
        }

        foreach (var (count, pointer) in (ReadOnlySpan<(int, string)>)[
                     (ruleTexts.Length, "/properties/routingRules"),
                     (memberTexts.Length, "/properties/backendPools"),
                     (Exclusions(desired).Length, "/properties/waf/exclusions"),
                     (CustomRules(desired).Length, "/properties/waf/customRules")
                 ]) {
            if (count > MaxEntries) {
                return $"'{pointer}' has {count.ToString(CultureInfo.InvariantCulture)} entries and the "
                    + $"limit is {MaxEntries.ToString(CultureInfo.InvariantCulture)}.";
            }
        }

        var members = new List<Member>();

        foreach (var text in memberTexts) {
            if (ParseMember(text) is not { } member) {
                return $"'/properties/backendPools' contains '{text}', which is not pool=target:port.";
            }

            if (MemberProblem(member, desired) is { } problem) {
                return problem;
            }

            members.Add(member);
        }

        var duplicate = members.GroupBy(static x => x.ToString(), StringComparer.Ordinal)
            .FirstOrDefault(static x => x.Count() > 1);

        if (duplicate is not null) {
            return $"'/properties/backendPools' names '{duplicate.Key}' twice. A member listed twice gets "
                + "twice the traffic, which is a weight this type does not offer.";
        }

        var pools = members.Select(static x => x.Pool).ToHashSet(StringComparer.Ordinal);

        foreach (var text in ruleTexts) {
            if (ParseRule(text) is not { } rule) {
                return $"'/properties/routingRules' contains '{text}', which is not host/path=pool.";
            }

            if (rule.Path.Contains("//", StringComparison.Ordinal)
                || (rule.Path.Length > 1 && rule.Path.EndsWith('/'))) {
                return $"'/properties/routingRules' contains '{text}', whose path '{rule.Path}' has an "
                    + "empty segment. A prefix is matched on whole segments, so write '/api' rather than "
                    + "'/api/'.";
            }

            if (!pools.Contains(rule.Pool)) {
                return $"'/properties/routingRules' contains '{text}', which sends to the pool "
                    + $"'{rule.Pool}', and no entry of '/properties/backendPools' is in a pool of that "
                    + "name. Every request that rule matched would get 503.";
            }
        }

        foreach (var text in Exclusions(desired)) {
            var dash = text.IndexOf('-', StringComparison.Ordinal);

            if (dash > 0
                && long.Parse(text.AsSpan(0, dash), CultureInfo.InvariantCulture)
                > long.Parse(text.AsSpan(dash + 1), CultureInfo.InvariantCulture)) {
                return $"'/properties/waf/exclusions' contains '{text}', a range that runs backwards.";
            }
        }

        foreach (var text in CustomRules(desired)) {
            if (CustomRuleProblem(text) is { } problem) {
                return problem;
            }
        }

        return null;
    }

    /// <summary>One list's element grammar: its pattern, its longest element, and how to name it.</summary>
    /// <param name="Pointer">The list's pointer.</param>
    /// <param name="Pattern">The whole-element pattern.</param>
    /// <param name="MaxLength">The longest element accepted.</param>
    /// <param name="Shape">The grammar as a tenant would write it, for the message.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             CHECKED HERE, AFTER THE 202, AND NOT BY THE SCHEMA — AND THE SCHEMA WOULD CARRY IT.
    ///         </b> <c>SchemaProperty</c> applies a <c>Pattern</c> per element on an array of text and
    ///         <c>ResourceSchema.Validate</c> enforces it; what refuses it is the fifth generated
    ///         surface, which cannot say a per-element pattern in a chart's <c>values.schema.json</c>
    ///         and so refuses the registration — <c>charts/managed/kafka/conformance.yaml § owed</c>,
    ///         <c>cidr-shape-is-unenforced</c>, and charts/README.md § What a chart cannot say. So a
    ///         malformed element is a <c>202</c> and then a terminal failure naming it, rather than a
    ///         <c>400</c> — <c>charts/managed/application-gateway/conformance.yaml § owed</c>,
    ///         <c>element-grammar-is-checked-after-202</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it is checked before anything is rendered, which is what the grammars are
    ///         for</b>: every value that reaches HAProxy's configuration or Coraza's SecLang has passed
    ///         one of these four, so none can close a quote or start a line.
    ///     </para>
    /// </remarks>
    sealed record Grammar(string Pointer, string Pattern, int MaxLength, string Shape) {
        readonly System.Text.RegularExpressions.Regex whole = new(
            "^(?:" + Pattern + ")$",
            System.Text.RegularExpressions.RegexOptions.NonBacktracking | System.Text.RegularExpressions.RegexOptions.CultureInvariant
        );

        /// <summary>What the first malformed element fails, or <see langword="null" />.</summary>
        public string? ProblemWith(ImmutableArray<string> elements) {
            foreach (var element in elements) {
                if (element.Length > MaxLength || !whole.IsMatch(element)) {
                    return $"'{Pointer}' contains '{(element.Length > 80 ? element[..80] + "…" : element)}', which "
                        + $"is not {Shape}"
                        + (element.Length > MaxLength ? $" of at most {MaxLength.ToString(CultureInfo.InvariantCulture)} characters." : ".");
                }
            }

            return null;
        }
    }

    static readonly Grammar RuleGrammar = new("/properties/routingRules", RulePattern, 512, "host/path=pool");
    static readonly Grammar MemberGrammar = new("/properties/backendPools", MemberPattern, 512, "pool=target:port");

    static readonly Grammar ExclusionGrammar = new(
        "/properties/waf/exclusions",
        ExclusionPattern,
        128,
        "a rule id, a range of them, or a rule id and ARGS:, REQUEST_HEADERS: or REQUEST_COOKIES: and a name"
    );

    static readonly Grammar CustomRuleGrammar = new(
        "/properties/waf/customRules",
        CustomRulePattern,
        280,
        "deny or allow, then ip, path, host, useragent or method and a value with no spaces or quotes"
    );

    static string? MemberProblem(Member member, JsonElement desired) {
        if (member.Port is < 1 or > 65535) {
            return $"'/properties/backendPools' contains '{member}', whose port is outside 1 to 65535.";
        }

        if (member.IsResource) {
            if (!ResourceId.TryParsePath(member.Target, out var id)) {
                return $"'/properties/backendPools' contains the target '{member.Target}', which is not a "
                    + "resource id path.";
            }

            return MachineTypeProblem(id);
        }

        if (!IPAddress.TryParse(member.Target, out var address)) {
            return $"'/properties/backendPools' contains the target '{member.Target}', which is neither "
                + "an IP address nor a resource id. Write an IPv6 address in brackets.";
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6 && member.Target.Any(char.IsAsciiLetterUpper)) {
            return $"'/properties/backendPools' contains '{member.Target}', which contains an upper-case "
                + "letter. Write IPv6 addresses in lower case.";
        }

        // ⚠ THE SPELLING HAS TO BE THE ADDRESS'S OWN, BECAUSE THE SPELLING IS WHAT IS RENDERED.
        // IPAddress.TryParse accepts `10.1` (10.0.0.1), `0x0a.0.0.1`, `010.0.0.1` (octal, 8.0.0.1) and
        // `fd00::1%3` (a zone), and the target used to be written into haproxy.cfg verbatim — so the
        // checks below ran on one reading of the text and HAProxy's resolver made another. #31's review.
        if (address.ToString() != member.Target) {
            return $"'/properties/backendPools' contains '{member.Target}', which is not how that address is "
                + $"written. Write '{address}'.";
        }

        if (MemberAddressProblem(address) is { } unroutable) {
            return $"'/properties/backendPools' contains '{member}', {unroutable}";
        }

        // ⚠ Compared as addresses, and a v4-mapped v6 member as the v4 address it is.
        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        return (IPAddress.TryParse(FrontendV4(desired), out var v4) && plain.Equals(v4))
            || (IPAddress.TryParse(FrontendV6(desired), out var v6) && plain.Equals(v6))
                ? $"'/properties/backendPools' contains '{member}', which is this gateway's own frontend "
                + "address. The gateway would send every request to itself."
                : null;
    }

    /// <summary>
    ///     What makes an address unusable as a pool member's, as the end of a sentence, or
    ///     <see langword="null" />.
    /// </summary>
    /// <param name="address">A parsed address — a member's, or one KubeVirt reported for a machine.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Loopback is the gateway's own pod</b>: the firewall agent listens on
    ///         <c>127.0.0.1:</c><see cref="WafPort" /> and the readiness monitor on
    ///         <see cref="ReadinessPort" />, so a member on loopback is a tenant speaking to the agent
    ///         that is supposed to be judging them. Link-local includes <c>169.254.169.254</c>, an
    ///         instance-metadata address on most clouds a node runs on; unspecified and multicast are
    ///         not a server.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Also applied to a machine's reported address</b>, which is not the platform's word
    ///         but the guest agent's — a tenant's own machine can report <c>127.0.0.1</c>.
    ///     </para>
    /// </remarks>
    public static string? MemberAddressProblem(IPAddress address) {
        ArgumentNullException.ThrowIfNull(address);

        var plain = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;

        if (plain.AddressFamily == AddressFamily.InterNetworkV6 && plain.ScopeId != 0) {
            return "which carries a zone. A pool member is an address on the gateway's network, not an interface of its pod.";
        }

        if (IPAddress.IsLoopback(plain)) {
            return "a loopback address — inside the gateway's pod that is the gateway itself, whose firewall "
                + $"agent listens on 127.0.0.1:{Int(WafPort)}.";
        }

        if (plain.Equals(IPAddress.Any) || plain.Equals(IPAddress.IPv6Any)) {
            return "the unspecified address, which is not a server.";
        }

        if (plain.AddressFamily == AddressFamily.InterNetworkV6) {
            return plain.IsIPv6LinkLocal ? "a link-local address, which is not routed to the gateway's network."
                : plain.IsIPv6Multicast ? "a multicast address, which is not a server."
                : null;
        }

        var bytes = plain.GetAddressBytes();

        return bytes[0] == 0 ? "an address in 0.0.0.0/8, which is not a server."
            : bytes[0] == 169 && bytes[1] == 254 ? "a link-local address (169.254.0.0/16), which includes the "
            + "instance-metadata address of the cloud a node may run on."
            : bytes[0] >= 224 ? "a multicast or reserved address, which is not a server."
            : null;
    }

    /// <summary>The one resource type a pool member may name.</summary>
    /// <remarks>
    ///     ⚠ Spelled as a string, because rule 2 of docs/plan/03 § Assembly graph rules keeps
    ///     <c>CyberCloud.Providers.Compute.Contracts</c> out of this assembly — the type name is the
    ///     other provider's public contract and its assembly is not.
    /// </remarks>
    public static ResourceTypeName MachineType { get; } = new("CyberCloud.Compute", "virtualMachines");

    /// <summary>What is wrong with a resource id as a pool member, or <see langword="null" />.</summary>
    /// <param name="id">The parsed member.</param>
    public static string? MachineTypeProblem(ResourceId id) =>
        id.Type == MachineType
            ? null
            : $"'/properties/backendPools' names '{id.Path}', a {id.Type}. A pool member may be an address "
            + $"or a {MachineType} in this network. docs/plan/14 also names container groups, and "
            + "CyberCloud.ContainerInstance/containerGroups is not a published type yet.";

    static string? CustomRuleProblem(string text) {
        var parts = text.Split(' ', 3);

        if (parts.Length != 3) {
            return $"'/properties/waf/customRules' contains '{text}', which is not 'deny|allow field value'.";
        }

        if (parts[1] == "ip") {
            var slash = parts[2].IndexOf('/', StringComparison.Ordinal);
            var address = slash < 0 ? parts[2] : parts[2][..slash];

            if (!IPAddress.TryParse(address, out var parsed)
                || (slash >= 0
                    && (!int.TryParse(parts[2].AsSpan(slash + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var bits)
                        || bits > (parsed.AddressFamily == AddressFamily.InterNetworkV6 ? 128 : 32)))) {
                return $"'/properties/waf/customRules' contains '{text}', whose address is not an IP "
                    + "address or range.";
            }
        }

        return null;
    }

    // ── The configuration a body becomes ──────────────────────────────────────────────────────

    /// <summary>The addresses resource-id members resolved to, keyed by resource id path.</summary>
    /// <remarks>
    ///     ⚠ Sorted and ordinal, because it is written into the <c>ConfigMap</c> and hashed: a record
    ///     whose order varied between passes would roll the gateway on every pass.
    /// </remarks>
    public static ImmutableSortedDictionary<string, string> NoResolution { get; } =
        ImmutableSortedDictionary<string, string>.Empty.WithComparers(StringComparer.Ordinal);

    /// <summary>The <c>haproxy.cfg</c> a desired body and its resolved members render.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="resolved">What each resource-id member resolved to; absent means not resolved.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The Host header is lower-cased and stripped of its port once, into a variable</b>,
    ///         and every rule compares against that — <c>hdr(host)</c> carries <c>:8080</c> when a
    ///         client sends one, and a rule for <c>shop.example.com</c> would miss it.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A path prefix matches whole segments</b>: <c>/api</c> matches <c>/api</c> and
    ///         <c>/api/…</c> and not <c>/apiary</c>, which is two conditions — <c>path</c> and
    ///         <c>path_beg</c> with a trailing slash — because <c>path_beg /api</c> alone is the
    ///         surprise every tenant would hit once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>In prevention mode an unanswered firewall is a 503</b>. SPOE sets
    ///         <c>txn.coraza.error</c> when the agent times out or is down, and passing the request
    ///         would make "the firewall was slow" a bypass. In detection mode the same error passes,
    ///         because detection is a promise to block nothing.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>option http-buffer-request</c> is what puts a body in front of the firewall.</b>
    ///         Without it <c>req.body</c> is whatever arrived in the first packet, and a POST's payload
    ///         would be inspected only when it happened to be small. What it buffers is bounded by
    ///         HAProxy's <c>tune.bufsize</c> — <c>conformance.yaml § owed</c>,
    ///         <c>large-bodies-are-inspected-up-to-the-buffer</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Deterministic, byte for byte</b>, for <see cref="LoadBalancers.HaproxyConfig" />'
    ///         reason: the pod template carries its hash.
    ///     </para>
    /// </remarks>
    public static string HaproxyConfig(JsonElement desired, IReadOnlyDictionary<string, string> resolved) {
        var waf = WafMode(desired);
        var maxConnections = MaxConnections(desired);
        var builder = new StringBuilder();

        Line(builder, "# Generated by CyberCloud from CyberCloud.Network/virtualNetworks/applicationGateways.");
        Line(builder, "# Edits are overwritten on the next reconcile pass.");
        Line(builder, "global");
        Line(builder, "  log stdout format raw local0 info");
        Line(builder, $"  maxconn {Int(maxConnections * 2)}");
        Line(builder, "");
        Line(builder, "defaults");
        Line(builder, "  mode http");
        Line(builder, "  log global");
        Line(builder, "  option httplog");
        Line(builder, "  option dontlognull");
        Line(builder, "  timeout connect 5s");
        Line(builder, "  timeout client 60s");
        Line(builder, "  timeout server 60s");
        Line(builder, "  timeout http-request 10s");
        Line(builder, "");
        Line(builder, "frontend health");
        Line(builder, $"  bind :{Int(ReadinessPort)}");
        Line(builder, $"  monitor-uri {ReadinessPath}");

        if (waf != WafOff) {
            Line(builder, "  monitor fail if { nbsrv(waf) lt 1 }");
        }

        Line(builder, "");
        Line(builder, "frontend inbound");
        Line(builder, $"  bind :{Int(HttpPort(desired))}");

        if (HasHttps(desired)) {
            Line(builder, $"  bind :{Int(HttpsPort(desired))} ssl crt {TlsDirectory}/{TlsFile}");
        }

        Line(builder, $"  maxconn {Int(maxConnections)}");
        Line(builder, "  option forwardfor");

        Line(
            builder,
            waf == WafOff
                ? "  log-format \"%ci:%cp [%tr] %ft %b/%s %TR/%Tw/%Tc/%Tr/%Ta %ST %B %tsc %{+Q}r host:%[var(txn.host)]\""
                : "  log-format \"%ci:%cp [%tr] %ft %b/%s %TR/%Tw/%Tc/%Tr/%Ta %ST %B %tsc %{+Q}r host:%[var(txn.host)] "
                + "waf-action:%[var(txn.coraza.action)] waf-score:%[var(txn.coraza.anomaly_score)] "
                + "waf-rules:%[var(txn.coraza.rule_ids)] waf-error:%[var(txn.coraza.error)]\""
        );

        Line(builder, "  http-request set-var(txn.host) req.hdr(host),field(1,:),lower");
        Line(builder, "  http-request set-header X-Forwarded-Proto https if { ssl_fc }");
        Line(builder, "  http-request set-header X-Forwarded-Proto http if !{ ssl_fc }");

        if (waf != WafOff) {
            Line(builder, "  option http-buffer-request");
            Line(builder, $"  filter spoe engine coraza config /usr/local/etc/haproxy/{SpoeConfigFile}");
            Line(builder, "  http-request send-spoe-group coraza coraza-req");

            if (waf == WafPrevention) {
                Line(builder, "  http-request deny deny_status 403 hdr waf-block request if { var(txn.coraza.action) -m str deny }");
                Line(builder, "  http-request silent-drop if { var(txn.coraza.action) -m str drop }");
                Line(builder, "  http-request deny deny_status 503 if { var(txn.coraza.error) -m int gt 0 }");
            }
        }

        foreach (var rule in Rules(desired)) {
            Line(builder, "  use_backend " + BackendNameOf(rule.Pool) + RuleCondition(rule));
        }

        Line(builder, "  default_backend no-route");
        Line(builder, "");
        Line(builder, "backend no-route");
        Line(builder, "  http-request return status 404 content-type text/plain string \"no routing rule matched this request\"");

        var members = Members(desired);
        var interval = HealthIntervalSeconds(desired) * 1000;

        foreach (var pool in Pools(desired)) {
            Line(builder, "");
            Line(builder, "backend " + BackendNameOf(pool));
            Line(builder, "  balance roundrobin");
            Line(builder, "  option httpchk");
            Line(builder, $"  http-check send meth GET uri {HealthPath(desired)}");
            Line(builder, "  http-check expect status 200-399");

            var index = 0;

            foreach (var member in members) {
                index++;

                if (member.Pool != pool) {
                    continue;
                }

                var address = member.IsResource
                    ? resolved.TryGetValue(member.Target, out var found) ? found : null
                    : member.Target;

                if (address is null) {
                    // ⚠ A COMMENT AND NOT A SERVER: a machine that did not resolve is reported by
                    // showRouting and is simply not in the pool, rather than a server line HAProxy
                    // would refuse to start on.
                    Line(builder, $"  # m{Int(index)} {member.Target} did not resolve to an address");

                    continue;
                }

                var target = address.Contains(':', StringComparison.Ordinal) ? "[" + address + "]" : address;

                Line(
                    builder,
                    $"  server m{Int(index)} {target}:{Int(member.Port)} check inter {Int(interval)}ms "
                    + $"rise {Int(HealthyAfter(desired))} fall {Int(UnhealthyAfter(desired))} "
                    + $"maxconn {Int(maxConnections)}"
                );
            }
        }

        if (waf != WafOff) {
            Line(builder, "");
            Line(builder, "backend waf");
            Line(builder, "  mode tcp");
            Line(builder, "  option spop-check");
            Line(builder, $"  server agent 127.0.0.1:{Int(WafPort)} check inter 2s");
        }

        return builder.ToString();
    }

    /// <summary>The HAProxy backend name a pool renders as.</summary>
    /// <param name="pool">The pool's name.</param>
    /// <remarks>
    ///     ⚠ Prefixed, so a pool called <c>waf</c> or <c>no-route</c> cannot collide with the two
    ///     backends the gateway itself declares.
    /// </remarks>
    public static string BackendNameOf(string pool) => "pool-" + pool;

    static string RuleCondition(Rule rule) {
        var host = rule.Host switch {
            "*" => "",
            _ when rule.Host.StartsWith("*.", StringComparison.Ordinal) => "{ var(txn.host) -m end " + rule.Host[1..] + " }",
            _ => "{ var(txn.host) -m str " + rule.Host + " }"
        };

        if (rule.Path == "/") {
            return host.Length == 0 ? "" : " if " + host;
        }

        var prefix = host.Length == 0 ? "" : host + " ";

        return $" if {prefix}{{ path {rule.Path} }} || {prefix}{{ path_beg {rule.Path}/ }}";
    }

    /// <summary>The SPOE configuration HAProxy's filter reads.</summary>
    /// <remarks>
    ///     ⚠ The message's arguments are in the order coraza-spoa's own example declares them — its
    ///     comment reads <i>"Arguments are required to be in this order"</i>. <c>exportRuleIDs</c> is on
    ///     so the proxy's log line names the rules that matched, which is how detection mode is read.
    /// </remarks>
    public static string SpoeConfig() =>
        """
        # Generated by CyberCloud from CyberCloud.Network/virtualNetworks/applicationGateways.
        [coraza]
        spoe-agent coraza-agent
            groups      coraza-req
            option      var-prefix      coraza
            option      set-on-error    error
            timeout     hello           2s
            timeout     idle            2m
            timeout     processing      500ms
            use-backend waf

        spoe-message coraza-req
            args app=str(gateway) src-ip=src src-port=src_port dst-ip=dst dst-port=dst_port method=method path=path query=query version=req.ver headers=req.hdrs body=req.body exportRuleIDs=bool(true)

        spoe-group coraza-req
            messages coraza-req

        """.ReplaceLineEndings("\n");

    /// <summary>The coraza-spoa configuration a body renders — the WAF policy.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The order is the policy.</b> <c>@coraza.conf-recommended</c> sets
    ///         <c>SecRuleEngine DetectionOnly</c>, so the mode line has to come after it; the paranoia
    ///         level is a <c>SecAction</c> the CRS's own setup file expects after it and before the
    ///         rules; custom rules go before the CRS so an <c>allow</c> can skip it; and exclusions
    ///         go after the CRS, because <c>SecRuleRemoveById</c> and <c>SecRuleUpdateTargetById</c> act
    ///         on rules already loaded.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Custom rule ids are the ranges coraza-spoa's README allocates</b>: denies from
    ///         190001, which the agent counts as attack rules in <c>rules_hit</c>, and allows from
    ///         100001, which it deliberately does not.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The YAML is written by hand, and only values this method chose reach it</b> — the
    ///         directives block is a literal block scalar indented under its key, and nothing a tenant
    ///         wrote can end it, because every tenant value is out of a character class with no newline.
    ///         coraza-spoa decodes with <c>KnownFields(true)</c>, so a key it does not know stops the
    ///         agent.
    ///     </para>
    /// </remarks>
    public static string WafConfig(JsonElement desired) {
        var builder = new StringBuilder();

        Line(builder, "# Generated by CyberCloud from CyberCloud.Network/virtualNetworks/applicationGateways.");
        Line(builder, $"bind: 127.0.0.1:{Int(WafPort)}");
        Line(builder, "log_level: info");
        Line(builder, "log_file: /dev/stdout");
        Line(builder, "log_format: json");
        Line(builder, "default_application: gateway");
        Line(builder, "applications:");
        Line(builder, "  - name: gateway");
        Line(builder, "    response_check: false");
        Line(builder, "    transaction_ttl_ms: 60000");
        Line(builder, "    log_level: info");
        Line(builder, "    log_file: /dev/stdout");
        Line(builder, "    log_format: json");
        Line(builder, "    directives: |");

        foreach (var directive in Directives(desired)) {
            Line(builder, "      " + directive);
        }

        return builder.ToString();
    }

    /// <summary>The SecLang directives of the WAF policy, one per line, in load order.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static ImmutableArray<string> Directives(JsonElement desired) {
        var lines = ImmutableArray.CreateBuilder<string>();
        var paranoia = Int(ParanoiaLevel(desired));

        lines.Add("Include @coraza.conf-recommended");
        lines.Add(WafMode(desired) == WafPrevention ? "SecRuleEngine On" : "SecRuleEngine DetectionOnly");
        lines.Add("Include @crs-setup.conf.example");
        lines.Add(
            $"SecAction \"id:900000,phase:1,pass,t:none,nolog,setvar:tx.blocking_paranoia_level={paranoia}\""
        );

        var index = 0;

        foreach (var text in CustomRules(desired)) {
            index++;

            if (CustomRuleDirective(text, index) is { } directive) {
                lines.Add(directive);
            }
        }

        lines.Add("Include @owasp_crs/*.conf");

        foreach (var text in Exclusions(desired)) {
            var colon = text.IndexOf(':', StringComparison.Ordinal);

            lines.Add(
                colon < 0
                    ? "SecRuleRemoveById " + text
                    : $"SecRuleUpdateTargetById {text[..colon]} \"!{text[(colon + 1)..]}\""
            );
        }

        return lines.ToImmutable();
    }

    static string? CustomRuleDirective(string text, int index) {
        var parts = text.Split(' ', 3);

        if (parts.Length != 3) {
            return null;
        }

        var deny = parts[0] == "deny";
        var id = (deny ? 190000 : 100000) + index;
        var value = parts[2];

        // ⚠ EVERY VARIABLE IS NORMALIZED THE WAY ROUTING OR THE BACKEND WILL READ IT, AND NOT COMPARED
        // RAW. #31's review: the host rule compared SERVER_NAME — the Host header as sent — with
        // `@streq` and `t:none`, while routing lower-cases the header and strips its port
        // (HaproxyConfig's `txn.host`), so `Host: SHOP.example.com` or `shop.example.com:80` evaded a
        // deny and still reached the pool the rule was written to protect. The path rule compared
        // REQUEST_FILENAME, which coraza-spoa derives by Go's url.Parse of the path: decoded once, and
        // `//secret/x` parses as the AUTHORITY `secret` and the path `/x`, so a doubled slash hid the
        // prefix entirely — while HAProxy passed `//secret/x` on to a backend that merges slashes.
        var (variable, operation, transform) = parts[1] switch {
            "ip" => ("REMOTE_ADDR", "@ipMatch " + value, "t:none,"),
            // The request line's target as HAProxy sent it (path and query), percent-decoded and with
            // `//`, `/./`, `/../` and `\` resolved — the path a backend that normalizes will serve.
            // ⚠ Decoding a second time over a value that arrives decoded is what catches `%252e`, and
            // on a deny it can only widen what is refused.
            "path" => ("REQUEST_URI_RAW", "@beginsWith " + value, "t:none,t:urlDecodeUni,t:normalizePathWin,"),
            // The Host header lower-cased, then matched with an optional trailing dot and port — the
            // name `req.hdr(host),field(1,:),lower` routes on. `[.]` rather than `\.`, so no backslash
            // reaches a SecLang string.
            "host" => ("REQUEST_HEADERS:Host", "@rx " + HostPattern(value), "t:none,t:lowercase,"),
            "useragent" => ("REQUEST_HEADERS:User-Agent", "@contains " + value.ToLowerInvariant(), "t:none,t:lowercase,"),
            "method" => ("REQUEST_METHOD", "@streq " + value, "t:none,"),
            _ => (null, null, null)
        };

        if (variable is null) {
            return null;
        }

        var action = deny ? "deny,status:403,log" : "allow,nolog";

        return $"SecRule {variable} \"{operation}\" \"id:{Int(id)},phase:1,{transform}{action},"
            + $"msg:'CyberCloud custom rule {Int(index)}: {parts[0]} {parts[1]} {value}'\"";
    }

    /// <summary>The regular expression a <c>host</c> custom rule matches the lower-cased Host header with.</summary>
    /// <param name="host">The name as the rule spells it — <c>[a-z0-9.-]</c> only, which the grammar guarantees.</param>
    /// <remarks>
    ///     Anchored, each dot a <c>[.]</c> class, then an optional trailing dot (an absolute name is the
    ///     same host to a backend) and an optional <c>:port</c>.
    /// </remarks>
    public static string HostPattern(string host) => "^" + host.Replace(".", "[.]", StringComparison.Ordinal) + "[.]?(:[0-9]*)?$";

    /// <summary>What the resolved members record says, as the <c>ConfigMap</c> carries it.</summary>
    /// <param name="resolved">What each resource-id member resolved to.</param>
    public static string ResolvedJson(IReadOnlyDictionary<string, string> resolved) {
        var record = new JsonObject();

        foreach (var (target, address) in resolved.OrderBy(static x => x.Key, StringComparer.Ordinal)) {
            record[target] = address;
        }

        return record.ToJsonString();
    }

    /// <summary>Reads a resolved-members record back, or an empty one when it does not parse.</summary>
    /// <param name="json">The record as the <c>ConfigMap</c> holds it.</param>
    public static ImmutableSortedDictionary<string, string> ParseResolved(string? json) {
        if (string.IsNullOrEmpty(json)) {
            return NoResolution;
        }

        try {
            return JsonNode.Parse(json) is JsonObject record
                ? record.Where(static x => x.Value is JsonValue)
                    .ToImmutableSortedDictionary(
                        static x => x.Key,
                        static x => x.Value!.GetValue<string>(),
                        StringComparer.Ordinal
                    )
                : NoResolution;
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException or FormatException) {
            return NoResolution;
        }
    }

    /// <summary>The hash of everything both processes read.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="resolved">What each resource-id member resolved to.</param>
    public static string ConfigHash(JsonElement desired, IReadOnlyDictionary<string, string> resolved) =>
        KubeLabels.ReconcileHash(
            HaproxyConfig(desired, resolved)
            + "\n---\n"
            + (WafMode(desired) == WafOff ? "" : SpoeConfig() + "\n---\n" + WafConfig(desired))
        );

    /// <summary>The <c>ConfigMap</c> document a desired body becomes.</summary>
    /// <param name="id">The gateway's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="resolved">What each resource-id member resolved to.</param>
    public static string ConfigMapJson(ResourceId id, JsonElement desired, IReadOnlyDictionary<string, string> resolved) {
        var data = new JsonObject {
            [ProxyConfigFile] = HaproxyConfig(desired, resolved),
            [ResolvedKey] = ResolvedJson(resolved)
        };

        if (WafMode(desired) != WafOff) {
            data[SpoeConfigFile] = SpoeConfig();
            data[WafConfigFile] = WafConfig(desired);
        }

        return new JsonObject {
            ["kind"] = ConfigMapKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) },
            ["data"] = data
        }.ToJsonString();
    }

    /// <summary>The certificate <c>Secret</c> a resolved handle becomes.</summary>
    /// <param name="id">The gateway's address.</param>
    /// <param name="pem">The resolved PEM bundle — the value, which goes here and nowhere else.</param>
    /// <remarks>
    ///     <c>data</c> with the base64 written out rather than <c>stringData</c>, for
    ///     <c>VirtualMachines.CloudInitSecretJson</c>'s reason: a read-back comes as <c>data</c>.
    /// </remarks>
    public static string TlsSecretJson(ResourceId id, string pem) {
        ArgumentNullException.ThrowIfNull(pem);

        return new JsonObject {
            ["kind"] = KubeSecret.Kind.Kind,
            ["metadata"] = new JsonObject { ["name"] = TlsSecretNameOf(id) },
            ["type"] = "Opaque",
            ["data"] = new JsonObject { [TlsFile] = Convert.ToBase64String(Encoding.UTF8.GetBytes(pem)) }
        }.ToJsonString();
    }

    /// <summary>What is wrong with a resolved PEM bundle, or <see langword="null" />.</summary>
    /// <param name="pem">The value the vault returned.</param>
    /// <remarks>
    ///     ⚠ HAProxy's <c>crt</c> wants the certificate and its key in one file, and refuses to start
    ///     on a file without the key — a gateway down for every listener, HTTP included, because of the
    ///     HTTPS one. Checking the two markers here turns that into a refusal naming the handle.
    /// </remarks>
    public static string? PemProblem(string pem) =>
        pem.Contains("-----BEGIN CERTIFICATE-----", StringComparison.Ordinal)
        && pem.Contains("PRIVATE KEY-----", StringComparison.Ordinal)
            ? null
            : "the value behind '/properties/listeners/certificate' is not a PEM bundle holding a "
            + "certificate and its private key. HAProxy reads both from one file and would not start.";

    /// <summary>The <c>Deployment</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <param name="resolved">What each resource-id member resolved to.</param>
    /// <param name="tlsHash">The hash of the resolved certificate, or empty without one.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One replica and <c>Recreate</c></b>, for <see cref="LoadBalancers.DeploymentJson" />'
    ///         reasons: a pinned address has one holder.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The two containers run as two different uids</b> — HAProxy's image user 99 and
    ///         distroless's 65532 — so the user is set per container and only
    ///         <c>runAsNonRoot</c>, seccomp and the sysctl on the pod.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The agent gets an <c>emptyDir</c> at <c>/tmp</c></b>, because Coraza spills a request
    ///         body larger than its in-memory limit to a temporary file and the root filesystem is
    ///         read-only.
    ///     </para>
    /// </remarks>
    public static string DeploymentJson(
        string ns,
        ResourceId id,
        JsonElement desired,
        IReadOnlyDictionary<string, string> resolved,
        string tlsHash
    ) {
        var name = ObjectNameOf(id);
        var (cpu, memory) = Resources(desired);
        var selector = new JsonObject { [LoadBalancers.NameLabel] = "application-gateway", [LoadBalancers.InstanceLabel] = name };
        var waf = WafMode(desired) != WafOff;

        var annotations = new JsonObject {
            [LoadBalancers.LogicalSwitchAnnotation] = LogicalSwitchOf(ns, id, desired),
            [LoadBalancers.IpPoolAnnotation] = PoolOf(desired),
            [ConfigChecksumAnnotation] = ConfigHash(desired, resolved)
        };

        if (tlsHash.Length > 0) {
            annotations[TlsChecksumAnnotation] = tlsHash;
        }

        var proxyMounts = new JsonArray {
            new JsonObject { ["name"] = "config", ["mountPath"] = LoadBalancers.ConfigDirectory, ["readOnly"] = true }
        };

        var volumes = new JsonArray {
            new JsonObject { ["name"] = "config", ["configMap"] = new JsonObject { ["name"] = name } }
        };

        if (HasHttps(desired)) {
            proxyMounts.Add(new JsonObject { ["name"] = "tls", ["mountPath"] = TlsDirectory, ["readOnly"] = true });
            volumes.Add(
                new JsonObject {
                    ["name"] = "tls",
                    ["secret"] = new JsonObject { ["secretName"] = TlsSecretNameOf(id), ["defaultMode"] = 288 }
                }
            );
        }

        var ports = new JsonArray {
            new JsonObject { ["name"] = "http", ["containerPort"] = HttpPort(desired), ["protocol"] = "TCP" }
        };

        if (HasHttps(desired)) {
            ports.Add(new JsonObject { ["name"] = "https", ["containerPort"] = HttpsPort(desired), ["protocol"] = "TCP" });
        }

        var containers = new JsonArray {
            new JsonObject {
                ["name"] = "haproxy",
                ["image"] = ProxyImage,
                ["securityContext"] = Hardened(LoadBalancers.ProxyUid),
                ["ports"] = ports,
                ["readinessProbe"] = new JsonObject {
                    ["httpGet"] = new JsonObject { ["path"] = ReadinessPath, ["port"] = ReadinessPort },
                    ["periodSeconds"] = 5
                },
                ["resources"] = Quantities(cpu, memory),
                ["volumeMounts"] = proxyMounts
            }
        };

        if (waf) {
            containers.Add(
                new JsonObject {
                    ["name"] = "waf",
                    ["image"] = WafImage,
                    // ⚠ `command` AND NOT `args`: the image declares a Cmd and no Entrypoint, so `args`
                    // REPLACES the binary and the container tries to execute "--config" — measured,
                    // "exec: \"--config\": executable file not found in $PATH".
                    ["command"] = new JsonArray { WafBinary, "--config", WafConfigDirectory + "/" + WafConfigFile },
                    ["securityContext"] = Hardened(WafUid),
                    ["resources"] = Quantities(cpu, memory),
                    ["volumeMounts"] = new JsonArray {
                        new JsonObject {
                            ["name"] = "config",
                            ["mountPath"] = WafConfigDirectory,
                            ["readOnly"] = true
                        },
                        new JsonObject { ["name"] = "tmp", ["mountPath"] = "/tmp" }
                    }
                }
            );

            volumes.Add(new JsonObject { ["name"] = "tmp", ["emptyDir"] = new JsonObject { ["sizeLimit"] = "256Mi" } });
        }

        return new JsonObject {
            ["kind"] = DeploymentKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["replicas"] = 1,
                ["strategy"] = new JsonObject { ["type"] = "Recreate" },
                ["selector"] = new JsonObject { ["matchLabels"] = selector.DeepClone() },
                ["template"] = new JsonObject {
                    ["metadata"] = new JsonObject { ["labels"] = selector.DeepClone(), ["annotations"] = annotations },
                    ["spec"] = new JsonObject {
                        ["automountServiceAccountToken"] = false,
                        ["terminationGracePeriodSeconds"] = 30,
                        ["securityContext"] = new JsonObject {
                            ["runAsNonRoot"] = true,
                            // ⚠ THE CERTIFICATE IS UNREADABLE WITHOUT THIS. A Secret volume is root-owned, and
                            // HAProxy runs as uid 99: with 0400 it could not open tls.pem and exited on every
                            // start — measured on the k3s run, a pod Running and never Ready. fsGroup 99 and
                            // 0440 give the proxy's group read and nobody else anything.
                            ["fsGroup"] = LoadBalancers.ProxyUid,
                            ["seccompProfile"] = new JsonObject { ["type"] = "RuntimeDefault" },
                            ["sysctls"] = new JsonArray {
                                new JsonObject { ["name"] = LoadBalancers.UnprivilegedPortSysctl, ["value"] = "0" }
                            }
                        },
                        ["containers"] = containers,
                        ["volumes"] = volumes
                    }
                }
            }
        }.ToJsonString();
    }

    static JsonObject Hardened(int uid) =>
        new() {
            ["runAsUser"] = uid,
            ["runAsGroup"] = uid,
            ["allowPrivilegeEscalation"] = false,
            ["readOnlyRootFilesystem"] = true,
            ["capabilities"] = new JsonObject { ["drop"] = new JsonArray { "ALL" } }
        };

    static JsonObject Quantities(string cpu, string memory) =>
        new() {
            ["requests"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory },
            ["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory }
        };

    /// <summary>The value of the pod template's address-pool annotation.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>Comma-joined into one dual-stack address — <see cref="LoadBalancers.PoolOf" />.</remarks>
    public static string PoolOf(JsonElement desired) =>
        FrontendV6(desired) is { Length: > 0 } v6 ? FrontendV4(desired) + "," + v6 : FrontendV4(desired);

    // ── What a read-back says ─────────────────────────────────────────────────────────────────

    /// <summary>Whether an object read back from a cluster carries what the desired body asks for.</summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The gateway's address.</param>
    /// <param name="desired">The desired body.</param>
    /// <param name="resolved">What each resource-id member resolved to.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The configuration exactly and the Deployment by containment</b>, for
    ///         <see cref="LoadBalancers.Matches" />' reason. The Deployment's five fields are the
    ///         switch, the address, the configuration hash, and both images — ⚠ <b>the agent's image
    ///         is compared by name as well as by presence</b>, because a gateway whose body turned the
    ///         WAF on and whose pod still has one container is a firewall that is not there.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The certificate hash is not compared here</b>; the reconciler compares it after
    ///         resolving the value, and a drift scan, which cannot resolve, trusts the pod template.
    ///     </para>
    /// </remarks>
    public static bool Matches(
        string objectJson,
        string ns,
        ResourceId id,
        JsonElement desired,
        IReadOnlyDictionary<string, string> resolved
    ) {
        if (Document(objectJson) is not { } document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "ConfigMap" => MatchesConfigMap(document, desired, resolved),
            "Deployment" => MatchesDeployment(document, ns, id, desired, resolved),
            _ => false
        };
    }

    /// <summary>What a <c>ConfigMap</c> read back says its members resolved to.</summary>
    /// <param name="objectJson">The <c>ConfigMap</c>'s JSON.</param>
    public static ImmutableSortedDictionary<string, string> ResolvedOf(string objectJson) =>
        ParseResolved(Document(objectJson)?["data"]?[ResolvedKey]?.GetValue<string>());

    static bool MatchesConfigMap(JsonObject document, JsonElement desired, IReadOnlyDictionary<string, string> resolved) {
        var data = document["data"];

        if (data?[ProxyConfigFile]?.GetValue<string>() != HaproxyConfig(desired, resolved)) {
            return false;
        }

        return WafMode(desired) == WafOff
            ? data[WafConfigFile] is null
            : data[SpoeConfigFile]?.GetValue<string>() == SpoeConfig()
            && data[WafConfigFile]?.GetValue<string>() == WafConfig(desired);
    }

    static bool MatchesDeployment(
        JsonObject document,
        string ns,
        ResourceId id,
        JsonElement desired,
        IReadOnlyDictionary<string, string> resolved
    ) {
        if (document["spec"]?["template"] is not JsonObject template) {
            return false;
        }

        var annotations = template["metadata"]?["annotations"];

        if (annotations?[LoadBalancers.LogicalSwitchAnnotation]?.GetValue<string>() != LogicalSwitchOf(ns, id, desired)
            || annotations[LoadBalancers.IpPoolAnnotation]?.GetValue<string>() != PoolOf(desired)
            || annotations[ConfigChecksumAnnotation]?.GetValue<string>() != ConfigHash(desired, resolved)
            || (annotations[TlsChecksumAnnotation] is not null) != HasHttps(desired)) {
            return false;
        }

        if (template["spec"]?["containers"] is not JsonArray containers) {
            return false;
        }

        var images = containers.Select(static x => x?["image"]?.GetValue<string>()).ToList();

        return WafMode(desired) == WafOff
            ? images.SequenceEqual([ProxyImage])
            : images.SequenceEqual([ProxyImage, WafImage]);
    }

    static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        } catch (JsonException) {
            return null;
        }
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the gateway runs in.</param>
    /// <param name="rules">The routing rules; the default sends everything to <c>web</c>.</param>
    /// <param name="members">The pool members; the default is one address in <c>web</c>.</param>
    /// <param name="wafMode">The WAF mode.</param>
    /// <param name="paranoiaLevel">The CRS paranoia level.</param>
    /// <param name="exclusions">The WAF exclusions.</param>
    /// <param name="customRules">The custom WAF rules.</param>
    /// <param name="certificate">The certificate's vault handle, or empty.</param>
    /// <param name="httpPort">The HTTP port.</param>
    /// <param name="httpsPort">The HTTPS port.</param>
    /// <param name="frontendV4">The address the gateway answers on.</param>
    /// <param name="subnet">The subnet it sits on.</param>
    /// <param name="preset">The sizing preset.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a leaf or an array, for <c>LoadBalancers.Body</c>'s reason.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        IEnumerable<string>? rules = null,
        IEnumerable<string>? members = null,
        string wafMode = WafPrevention,
        int paranoiaLevel = 1,
        IEnumerable<string>? exclusions = null,
        IEnumerable<string>? customRules = null,
        string certificate = "",
        int httpPort = 80,
        int httpsPort = 443,
        string frontendV4 = DefaultFrontendV4,
        string subnet = DefaultSubnet,
        string preset = DefaultPreset,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["subnet"] = subnet,
                ["frontend"] = new JsonObject { ["v4"] = frontendV4, ["v6"] = "" },
                ["listeners"] = new JsonObject {
                    ["httpPort"] = httpPort, ["httpsPort"] = httpsPort, ["certificate"] = certificate
                },
                ["routingRules"] = Array(rules ?? [DefaultRule]),
                ["backendPools"] = Array(members ?? [DefaultMember]),
                ["health"] = new JsonObject {
                    ["path"] = "/", ["intervalSeconds"] = 5, ["unhealthyAfter"] = 3, ["healthyAfter"] = 2
                },
                ["waf"] = new JsonObject {
                    ["mode"] = wafMode,
                    ["crsVersion"] = DefaultCrsVersion,
                    ["paranoiaLevel"] = paranoiaLevel,
                    ["exclusions"] = Array(exclusions ?? []),
                    ["customRules"] = Array(customRules ?? [])
                },
                ["limits"] = new JsonObject { ["maxConnections"] = 2000 },
                ["sizing"] = new JsonObject { ["preset"] = preset }
            }
        }.ToJsonString();

    static JsonArray Array(IEnumerable<string> values) => [.. values.Select(static x => (JsonNode)JsonValue.Create(x))];

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    static void Line(StringBuilder builder, string line) => builder.Append(line).Append('\n');

    static JsonElement? Properties(JsonElement desired) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
            ? properties
            : null;

    static JsonElement? Section(JsonElement desired, string name) =>
        Properties(desired) is { } properties
        && properties.TryGetProperty(name, out var section)
        && section.ValueKind is JsonValueKind.Object
            ? section
            : null;

    static string Text(JsonElement? section, string name, string fallback) =>
        section is { } found
        && found.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    static int Whole(JsonElement? section, string name, int fallback) =>
        section is { } found
        && found.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    static ImmutableArray<string> Strings(JsonElement? section, string name) =>
        section is { } found
        && found.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Array
            ? [
                .. value.EnumerateArray()
                    .Where(static x => x.ValueKind is JsonValueKind.String)
                    .Select(static x => x.GetString() ?? "")
            ]
            : [];
}
