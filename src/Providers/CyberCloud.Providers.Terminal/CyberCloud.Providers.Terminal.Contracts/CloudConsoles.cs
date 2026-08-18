using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Terminal.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Terminal/consoles</c> — the browser shell of
///     docs/plan/19 § <c>CyberCloud.Terminal/consoles</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE PRODUCT IS AN INTERACTIVE SESSION AND A RESOURCE IS A CONVERGED OBJECT, AND
///         EVERY DECISION BELOW IS THE SEAM BETWEEN THOSE TWO SENTENCES.</b> Eleven families before
///         this one reconcile a resource to a desired state and leave it: the tenant buys a thing that
///         keeps existing. A console is created, attached to, idled out and re-created, and the states
///         a person cares about are <i>attached</i> and <i>idle</i> — neither of which a reconciler
///         may drive, because docs/plan/19 § The pod makes idle reclamation the design constraint:
///         <i>"A million users with an idle shell pod each is a million idle pods."</i>
///     </para>
///     <para>
///         <b>§ What is a resource and what is a session.</b> The split is the whole design and it is
///         drawn at the object level rather than described:
///     </para>
///     <list type="table">
///         <listheader>
///             <term>Object</term>
///             <description>Who applies it, and what its absence means</description>
///         </listheader>
///         <item>
///             <term><see cref="HomeClaimRef" /> — the <c>PersistentVolumeClaim</c></term>
///             <description>
///                 The reconciler. Absent means the console is not provisioned. It is the only thing
///                 on this row that survives everything.
///             </description>
///         </item>
///         <item>
///             <term><see cref="ServiceAccountRef" /></term>
///             <description>The reconciler. Absent means the pod has no identity to run under.</description>
///         </item>
///         <item>
///             <term><see cref="NetworkPolicyRef" /></term>
///             <description>
///                 The reconciler. ⚠ Absent means the shell reaches <b>everything</b> the cluster's
///                 default posture allows — see <see cref="NetworkPolicyJson" />, which is where this
///                 row's unsafe-default question is decided.
///             </description>
///         </item>
///         <item>
///             <term><see cref="PodRef" /></term>
///             <description>
///                 ⚠ <b>The <c>connect</c> action, and NEVER the reconciler.</b> Absent is the
///                 <i>ordinary, correct, billable-to-nobody</i> state of a console nobody is using. A
///                 reconciler that applied it would re-create the pod on the next reminder after every
///                 idle reclaim, forever, which is the exact cost docs/plan/19 says the design exists
///                 to avoid — and the drift scanner would report each reclaim as drift and repair it.
///             </description>
///         </item>
///     </list>
///     <para>
///         ⚠ <b>SO <see cref="ReconcileOutcome.Converged" /> ON THIS TYPE MEANS "THE CONSOLE CAN BE
///         ATTACHED TO", NOT "SOMEBODY IS ATTACHED".</b> The reconciler reads back the three durable
///         objects and nothing else. It reads no <c>status</c> anywhere, which is deliberate and is
///         the lesson of the one type that does: <c>ManagedClusterReconciler</c> is the only
///         reconciler in the tree whose <c>Converged</c> reads a <c>status</c>, and doing so named a
///         real hole — <c>ClusterReadinessKind.NotReported</c>, an object no controller has written a
///         status onto, which converges because neither conformance harness can produce anything else.
///     </para>
///     <para>
///         ⚠ <b>A <c>PersistentVolumeClaim</c> WOULD HAVE WALKED INTO THAT HOLE ON ITS FIRST DAY, AND
///         THE SUBSTRATE IS WHY.</b> The obvious readiness gate for this row is
///         <c>status.phase == "Bound"</c>. It is unimplementable: the default provisioner in k3s —
///         which is what the cluster-backed suite runs, and what docs/plan/09 stands the bootstrap
///         cluster up on — binds with <c>volumeBindingMode: WaitForFirstConsumer</c>, so a claim with
///         no pod scheduled against it stays <c>Pending</c> <b>forever and correctly</b>. On this row
///         there is deliberately no pod until somebody attaches. A <c>Bound</c> gate would therefore
///         make every console in every environment hang in <c>InProgress</c> until its first
///         <c>connect</c>, and the first <c>connect</c> is refused for a console that has not
///         converged. That is a deadlock, not a stricter check, and it is why the read-back below is a
///         spec read-back like the other ten families' and not a status read.
///         <c>ConsoleReconcilerTests.ConvergedDoesNotWaitForTheHomeVolumeToBind</c> pins it, and
///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>,
///         <c>converged-is-not-attachable</c>, records what is left unproven.
///     </para>
///     <para>
///         ⚠ <b>ONE CONSOLE IS ONE PRINCIPAL, AND docs/plan/19 IS CORRECTED ON HOW.</b> That document
///         says the pod runs as <i>"the invoking user's managed identity"</i> and gives the home volume
///         the name <c>home-{userId}</c>. Neither is expressible: a resource in this platform is
///         addressed by (tenant, subscription, resource group, name) and has <b>no user dimension at
///         all</b> — docs/plan/06 § The hierarchy — and an action handler cannot see who invoked it,
///         because <see cref="ActionContext" /> carries no <c>CallerContext</c>. So the identity is a
///         <i>property of the console</i>, <see cref="PrincipalIdPointer" />, immutable after create,
///         and the home volume is the console's own. What that buys is that "the shell runs as you" is
///         true by construction for a console you created; what it does not buy is enforcement that
///         the caller of <c>connect</c> is that principal, which is
///         <c>conformance.yaml § owed</c>, <c>connect-cannot-see-its-caller</c>. ⚠ Until that closes,
///         the honest description of the ReBAC posture is: <b>anyone who may <c>connect</c> to a
///         console gets a shell holding that console's identity.</b> One console per user is a
///         convention the portal follows, not a fact the schema enforces.
///     </para>
///     <para>
///         ⚠ <b>WHAT THE PORTAL NEEDS FROM THIS TYPE IS <see cref="ConnectResponse" /> AND NOTHING
///         ELSE.</b> docs/plan/20 § The pages that are not generated budgets 0.4 EM for
///         <c>xterm.js</c> in a dockable panel and nobody had written down what it talks to. It is:
///         <c>POST …/connect</c> → the five fields of <see cref="ConnectResponse" />, then
///         <c>/hubs/terminal</c> with the returned <see cref="SessionIdField" />. The hub is already
///         mapped and already refuses by name (<c>TerminalHub.SendAsync</c>); the byte path behind it
///         is docs/plan/19's session grain and is owed.
///     </para>
/// </remarks>
public static class CloudConsoles {
    /// <summary>The provider namespace. docs/plan/19's own spelling.</summary>
    public const string ProviderNamespace = "CyberCloud.Terminal";

    /// <summary>The type path, relative to <see cref="ProviderNamespace" />.</summary>
    public const string TypePath = "consoles";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type's configuration surface is generated into. ADR-012's fifth surface.</summary>
    public const string ChartName = "managed/cloud-shell";

    /// <summary>Where the cluster this console runs in is named.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>Where the identity the shell runs as is named.</summary>
    /// <remarks>
    ///     ⚠ Immutable, and that is the load-bearing half. A console whose principal could be changed
    ///     is a console whose audit trail says one thing and whose live shell holds another.
    /// </remarks>
    public const string PrincipalIdPointer = "/properties/identity/principalId";

    /// <summary>The type, as the registry and every generated surface spell it.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The actions ───────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     <c>POST …/consoles/{name}/connect</c> — start or re-join the session and describe it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>SYNCHRONOUS WITH A HANDLER, AND THE OTHER TWO KINDS ARE BOTH WRONG HERE RATHER
    ///         THAN MERELY WORSE.</b> A <b>long-running</b> action answers <c>202</c> and re-runs the
    ///         type's reconciler through <c>OperationGrain</c> — which on this type would converge the
    ///         three durable objects and never start a pod, so the caller would poll an operation to
    ///         success and still have no shell. A <b>synchronous action with no handler</b> is refused
    ///         by name by the dispatcher, which is the right answer for something not yet built and
    ///         the wrong one for something that is. What a terminal needs is an answer <i>now</i>: a
    ///         person clicked a panel open, and a 202 with a polling loop in front of a text cursor is
    ///         not a terminal.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is not <c>secret: true</c>.</b> Nothing it returns is a credential — the shell's
    ///         authority is the pod's identity, which is never handed to the caller. Declaring it
    ///         secret would keep the response off the operation record for no reason and would tell
    ///         every generated surface something untrue about it.
    ///     </para>
    /// </remarks>
    public const string ConnectAction = "connect";

    /// <summary>The permission <see cref="ConnectAction" /> requires.</summary>
    /// <remarks>
    ///     ⚠ Its own permission rather than <c>read</c>, because attaching to a shell that holds an
    ///     identity is not reading a resource. A Reader on a resource group must not inherit a
    ///     terminal inside it — docs/plan/07's roles are the reason this is a separate string.
    /// </remarks>
    public const string ConnectPermission = "connect";

    /// <summary><c>POST …/consoles/{name}/terminate</c> — end the session now.</summary>
    /// <remarks>
    ///     ⚠ <b>The manual half of the idle policy, and it exists because the automatic half is
    ///     owed.</b> docs/plan/19 gives the pod a 20-minute idle timeout enforced by the session
    ///     grain, and that grain is not built. Until it is, this action is the only thing in the
    ///     platform that can stop a console's pod on purpose — <see cref="MaxDurationHoursPointer" />
    ///     is the only one that can stop it by accident. Both are named on this type rather than left
    ///     to a sweeper nobody has written.
    /// </remarks>
    public const string TerminateAction = "terminate";

    /// <summary>The permission <see cref="TerminateAction" /> requires.</summary>
    /// <remarks>
    ///     ⚠ The same permission <see cref="ConnectAction" /> takes, and not <c>write</c> or
    ///     <c>delete</c>. Ending your own session is not a change to the resource and must not require
    ///     a right that would also let you delete the home volume; and anyone who can open a shell can
    ///     already close it by other means.
    /// </remarks>
    public const string TerminatePermission = ConnectPermission;

    // ── The objects a console becomes ─────────────────────────────────────────────────────────

    /// <summary>The home volume's kind.</summary>
    public static GroupVersionKind ClaimKind { get; } =
        new() { Group = "", Version = "v1", Kind = "PersistentVolumeClaim", Plural = "persistentvolumeclaims" };

    /// <summary>The identity the pod runs under, inside the cluster.</summary>
    public static GroupVersionKind ServiceAccountKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ServiceAccount", Plural = "serviceaccounts" };

    /// <summary>What the shell may reach.</summary>
    public static GroupVersionKind NetworkPolicyKind { get; } =
        new() { Group = "networking.k8s.io", Version = "v1", Kind = "NetworkPolicy", Plural = "networkpolicies" };

    /// <summary>The shell itself. ⚠ Applied by the <c>connect</c> handler, never by the reconciler.</summary>
    public static GroupVersionKind PodKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Pod", Plural = "pods" };

    /// <summary>The home volume's object name.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string HomeClaimName(string name) => name + "-home";

    /// <summary>The shell pod's object name, and the service account's.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>ONE POD PER CONSOLE, NAMED DETERMINISTICALLY, AND THAT IS WHAT MAKES <c>connect</c>
    ///     IDEMPOTENT.</b> A second <c>connect</c> while a shell is running applies the same object
    ///     and gets it back unchanged, so re-joining a live session and starting a new one are the
    ///     same call. A generated per-attach name would make the second browser tab a second pod and a
    ///     second bill.
    /// </remarks>
    public static string ShellName(string name) => name + "-shell";

    /// <summary>The home volume.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef HomeClaimRef(string ns, string name) =>
        new() { Kind = ClaimKind, Namespace = ns, Name = HomeClaimName(name) };

    /// <summary>The pod's service account.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ServiceAccountRef(string ns, string name) =>
        new() { Kind = ServiceAccountKind, Namespace = ns, Name = ShellName(name) };

    /// <summary>The network policy over the shell pod.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef NetworkPolicyRef(string ns, string name) =>
        new() { Kind = NetworkPolicyKind, Namespace = ns, Name = ShellName(name) };

    /// <summary>The shell pod. ⚠ Not part of convergence — see this class's remarks.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef PodRef(string ns, string name) =>
        new() { Kind = PodKind, Namespace = ns, Name = ShellName(name) };

    /// <summary>The three objects a converged console owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ The claim first and the policy last, and the order is the security argument rather than a
    ///     dependency: the last object applied is the one that constrains, so a pass that dies half
    ///     way leaves a console with a home volume and <b>no way to start a shell</b> rather than a
    ///     shell with no constraint. The pod is applied by a different code path entirely and is
    ///     refused unless all three of these read back.
    /// </remarks>
    public static ImmutableArray<ObjectRef> Objects(string ns, string name) =>
        [HomeClaimRef(ns, name), ServiceAccountRef(ns, name), NetworkPolicyRef(ns, name)];

    // ── The image ─────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The repository the shell image is published to. docs/plan/19 § The image.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>NOTHING IN THIS REPOSITORY BUILDS THIS IMAGE, AND THE BUILD TARGET THAT WOULD IS
    ///         BOTH UNABLE AND FORBIDDEN TO.</b> <c>build/Build.Images.cs</c> publishes
    ///         <b>everything under <c>src/Hosts</c> that is publishable</b>, through
    ///         <c>dotnet publish -t:PublishContainer</c>, and its header says in as many words:
    ///         <i>"THERE IS NO DOCKERFILE IN THIS REPOSITORY, AND THERE MUST NOT BE ONE."</i> That
    ///         rule was written about .NET host images and is right about them. This is the first
    ///         image in the tree that is <b>not a .NET application</b> — docs/plan/19 § The image asks
    ///         for <c>psql</c>, <c>kubectl</c>, <c>opentofu</c>, six language runtimes and
    ///         <c>tcpdump</c> in ~2.5 GB — and the SDK's container tooling composes layers from a
    ///         published .NET output and cannot express any of it. So the shell image needs a second
    ///         build route, and choosing one is a platform decision rather than a provider's:
    ///         <c>charts/managed/cloud-shell/conformance.yaml § owed</c>, <c>no-image-pipeline</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What is decided here is only the reference, and it is a DIGEST rather than a
    ///         tag</b>, because docs/plan/18 § Platform security says <i>"a pinned digest, never a
    ///         tag"</i> and because a shell image resolved by tag would let a registry change what
    ///         every tenant's terminal is, silently, between two attaches of one session.
    ///     </para>
    /// </remarks>
    public const string ImageRepository = "cybercloud/cloud-shell";

    /// <summary>The two variants of docs/plan/19 § The image, and the digest each resolves to.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE DIGESTS ARE PLACEHOLDERS AND THEY ARE SPELLED SO THAT NOTHING CAN MISTAKE
    ///         THEM FOR REAL ONES.</b> There is no image to take a digest of — see
    ///         <see cref="ImageRepository" /> — and a plausible-looking 64 hex characters here would
    ///         be a reference that fails to pull at the worst possible moment with nothing in the tree
    ///         to say why. <c>ConsoleDeclarationTests.TheImageDigestsAreVisiblyPlaceholders</c> asserts
    ///         they stay that way until the pipeline exists, so this cannot be forgotten into
    ///         production.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>minimal</c> exists for a reason about people rather than about bytes.</b>
    ///         docs/plan/19: <i>"a 40-second cold start for someone who wants to run one command is
    ///         the wrong trade"</i>. It is not the default, because a shell that lacks the tool you
    ///         need is worthless and the tenant who wants one command knows they do.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, string> ImageDigests { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["default"] = "sha256:0000000000000000000000000000000000000000000000000000000000000000",
            ["minimal"] = "sha256:1111111111111111111111111111111111111111111111111111111111111111"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The image reference for a body's chosen variant.</summary>
    /// <param name="desired">The desired body.</param>
    public static string Image(JsonElement desired) =>
        ImageRepository + "@" + ImageDigests[ImageVariant(desired)];

    // ── Sizing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The Kubernetes quantity grammar. Pointed at, never copied.</summary>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <summary>
    ///     The three sizes a console is offered, and what each reserves.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>THREE, AND THE CEILING IS docs/plan/19's RATHER THAN A ROUND NUMBER.</b> That
    ///     document's § The pod gives a console <i>"0.5–2 vCPU, 1–4 GB"</i>, and these three span
    ///     exactly that and stop. There is deliberately no larger preset: a shell is a place to type
    ///     from, and a tenant who needs sixteen cores needs
    ///     <c>CyberCloud.ContainerService/managedClusters</c> and a job, not a terminal that bills like
    ///     one. Every other family in the catalogue exposes an eight-row <c>m1</c>/<c>s1</c> ladder;
    ///     this row's short one is the decision.
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["c1.small"] = ("500m", "1Gi"),
            ["c1.medium"] = ("1", "2Gi"),
            ["c1.large"] = ("2", "4Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The CPU and memory a body's preset asks for.</summary>
    /// <param name="desired">The desired body.</param>
    public static (string Cpu, string Memory) Resources(JsonElement desired) =>
        Presets.TryGetValue(SizingPreset(desired), out var found) ? found : Presets[DefaultPreset];

    /// <summary>
    ///     The ephemeral storage a shell may use outside its home volume.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A CONSTANT AND NOT A SETTING, AND IT IS THE ONLY THING BETWEEN A <c>git clone</c> IN
    ///     <c>/tmp</c> AND A FULL NODE.</b> docs/plan/19 § The pod asks for "ephemeral storage capped"
    ///     without a number. It is not a tenant choice because the failure it prevents is the
    ///     <i>node's</i> rather than the tenant's: an unbounded <c>emptyDir</c> fills the kubelet's
    ///     disk and evicts every pod on the machine, including other tenants'.
    /// </remarks>
    public const string EphemeralStorageLimit = "2Gi";

    // ── The session policy ────────────────────────────────────────────────────────────────────

    /// <summary>Where the idle timeout is named.</summary>
    public const string IdleTimeoutMinutesPointer = "/properties/session/idleTimeoutMinutes";

    /// <summary>Where the hard cap is named.</summary>
    public const string MaxDurationHoursPointer = "/properties/session/maxDurationHours";

    /// <summary>
    ///     The annotation the idle reaper reads the timeout from.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>AN ANNOTATION ON THE POD RATHER THAN A NUMBER THE REAPER LOOKS UP, BECAUSE THE REAPER
    ///     DOES NOT EXIST YET AND SOMETHING HAD TO OUTLIVE THAT.</b> A sweeper that had to resolve
    ///     every pod back to a resource body to learn its timeout would be a sweeper that cannot run
    ///     without the resource manager; carrying the number on the object makes the reclaim decision
    ///     a property of the cluster, readable by <c>kubectl</c> and by whatever eventually sweeps.
    /// </remarks>
    public const string IdleTimeoutAnnotation = "cybercloud.io/idle-timeout-seconds";

    /// <summary>The annotation that says whether this session is being recorded.</summary>
    /// <remarks>
    ///     ⚠ On the pod so that it is visible from inside the cluster to an operator holding
    ///     <c>kubectl</c> and nothing else. docs/plan/19 § Auditing wants recording to be <i>"loud in
    ///     the UI when it is on"</i>; this is the same fact one layer down, where a person debugging a
    ///     tenant's shell can see it without asking the API.
    /// </remarks>
    public const string RecordingAnnotation = "cybercloud.io/session-recording";

    /// <summary>How long a shell may run before the kubelet stops it, in seconds.</summary>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b>THIS IS THE ONLY HALF OF docs/plan/19's SESSION POLICY THAT IS ENFORCED BY ANYTHING
    ///     TODAY, AND IT IS ENFORCED BY THE KUBELET.</b> Rendered into <c>spec.activeDeadlineSeconds</c>,
    ///     it is a promise the cluster keeps with no platform component running at all: a console whose
    ///     session grain died, whose silo moved, or whose reaper was never written still stops burning
    ///     CPU at the cap. The <i>idle</i> timeout cannot be expressed this way and is not — a kubelet
    ///     cannot know about keystrokes — which is why the two numbers are enforced in two different
    ///     places and only one of them is real yet.
    /// </remarks>
    public static int MaxDurationSeconds(JsonElement desired) =>
        Number(desired, "session", "maxDurationHours", DefaultMaxDurationHours) * 3600;

    /// <summary>How long a shell may sit idle before it should be reclaimed, in seconds.</summary>
    /// <param name="desired">The desired body.</param>
    public static int IdleTimeoutSeconds(JsonElement desired) =>
        Number(desired, "session", "idleTimeoutMinutes", DefaultIdleTimeoutMinutes) * 60;

    /// <summary>The two egress postures.</summary>
    /// <remarks>
    ///     ⚠ <b>DECLARED ABOVE <see cref="Schema2026" /> AND IT HAS TO BE, WHICH IS A C# HAZARD RATHER
    ///     THAN A STYLE PREFERENCE.</b> Static field initialisers run in <b>declaration order</b>, so
    ///     a collection this schema's <c>AllowedValues</c> reads must be initialised first. It was
    ///     declared below, once, and the consequence was not a crash: the enum was silently
    ///     <b>empty</b>, so the schema accepted any string at
    ///     <c>/properties/network/egress</c> — a console could be created asking for a posture nothing
    ///     renders, and <c>NetworkPolicyJson</c>'s <c>Internet</c> comparison would quietly fall
    ///     through to the three-rule form. ⚠ <b>Nothing in C# warns, and no test caught it</b>: the
    ///     assertion that did was <c>./build.sh Charts</c>, which emitted a <c>@param</c> block with
    ///     no <c>@enum</c> line and failed on the diff. A reformat that reorders this file can
    ///     reintroduce it.
    /// </remarks>
    public static ImmutableArray<string> EgressModes { get; } = ["Internet", "TenantOnly"];

    /// <summary>Whether this console records what is typed into it.</summary>
    /// <param name="desired">The desired body.</param>
    public static bool SessionRecording(JsonElement desired) =>
        Flag(desired, "audit", "sessionRecording", false);

    // ── Reading the body ──────────────────────────────────────────────────────────────────────

    /// <summary>The image variant a body chose.</summary>
    /// <param name="desired">The desired body.</param>
    public static string ImageVariant(JsonElement desired) {
        var variant = Text(desired, "image", "variant", DefaultVariant);
        return ImageDigests.ContainsKey(variant) ? variant : DefaultVariant;
    }

    /// <summary>The sizing preset a body chose.</summary>
    /// <param name="desired">The desired body.</param>
    public static string SizingPreset(JsonElement desired) =>
        Text(desired, "sizing", "preset", DefaultPreset);

    /// <summary>The home volume's size.</summary>
    /// <param name="desired">The desired body.</param>
    public static string HomeSize(JsonElement desired) => Text(desired, "home", "size", DefaultHomeSize);

    /// <summary>How long the home volume is kept after the console was last attached to.</summary>
    /// <param name="desired">The desired body.</param>
    public static int HomeRetentionDays(JsonElement desired) =>
        Number(desired, "home", "retentionDays", DefaultRetentionDays);

    /// <summary>Whether the shell may reach anything outside the cluster.</summary>
    /// <param name="desired">The desired body.</param>
    public static string EgressMode(JsonElement desired) =>
        Text(desired, "network", "egress", DefaultEgress);

    /// <summary>The principal the shell runs as.</summary>
    /// <param name="desired">The desired body.</param>
    public static string PrincipalId(JsonElement desired) =>
        Text(desired, "identity", "principalId", string.Empty);

    // ── The schema ────────────────────────────────────────────────────────────────────────────

    /// <summary>The console's body at <see cref="V2026" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ADR-010 § Which end authors the schema: this is the author, and
    ///         <c>charts/managed/cloud-shell/values.yaml</c>'s non-<c>@internal</c> <c>@param</c> block
    ///         is generated from it by <c>./build.sh Charts</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE ABSENCES ARE THE INTERESTING PART ON THIS ROW.</b> There is no
    ///         <c>image.repository</c> or <c>image.digest</c> — a tenant choosing what runs in a pod
    ///         holding their own identity is the whole attack; there is no <c>network.allowedCidrs</c>
    ///         — the policy this row renders is not expressible as a list a tenant supplies, and a
    ///         half-expressible one would read as a guarantee (see <see cref="NetworkPolicyJson" />);
    ///         and there is no <c>session.idleTimeoutMinutes: 0</c> meaning "never", which is why
    ///         that property has a <b>maximum</b> as well as a minimum.
    ///     </para>
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the console is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The console's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the shell and its home volume."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },

                // ── Identity ───────────────────────────────────────────────────────────────────
                new(
                    "/properties/identity",
                    SchemaKind.Nested,
                    Description: "Who the shell runs as."
                ),
                new(
                    PrincipalIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The managed identity every command in this shell acts as. It may "
                    + "not be changed: a console whose identity moved would have an audit trail "
                    + "describing a shell that no longer exists."
                ) {
                    Format = SchemaFormat.Uuid,
                    Immutable = true
                },

                // ── The image ──────────────────────────────────────────────────────────────────
                new("/properties/image", SchemaKind.Nested, Description: "Which shell image to run."),
                new(
                    "/properties/image/variant",
                    SchemaKind.Text,
                    Description: "default carries every tool in docs/plan/19's table and takes about "
                    + "40 seconds to pull cold; minimal carries shells, editors, cyc and kubectl and "
                    + "starts in about 8. The repository and the digest are the platform's."
                ) {
                    AllowedValues = [.. ImageDigests.Keys.Order(StringComparer.Ordinal)],
                    DefaultJson = "\"" + DefaultVariant + "\""
                },

                // ── Sizing ─────────────────────────────────────────────────────────────────────
                new("/properties/sizing", SchemaKind.Nested, Description: "CPU and memory for the shell."),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "How much a shell gets. The ladder stops at 2 vCPU and 4 GiB, which "
                    + "is docs/plan/19's ceiling for this row — a workload that needs more needs a "
                    + "cluster and a job rather than a terminal."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"" + DefaultPreset + "\""
                },

                // ── The home volume ────────────────────────────────────────────────────────────
                new(
                    "/properties/home",
                    SchemaKind.Nested,
                    Description: "The persistent home directory. The only part of a console that "
                    + "survives an idle reclaim."
                ),
                new(
                    "/properties/home/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The size of $HOME. It grows and never shrinks: a PersistentVolumeClaim "
                    + "refuses a decrease at the API."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"" + DefaultHomeSize + "\"",
                    ExampleJson = "\"5Gi\""
                },
                new(
                    "/properties/home/retentionDays",
                    SchemaKind.WholeNumber,
                    Description: "How long $HOME is kept after the console was last attached to. "
                    + "⚠ Nothing sweeps on it yet — it is carried so that a console created today is "
                    + "swept correctly by whatever does."
                ) {
                    Minimum = 1,
                    Maximum = 365,
                    DefaultJson = "90"
                },

                // ── The session ────────────────────────────────────────────────────────────────
                new(
                    "/properties/session",
                    SchemaKind.Nested,
                    Description: "When a shell stops. Both numbers are ceilings a tenant may lower "
                    + "and neither may be turned off."
                ),
                new(
                    IdleTimeoutMinutesPointer,
                    SchemaKind.WholeNumber,
                    Description: "How long a shell may sit with nobody typing into it before it is "
                    + "reclaimed. The home volume survives; the pod does not. ⚠ There is no value "
                    + "meaning never — an idle terminal is a tenant paying for something they closed."
                ) {
                    Minimum = 5,
                    Maximum = 120,
                    DefaultJson = "20"
                },
                new(
                    MaxDurationHoursPointer,
                    SchemaKind.WholeNumber,
                    Description: "The longest a single shell may run, busy or not. Enforced by the "
                    + "kubelet through the pod's own deadline, so it holds even when nothing of this "
                    + "platform is running."
                ) {
                    Minimum = 1,
                    Maximum = 24,
                    DefaultJson = "8"
                },

                // ── The network ────────────────────────────────────────────────────────────────
                new(
                    "/properties/network",
                    SchemaKind.Nested,
                    Description: "What the shell may reach. Nothing may ever reach the shell."
                ),
                new(
                    "/properties/network/egress",
                    SchemaKind.Text,
                    Description: "Internet lets the shell reach public addresses as well as this "
                    + "subscription's own workloads and DNS — a shell that cannot git clone is not a "
                    + "shell. TenantOnly removes the public half and leaves the rest."
                ) {
                    AllowedValues = EgressModes,
                    DefaultJson = "\"" + DefaultEgress + "\""
                },

                // ── Auditing ───────────────────────────────────────────────────────────────────
                new(
                    "/properties/audit",
                    SchemaKind.Nested,
                    Description: "docs/plan/19 § Auditing. Who, when, from where, which subscription "
                    + "and for how long is always recorded and is not a setting."
                ),
                new(
                    "/properties/audit/sessionRecording",
                    SchemaKind.Boolean,
                    Description: "Whether everything typed into and printed by this shell is recorded. "
                    + "⚠ Off by default and immutable afterwards: a shell contains secrets, a keystroke "
                    + "log is a liability, and a recording that could be switched off mid-life would be "
                    + "worth nothing to the compliance requirement it exists for."
                ) {
                    Immutable = true,
                    DefaultJson = "false"
                }
            ]
        );

    /// <summary>What <see cref="ConnectAction" /> answers with.</summary>
    /// <remarks>
    ///     ⚠ <b>THIS IS THE CONTRACT docs/plan/20's TERMINAL PANEL IS BUILT AGAINST AND NOBODY HAD
    ///     WRITTEN IT DOWN.</b> Five fields, and each is there because the panel cannot proceed
    ///     without it: the hub to open, the session to name on it, and three numbers the panel must
    ///     show a person rather than discover by being disconnected.
    /// </remarks>
    public static ResourceSchema ConnectResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/" + SessionIdField,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The session to name on the terminal hub. ⚠ It is the shell pod's own "
                    + "UID, so it changes when a reclaimed console is re-created — which is exactly "
                    + "when a client's replay buffer has become meaningless."
                ),
                new(
                    "/hub",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The SignalR hub path to open. docs/plan/10 § SignalR."
                ),
                new(
                    "/state",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Starting while the shell is still being scheduled, Ready once it is "
                    + "running. A client opens the hub either way and sees output when there is some."
                ),
                new(
                    "/idleTimeoutSeconds",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How long this shell may sit idle before it is reclaimed. Shown to "
                    + "the person, because a terminal that vanishes without warning reads as a bug."
                ),
                new(
                    "/maxDurationSeconds",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How long this shell may run in total before the kubelet stops it."
                ),
                new(
                    "/recording",
                    SchemaKind.Boolean,
                    Required: true,
                    Description: "Whether this session is being recorded. ⚠ docs/plan/19 § Auditing "
                    + "requires the portal to be loud about this, which is why it is on the connect "
                    + "response rather than only on the resource body: a panel that had to fetch the "
                    + "resource to find out would render one frame of a terminal that lies."
                )
            ]
        );

    /// <summary>What <see cref="TerminateAction" /> answers with.</summary>
    public static ResourceSchema TerminateResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/terminated",
                    SchemaKind.Boolean,
                    Required: true,
                    Description: "True when this call stopped a running shell, false when there was "
                    + "none. ⚠ Both are success: the caller's goal is that no shell is running."
                )
            ]
        );

    /// <summary>The field <see cref="ConnectResponse" /> names the session in.</summary>
    public const string SessionIdField = "sessionId";

    /// <summary>The hub <see cref="ConnectResponse" /> sends a client to.</summary>
    /// <remarks>
    ///     ⚠ <b>A CONSTANT HERE AND A CONSTANT IN <c>HubNames.Terminal</c>, AND THEY MAY NOT BE ONE.</b>
    ///     Routing <c>/hubs/{name}</c> is the gateway's job and nothing else in the platform decides
    ///     what a hub is — <c>HubNames</c>' own remarks — and a provider assembly may not reference a
    ///     host. So the string is duplicated on purpose and
    ///     <c>ConsoleDeclarationTests.TheHubPathIsTheOneTheGatewayMaps</c> is what keeps the two in
    ///     step, as a literal, the way every short-name check in this tree is done.
    /// </remarks>
    public const string HubPath = "/hubs/terminal";

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── Rendering ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The home volume, as applied.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b>NO <c>storageClassName</c>, AND THAT IS A DECISION WITH A CONSEQUENCE THIS ROW CANNOT
    ///     ESCAPE.</b> Omitting it takes the cluster's default class, which is the only portable
    ///     answer — the platform does not install a storage class and docs/plan/09's bundle does not
    ///     name one. The consequence is that <b>a console's delete is irreversible whatever this
    ///     provider does</b>: <c>persistentVolumeReclaimPolicy</c> lives on the PersistentVolume and
    ///     is defaulted from the StorageClass, so a claim cannot ask for its bytes to outlive it. That
    ///     is the counter-argument to this type declining soft delete, and it is recorded rather than
    ///     answered — <c>conformance.yaml § owed</c>, <c>delete-takes-the-home-directory</c>.
    /// </remarks>
    public static string HomeClaimJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = HomeClaimName(name) },
            ["spec"] = new JsonObject {
                // ⚠ ReadWriteOnce and not ReadWriteMany, because there is one pod by construction —
                // see ShellName. RWX would be a promise about concurrent shells this row does not make
                // and most storage classes cannot keep.
                ["accessModes"] = new JsonArray { "ReadWriteOnce" },
                ["resources"] = new JsonObject {
                    ["requests"] = new JsonObject { ["storage"] = HomeSize(desired) }
                }
            }
        }.ToJsonString();
    }

    /// <summary>The identity the shell pod runs under inside the cluster.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     ⚠ <b><c>automountServiceAccountToken: false</c> IS THE WHOLE OBJECT AND IT IS THE ANSWER TO
    ///     "WHAT CAN THE POD REACH BY DEFAULT".</b> A pod with no <c>serviceAccountName</c> runs as
    ///     the namespace's <c>default</c> account with its token mounted at
    ///     <c>/var/run/secrets/kubernetes.io/serviceaccount</c>, and a shell containing
    ///     <c>kubectl</c> would find it on the first tab-completion. Whatever RBAC that account has
    ///     — and in a cluster where somebody once ran <c>kubectl create clusterrolebinding … --serviceaccount=…:default</c>
    ///     it is everything — becomes the tenant's. So this account exists in order to be empty, it is
    ///     named on the pod, and the mount is refused in <b>both</b> places the API allows it to be
    ///     refused, because the pod-level field is the one that actually holds and the account-level
    ///     one is the one an auditor reads.
    ///     <para>
    ///         ⚠ It carries the console's principal as an annotation so that an operator reading the
    ///         cluster can tell whose shell a pod is without resolving a resource id through an API
    ///         they may not have. That is the in-cluster half of docs/plan/19 § Auditing's "who".
    ///     </para>
    /// </remarks>
    public static string ServiceAccountJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var account = new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ShellName(name) },
            ["automountServiceAccountToken"] = false
        };

        if (PrincipalId(desired) is { Length: > 0 } principal) {
            account["metadata"]!["annotations"] = new JsonObject { [PrincipalAnnotation] = principal };
        }

        return account.ToJsonString();
    }

    /// <summary>The annotation naming the identity a console's shell acts as.</summary>
    public const string PrincipalAnnotation = "cybercloud.io/principal-id";

    /// <summary>What the shell may reach, and what may reach it.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="tenantId">The owning tenant, for the namespace selector.</param>
    /// <param name="ns">The console's own namespace.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS FUNCTION IS THIS ROW'S ANSWER TO "WHAT CAN THE POD REACH WHEN THE TENANT ASKS
    ///         FOR NOTHING", AND THE ANSWER IS ESTABLISHED HERE RATHER THAN INTENDED ELSEWHERE.</b>
    ///         With an empty <c>network</c> block the rendered policy is: <b>no ingress at all</b>;
    ///         egress to this console's own namespace; egress to any namespace labelled with this
    ///         tenant's id; egress to <c>kube-dns</c>; and egress to public addresses with RFC 1918,
    ///         CGNAT and link-local removed. Nothing else. In particular the shell cannot reach the
    ///         platform's own namespaces, another tenant's workloads, or <c>169.254.169.254</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>docs/plan/19 ASKS FOR "a NetworkPolicy denying access to the platform's own
    ///         namespaces" AND THAT IS NOT A POLICY THAT CAN BE WRITTEN.</b> A NetworkPolicy's
    ///         <c>egress</c> rules are an allow-list — there is no <c>deny</c> — so "everything except
    ///         those namespaces" has no spelling: a rule allowing <c>0.0.0.0/0</c> allows the
    ///         platform's pods too, because their addresses are in the cluster CIDR, and
    ///         <c>ipBlock</c> may not be combined with a <c>namespaceSelector</c> in one peer. The
    ///         document's requirement is therefore met the only way it can be — <b>by allowing the
    ///         tenant's own namespaces positively and excising every private range from the public
    ///         rule</b>, so the platform is excluded by construction rather than by exception. The
    ///         correction is worth stating because the natural reading of that sentence produces a
    ///         policy that silently allows what it was written to forbid.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE TENANT-WIDE RULE MATCHES NOTHING TODAY AND IS RENDERED ANYWAY.</b> It selects
    ///         namespaces carrying <c>cybercloud.io/tenant-id</c>, and <b>nothing in this repository
    ///         creates or labels a namespace</b> — <c>ReconcileDriver.NamespaceFor</c> derives a name
    ///         and every reconciler assumes it exists. So today the reach is the console's own
    ///         resource group and no further, which means <b>docs/plan/24's M1 exit story does not
    ///         work across resource groups</b>: <c>psql</c> into a Postgres server in a different
    ///         group is refused by this policy. It fails closed, which is the right direction to be
    ///         wrong in, and the rule is rendered now so that the day namespaces are labelled every
    ///         existing console gains the reach with no api-version change.
    ///         <c>conformance.yaml § owed</c>, <c>namespaces-are-not-labelled</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>THE PRIVATE-RANGE EXCEPTIONS ASSUME THE CLUSTER'S OWN CIDRs ARE PRIVATE.</b> They
    ///         are, for every cluster this platform creates —
    ///         <c>CyberCloud.ContainerService/managedClusters</c> defaults <c>10.244.0.0/16</c> and
    ///         <c>10.96.0.0/12</c> — but a BYO cluster on a public or unusual range would have its pod
    ///         network reachable through the public rule. This provider cannot read those CIDRs: they
    ///         belong to another provider's resource and <c>src/Providers/README.md § Hard rule</c>
    ///         forbids the reference. <c>conformance.yaml § owed</c>,
    ///         <c>cluster-cidrs-are-assumed-private</c>.
    ///     </para>
    /// </remarks>
    public static string NetworkPolicyJson(string name, Guid tenantId, string ns, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(ns);

        var egress = new JsonArray {
            // 1. This console's own namespace — the tenant's resources in this resource group.
            //    ⚠ `kubernetes.io/metadata.name` is set by the API server on every namespace since
            //    1.21 and needs nobody to remember it, which is the only reason this rule works today
            //    and the tenant-wide one below does not.
            new JsonObject {
                ["to"] = new JsonArray {
                    new JsonObject {
                        ["namespaceSelector"] = new JsonObject {
                            ["matchLabels"] = new JsonObject { ["kubernetes.io/metadata.name"] = ns }
                        }
                    }
                }
            },
            // 2. The rest of this tenant. See the remarks: it matches nothing yet.
            new JsonObject {
                ["to"] = new JsonArray {
                    new JsonObject {
                        ["namespaceSelector"] = new JsonObject {
                            ["matchLabels"] = new JsonObject {
                                [KubeLabels.TenantId] = KubeLabels.GuidValue(tenantId)
                            }
                        }
                    }
                }
            },
            // 3. DNS. ⚠ Both protocols: a UDP-only rule works until a response exceeds 512 bytes and
            //    the resolver retries over TCP, which is the failure that presents as "curl works and
            //    dig doesn't, sometimes".
            new JsonObject {
                ["to"] = new JsonArray {
                    new JsonObject {
                        ["namespaceSelector"] = new JsonObject {
                            ["matchLabels"] = new JsonObject { ["kubernetes.io/metadata.name"] = "kube-system" }
                        },
                        ["podSelector"] = new JsonObject {
                            ["matchLabels"] = new JsonObject { ["k8s-app"] = "kube-dns" }
                        }
                    }
                },
                ["ports"] = new JsonArray {
                    new JsonObject { ["protocol"] = "UDP", ["port"] = 53 },
                    new JsonObject { ["protocol"] = "TCP", ["port"] = 53 }
                }
            }
        };

        if (string.Equals(EgressMode(desired), "Internet", StringComparison.Ordinal)) {
            var except = new JsonArray();
            foreach (var range in PrivateRanges) {
                except.Add(range);
            }

            egress.Add(
                new JsonObject {
                    ["to"] = new JsonArray {
                        new JsonObject {
                            ["ipBlock"] = new JsonObject { ["cidr"] = "0.0.0.0/0", ["except"] = except }
                        }
                    }
                }
            );
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ShellName(name) },
            ["spec"] = new JsonObject {
                // ⚠ It selects the CONSOLE'S OWN POD by resource id rather than every pod in the
                // namespace. A resource group's namespace holds the tenant's other workloads, and a
                // policy that selected all of them would silently apply a shell's egress rules to a
                // database.
                ["podSelector"] = new JsonObject {
                    ["matchLabels"] = new JsonObject { [KubeLabels.ResourceType] = ResourceTypeLabelValue }
                },
                ["policyTypes"] = new JsonArray { "Ingress", "Egress" },
                // ⚠ An EMPTY ingress array, which is not the same as no `ingress` key: with Ingress in
                // policyTypes and no rules, nothing may open a connection to a shell. That is right —
                // every byte reaches this pod through the API server's exec stream, which is not
                // pod-network traffic and is not affected.
                ["ingress"] = new JsonArray(),
                ["egress"] = egress
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     The value <c>cybercloud.io/resource-type</c> carries for this type — the selector the
    ///     network policy matches its own pod on.
    /// </summary>
    public static string ResourceTypeLabelValue { get; } = KubeLabels.ResourceTypeValue(Type);

    /// <summary>
    ///     The address ranges cut out of the public egress rule.
    /// </summary>
    /// <remarks>
    ///     ⚠ <c>169.254.0.0/16</c> is the one to read twice. It is the cloud metadata address, and a
    ///     shell that can reach it can usually read a node's own instance credentials — which is the
    ///     single most-used escalation from a container anywhere. The three RFC 1918 blocks and the
    ///     RFC 6598 CGNAT block are what keep the public rule from re-admitting the cluster.
    /// </remarks>
    public static ImmutableArray<string> PrivateRanges { get; } = [
        "10.0.0.0/8",
        "172.16.0.0/12",
        "192.168.0.0/16",
        "100.64.0.0/10",
        "169.254.0.0/16"
    ];

    /// <summary>The shell pod. ⚠ Applied by the <c>connect</c> handler; see this class's remarks.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>EVERY FIELD IN THE SECURITY CONTEXT IS THERE BECAUSE ITS DEFAULT IS THE WRONG
    ///         ONE.</b> docs/plan/19 § The pod asks for "non-root, read-only root filesystem except
    ///         <c>$HOME</c> and <c>/tmp</c>, no privilege escalation, seccomp RuntimeDefault, dropped
    ///         capabilities" — and a pod that omits all of it runs as whatever the image says, with
    ///         a writable root filesystem, with <c>allowPrivilegeEscalation</c> defaulting to
    ///         <b>true</b>, with the container runtime's default capability set, and with seccomp
    ///         <c>Unconfined</c> unless the kubelet was started with a default profile. Every one of
    ///         those is a default, and this row's whole job is to have decided them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>tcpdump</c> IS IN THE IMAGE AND WILL NOT RUN, AND THAT IS docs/plan/19's OWN
    ///         DECISION HONOURED RATHER THAN A BUG.</b> It needs <c>NET_RAW</c>, which is in the
    ///         dropped set below. The document says it should be "documented rather than silently
    ///         absent"; the honest consequence is that the tool is present and fails with
    ///         <c>Operation not permitted</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>restartPolicy: Never</c>, WHICH IS UNUSUAL AND IS THE POINT.</b> A shell that
    ///         restarted after its own hard cap expired would defeat the cap; a shell that restarted
    ///         after the user typed <c>exit</c> would be a terminal that will not close. A console's
    ///         pod is meant to end.
    ///     </para>
    /// </remarks>
    public static string PodJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (cpu, memory) = Resources(desired);

        return new JsonObject {
            ["metadata"] = new JsonObject {
                ["name"] = ShellName(name),
                ["annotations"] = new JsonObject {
                    [IdleTimeoutAnnotation] = IdleTimeoutSeconds(desired)
                        .ToString(CultureInfo.InvariantCulture),
                    [RecordingAnnotation] = SessionRecording(desired)
                        ? "true"
                        : "false"
                }
            },
            ["spec"] = new JsonObject {
                ["serviceAccountName"] = ShellName(name),
                ["automountServiceAccountToken"] = false,
                ["restartPolicy"] = "Never",
                ["activeDeadlineSeconds"] = MaxDurationSeconds(desired),
                // ⚠ Named explicitly rather than left to the scheduler's grace: the default is 30
                // seconds, and a shell whose user typed `exit` should not hold a node's resources for
                // half a minute afterwards.
                ["terminationGracePeriodSeconds"] = 5,
                ["securityContext"] = new JsonObject {
                    ["runAsNonRoot"] = true,
                    ["runAsUser"] = ShellUid,
                    ["runAsGroup"] = ShellUid,
                    // The home volume is chowned to the shell's group on mount. Without it a
                    // dynamically provisioned volume comes up root-owned and $HOME is unwritable,
                    // which presents as a shell that starts and cannot save anything.
                    ["fsGroup"] = ShellUid,
                    ["seccompProfile"] = new JsonObject { ["type"] = "RuntimeDefault" }
                },
                ["containers"] = new JsonArray {
                    new JsonObject {
                        ["name"] = "shell",
                        ["image"] = Image(desired),
                        // ⚠ A login shell and nothing else. There is no command a tenant may put here
                        // — see Schema2026's remarks — because a console's product is the interactive
                        // session and a `command` property would make it a job runner with an
                        // identity.
                        ["command"] = new JsonArray { "/bin/bash", "-l" },
                        ["stdin"] = true,
                        ["tty"] = true,
                        ["securityContext"] = new JsonObject {
                            ["allowPrivilegeEscalation"] = false,
                            ["readOnlyRootFilesystem"] = true,
                            ["capabilities"] = new JsonObject { ["drop"] = new JsonArray { "ALL" } }
                        },
                        ["resources"] = new JsonObject {
                            ["requests"] = new JsonObject {
                                ["cpu"] = cpu,
                                ["memory"] = memory,
                                ["ephemeral-storage"] = EphemeralStorageLimit
                            },
                            ["limits"] = new JsonObject {
                                ["cpu"] = cpu,
                                ["memory"] = memory,
                                ["ephemeral-storage"] = EphemeralStorageLimit
                            }
                        },
                        ["volumeMounts"] = new JsonArray {
                            new JsonObject { ["name"] = "home", ["mountPath"] = HomePath },
                            new JsonObject { ["name"] = "tmp", ["mountPath"] = "/tmp" }
                        },
                        ["env"] = new JsonArray {
                            new JsonObject { ["name"] = "HOME", ["value"] = HomePath }
                        }
                    }
                },
                ["volumes"] = new JsonArray {
                    new JsonObject {
                        ["name"] = "home",
                        ["persistentVolumeClaim"] = new JsonObject { ["claimName"] = HomeClaimName(name) }
                    },
                    // ⚠ The one writable place outside $HOME, and it is bounded. readOnlyRootFilesystem
                    // makes /tmp unwritable without this, which breaks almost every tool in the image;
                    // an unbounded emptyDir here would be the node-filling failure
                    // EphemeralStorageLimit exists to stop, so the limit is on the container and this
                    // volume counts against it.
                    new JsonObject { ["name"] = "tmp", ["emptyDir"] = new JsonObject() }
                }
            }
        }.ToJsonString();
    }

    /// <summary>Where <c>$HOME</c> is mounted.</summary>
    public const string HomePath = "/home/cloudshell";

    /// <summary>The uid and gid the shell runs as.</summary>
    /// <remarks>
    ///     ⚠ A number rather than a name, because <c>runAsNonRoot</c> is checked by the kubelet
    ///     against the numeric uid: an image whose <c>USER</c> is a name the kubelet cannot resolve
    ///     fails admission with "container has runAsNonRoot and image has non-numeric user".
    /// </remarks>
    public const int ShellUid = 10001;

    // ── Reading the world back ────────────────────────────────────────────────────────────────

    /// <summary>Whether an object read out of the cluster carries what the body asked for.</summary>
    /// <param name="objectJson">The object, as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CONTAINMENT, NOT EQUALITY, AND ON CORE-GROUP OBJECTS THAT MATTERS MORE THAN ON A
    ///         CRD.</b> Eleven families before this one compare custom resources, whose defaulting is
    ///         an operator's and is at least reviewable. These are built-in types, and the API server
    ///         defaults them itself, in the same request: a PersistentVolumeClaim comes back with
    ///         <c>volumeMode: Filesystem</c> and a <c>storageClassName</c> it chose; a Pod comes back
    ///         with a <c>dnsPolicy</c>, a <c>schedulerName</c>, a <c>terminationMessagePath</c>, a
    ///         <c>nodeName</c> and a <c>status</c>; a NetworkPolicy comes back with its
    ///         <c>policyTypes</c> normalised. An equality comparison would report drift on the pass
    ///         immediately after the first apply and on every pass after that, forever.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>AND THE HARNESSES WOULD NOT CATCH IT.</b> The Docker-free harness echoes the apply
    ///         back and the cluster-backed harness derives an open CRD stub with no defaults, so an
    ///         equality bug on this type would be green in both suites and red only against a real API
    ///         server — which <c>CyberCloud.Providers.Search</c> measured at 27 of 27 green over
    ///         exactly that. <c>ConsoleMatchesTests</c> is the hand-written test that catches it and
    ///         it is the only thing that can.
    ///     </para>
    ///     <para>
    ///         ⚠ Dispatches on <c>kind</c>, because a conformance case supplies one comparison over
    ///         every object the resource owns and this resource owns three. ⚠ <b>A document with NO
    ///         kind is <see langword="false" /> here, where two earlier families fold it into their
    ///         one real object.</b> They can: they own one kind, so a kindless body is unambiguous.
    ///         This one owns three, and guessing which a kindless document was would mean judging a
    ///         PersistentVolumeClaim by a NetworkPolicy's rules and reporting a match. Every object
    ///         that has been through <c>KubeCommandBuilder</c> carries a kind, so nothing legitimate
    ///         reaches that branch.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        if (Document(objectJson) is not { } document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "PersistentVolumeClaim" => MatchesClaim(document, desired),
            "ServiceAccount" => MatchesServiceAccount(document),
            "NetworkPolicy" => MatchesNetworkPolicy(document, desired),
            _ => false
        };
    }

    /// <summary>
    ///     ⚠ The claim is judged on the size the tenant asked for and nothing else. Its
    ///     <c>storageClassName</c>, its <c>volumeName</c> and its whole <c>status</c> are the
    ///     cluster's.
    /// </summary>
    static bool MatchesClaim(JsonObject document, JsonElement desired) =>
        document["spec"]?["resources"]?["requests"]?["storage"]?.GetValue<string>() == HomeSize(desired);

    /// <summary>
    ///     ⚠ The one field on this object whose drift is a security event. A ServiceAccount whose
    ///     <c>automountServiceAccountToken</c> was removed by a hand edit or a mutating policy is an
    ///     account that mounts its token again, and every other field would still match.
    /// </summary>
    static bool MatchesServiceAccount(JsonObject document) =>
        document["automountServiceAccountToken"] is JsonValue value
        && value.TryGetValue<bool>(out var mount)
        && !mount;

    /// <summary>
    ///     ⚠ The policy is judged on the three facts that make it a constraint rather than on its
    ///     rules one by one: that it governs both directions, that its ingress list is empty, and that
    ///     it has as many egress rules as the body asks for. A rule-by-rule comparison would be an
    ///     equality test wearing a disguise — the API server reorders nothing here, but a future CNI
    ///     admission webhook adding a rule would then read as drift forever.
    /// </summary>
    static bool MatchesNetworkPolicy(JsonObject document, JsonElement desired) {
        if (document["spec"] is not JsonObject spec) {
            return false;
        }

        var types = spec["policyTypes"] as JsonArray;
        var declared = types?.Select(x => x?.GetValue<string>()).ToList() ?? [];

        return declared.Contains("Ingress")
            && declared.Contains("Egress")
            && spec["ingress"] is JsonArray { Count: 0 }
            && spec["egress"] is JsonArray egress
            && egress.Count >= ExpectedEgressRules(desired);
    }

    /// <summary>How many egress rules a body's posture renders.</summary>
    /// <param name="desired">The desired body.</param>
    public static int ExpectedEgressRules(JsonElement desired) =>
        string.Equals(EgressMode(desired), "Internet", StringComparison.Ordinal) ? 4 : 3;

    static JsonObject? Document(string objectJson) {
        try {
            return JsonNode.Parse(objectJson) as JsonObject;
        }
        catch (JsonException) {
            return null;
        }
    }

    // ── A body, for tests and for the conformance case ────────────────────────────────────────

    /// <summary>A valid body.</summary>
    /// <param name="clusterId">The cluster the console runs in.</param>
    /// <param name="principalId">The identity the shell acts as.</param>
    /// <param name="homeSize">The home volume's size.</param>
    /// <param name="preset">The sizing preset.</param>
    /// <param name="variant">The image variant.</param>
    /// <param name="egress">The egress posture.</param>
    /// <param name="recording">Whether the session is recorded.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. The read-back the conformance suite compares
    ///     rebuilds a <see cref="SchemaKind.Nested" /> container from whichever leaf lands first, so a
    ///     body carrying an empty object would not survive it.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        Guid? principalId = null,
        string homeSize = DefaultHomeSize,
        string preset = DefaultPreset,
        string variant = DefaultVariant,
        string egress = DefaultEgress,
        bool recording = false,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["identity"] = new JsonObject {
                    ["principalId"] = (principalId ?? DefaultPrincipal).ToString("D", CultureInfo.InvariantCulture)
                },
                ["image"] = new JsonObject { ["variant"] = variant },
                ["sizing"] = new JsonObject { ["preset"] = preset },
                ["home"] = new JsonObject { ["size"] = homeSize, ["retentionDays"] = DefaultRetentionDays },
                ["session"] = new JsonObject {
                    ["idleTimeoutMinutes"] = DefaultIdleTimeoutMinutes,
                    ["maxDurationHours"] = DefaultMaxDurationHours
                },
                ["network"] = new JsonObject { ["egress"] = egress },
                ["audit"] = new JsonObject { ["sessionRecording"] = recording }
            }
        }.ToJsonString();

    // ── Defaults, duplicated as consts because the write path stores the body AS SENT ─────────
    //
    // ⚠ The validator does not substitute defaults, so a body that omitted a property arrives at a
    // reader with the property absent. Every reader therefore needs one, and DefaultJson above and
    // these have to agree — ConsoleDeclarationTests.EveryDeclaredDefaultIsTheOneTheReadersFallBackTo
    // compares them.

    const string DefaultVariant = "default";
    const string DefaultPreset = "c1.small";
    const string DefaultHomeSize = "5Gi";
    const string DefaultEgress = "Internet";
    const int DefaultRetentionDays = 90;
    const int DefaultIdleTimeoutMinutes = 20;
    const int DefaultMaxDurationHours = 8;

    static readonly Guid DefaultPrincipal = Guid.Parse("cccccccc-0000-4000-8000-00000000c0de");

    // ── Reading one pointer out of a body ─────────────────────────────────────────────────────

    static JsonElement? Root(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static JsonElement? Member(JsonElement desired, string parent, string name) =>
        Root(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string parent, string name, string fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.String } value
            ? value.GetString() ?? fallback
            : fallback;

    static int Number(JsonElement desired, string parent, string name, int fallback) =>
        Member(desired, parent, name) is { ValueKind: JsonValueKind.Number } value
        && value.TryGetInt32(out var found)
            ? found
            : fallback;

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Member(desired, parent, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
