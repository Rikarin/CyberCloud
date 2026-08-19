using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Network/virtualNetworks/loadBalancers</c> — one L4
///     load balancer inside a tenant's virtual network, as an HAProxy <c>Deployment</c> and the
///     <c>ConfigMap</c> holding its configuration.
/// </summary>
/// <remarks>
///     <para>
///         <b>The authority is docs/plan/14 § Load balancing, M1 · 0.8 EM</b>: <i>"L4. An address from
///         the tenant's pool plus a <c>Service type=LoadBalancer</c> (announced per ADR-019) <b>or an
///         HAProxy deployment for TCP with health checks and connection limits</b>."</i> This type is
///         the second of those two, and the first is not available on this substrate.
///     </para>
///     <para>
///         ⚠ <b>KUBE-OVN'S OWN L4 LOAD BALANCING IS UNREACHABLE ON THIS PLATFORM, AND THAT IS A
///         REFUTATION RATHER THAN A PREFERENCE.</b> The obvious object for this row is
///         <c>kubeovn.io/v1 SwitchLBRule</c> — a VIP on a tenant's logical switch, served by OVN's own
///         load balancer, with no pod anywhere. Read firsthand in <c>pkg/controller/controller.go</c> at
///         <c>v1.16.2</c>: the <c>SwitchLBRule</c> lister, its three work queues, its event handler and
///         its three workers are <b>all inside <c>if config.EnableLb</c></b>, and so are the
///         <c>Service</c> handlers and the whole of <c>VpcDns</c>. ADR-019 runs Kube-OVN with
///         <c>ENABLE_LB=false</c>, deliberately, because Cilium owns the service datapath. So on the
///         cluster this platform actually deploys, a <c>SwitchLBRule</c> is an object nothing reconciles:
///         it would be accepted by the API server, reported <c>Succeeded</c> by this platform, and
///         balance nothing. ⚠ <b>The same block is why a tenant VPC has no DNS either</b>, which is one
///         of the three things <c>dnsZones</c> is owed for.
///     </para>
///     <para>
///         ⚠ <b>AND THE CILIUM HALF IS NOT AVAILABLE EITHER, FOR A DIFFERENT REASON.</b>
///         ADR-019 gives Cilium LB-IPAM and BGP the <i>platform's</i> service VIPs and says of the
///         tenant half that <i>"an address terminating on an OVN logical router is invisible to a
///         host-network speaker"</i>. A <c>Service type=LoadBalancer</c> is announced from the host
///         network namespace, and the workloads this type balances are on a tenant's own routing domain
///         with an address space the platform allows to overlap another tenant's. There is no
///         arrangement of the two in which a Kubernetes <c>Service</c> reaches a custom VPC.
///     </para>
///     <para>
///         ⚠ <b>SO THE PROXY IS A POD ON THE TENANT'S OWN SUBNET, AND EVERY OTHER DECISION HERE FOLLOWS
///         FROM THAT.</b> A pod joins a Kube-OVN subnet through the
///         <c>ovn.kubernetes.io/logical_switch</c> annotation (<c>pkg/util/const.go</c>), which is the
///         only seam by which anything this platform runs can be <i>inside</i> a tenant's network. Once
///         the proxy is a pod, its address comes from that subnet's IPAM, its configuration is a file,
///         and its failure modes are a workload's rather than a fabric's.
///     </para>
///     <para>
///         ⚠ <b>A CHILD OF <c>virtualNetworks</c>, WHICH IS NOT HOW docs/plan/14 SPELLS IT.</b> That
///         document writes <c>CyberCloud.Network/loadBalancers</c>, with no network segment — the same
///         spelling <see cref="PublicIpAddresses" /> kept, because an <c>OvnEip</c> genuinely names no
///         VPC. Here the substrate says the opposite: every object this type renders is annotated onto
///         <b>one subnet of one VPC</b>, and the frontend address is only meaningful inside that VPC's
///         address space. Making it a child means the network comes from
///         <see cref="ResourceId.ParentNames" /> and cannot be wrong — the same argument
///         <c>NetworkSubnets.VpcRefOf</c> makes for reading <c>spec.vpc</c> off the address rather than
///         out of the body. A top-level spelling would need a network <i>name</i> property that nothing
///         validates.
///     </para>
///     <para>
///         ⚠ <b>THE FIRST TYPE IN THIS FAMILY WHOSE OBJECTS ARE NAMESPACED, AND THE NAME ARITHMETIC
///         INVERTS BECAUSE OF IT.</b> A <c>Vpc</c>, a <c>Subnet</c>, a <c>SecurityGroup</c> and an
///         <c>OvnEip</c> are all <c>scope="Cluster"</c>, so every one of them folds the namespace into
///         its object name. A <c>Deployment</c> and a <c>ConfigMap</c> are namespaced, so
///         <c>ReconcileDriver.NamespaceFor</c> already separates two subscriptions and
///         <see cref="ObjectNameOf" /> folds in the <b>parent network</b> and nothing else — two networks
///         in one resource group each holding a load balancer called <c>web</c> is the collision that is
///         still live here.
///     </para>
///     <para>
///         ⚠ <b>THE BACKEND POOL IS ADDRESSES AND NOT RESOURCE IDS, AND THE SUBSTRATE AGREES WITH THE
///         LIMIT RATHER THAN MERELY IMPOSING IT.</b> docs/plan/14 wants backend pools that
///         <i>"reference resource ids (a VM, a scale set, a cluster's node pool), resolved by the
///         reconciler into endpoints"</i>. <c>ReconcileContext</c> carries a cluster connection, an
///         <c>ISecretResolver</c> and nothing else — there is no reader, which is the blocker
///         <c>NetworkProvider</c> recorded for this type — <b>and</b> a comma-separated list of objects
///         is what <c>SchemaProperty.ElementKind</c> refuses. What makes the address list honest rather
///         than a workaround is the first paragraph: with <c>ENABLE_LB=false</c> there is no service
///         discovery inside a tenant VPC and with <c>VpcDns</c> in the same block there is no name
///         resolution either, so an address is what a tenant has for <i>every</i> workload in their own
///         network. The reader is still owed and the endpoints it would resolve to would still be
///         addresses.
///     </para>
///     <para>
///         ⚠ <b>THE FRONTEND ADDRESS IS REQUIRED, WHICH IS THE OPPOSITE OF
///         <see cref="PublicIpAddresses" />' DECISION, FOR THE SAME REASON.</b> There an address the
///         fabric picks is the ordinary request, because the tenant learns it from an action and points
///         DNS at it. Here there is <b>no DNS in the VPC to point</b>, so an address nobody chose is an
///         address nothing can be configured to reach. A required patterned property needs a
///         <see cref="SchemaProperty.DefaultJson" /> that satisfies its own pattern — otherwise
///         <c>ChartAnnotationEmitter</c> writes <c>value: ""</c> and <c>helm lint</c> refuses the chart —
///         which is the interaction <c>NetworkSubnets</c> recorded and this type is the second to meet.
///     </para>
///     <para>
///         ⚠ <b>ONE REPLICA, AND SAYING SO IS BETTER THAN THE ALTERNATIVES.</b> Two replicas would need
///         two addresses and something to share a VIP between them, and the thing that shares a VIP on
///         this substrate is the OVN load balancer that <c>ENABLE_LB=false</c> turned off. So this row
///         is a single proxy pod, restarted by its Deployment, and
///         <c>charts/managed/haproxy/conformance.yaml § owed</c>,
///         <c>one-proxy-is-a-single-point-of-failure</c>, says it in the place a tenant's architect
///         reads.
///     </para>
///     <para>
///         ⚠ <b>THE CONFIG CHECKSUM ON THE POD TEMPLATE IS LOAD-BEARING AND ITS ABSENCE WOULD BE
///         INVISIBLE.</b> A <c>ConfigMap</c> that changes does <b>not</b> restart the pods that mount
///         it, and HAProxy reads its file once at start. Without
///         <see cref="ConfigChecksumAnnotation" /> in the pod template, an edited backend list would
///         apply cleanly, read back exactly as desired, converge, report <c>Succeeded</c> — and traffic
///         would keep going to the old servers for as long as the pod lived. The annotation makes the
///         config part of the pod template, so a config change is a rollout.
///     </para>
///     <para>
///         ⚠ <b><c>Recreate</c> RATHER THAN <c>RollingUpdate</c>, AND THAT IS THE PINNED ADDRESS'S
///         DOING.</b> A rolling update starts the new pod before the old one goes, and the new pod
///         would ask IPAM for an address the old pod still holds —
///         <c>acquireStaticAddress</c> refuses it, so the rollout stalls and the resource sits in
///         <c>InProgress</c> forever. The cost is a few seconds of downtime on every change, which is
///         stated at <c>conformance.yaml § owed</c>, <c>a-config-change-drops-connections</c>.
///     </para>
///     <para>
///         ⚠ <b>LICENCE.</b> HAProxy is GPL-2.0-or-later and is run as an unmodified upstream container
///         image, in its own process, with nothing of this platform linked into it. ADR-011 does not
///         list HAProxy; the row it decides this by is ClamAV's — <i>"GPL-2.0 ✓ — separate process,
///         separate container, no linking"</i> — and the same reasoning already ships
///         <c>charts/managed/mariadb</c>. No SSPL or BUSL component is involved.
///     </para>
/// </remarks>
public static class LoadBalancers {
    /// <summary>The provider namespace — the family's.</summary>
    public const string ProviderNamespace = VirtualNetworks.ProviderNamespace;

    /// <summary>
    ///     The type path. ⚠ <b>A child of <c>virtualNetworks</c>, which docs/plan/14 does not spell.</b>
    /// </summary>
    /// <remarks>See this class's remarks: every object rendered here is annotated onto one subnet.</remarks>
    public const string TypePath = "virtualNetworks/loadBalancers";

    /// <summary>The one api-version. ⚠ Equal to the rest of the family's.</summary>
    public const string V2026 = VirtualNetworks.V2026;

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/haproxy";

    /// <summary>The pointer <c>RequiresCluster</c> names.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The image ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The image repository. ⚠ Docker's own <c>library/haproxy</c>, unmodified.</summary>
    public const string ImageRepository = "haproxy";

    /// <summary>The default HAProxy line — the current long-term-support one.</summary>
    /// <remarks>
    ///     ⚠ <b>Both values in <see cref="Versions" /> were resolved against the registry rather than
    ///     assumed</b>, on 2026-08-19, through
    ///     <c>hub.docker.com/v2/repositories/library/haproxy/tags</c>: <c>3.2-alpine</c> and
    ///     <c>3.4-alpine</c> both exist, alongside <c>3.2.22-alpine</c> and <c>3.4.3-alpine</c>. A pin
    ///     that names a tag nobody checked is the defect <c>CyberCloud.ContainerService</c> shipped, and
    ///     the symptom here would be a proxy stuck in <c>ImagePullBackOff</c> with the resource reporting
    ///     <c>InProgress</c> and nothing naming the tag.
    /// </remarks>
    public const string DefaultVersion = "3.2";

    /// <summary>The HAProxy lines a tenant may ask for.</summary>
    /// <remarks>
    ///     ⚠ <b>Two, and the even minor is the supported one.</b> HAProxy's release policy makes even
    ///     minors long-term-support lines (2.8, 3.0, 3.2) and odd ones the development-forward branch,
    ///     so <c>3.2</c> is the default and <c>3.4</c> is available for a tenant who wants the newer
    ///     one. ⚠ <b>Minor rather than patch</b>, so a patched image arrives on a pod restart without a
    ///     resource body having to change; the cost is that the exact build is not pinned, which is
    ///     <c>conformance.yaml § owed</c>, <c>the-image-is-a-tag-and-not-a-digest</c>.
    /// </remarks>
    public static ImmutableArray<string> Versions { get; } = ["3.2", "3.4"];

    /// <summary>The image a body asks for.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b><c>-alpine</c> and not the Debian variant</b>: the two are the same HAProxy and the
    ///     Alpine one is about a fifth of the size, which on a proxy that carries no tooling is the
    ///     whole difference. ⚠ <b>The joining exists in the chart too</b> and
    ///     <c>NetworkLoadBalancerTests</c> compares the two, for
    ///     <c>ConsoleSizingTests.TheImageDigestsAreTheSameInCSharpAndInTheChart</c>'s reason.
    /// </remarks>
    public static string Image(JsonElement desired) =>
        ImageRepository + ":" + Version(desired) + "-alpine";

    // ── Sizing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The default preset.</summary>
    public const string DefaultPreset = "c1.small";

    /// <summary>
    ///     What each preset gives the proxy pod.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The ladder is deliberately short and starts low.</b> An L4 TCP proxy is almost all
    ///     kernel work; HAProxy's own documentation puts tens of thousands of concurrent connections on
    ///     a single core, so the small row is the right default and the large row exists for a tenant
    ///     terminating a lot of long-lived connections. ⚠ <b>The same table is in the chart</b> and
    ///     <c>NetworkLoadBalancerTests</c> compares it row for row — two spellings of a sizing table is
    ///     a resource that reserves one quantity and runs another.
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal) {
            ["c1.small"] = ("250m", "256Mi"),
            ["c1.medium"] = ("500m", "512Mi"),
            ["c1.large"] = ("1", "1Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>What a body's preset costs.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static (string Cpu, string Memory) Resources(JsonElement desired) =>
        Presets.TryGetValue(SizingPreset(desired), out var chosen) ? chosen : Presets[DefaultPreset];

    // ── The action ────────────────────────────────────────────────────────────────────────────

    /// <summary>The action that reports what the proxy is configured to do and whether it is up.</summary>
    /// <remarks>
    ///     ⚠ <b>It is <see cref="NetworkSecurityGroups.EffectiveRulesAction" />'s shape plus one fact
    ///     the body cannot carry.</b> The server list is an expansion of two scalars — the address list
    ///     and the backend port — which is arithmetic a tenant would otherwise do in their head; the
    ///     fact that is not in the body is whether the proxy pod is actually running, which lives on
    ///     <c>Deployment.status.readyReplicas</c> and nowhere else. A load balancer whose pod is
    ///     <c>ImagePullBackOff</c> looks exactly like one that is working from the resource body alone.
    /// </remarks>
    public const string BackendsAction = "showBackends";

    /// <summary>The permission <see cref="BackendsAction" /> checks.</summary>
    /// <remarks>
    ///     ⚠ <c>read</c>. Everything it returns is either in the body the caller can already read or is
    ///     a replica count; nothing here is a credential.
    /// </remarks>
    public const string BackendsPermission = "read";

    // ── The objects a load balancer IS ────────────────────────────────────────────────────────

    /// <summary>The proxy's configuration file, as a <c>ConfigMap</c>.</summary>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>The proxy itself.</summary>
    public static GroupVersionKind DeploymentKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    /// <summary>
    ///     The object name both objects take: the parent network's name and this resource's, joined.
    /// </summary>
    /// <param name="id">The load balancer's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> carries no parent name.</exception>
    /// <remarks>
    ///     ⚠ <b>TWO COMPONENTS AND NOT THE THREE ITS SIBLINGS NEED, BECAUSE THESE OBJECTS ARE
    ///     NAMESPACED.</b> <c>ReconcileDriver.NamespaceFor</c> is
    ///     <c>{subscriptionId:N}-{resourceGroup}</c>, so a subscription and a resource group are already
    ///     folded into the namespace this object lands in — which is exactly the separation
    ///     <c>NetworkSubnets.ObjectNameOf</c> has to add by hand for a cluster-scoped <c>Subnet</c>. What
    ///     the namespace does <b>not</b> separate is two networks in one resource group, so the parent's
    ///     name is still a component and an id without one throws rather than rendering a colliding
    ///     name.
    ///     <para>
    ///         ⚠ <b>One name for both objects</b>, which is <c>CloudConsoles.ShellName</c>'s choice for
    ///         its three: they are different kinds, so there is nothing to collide, and the Deployment's
    ///         volume names the ConfigMap — two spellings would be a pod that mounts a file nobody
    ///         wrote.
    ///     </para>
    /// </remarks>
    public static string ObjectNameOf(ResourceId id) =>
        id.ParentNames.Length == 0
            ? throw new ArgumentException(
                $"'{id.Path}' carries no parent name, so the Deployment it renders would collide with "
                + "every other network's load balancer of the same name in the same resource group. A "
                + "load balancer is a child type and its address always interleaves its network — see "
                + "LoadBalancers.TypePath.",
                nameof(id)
            )
            : id.ParentNames.Replace('/', '-') + "-" + id.Name;

    /// <summary>
    ///     The name of the Kube-OVN <c>Subnet</c> the proxy pod is placed on.
    /// </summary>
    /// <param name="ns">The resource's namespace, a name component of the cluster-scoped Subnet.</param>
    /// <param name="id">The load balancer's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Composed through <see cref="NetworkSubnets.ObjectNameOf(string, string, string)" />
    ///     rather than spelled again here</b>, on the rule <c>NetworkSubnets.VpcRefOf</c> states: a
    ///     second spelling of another type's object name is the thing that stops agreeing the day that
    ///     type's naming changes. ⚠ <b>The network half comes from the ADDRESS and only the subnet half
    ///     from the body</b>, so a load balancer cannot be placed on another network's subnet even if a
    ///     body names one — the worst failure available here, because a subnet in another tenant's VPC
    ///     with a colliding name would put this tenant's proxy inside it.
    /// </remarks>
    public static string LogicalSwitchOf(string ns, ResourceId id, JsonElement desired) =>
        NetworkSubnets.ObjectNameOf(ns, NetworkOf(id), Subnet(desired));

    /// <summary>The parent network's name.</summary>
    /// <param name="id">The load balancer's address.</param>
    /// <exception cref="ArgumentException"><paramref name="id" /> has no parent.</exception>
    public static string NetworkOf(ResourceId id) =>
        id.Parent?.Name
        ?? throw new ArgumentException(
            $"'{id.Path}' has no parent, so there is no virtual network for its proxy to sit in. An "
            + "unbound pod joins the platform's own default VPC, which is the failure this throws "
            + "rather than renders.",
            nameof(id)
        );

    /// <summary>The <c>ConfigMap</c> a load balancer owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The load balancer's address.</param>
    public static ObjectRef ConfigMapRef(string ns, ResourceId id) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>The <c>Deployment</c> a load balancer owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The load balancer's address.</param>
    public static ObjectRef DeploymentRef(string ns, ResourceId id) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = ObjectNameOf(id) };

    /// <summary>Both objects, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The load balancer's address.</param>
    /// <remarks>
    ///     ⚠ <b>The config first and the proxy second</b>, because a Deployment whose <c>ConfigMap</c>
    ///     does not exist yet schedules a pod that cannot mount it and reports
    ///     <c>CreateContainerConfigError</c> — a message about a volume rather than about a missing
    ///     apply. The reverse order converges eventually and looks broken while it does.
    /// </remarks>
    public static ImmutableArray<ObjectRef> Objects(string ns, ResourceId id) =>
        [ConfigMapRef(ns, id), DeploymentRef(ns, id)];

    /// <summary>The annotation carrying the hash of the rendered configuration.</summary>
    /// <remarks>
    ///     ⚠ <b>WITHOUT THIS THE UPDATE PATH IS A NO-OP THAT REPORTS SUCCESS.</b> See this class's
    ///     remarks: a changed <c>ConfigMap</c> does not restart anything, and HAProxy reads its file
    ///     once. The value is <c>KubeLabels.ReconcileHash</c> of the rendered config, which is the same
    ///     spelling ADR-013's own <c>cybercloud.io/reconcile-hash</c> uses — one hash format in the
    ///     platform rather than two.
    /// </remarks>
    public const string ConfigChecksumAnnotation = "cybercloud.io/haproxy-config";

    /// <summary>
    ///     The annotation that puts a pod on a tenant's subnet — Kube-OVN's, not this platform's.
    /// </summary>
    /// <remarks>
    ///     ⚠ Read firsthand from <c>pkg/util/const.go</c> at <c>v1.16.2</c>:
    ///     <c>LogicalSwitchAnnotation = "ovn.kubernetes.io/logical_switch"</c>. It is the only seam by
    ///     which anything this platform runs is <i>inside</i> a tenant's routing domain: without it the
    ///     pod joins the namespace's default subnet, which is the platform's own VPC, and the proxy
    ///     can neither be reached from the tenant's network nor reach anything in it.
    /// </remarks>
    public const string LogicalSwitchAnnotation = "ovn.kubernetes.io/logical_switch";

    /// <summary>The annotation that pins the address a workload's pod is given.</summary>
    /// <remarks>
    ///     ⚠ <c>IPPoolAnnotation = "ovn.kubernetes.io/ip_pool"</c>, same file. See
    ///     <see cref="PoolOf" /> for why the value is comma-joined.
    /// </remarks>
    public const string IpPoolAnnotation = "ovn.kubernetes.io/ip_pool";

    /// <summary>The name label every proxy pod carries.</summary>
    public const string NameLabel = "app.kubernetes.io/name";

    /// <summary>The instance label that keeps two proxies in one namespace apart.</summary>
    /// <remarks>
    ///     ⚠ <b>ADR-013's seven labels are NOT the selector, and that is deliberate.</b>
    ///     <c>KubeCommand</c> injects them into the object it applies — the Deployment — and not into
    ///     <c>spec.template.metadata.labels</c>, which is a different object's labels that this
    ///     provider writes itself. A selector naming a label nothing puts on the pod is a Deployment
    ///     that creates pods forever and never counts one as its own.
    /// </remarks>
    public const string InstanceLabel = "app.kubernetes.io/instance";

    /// <summary>Where the image expects its configuration file.</summary>
    /// <remarks>
    ///     ⚠ Read firsthand from <c>docker-library/haproxy</c>'s Dockerfile:
    ///     <c>CMD ["haproxy", "-f", "/usr/local/etc/haproxy/haproxy.cfg"]</c>. Mounting the ConfigMap
    ///     anywhere else is a proxy that starts with the image's own example configuration and listens
    ///     on nothing.
    /// </remarks>
    public const string ConfigDirectory = "/usr/local/etc/haproxy";

    /// <summary>The key inside the <c>ConfigMap</c>, and the file name it becomes.</summary>
    public const string ConfigFile = "haproxy.cfg";

    /// <summary>The uid and gid the upstream image runs as.</summary>
    /// <remarks>
    ///     ⚠ <b>Named explicitly, and <c>runAsNonRoot: true</c> alone would BREAK THE POD.</b> The
    ///     image ends with <c>USER haproxy</c> — a name, not a number — and a kubelet asked to enforce
    ///     <c>runAsNonRoot</c> against an image whose user is non-numeric refuses to start the container
    ///     with <i>"container has runAsNonRoot and image has non-numeric user (haproxy), cannot verify
    ///     user is non-root"</i>. The uid is 99, read from the same Dockerfile
    ///     (<c>addgroup --gid 99</c>, <c>adduser --uid 99</c>).
    /// </remarks>
    public const int ProxyUid = 99;

    /// <summary>
    ///     The sysctl that lets a non-root proxy bind a port below 1024.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>THE ROW THAT MAKES <c>listener.port: 80</c> POSSIBLE AT ALL.</b> The image runs as uid
    ///     99, and binding a privileged port as a non-root process fails with <c>EACCES</c> — HAProxy
    ///     reports <i>"cannot bind socket"</i> and exits, which presents as a <c>CrashLoopBackOff</c>
    ///     with a resource stuck in <c>InProgress</c>. <c>net.ipv4.ip_unprivileged_port_start</c> is in
    ///     the kubelet's <b>safe</b> sysctl set (Kubernetes 1.22 and later, and this chart's
    ///     <c>kubeVersion</c> is <c>&gt;=1.31</c>), so it needs no allow-list on the node and no
    ///     capability on the container. ⚠ The alternative — <c>NET_BIND_SERVICE</c> — does not work for
    ///     a non-root process, because Kubernetes sets no ambient capabilities and the image's binary
    ///     carries no file capability.
    /// </remarks>
    public const string UnprivilegedPortSysctl = "net.ipv4.ip_unprivileged_port_start";

    // ── The body shape ────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The shape of one address, or of a comma-separated list of them.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Deliberately loose, for <see cref="IpAddresses.V6Pattern" />'s reason</b>: the character
    ///     class admits both families and the exact reading is <see cref="System.Net.IPAddress" />'s in
    ///     <see cref="BackendProblem" />. The separator is outside the class, so the expression is
    ///     unambiguous and linear — a full IPv6 grammar under a repetition would be the catastrophic
    ///     backtracking hazard this family refuses to put on a request path.
    /// </remarks>
    public const string AddressListPattern = "[0-9A-Fa-f:.]+(,[0-9A-Fa-f:.]+)*";

    /// <summary>How many servers one backend may carry.</summary>
    /// <remarks>
    ///     ⚠ <b>Checked in the reconciler rather than in the schema, because it is a count of list
    ///     elements inside a string</b> and <c>SchemaProperty</c> can bound a length but not an arity.
    ///     The number is a product decision rather than a limit of HAProxy, which handles thousands:
    ///     a backend list this long is a tenant who wants a service mesh, and the failure of allowing
    ///     it is a ConfigMap that stops fitting in etcd's 1 MiB object limit.
    /// </remarks>
    public const int MaxBackends = 32;

    /// <summary>The default frontend address. ⚠ Inside the family's own example subnet.</summary>
    /// <remarks>
    ///     A required patterned property must carry a default its own pattern accepts — see this
    ///     class's remarks — and the value has to be a plausible address rather than a placeholder,
    ///     because <c>helm lint</c> renders the chart with it.
    /// </remarks>
    public const string DefaultFrontendV4 = "10.20.1.10";

    /// <summary>The default backend. ⚠ One address, in the same example subnet.</summary>
    public const string DefaultBackendAddresses = "10.20.1.11";

    /// <summary>The default subnet name a load balancer sits on.</summary>
    public const string DefaultSubnet = "web";

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NO <c>protocol</c> PROPERTY, AND THE ABSENCE IS THE SUBSTRATE'S RATHER THAN A
    ///         SIMPLIFICATION.</b> HAProxy proxies TCP and HTTP; it does <b>not</b> proxy UDP at all, in
    ///         any version. A <c>protocol</c> property with one legal value is a control that suggests a
    ///         choice nobody has, and one with <c>udp</c> in it is a <c>400</c> the tenant discovers
    ///         after they have designed around it. UDP load balancing on this substrate is
    ///         <c>conformance.yaml § owed</c>, <c>udp-is-not-balanced</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>NO <c>publicIpAddressId</c> EITHER.</b> Attaching a public address is a second
    ///         Kube-OVN object — an <c>OvnFip</c> or an <c>OvnDnatRule</c> naming
    ///         <see cref="PublicIpAddresses.ObjectNameOf" />'s output — and the join is a resource id
    ///         this provider would have to resolve through <c>CyberCloud.ResourceManager</c>, which is
    ///         the reader that does not exist. The address type's own
    ///         <c>nothing-can-be-given-an-address-yet</c> is the other half of this sentence and neither
    ///         half is closed by declaring a property.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE PORTS ARE BOUNDED WHOLE NUMBERS RATHER THAN A PATTERNED STRING</b>, which is the
    ///         opposite of <c>NetworkSecurityGroups</c>' choice and is right for the opposite reason:
    ///         that type needs a <i>list</i> of ports and <c>Minimum</c>/<c>Maximum</c> are property
    ///         constraints with no per-element form, whereas a listener has exactly one port and a
    ///         number with a range is the strongest thing available for it.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the load balancer is billed in. ⚠ It must be the region "
                    + "its virtual network is in — nothing checks that, because the network's own "
                    + "region is not readable from here."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The load balancer's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the proxy runs in. ⚠ It must be the cluster the virtual "
                    + "network was created in: a proxy in another cluster has no route into this "
                    + "network at all."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },
                new(
                    "/properties/subnet",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The subnet of this virtual network the proxy sits on. ⚠ The proxy "
                    + "gets an address from it, so the frontend address below must be inside its "
                    + "range. A name that is not a subnet of this network is refused by the fabric "
                    + "rather than by the API, and the proxy pod never schedules."
                ) {
                    Pattern = ResourceNaming.Pattern,
                    MaxLength = ResourceNaming.MaxLength,
                    Widget = WidgetHint.Subnet,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultSubnet + "\"",
                    ExampleJson = "\"web\""
                },

                // ── The frontend ───────────────────────────────────────────────────────────────
                new(
                    "/properties/frontend",
                    SchemaKind.Nested,
                    Description: "The address and port workloads connect to."
                ),
                new(
                    "/properties/frontend/v4",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The IPv4 address the proxy answers on, inside the subnet's range. "
                    + "⚠ Required, and it is the one thing about this resource a tenant must choose: "
                    + "there is no DNS inside a virtual network, so an address nobody picked is an "
                    + "address nothing can be pointed at. A bare address and never a prefix."
                ) {
                    Pattern = IpAddresses.V4Pattern,
                    Widget = WidgetHint.Cidr,
                    Immutable = true,
                    DefaultJson = "\"" + DefaultFrontendV4 + "\"",
                    ExampleJson = "\"10.20.1.10\""
                },
                new(
                    "/properties/frontend/v6",
                    SchemaKind.Text,
                    Description: "The IPv6 address the proxy also answers on, or empty. ⚠ Lower case "
                    + "only, for the fabric's reason. It must be inside the subnet's IPv6 range, which "
                    + "means the subnet has to have one."
                ) {
                    Pattern = IpAddresses.OptionalV6Pattern,
                    Widget = WidgetHint.Cidr,
                    Immutable = true,
                    DefaultJson = "\"\"",
                    ExampleJson = "\"fd00:20:1::10\""
                },
                new(
                    "/properties/frontend/port",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "The TCP port the proxy listens on. ⚠ There is no protocol setting: "
                    + "HAProxy does not proxy UDP in any version, so every rule here is TCP."
                ) {
                    Minimum = 1,
                    Maximum = 65535,
                    DefaultJson = "80"
                },

                // ── The backend ────────────────────────────────────────────────────────────────
                new(
                    "/properties/backend",
                    SchemaKind.Nested,
                    Description: "Where the connections go."
                ),
                new(
                    "/properties/backend/addresses",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The workload addresses to balance across, comma separated — for "
                    + "example 10.20.1.11,10.20.1.12. ⚠ Addresses and not resource ids: there is no "
                    + "service discovery and no DNS inside a virtual network, so an address is what a "
                    + "tenant has for their own workloads. At most 32."
                ) {
                    Pattern = AddressListPattern,
                    MaxLength = 1024,
                    DefaultJson = "\"" + DefaultBackendAddresses + "\"",
                    ExampleJson = "\"10.20.1.11,10.20.1.12\""
                },
                new(
                    "/properties/backend/port",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "The TCP port every backend address is reached on. ⚠ One port for the "
                    + "whole pool: a pool whose members listen on different ports is two pools."
                ) {
                    Minimum = 1,
                    Maximum = 65535,
                    DefaultJson = "8080"
                },

                // ── Health checking ────────────────────────────────────────────────────────────
                new(
                    "/properties/health",
                    SchemaKind.Nested,
                    Description: "How a backend is decided to be up. ⚠ Checking cannot be turned off — "
                    + "a proxy that sends connections to a dead server is worse than no proxy."
                ),
                new(
                    "/properties/health/intervalSeconds",
                    SchemaKind.WholeNumber,
                    Description: "How often each backend is probed with a TCP connection."
                ) {
                    Minimum = 2,
                    Maximum = 60,
                    DefaultJson = "5"
                },
                new(
                    "/properties/health/unhealthyAfter",
                    SchemaKind.WholeNumber,
                    Description: "How many failed probes take a backend out of the pool."
                ) {
                    Minimum = 1,
                    Maximum = 10,
                    DefaultJson = "3"
                },
                new(
                    "/properties/health/healthyAfter",
                    SchemaKind.WholeNumber,
                    Description: "How many successful probes put a backend back into the pool."
                ) {
                    Minimum = 1,
                    Maximum = 10,
                    DefaultJson = "2"
                },

                // ── Limits ─────────────────────────────────────────────────────────────────────
                new(
                    "/properties/limits",
                    SchemaKind.Nested,
                    Description: "What the proxy refuses rather than passes on."
                ),
                new(
                    "/properties/limits/maxConnections",
                    SchemaKind.WholeNumber,
                    Description: "How many connections the frontend accepts at once. ⚠ Further "
                    + "connections wait in the kernel's accept queue rather than being refused, so "
                    + "this is a back-pressure setting rather than a firewall. It is also applied per "
                    + "backend server."
                ) {
                    Minimum = 10,
                    Maximum = 100000,
                    DefaultJson = "2000"
                },

                // ── The proxy itself ───────────────────────────────────────────────────────────
                new("/properties/sizing", SchemaKind.Nested, Description: "CPU and memory for the proxy."),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "How much the proxy pod gets. An L4 proxy is mostly kernel work, so "
                    + "the small row carries far more than its size suggests; the larger rows are for "
                    + "many long-lived connections."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"" + DefaultPreset + "\""
                },
                new(
                    "/properties/version",
                    SchemaKind.Text,
                    Description: "Which HAProxy line to run. 3.2 is the long-term-support line and is "
                    + "the default; 3.4 is the current one. ⚠ A change here restarts the proxy and "
                    + "drops open connections."
                ) {
                    AllowedValues = [.. Versions.Order(StringComparer.Ordinal)],
                    DefaultJson = "\"" + DefaultVersion + "\""
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>
    ///     What a <c>POST …/showBackends</c> returns.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b><c>readyReplicas</c> IS THE FIELD THE OTHERS ARE WORTHLESS WITHOUT.</b> Everything else
    ///     here is an expansion of the stored body, so a load balancer whose image will not pull, whose
    ///     subnet does not exist, or whose frontend address is already taken reports exactly the same
    ///     servers as one that is working. The replica count is the only fact in this response the
    ///     tenant did not supply.
    ///     <para>
    ///         ⚠ <b>The per-server health is NOT here, and that is stated rather than omitted.</b>
    ///         Which backends HAProxy currently believes are up lives in its runtime API, on a UNIX
    ///         socket inside the pod, and reaching it needs an exec seam this platform does not have —
    ///         <c>conformance.yaml § owed</c>, <c>backend-health-is-not-observable</c>.
    ///     </para>
    /// </remarks>
    public static ResourceSchema BackendsResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/frontend",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The address and port the proxy answers on."
                ),
                new(
                    "/servers",
                    SchemaKind.Array,
                    Required: true,
                    Description: "One line per backend server, in the order the proxy is given them."
                ) {
                    ElementKind = SchemaKind.Text
                },
                new(
                    "/serverCount",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many servers the pool carries."
                ),
                new(
                    "/readyReplicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many proxy pods are running and passing their readiness probe. "
                    + "⚠ 0 with servers listed is a load balancer that is configured and carrying no "
                    + "traffic, which is the state this action exists to make visible."
                ),
                new(
                    "/note",
                    SchemaKind.Text,
                    Required: true,
                    Description: "What the answer is and is not — in particular that the servers are "
                    + "what the platform configured rather than what the proxy currently believes is "
                    + "healthy."
                ),
                new(
                    "/sampledAt",
                    SchemaKind.Text,
                    Required: true,
                    Description: "When the platform read the Deployment, RFC 3339."
                ) {
                    Format = SchemaFormat.DateTime
                }
            ]
        );

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The region a body declares.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Location(JsonElement desired) => VirtualNetworks.Location(desired);

    /// <summary>The subnet the proxy sits on.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Subnet(JsonElement desired) => Text(desired, "subnet", DefaultSubnet);

    /// <summary>The IPv4 address the proxy answers on.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string FrontendV4(JsonElement desired) =>
        Nested(desired, "frontend", "v4", DefaultFrontendV4);

    /// <summary>The IPv6 address the proxy also answers on, or empty.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string FrontendV6(JsonElement desired) => Nested(desired, "frontend", "v6", "");

    /// <summary>The port the proxy listens on.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int FrontendPort(JsonElement desired) => Whole(desired, "frontend", "port", 80);

    /// <summary>The backend addresses, as the body spells them.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string BackendAddressList(JsonElement desired) =>
        Nested(desired, "backend", "addresses", DefaultBackendAddresses);

    /// <summary>The backend addresses, split and trimmed.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ Empty entries are dropped rather than rendered, because a trailing comma is a typo and
    ///     <c>server s2 :8080</c> is a configuration HAProxy refuses to start with — a whole load
    ///     balancer down for a punctuation mistake. <see cref="BackendProblem" /> is what refuses the
    ///     rest.
    /// </remarks>
    public static ImmutableArray<string> BackendAddresses(JsonElement desired) =>
        [
            .. BackendAddressList(desired)
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
        ];

    /// <summary>The port every backend is reached on.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int BackendPort(JsonElement desired) => Whole(desired, "backend", "port", 8080);

    /// <summary>How often a backend is probed, in seconds.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int HealthIntervalSeconds(JsonElement desired) =>
        Whole(desired, "health", "intervalSeconds", 5);

    /// <summary>How many failed probes remove a backend.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int UnhealthyAfter(JsonElement desired) => Whole(desired, "health", "unhealthyAfter", 3);

    /// <summary>How many successful probes restore a backend.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int HealthyAfter(JsonElement desired) => Whole(desired, "health", "healthyAfter", 2);

    /// <summary>How many connections the frontend accepts at once.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static int MaxConnections(JsonElement desired) =>
        Whole(desired, "limits", "maxConnections", 2000);

    /// <summary>The sizing preset.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string SizingPreset(JsonElement desired) =>
        Presets.ContainsKey(Nested(desired, "sizing", "preset", DefaultPreset))
            ? Nested(desired, "sizing", "preset", DefaultPreset)
            : DefaultPreset;

    /// <summary>The HAProxy line.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Version(JsonElement desired) =>
        Versions.Contains(Text(desired, "version", DefaultVersion))
            ? Text(desired, "version", DefaultVersion)
            : DefaultVersion;

    /// <summary>
    ///     What is wrong with a body's addresses, or <see langword="null" /> when nothing is.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>A pure function of its argument and it must stay one</b> — it is called from a
    ///     reconciler, which is a singleton serving every tenant in the process. Same rule, same reason,
    ///     as <c>NetworkAddressing.ProblemWith</c>.
    ///     <para>
    ///         ⚠ <b>What is left after the pattern is the family's recurring shape and it is narrow
    ///         here.</b> <see cref="AddressListPattern" /> refuses everything that is not a list of
    ///         address-shaped tokens, at the API, with a pointer, before the <c>202</c>. What it cannot
    ///         say is that a token parses, that there are not too many of them, and — the one that
    ///         matters — that an address is not the <b>same</b> as the frontend's, which would be a
    ///         proxy configured to connect to itself and a connection loop until the connection limit
    ///         is reached.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Whether an address is inside the subnet's range is NOT checked, by anything.</b>
    ///         The prefix is on the <c>Subnet</c> object in the cluster, which this platform can read
    ///         only after the write path has answered — the same shape as
    ///         <c>an-address-is-not-checked-against-the-pool-before-202</c>.
    ///     </para>
    /// </remarks>
    public static string? BackendProblem(JsonElement desired) {
        if (IpAddresses.ProblemWith(FrontendV4(desired), false, "/properties/frontend/v4") is { } v4) {
            return v4;
        }

        if (IpAddresses.ProblemWith(FrontendV6(desired), true, "/properties/frontend/v6") is { } v6) {
            return v6;
        }

        var backends = BackendAddresses(desired);

        if (backends.Length == 0) {
            return "'/properties/backend/addresses' names no address, so the proxy would accept "
                + "connections and have nowhere to send them. Give at least one workload address.";
        }

        if (backends.Length > MaxBackends) {
            return $"'/properties/backend/addresses' names {backends.Length} addresses and the limit "
                + $"is {MaxBackends}. A pool that large is a service mesh rather than a load balancer.";
        }

        foreach (var backend in backends) {
            if (!System.Net.IPAddress.TryParse(backend, out _)) {
                return $"'/properties/backend/addresses' contains '{backend}', which is not an IP "
                    + "address. Each entry is a bare IPv4 or IPv6 address with no prefix length and no "
                    + "port — the port is '/properties/backend/port'.";
            }

            if (string.Equals(backend, FrontendV4(desired), StringComparison.Ordinal)
                || (FrontendV6(desired).Length > 0
                    && string.Equals(backend, FrontendV6(desired), StringComparison.OrdinalIgnoreCase))) {
                return $"'/properties/backend/addresses' contains '{backend}', which is this load "
                    + "balancer's own frontend address. The proxy would connect to itself on every "
                    + "request until the connection limit was reached.";
            }
        }

        return null;
    }

    // ── The configuration a body becomes ──────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>haproxy.cfg</c> a desired body renders.
    /// </summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>mode tcp</c> AND NOT <c>mode http</c>, WHICH IS THE WHOLE OF WHAT THIS ROW
    ///         CLAIMS.</b> docs/plan/14 puts L7 — host and path routing, TLS termination, header
    ///         rewrites, a WAF — on <c>applicationGateways</c> at M2, over Envoy. An HTTP-mode HAProxy
    ///         here would be a second, quieter L7 product with none of those, and the first tenant to
    ///         ask for a path rule would be told to migrate.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The global connection limit is TWICE the frontend's</b>, because HAProxy counts
    ///         both sides of a proxied connection against <c>maxconn</c>: a global limit equal to the
    ///         frontend's would refuse the server-side half of the last accepted connection and present
    ///         as a proxy that stalls at exactly half its configured limit.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Logging goes to stdout and nowhere else.</b> The upstream image has no syslog
    ///         daemon, and <c>log /dev/log</c> — which almost every HAProxy example on the internet
    ///         carries — is a socket that does not exist in a container, so HAProxy starts and logs
    ///         nothing at all. <c>log stdout format raw</c> puts every connection line where
    ///         docs/plan/16's collector already reads.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Deterministic, byte for byte, for one body.</b> The pod template carries
    ///         <see cref="ConfigHash" /> of this text, so a rendering that varied — a dictionary order,
    ///         a timestamp, a culture-dependent number — would roll the proxy on every reconcile pass.
    ///         Every number here goes through <see cref="CultureInfo.InvariantCulture" /> for that
    ///         reason rather than for a style rule.
    ///     </para>
    /// </remarks>
    public static string HaproxyConfig(JsonElement desired) {
        var maxConnections = MaxConnections(desired);
        var interval = HealthIntervalSeconds(desired) * 1000;
        var builder = new StringBuilder();

        builder.Append(CultureInfo.InvariantCulture, $"""
            # Generated by CyberCloud from CyberCloud.Network/virtualNetworks/loadBalancers.
            # Edits are overwritten on the next reconcile pass.
            global
              log stdout format raw local0 info
              maxconn {maxConnections * 2}

            defaults
              mode tcp
              log global
              option tcplog
              option dontlognull
              timeout connect 5s
              timeout client 60s
              timeout server 60s

            frontend inbound
              bind :{FrontendPort(desired)}
              maxconn {maxConnections}
              default_backend workloads

            backend workloads
              balance roundrobin
              option tcp-check

            """);

        var port = BackendPort(desired);
        var index = 0;

        foreach (var address in BackendAddresses(desired)) {
            index++;

            // ⚠ THE BRACKETS AROUND AN IPv6 ADDRESS ARE NOT COSMETIC. `server s1 fd00::11:8080` is
            // ambiguous to HAProxy's own parser and it refuses to start, which takes the whole load
            // balancer down for one backend written in the other family.
            var target = address.Contains(':', StringComparison.Ordinal)
                ? "[" + address + "]"
                : address;

            builder.Append(
                CultureInfo.InvariantCulture,
                $"  server s{index} {target}:{port} check inter {interval}ms rise {HealthyAfter(desired)} "
            );

            builder.Append(
                CultureInfo.InvariantCulture,
                $"fall {UnhealthyAfter(desired)} maxconn {maxConnections}\n"
            );
        }

        return builder.ToString();
    }

    /// <summary>The hash of the rendered configuration, as the pod template carries it.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <c>KubeLabels.ReconcileHash</c> rather than a second hashing, so the platform has one
    ///     <c>sha256:…</c> spelling. See <see cref="ConfigChecksumAnnotation" /> for why the value has
    ///     to be on the pod template at all.
    /// </remarks>
    public static string ConfigHash(JsonElement desired) =>
        KubeLabels.ReconcileHash(HaproxyConfig(desired));

    /// <summary>The <c>ConfigMap</c> document a desired body becomes.</summary>
    /// <param name="id">The load balancer's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ No labels, no annotations and no namespace here — ADR-013's seven labels and two
    ///     annotations are injected by <c>KubeCommand</c> non-overridably, and the namespace comes from
    ///     <c>InNamespace</c>.
    /// </remarks>
    public static string ConfigMapJson(ResourceId id, JsonElement desired) =>
        new JsonObject {
            ["kind"] = ConfigMapKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(id) },
            ["data"] = new JsonObject { [ConfigFile] = HaproxyConfig(desired) }
        }.ToJsonString();

    /// <summary>The <c>Deployment</c> document a desired body becomes.</summary>
    /// <param name="ns">The resource's namespace, a name component of the Subnet the pod joins.</param>
    /// <param name="id">The load balancer's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>ovn.kubernetes.io/ip_pool</c> AND NOT <c>ovn.kubernetes.io/ip_address</c>, AND
    ///         THE TWO ARE READ BY THE SAME FUNCTION.</b> Read firsthand in
    ///         <c>pkg/controller/pod.go</c> at <c>v1.16.2</c>: <c>acquireStaticAddressHelper</c> takes
    ///         either, and the pool form splits on commas — with the special case that <b>two entries of
    ///         different families are one dual-stack address</b> rather than two servers. The pool
    ///         spelling is Kube-OVN's documented one for a workload rather than a bare pod, so a future
    ///         second replica is a second entry here rather than a different annotation.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The label selector is the object's own name and it is IMMUTABLE ON A DEPLOYMENT.</b>
    ///         <c>spec.selector</c> cannot be changed after create — the API server refuses the update —
    ///         so it is derived from the resource address, which cannot change either, rather than from
    ///         anything in the body.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The readiness probe is a TCP connection to the frontend port and not an HTTP
    ///         get.</b> This is an L4 proxy: there is no path to ask for, and a probe that assumed one
    ///         would report a healthy proxy as unready in front of any non-HTTP workload.
    ///     </para>
    /// </remarks>
    public static string DeploymentJson(string ns, ResourceId id, JsonElement desired) {
        var name = ObjectNameOf(id);
        var (cpu, memory) = Resources(desired);
        var selector = new JsonObject {
            [NameLabel] = ImageRepository,
            [InstanceLabel] = name
        };

        return new JsonObject {
            ["kind"] = DeploymentKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                // ⚠ ONE. See this class's remarks: a second replica needs a second address and
                // something to share a VIP between them, and the thing that shares a VIP on this
                // substrate is the OVN load balancer ADR-019 turns off.
                ["replicas"] = 1,
                // ⚠ Recreate, because the new pod would ask IPAM for an address the old pod still
                // holds and the rollout would stall forever.
                ["strategy"] = new JsonObject { ["type"] = "Recreate" },
                ["selector"] = new JsonObject { ["matchLabels"] = selector.DeepClone() },
                ["template"] = new JsonObject {
                    ["metadata"] = new JsonObject {
                        ["labels"] = selector.DeepClone(),
                        ["annotations"] = new JsonObject {
                            [LogicalSwitchAnnotation] = LogicalSwitchOf(ns, id, desired),
                            [IpPoolAnnotation] = PoolOf(desired),
                            [ConfigChecksumAnnotation] = ConfigHash(desired)
                        }
                    },
                    ["spec"] = new JsonObject {
                        ["automountServiceAccountToken"] = false,
                        ["terminationGracePeriodSeconds"] = 30,
                        ["securityContext"] = new JsonObject {
                            ["runAsNonRoot"] = true,
                            ["runAsUser"] = ProxyUid,
                            ["runAsGroup"] = ProxyUid,
                            ["seccompProfile"] = new JsonObject { ["type"] = "RuntimeDefault" },
                            ["sysctls"] = new JsonArray {
                                new JsonObject {
                                    ["name"] = UnprivilegedPortSysctl, ["value"] = "0"
                                }
                            }
                        },
                        ["containers"] = new JsonArray {
                            new JsonObject {
                                ["name"] = "haproxy",
                                ["image"] = Image(desired),
                                ["securityContext"] = new JsonObject {
                                    ["allowPrivilegeEscalation"] = false,
                                    ["readOnlyRootFilesystem"] = true,
                                    ["capabilities"] = new JsonObject {
                                        ["drop"] = new JsonArray { "ALL" }
                                    }
                                },
                                ["ports"] = new JsonArray {
                                    new JsonObject {
                                        ["name"] = "inbound",
                                        ["containerPort"] = FrontendPort(desired),
                                        ["protocol"] = "TCP"
                                    }
                                },
                                ["readinessProbe"] = new JsonObject {
                                    ["tcpSocket"] = new JsonObject {
                                        ["port"] = FrontendPort(desired)
                                    },
                                    ["periodSeconds"] = 5
                                },
                                ["resources"] = new JsonObject {
                                    ["requests"] = new JsonObject {
                                        ["cpu"] = cpu, ["memory"] = memory
                                    },
                                    ["limits"] = new JsonObject {
                                        ["cpu"] = cpu, ["memory"] = memory
                                    }
                                },
                                ["volumeMounts"] = new JsonArray {
                                    new JsonObject {
                                        ["name"] = "config",
                                        ["mountPath"] = ConfigDirectory,
                                        ["readOnly"] = true
                                    }
                                }
                            }
                        },
                        ["volumes"] = new JsonArray {
                            new JsonObject {
                                ["name"] = "config",
                                ["configMap"] = new JsonObject { ["name"] = name }
                            }
                        }
                    }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The value of the pod template's address-pool annotation.</summary>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Comma-joined, which Kube-OVN reads as ONE dual-stack address rather than as two
    ///     servers</b> — <c>acquireStaticAddressHelper</c> compares the families of a two-entry list
    ///     and folds them when they differ. A semicolon, which the same function also accepts, would
    ///     mean two single-stack addresses for two pods and would leave this proxy's v6 half
    ///     unallocated.
    /// </remarks>
    public static string PoolOf(JsonElement desired) =>
        FrontendV6(desired) is { Length: > 0 } v6
            ? FrontendV4(desired) + "," + v6
            : FrontendV4(desired);

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="id">The load balancer's address.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Dispatches on <c>kind</c>, and a document with no kind is <see langword="false" />
    ///         </b> — <c>CloudConsoles.Matches</c>' rule, for its reason: this resource owns two kinds,
    ///         and guessing which a kindless document was would mean judging a Deployment by a
    ///         ConfigMap's data.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE CONFIG IS COMPARED EXACTLY AND THE DEPLOYMENT BY CONTAINMENT, WHICH IS NOT AN
    ///         INCONSISTENCY.</b> Nothing in Kubernetes rewrites a <c>ConfigMap</c>'s <c>data</c>, so an
    ///         exact comparison there is the strongest available and catches a hand edit. A
    ///         <c>Deployment</c> is defaulted heavily by the API server — <c>revisionHistoryLimit</c>,
    ///         <c>progressDeadlineSeconds</c>, <c>dnsPolicy</c>, <c>schedulerName</c>, every container's
    ///         <c>terminationMessagePath</c> — so an equality comparison there would report drift on a
    ///         perfectly converged proxy forever. This family's recurring trap in its third shape.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE FOUR FIELDS COMPARED ON THE DEPLOYMENT ARE THE FOUR THAT DECIDE WHETHER TRAFFIC
    ///         GOES ANYWHERE.</b> The logical switch decides which network the proxy is on; the address
    ///         pool decides whether anything can reach it; the config hash decides whether the running
    ///         proxy has the configuration this platform last wrote — <b>this is the one an obvious
    ///         implementation leaves out</b>, and without it every backend change converges instantly
    ///         and changes nothing; the image decides what is running.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, string ns, ResourceId id, JsonElement desired) {
        if (Document(objectJson) is not { } document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "ConfigMap" => document["data"]?[ConfigFile]?.GetValue<string>() == HaproxyConfig(desired),
            "Deployment" => MatchesDeployment(document, ns, id, desired),
            _ => false
        };
    }

    static bool MatchesDeployment(JsonObject document, string ns, ResourceId id, JsonElement desired) {
        if (document["spec"] is not JsonObject spec
            || spec["template"] is not JsonObject template) {
            return false;
        }

        var annotations = template["metadata"]?["annotations"];

        if (annotations?[LogicalSwitchAnnotation]?.GetValue<string>()
            != LogicalSwitchOf(ns, id, desired)) {
            return false;
        }

        if (annotations[IpPoolAnnotation]?.GetValue<string>() != PoolOf(desired)) {
            return false;
        }

        if (annotations[ConfigChecksumAnnotation]?.GetValue<string>() != ConfigHash(desired)) {
            return false;
        }

        return template["spec"]?["containers"] is JsonArray containers
            && containers.Count > 0
            && containers[0]?["image"]?.GetValue<string>() == Image(desired);
    }

    static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        }
        catch (JsonException) {
            return null;
        }
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster the proxy runs in.</param>
    /// <param name="subnet">The subnet of this network the proxy sits on.</param>
    /// <param name="frontendV4">The address the proxy answers on.</param>
    /// <param name="frontendV6">The IPv6 address the proxy also answers on, or empty.</param>
    /// <param name="frontendPort">The port the proxy listens on.</param>
    /// <param name="backendAddresses">The workload addresses, comma separated.</param>
    /// <param name="backendPort">The port every backend is reached on.</param>
    /// <param name="maxConnections">How many connections the frontend accepts at once.</param>
    /// <param name="preset">The sizing preset.</param>
    /// <param name="version">The HAProxy line.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>, for <c>CloudConsoles.Body</c>'s reason: the
    ///     read-back the conformance suite compares rebuilds a <see cref="SchemaKind.Nested" />
    ///     container from whichever leaf lands first, so a body carrying an empty object would not
    ///     survive it.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        string subnet = DefaultSubnet,
        string frontendV4 = DefaultFrontendV4,
        string frontendV6 = "",
        int frontendPort = 80,
        string backendAddresses = DefaultBackendAddresses,
        int backendPort = 8080,
        int maxConnections = 2000,
        string preset = DefaultPreset,
        string version = DefaultVersion,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["subnet"] = subnet,
                ["frontend"] = new JsonObject {
                    ["v4"] = frontendV4, ["v6"] = frontendV6, ["port"] = frontendPort
                },
                ["backend"] = new JsonObject {
                    ["addresses"] = backendAddresses, ["port"] = backendPort
                },
                ["health"] = new JsonObject {
                    ["intervalSeconds"] = 5, ["unhealthyAfter"] = 3, ["healthyAfter"] = 2
                },
                ["limits"] = new JsonObject { ["maxConnections"] = maxConnections },
                ["sizing"] = new JsonObject { ["preset"] = preset },
                ["version"] = version
            }
        }.ToJsonString();

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Properties(JsonElement desired) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
            ? properties
            : null;

    static string Text(JsonElement desired, string name, string fallback) =>
        Properties(desired) is { } properties
        && properties.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    static string Nested(JsonElement desired, string parent, string name, string fallback) =>
        Section(desired, parent) is { } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    static int Whole(JsonElement desired, string parent, string name, int fallback) =>
        Section(desired, parent) is { } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt32(out var number)
            ? number
            : fallback;

    static JsonElement? Section(JsonElement desired, string parent) =>
        Properties(desired) is { } properties
        && properties.TryGetProperty(parent, out var section)
        && section.ValueKind is JsonValueKind.Object
            ? section
            : null;
}
