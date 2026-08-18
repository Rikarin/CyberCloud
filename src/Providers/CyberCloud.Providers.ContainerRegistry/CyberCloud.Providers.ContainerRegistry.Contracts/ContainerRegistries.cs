// ⚠ For SecretRef, which the credential block below hands to ISecretWriter and ISecretResolver.
using CyberCloud.Core.Contracts;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerRegistry.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.ContainerRegistry/registries</c>: the type, its
///     api-version, its body shape, and the fifteen Kubernetes objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THE OPERATOR THIS ROW WAS PLANNED AROUND IS ARCHIVED, AND THAT IS THE FIRST THING TO
///         KNOW ABOUT IT.</b> ADR-010 clause 1's survey names <i>"Harbor"</i> and
///         [13 § Container Registry](../../../../docs/plan/13-compute-vm-containers.md) says
///         <i>"Harbor, one instance per tenant"</i>. <c>goharbor/harbor-operator</c> answers the GitHub
///         API with <c>"archived": true</c> and <c>"description": "[DEPRECATED] Kubernetes operator for
///         Harbor service components"</c>; its README opens <i>"Due to low activity in maintanance this
///         sub-project we are archiving it"</i>, its last stable release is <b>v1.3.0, 2022-07-02</b>,
///         and the only newer tag is a release candidate that never shipped. Checked against the API on
///         2026-08-18 rather than against a README that could be stale in the other direction.
///     </para>
///     <para>
///         ⚠ <b>THAT IS THE THIRD OPERATOR IN ADR-010 CLAUSE 1'S SURVEY THAT CANNOT BE USED, AND THE
///         PATTERN IS NOW A FINDING ABOUT THE CLAUSE RATHER THAN ABOUT THREE SERVICES.</b>
///         <c>charts/managed/nats</c> found <c>nats-operator</c> archived on 2025-04-10;
///         <c>SearchProvider</c> found <c>qdrant/qdrant-operator</c> answering <c>404</c>;
///         <c>DocumentDbAccounts</c> found the FerretDB organisation holds no operator at all. Clause 1
///         calls itself <i>"the operator selection per managed service"</i> and is, three rows in, a
///         survey of <b>software choices</b> that is only sometimes a survey of operators. The
///         correction belongs in ADR-010.
///     </para>
///     <para>
///         ⚠ <b>SO THIS IS THE OPERATOR-LESS SHAPE, AND IT IS THAT SHAPE'S THIRD AND LARGEST
///         SIGHTING.</b> <c>CyberCloud.Messaging/natsClusters</c> renders five objects because there is
///         no operator; <c>CyberCloud.Storage/accounts</c> renders one because there is; this renders
///         <b>fifteen</b>. Every default a controller would have supplied is a decision here, and every
///         one of them is written down where it is taken. ⚠ The consequence for the catalogue's running
///         claim — <i>"object count is not a measure of a service's size"</i> — is that the range is
///         now one to fifteen and the top of it is a service docs/plan/13 costs at <b>1.5 EM</b>, less
///         than the three-object managed Kubernetes row's 4.0.
///     </para>
///     <para>
///         ⚠ <b>THE CREDENTIAL IS THE SHARPEST SIGHTING OF FAILURE CLASS (c) IN THE CATALOGUE, AND IT
///         IS NOT AN ABSENCE — IT IS A PUBLISHED CONSTANT.</b> The three earlier sightings are things
///         that are <i>unset</i>: SeaweedFS serves anonymous admin when no identities file exists,
///         Qdrant's chart leaves <c>service.api_key</c> unset, MariaDB's operator generates a root
///         password. <c>goharbor/harbor-helm</c>'s <c>values.yaml</c> ships
///         <c>harborAdminPassword: "Harbor12345"</c> and <c>templates/core/core-secret.yaml</c>
///         consumes it with <b>no generation fallback</b> — while, in the same file, <c>secret</c>,
///         <c>CSRF_KEY</c>, <c>JOBSERVICE_SECRET</c> and <c>REGISTRY_HTTP_SECRET</c> all end in
///         <c>| default (randAlphaNum 16)</c>. The admin password is the one credential in that chart
///         that is not randomised. A default <c>helm install</c> is a registry reachable at
///         <c>admin</c>/<c>Harbor12345</c>, over a protocol every CI system in the industry already
///         speaks.
///         <para>
///             ⚠ <b>Harbor's own core does something different again, and reading only the chart would
///             get it wrong.</b> <c>src/lib/config/metadata/metadatalist.go</c> gives
///             <c>HARBOR_ADMIN_PASSWORD</c> a <c>DefaultValue: ""</c>, and
///             <c>make/migrations/postgresql/0001_initial_schema.up.sql</c> seeds
///             <c>('admin', '', …)</c> — so the <i>code</i> default is the empty string and
///             <c>Harbor12345</c> lives in <c>make/harbor.yml.tmpl</c>, the offline installer's config
///             template. ⚠ And <c>src/core/main.go</c> applies the environment value <b>only when
///             <c>user.Salt == ""</c></b>, with no non-empty guard: an unset variable seeds the
///             administrator with the hash of the empty string, once, permanently. Three layers, three
///             behaviours, and the one this platform must not inherit is the chart's.
///         </para>
///         <para>
///             So <see cref="CredentialsSecretName" /> is minted into the tenant's vault by
///             <c>ContainerRegistryReconciler</c> <b>before anything is applied</b>, exactly as
///             <c>CyberCloud.Storage/accounts</c> does, and <c>listCredentials</c> hands it back. This
///             row never renders a literal.
///         </para>
///     </para>
///     <para>
///         ⚠ <b>THE FIRST TYPE IN THE TREE TO DECLARE A RECOVERY WINDOW.</b> Eleven families declined
///         <c>SupportsSoftDelete</c> for one shared reason, and the reason expired: docs/plan/08
///         § Soft delete is built. See <c>ContainerRegistryProvider</c> for why this row is the one
///         where a window is worth having, and <see cref="PurgeProtectionPointer" /> for the flag.
///     </para>
///     <para>
///         ⚠ <b>WHAT THIS ROW DOES NOT DO, said once so nobody has to infer it.</b> docs/plan/13's
///         bullets under this heading are four and only one of them ships. Vulnerability scanning is
///         <b>M2</b> in docs/plan/01's own table (<i>"ACR — vulnerability scanning · ⊂ registries ·
///         M2"</i>) and no Trivy is rendered; replication between regions needs a second registry to
///         replicate to and is a sub-resource nothing declares; retention policies are a Harbor
///         <i>project</i> setting configured over Harbor's API rather than a field of any deployment,
///         so no body property can carry one; robot accounts are the same. All four are at
///         <c>charts/managed/harbor/conformance.yaml § owed</c> with what blocks each. ⚠ The
///         <c>feeds</c> sibling type is M2 and is <b>not</b> declared — docs/plan/13 says in as many
///         words that <i>"Harbor does OCI only"</i> and that the three artifact protocols are a .NET
///         service, so declaring the type here would publish an API nothing serves.
///     </para>
///     <para>
///         ⚠ <b><see cref="Schema2026" /> is the authored side of the pair</b> and
///         <c>charts/managed/harbor/values.yaml</c> is the other half — ADR-010 § Which end authors the
///         schema.
///     </para>
/// </remarks>
public static class ContainerRegistries {
    /// <summary>The provider namespace, as docs/plan/13 § Container Registry spells it.</summary>
    public const string ProviderNamespace = "CyberCloud.ContainerRegistry";

    /// <summary>The resource type.</summary>
    public const string TypePath = "registries";

    /// <summary>
    ///     The one api-version. ⚠ Immutable — adding a field is a new date, and it must equal the
    ///     <c>cybercloud.io/api-version</c> annotation in <c>charts/managed/harbor/Chart.yaml</c>.
    /// </summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The chart this type is the configuration surface of.</summary>
    public const string ChartName = "managed/harbor";

    /// <summary>The pointer <c>RequiresCluster</c> names. docs/plan/06 § The hierarchy.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>The action that hands a caller the endpoint and the administrator's password.</summary>
    /// <remarks>
    ///     ⚠ <b>Not <c>listKeys</c>, and the difference is what comes back.</b> Four types in the
    ///     catalogue spell this <c>listKeys</c> because what they return is a key <i>pair</i> or a
    ///     connection string. What a registry returns is a <b>username and a password</b> — the two
    ///     things <c>docker login</c> takes — so the name says credentials. docs/plan/13:
    ///     <i>"<c>docker login</c> uses a platform token"</i>; that sentence describes the M2 shape,
    ///     where a robot account is minted per service principal, and this action is what exists until
    ///     then. <c>conformance.yaml § owed</c>, <c>robot-accounts-are-not-service-principals</c>.
    /// </remarks>
    public const string ListCredentialsAction = "listCredentials";

    /// <summary>The permission <see cref="ListCredentialsAction" /> checks. ⚠ Not <c>read</c>.</summary>
    /// <remarks>
    ///     docs/plan/07 § Consistency puts a key export in the fully-consistent row by name. Sharing
    ///     <c>read</c> would make every viewer of a registry an administrator of it — and on this type
    ///     the credential is not merely read access to the resource, it is <b>push</b> access to every
    ///     image a tenant runs in production.
    /// </remarks>
    public const string ListCredentialsPermission = "listCredentials";

    // ── The recovery window this row was going to declare, and does not ───────────────────────
    //
    // ⚠ THESE THREE ARE THE ARGUMENTS FOR A DECLARATION THAT IS WITHHELD, AND WITHHOLDING IT IS THE
    // MOST VALUABLE THING THIS ROW FOUND. The full account is on ContainerRegistryProvider: declaring
    // SupportsSoftDelete made this family the first to exercise docs/plan/08 § Soft delete end to end
    // against a real API server, and the cluster-backed suite reported that a soft-deleted registry
    // REBUILDS ITS ENTIRE DATA PLANE after a converged teardown. They are kept, documented and
    // asserted-against rather than deleted, because closing the platform defect makes the declaration
    // three arguments to one method call.

    /// <summary>How long a deleted registry would be recoverable for. docs/plan/06 § Tags, locks.</summary>
    /// <remarks>
    ///     ⚠ Seven, which is what that section gives <i>"types carrying data"</i>, and the argument for
    ///     this type being one of them is about the data rather than about the platform: a deleted
    ///     registry's images, its metadata database and its job queue are all on
    ///     <c>PersistentVolumeClaim</c>s that its <c>StatefulSet</c>s leave behind, so there is
    ///     genuinely something to hand back. That half stands; see
    ///     <c>ContainerRegistryProvider</c> for the half that does not.
    /// </remarks>
    public const int SoftDeleteDays = 7;

    /// <summary>The permission a purge would need. ⚠ A fourth permission, not the delete one.</summary>
    public const string PurgePermission = SoftDeletePolicy.DefaultPurgePermission;

    /// <summary>The body flag that would refuse every purge of a resource for the rest of its window.</summary>
    /// <remarks>
    ///     ⚠ <b>Not declared as a property, because <c>ProviderBuilder</c> refuses a purge-protection
    ///     pointer on a type with no window</b> — <i>"the flag would be a property callers can set and
    ///     nothing reads"</i> — and that refusal is right. A flag published to every generated surface
    ///     while the platform honours no window at all is the promise docs/plan/08 § Soft delete says
    ///     is worse than promising nothing.
    /// </remarks>
    public const string PurgeProtectionPointer = "/properties/purgeProtection";

    /// <summary>The type, namespace and path together.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The objects a registry IS ─────────────────────────────────────────────────────────────
    //
    // ⚠ SIX KINDS ACROSS THREE API GROUPS, AND FIVE OF THE SIX ARE BUILT-IN. That is the exact
    // reverse of the catalogue's usual shape — nine families render custom resources and let an
    // operator expand them — and it is a consequence of the archived operator rather than a choice.
    // What it costs the cluster-backed suite is recorded on the *.Cluster.Conformance project: four
    // of the six kinds need no CRD stub at all, because the API server serves them without being
    // told.

    /// <summary>The <c>Secret</c> holding every credential a registry has.</summary>
    public static GroupVersionKind SecretKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    /// <summary>The <c>ConfigMap</c> holding the registry's and the job service's configuration.</summary>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>A <c>Service</c> — all six of them are the same kind.</summary>
    public static GroupVersionKind ServiceKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Service", Plural = "services" };

    /// <summary>A <c>Deployment</c> — the three stateless components.</summary>
    public static GroupVersionKind DeploymentKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    /// <summary>A <c>StatefulSet</c> — the three components that own a volume.</summary>
    /// <remarks>
    ///     ⚠ <b>A <c>StatefulSet</c> for the registry rather than a <c>Deployment</c> with a
    ///     <c>PersistentVolumeClaim</c> beside it, and the reason is the delete path.</b> A claim
    ///     created by a <c>volumeClaimTemplate</c> is named after the set and is <i>not</i> removed by
    ///     deleting it, which is precisely what makes this type's recovery window honourable — see
    ///     <see cref="SoftDeleteDays" />. A separately-applied claim would be a fifteenth object this
    ///     provider deletes, and a soft delete that erased the images would be a window with nothing
    ///     behind it.
    /// </remarks>
    public static GroupVersionKind StatefulSetKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "StatefulSet", Plural = "statefulsets" };

    /// <summary>Prometheus Operator's <c>PodMonitor</c> — docs/plan/12 § The pattern, once, piece 6.</summary>
    /// <remarks>
    ///     ⚠ <b>Piece 6's SECOND branch, discharged for the reason <c>charts/managed/nats</c>
    ///     established and this is the third sighting of.</b> The corrected piece 6 reads
    ///     <i>"ask the operator … and hand-write one into the chart only when there is no operator to
    ///     ask"</i>, and warns that a hand-written scrape hard-codes somebody else's pod labels. The
    ///     labels this selector matches are <see cref="PodLabels" /> — written by this file onto pods
    ///     created by this file — so there is no upstream release that can move them. Hand-writing is
    ///     safe exactly when there is no operator, which is the same condition that forces the branch.
    /// </remarks>
    public static GroupVersionKind PodMonitorKind { get; } =
        new() {
            Group = "monitoring.coreos.com", Version = "v1", Kind = "PodMonitor", Plural = "podmonitors"
        };

    // ── Ports ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The port Harbor's core serves on — the API, the portal proxy and <c>/v2/</c>.</summary>
    /// <remarks>
    ///     ⚠ <b>Core is the front door and there is no nginx, which is a scope decision with a
    ///     consequence.</b> <c>goharbor/harbor-helm</c> puts an nginx in front that routes <c>/</c> to
    ///     the portal and <c>/api/</c>, <c>/v2/</c>, <c>/service/</c> to core. Core serves the second
    ///     set itself, so <c>docker login</c> and <c>docker push</c> work against it directly; what is
    ///     lost is one address that serves both the UI and the API. The portal has its own
    ///     <c>Service</c> instead, and the missing sixteenth object is at <c>conformance.yaml § owed</c>,
    ///     <c>one-front-door</c>, together with the reason it is not simply added: an ingress is where
    ///     docs/plan/12 § Cross-cutting decisions' mandatory CIDR allow-list would have to live, and
    ///     which ingress controller the bundle standardises on is a platform decision rather than a
    ///     provider's.
    /// </remarks>
    public const int CorePort = 8080;

    /// <summary>The port the portal's nginx serves the web UI on.</summary>
    public const int PortalPort = 8080;

    /// <summary>The port the job service serves its API on.</summary>
    public const int JobServicePort = 8080;

    /// <summary>The port <c>distribution</c> serves the OCI API on.</summary>
    /// <remarks>
    ///     ⚠ <b>Reachable only from inside the namespace, and that is load-bearing rather than
    ///     incidental — see <see cref="RegistryConfigYaml" />.</b>
    /// </remarks>
    public const int RegistryPort = 5000;

    /// <summary>The port <c>registryctl</c> serves on. Core calls it for garbage collection.</summary>
    public const int RegistryControllerPort = 8080;

    /// <summary>PostgreSQL's port. Harbor's core, job service and registry all read this database.</summary>
    public const int DatabasePort = 5432;

    /// <summary>Redis' port.</summary>
    public const int RedisPort = 6379;

    /// <summary>The port core serves Prometheus metrics on when monitoring is asked for.</summary>
    /// <remarks>
    ///     ⚠ Off unless <c>METRIC_ENABLE</c> is <c>true</c>, which <see cref="CoreDeploymentJson" />
    ///     writes from the body. A <see cref="PodMonitorKind" /> pointed at a port nothing is listening
    ///     on applies cleanly, selects the right pods and scrapes a connection refusal forever — the
    ///     failure <c>charts/managed/nats</c> records about pointing a scrape at NATS' JSON monitoring
    ///     port, reached from the other side.
    /// </remarks>
    public const int MetricsPort = 8001;

    // ── The versions on offer ─────────────────────────────────────────────────────────────────

    /// <summary>The Harbor minors this api-version offers.</summary>
    /// <remarks>
    ///     ⚠ Read off <c>goharbor/harbor</c>'s releases on 2026-08-18 rather than off a README: these
    ///     are the two minor lines with a shipped patch. A third value is an edit here and in
    ///     <see cref="PinnedPatch" />, and the pair is checked against each other by
    ///     <c>ContainerRegistryDeclarationTests</c>.
    /// </remarks>
    public static ImmutableArray<string> Versions { get; } = ["2.14", "2.15"];

    /// <summary>The patch each offered minor is pinned to, as an image tag.</summary>
    /// <remarks>
    ///     ⚠ <b>The API takes a MINOR and a container image takes a full tag, so the platform pins the
    ///     patch.</b> Offering a bare minor as a tag would resolve to nothing — Harbor publishes
    ///     <c>v2.15.2</c> and not <c>v2.15</c> — and the failure is one image pull back-off per pod,
    ///     after the caller was told <c>202</c>. The same shape <c>ManagedClusters.PinnedPatch</c>
    ///     records for Kubernetes minors, reached from a registry rather than from a webhook.
    /// </remarks>
    public static FrozenDictionary<string, string> PinnedPatch { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal) {
            ["2.14"] = "v2.14.4",
            ["2.15"] = "v2.15.2"
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The registry Harbor's own images are pulled from.</summary>
    public const string ImageRegistry = "docker.io/goharbor";

    /// <summary>The PostgreSQL image Harbor ships. ⚠ Harbor's build, not upstream Postgres.</summary>
    /// <remarks>
    ///     ⚠ <b><c>goharbor/harbor-db</c> and not <c>postgres</c>, and substituting the upstream image
    ///     does not work.</b> Harbor's build carries the migration entrypoint and the
    ///     <c>registry</c>/<c>notaryserver</c>/<c>notarysigner</c> database bootstrap that core's
    ///     schema migration expects to already exist. That is also why this type does <b>not</b> reach
    ///     for <c>CyberCloud.DBforPostgreSQL/servers</c> even setting rule 2 aside: the platform's
    ///     managed PostgreSQL is CloudNativePG running upstream Postgres, and Harbor wants its own.
    /// </remarks>
    public const string DatabaseImageRepository = "harbor-db";

    /// <summary>The Redis image Harbor ships.</summary>
    public const string RedisImageRepository = "redis-photon";

    // ── Naming ────────────────────────────────────────────────────────────────────────────────
    //
    // ⚠ EVERY NAME IS DERIVED FROM THE RESOURCE'S OWN NAME AND NOTHING ELSE. `ReconcileDriver
    // .NamespaceFor` is `{subscriptionId:N}-{resourceGroup}`, so two tenants naming a registry
    // `images` land in different namespaces and never collide — the property
    // `OneReconcilerInstanceServesTwoTenantsWithoutMixingThem` exists to check.

    /// <summary>The <c>Secret</c> every component reads its credentials out of.</summary>
    /// <remarks>
    ///     ⚠ <b>ONE <c>Secret</c> FOR SIX SECRETS, and the alternative was six objects.</b> Harbor
    ///     needs an administrator password, a core secret, a CSRF key, a job-service secret, a
    ///     registry HTTP secret and a database password; all six are minted together into one vault
    ///     document (see <see cref="SecretPath" />) and projected into one Kubernetes object, because
    ///     they share a lifetime exactly. Splitting them would be six applies, six read-backs and six
    ///     chances for a teardown to be interrupted between them.
    /// </remarks>
    public static string CredentialsSecretName(string name) => name + "-credentials";

    /// <summary>The <c>ConfigMap</c> holding the registry's and the job service's YAML.</summary>
    public static string ConfigMapName(string name) => name + "-config";

    /// <summary>The core <c>Deployment</c>'s name. ⚠ The <c>Service</c> takes the bare name.</summary>
    public static string CoreName(string name) => name + "-core";

    /// <summary>The portal's name, for both its <c>Deployment</c> and its <c>Service</c>.</summary>
    public static string PortalName(string name) => name + "-portal";

    /// <summary>The job service's name, for both its <c>Deployment</c> and its <c>Service</c>.</summary>
    public static string JobServiceName(string name) => name + "-jobservice";

    /// <summary>The registry's name, for both its <c>StatefulSet</c> and its <c>Service</c>.</summary>
    public static string RegistryName(string name) => name + "-registry";

    /// <summary>The database's name, for both its <c>StatefulSet</c> and its <c>Service</c>.</summary>
    public static string DatabaseName(string name) => name + "-database";

    /// <summary>Redis' name, for both its <c>StatefulSet</c> and its <c>Service</c>.</summary>
    public static string RedisName(string name) => name + "-redis";

    // ── The component vocabulary ──────────────────────────────────────────────────────────────
    //
    // ⚠ THE COMPONENT NAME IS WHAT `Matches` DISPATCHES ON, AND THAT IS WHY IT IS A CONSTANT RATHER
    // THAN A SUFFIX TEST. `ObjectMatchesDesired` is `(objectJson, desiredJson) => bool` and carries
    // NO ADDRESS — the limit `charts/managed/seaweedfs-bucket/conformance.yaml § owed` records as
    // `object-matches-desired-cannot-see-an-address`. So a comparison that worked out which of six
    // Deployments it was looking at by stripping the resource's name off `metadata.name` could not,
    // because it does not know the resource's name. The component label round-trips through the API
    // server inside `spec.template.metadata.labels` and `spec.selector`, which is a field of the
    // object's own body, so it is readable from a read-back document with nothing else in hand.

    /// <summary>The core component's <c>app.kubernetes.io/component</c> value.</summary>
    public const string CoreComponent = "core";

    /// <summary>The portal component's value.</summary>
    public const string PortalComponent = "portal";

    /// <summary>The job service's value.</summary>
    public const string JobServiceComponent = "jobservice";

    /// <summary>The registry's value.</summary>
    public const string RegistryComponent = "registry";

    /// <summary>The database's value.</summary>
    public const string DatabaseComponent = "database";

    /// <summary>Redis' value.</summary>
    public const string RedisComponent = "redis";

    // ── Object references ─────────────────────────────────────────────────────────────────────

    /// <summary>The credentials <c>Secret</c> a registry owns.</summary>
    public static ObjectRef CredentialsSecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = CredentialsSecretName(name) };

    /// <summary>The configuration <c>ConfigMap</c> a registry owns.</summary>
    public static ObjectRef ConfigMapRef(string ns, string name) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ConfigMapName(name) };

    /// <summary>The database <c>StatefulSet</c>.</summary>
    public static ObjectRef DatabaseSetRef(string ns, string name) =>
        new() { Kind = StatefulSetKind, Namespace = ns, Name = DatabaseName(name) };

    /// <summary>The database <c>Service</c>.</summary>
    public static ObjectRef DatabaseServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = DatabaseName(name) };

    /// <summary>The Redis <c>StatefulSet</c>.</summary>
    public static ObjectRef RedisSetRef(string ns, string name) =>
        new() { Kind = StatefulSetKind, Namespace = ns, Name = RedisName(name) };

    /// <summary>The Redis <c>Service</c>.</summary>
    public static ObjectRef RedisServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = RedisName(name) };

    /// <summary>The registry <c>StatefulSet</c> — the one that owns the images.</summary>
    public static ObjectRef RegistrySetRef(string ns, string name) =>
        new() { Kind = StatefulSetKind, Namespace = ns, Name = RegistryName(name) };

    /// <summary>The registry <c>Service</c>.</summary>
    public static ObjectRef RegistryServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = RegistryName(name) };

    /// <summary>The core <c>Deployment</c>.</summary>
    public static ObjectRef CoreDeploymentRef(string ns, string name) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = CoreName(name) };

    /// <summary>The front-door <c>Service</c>, which takes the resource's own name.</summary>
    public static ObjectRef CoreServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = name };

    /// <summary>The portal <c>Deployment</c>.</summary>
    public static ObjectRef PortalDeploymentRef(string ns, string name) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = PortalName(name) };

    /// <summary>The portal <c>Service</c>.</summary>
    public static ObjectRef PortalServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = PortalName(name) };

    /// <summary>The job service <c>Deployment</c>.</summary>
    public static ObjectRef JobServiceDeploymentRef(string ns, string name) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = JobServiceName(name) };

    /// <summary>The job service <c>Service</c>.</summary>
    public static ObjectRef JobServiceServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = JobServiceName(name) };

    /// <summary>The <c>PodMonitor</c> a registry owns when monitoring is on.</summary>
    public static ObjectRef PodMonitorRef(string ns, string name) =>
        new() { Kind = PodMonitorKind, Namespace = ns, Name = name };

    /// <summary>
    ///     The labels on a component's pods, on its workload selector and on its <c>Service</c>'s
    ///     selector.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>These are NOT ADR-013's seven, and the distinction is the one
    ///     <c>DocumentDbAccounts</c> measured.</b> The seven are injected by <c>KubeCommand</c> onto
    ///     each object's own <c>metadata.labels</c>, non-overridably, so the Labels architecture gate
    ///     cannot be failed by a provider. These four sit inside <c>spec.template.metadata.labels</c>
    ///     and <c>spec.selector</c>, which no builder reaches — so what a provider <i>can</i> get wrong
    ///     is exactly this set, written into three places per component that must agree: an immutable
    ///     workload selector, its pod template, and a <c>Service</c> selector.
    ///     <c>ContainerRegistryReconcilerTests.EverySelectorAgreesWithThePodTemplateItSelects</c> is
    ///     that test.
    ///     <para>
    ///         ⚠ <b>A workload's <c>spec.selector</c> is immutable after create</b>, so this function's
    ///         output is part of every component's identity: changing it is six resources that can
    ///         never be updated again, which the API server reports as an invalid update rather than as
    ///         drift.
    ///     </para>
    ///     <para>
    ///         ⚠ The pods still do not carry the seven, which is the gap
    ///         <c>charts/managed/nats/conformance.yaml § owed</c> records as <c>pod-labels</c> and this
    ///         family inherits unchanged — fifteen objects carry them and none of the pods do.
    ///     </para>
    /// </remarks>
    public static (string Key, string Value)[] PodLabels(string name, string component) => [
        ("app.kubernetes.io/name", "harbor"),
        ("app.kubernetes.io/instance", name),
        ("app.kubernetes.io/component", component),
        ("app.kubernetes.io/managed-by", "cybercloud")
    ];

    /// <summary>The in-cluster registry endpoint <c>listCredentials</c> hands out.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b><c>http</c> and not <c>https</c>, and that is a statement rather than an oversight.</b>
    ///     Nothing terminates TLS in front of core — see <see cref="CorePort" /> — and a Docker client
    ///     talking to a plain-HTTP registry needs it in its own <c>insecure-registries</c> list. The
    ///     <c>https</c> docs/plan/13 implies is an ingress-side promise this row cannot keep;
    ///     <c>conformance.yaml § owed</c>, <c>one-front-door</c>.
    /// </remarks>
    public static string Endpoint(string ns, string name) =>
        "http://"
        + name
        + "."
        + ns
        + ".svc:"
        + CorePort.ToString(CultureInfo.InvariantCulture);

    /// <summary>The portal URL <c>listCredentials</c> hands out beside the endpoint.</summary>
    public static string PortalUrl(string ns, string name) =>
        "http://"
        + PortalName(name)
        + "."
        + ns
        + ".svc:"
        + PortalPort.ToString(CultureInfo.InvariantCulture);

    // ── The credential: where it lives, what it is called, and how one is made ────────────────
    //
    // ⚠ THIS BLOCK IS docs/plan/12 § The pattern, once, PIECE 5, AND ON THIS ROW IT IS THE
    // DIFFERENCE BETWEEN A PRIVATE REGISTRY AND A PUBLIC ONE. See the type's own remarks for what
    // goharbor/harbor-helm ships instead.

    /// <summary>The vault path a registry's credentials live at.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <remarks>
    ///     ⚠ <b>KEYED ON THE RESOURCE GUID AND NOT ON ITS NAME</b>, for the reason
    ///     <c>StorageAccounts.SecretPath</c> gives at length: docs/plan/06 § Identifiers makes a name
    ///     reusable the moment the index entry is released, so a path built from the name would hand a
    ///     brand-new registry the credentials of one somebody deleted, and mint-once would make that
    ///     permanent.
    ///     <para>
    ///         ⚠ <b>On THIS type the name is held for the whole recovery window rather than released
    ///         immediately</b> — docs/plan/08 § Soft delete, <i>"the name is held for the whole
    ///         window"</i> — so the collision the GUID prevents is a week away rather than an hour
    ///         away. That makes the GUID <i>more</i> necessary and not less: a restore has to find the
    ///         same credential the tenant was using, and only an address that survived the delete can.
    ///     </para>
    /// </remarks>
    public static string SecretPath(ResourceId id) {
        ArgumentNullException.ThrowIfNull(id.Path);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tenants/{id.TenantId:D}/{ProviderNamespace}/{TypePath}/{id.Id:D}"
        );
    }

    /// <summary>The field Harbor's administrator password is filed under.</summary>
    public const string AdminPasswordField = "adminPassword";

    /// <summary>The field core's own secret is filed under. Signs internal requests between components.</summary>
    public const string CoreSecretField = "coreSecret";

    /// <summary>The field core's CSRF key is filed under. ⚠ Exactly 32 characters — Harbor requires it.</summary>
    public const string CsrfKeyField = "csrfKey";

    /// <summary>The field the job service's shared secret is filed under.</summary>
    public const string JobServiceSecretField = "jobServiceSecret";

    /// <summary>The field the registry's HTTP secret is filed under.</summary>
    /// <remarks>
    ///     ⚠ <c>distribution</c>'s <c>http.secret</c> signs upload state so that a resumable push
    ///     survives being routed to a different replica. It is not a login credential and it still must
    ///     not be a constant: a shared value across tenants would let one tenant's client resume
    ///     another's upload.
    /// </remarks>
    public const string RegistryHttpSecretField = "registryHttpSecret";

    /// <summary>The field the PostgreSQL superuser password is filed under.</summary>
    public const string DatabasePasswordField = "databasePassword";

    /// <summary>Every field one mint writes, in the order they are generated.</summary>
    /// <remarks>
    ///     ⚠ <b>Listed once so that the mint, the rendered <c>Secret</c> and the tests cannot disagree
    ///     about the set.</b> <c>ContainerRegistryCredentialTests</c> walks this array against what
    ///     <see cref="GenerateCredentials" /> returns and against what
    ///     <see cref="CredentialsSecretJson" /> renders, which is what catches a seventh field added to
    ///     one of the three.
    /// </remarks>
    public static ImmutableArray<string> CredentialFields { get; } = [
        AdminPasswordField,
        CoreSecretField,
        CsrfKeyField,
        JobServiceSecretField,
        RegistryHttpSecretField,
        DatabasePasswordField
    ];

    /// <summary>The handle that reads one of a registry's credentials back.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <param name="field">One of <see cref="CredentialFields" />.</param>
    /// <remarks>
    ///     ⚠ Built here rather than at the two call sites, so that the vault path is spelled once. A
    ///     reconciler and an action handler that each composed their own would be two spellings of one
    ///     address, and the failure — the handler reading a path the reconciler never wrote — is a
    ///     <c>listCredentials</c> that answers "not found" on a registry that works.
    /// </remarks>
    public static SecretRef CredentialRef(ResourceId id, string field) =>
        new() { Path = SecretPath(id), Field = field };

    /// <summary>The handle that reads a registry's administrator password back.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static SecretRef AdminPasswordRef(ResourceId id) => CredentialRef(id, AdminPasswordField);

    /// <summary>The username <c>listCredentials</c> returns.</summary>
    /// <remarks>
    ///     ⚠ <b><c>admin</c>, because Harbor seeds exactly that row and nothing renames it.</b>
    ///     <c>make/migrations/postgresql/0001_initial_schema.up.sql</c> inserts
    ///     <c>('admin', '', 'system admin', …)</c>; the username is not configurable and pretending
    ///     otherwise with a property would be a field that renders nothing.
    /// </remarks>
    public const string AdminUsername = "admin";

    /// <summary>The PostgreSQL role Harbor connects as.</summary>
    public const string DatabaseUsername = "postgres";

    /// <summary>The PostgreSQL database Harbor's core keeps its schema in.</summary>
    /// <remarks>
    ///     ⚠ <c>registry</c>, which is Harbor's own name for it and is not configurable: core's schema
    ///     migration and the job service both hard-code it. Two databases beside it —
    ///     <c>notaryserver</c> and <c>notarysigner</c> — are created by the same image's entrypoint and
    ///     are unused here, because Notary is removed from Harbor 2.12 onwards.
    /// </remarks>
    public const string SchemaDatabase = "registry";

    /// <summary>How many characters a generated password has.</summary>
    /// <remarks>
    ///     ⚠ 32 over <see cref="PasswordAlphabet" />'s 62 symbols is 190 bits, which is far past the
    ///     point where the password is the weakest thing about an endpoint served over plain
    ///     <c>http</c>. The honest reason not to go further is that this is a value a human pastes into
    ///     <c>docker login</c>.
    /// </remarks>
    public const int PasswordLength = 32;

    /// <summary>
    ///     How many characters core's CSRF key has. ⚠ <b>Exactly 32, and Harbor refuses to start
    ///     otherwise.</b>
    /// </summary>
    /// <remarks>
    ///     ⚠ It is an AES-256 key, hex-decoded from the environment; a value of any other length is a
    ///     core that crash-loops with a message about key size, per pod, after the caller was told
    ///     <c>202</c>. <c>goharbor/harbor-helm</c> spells the same constraint
    ///     <c>randAlphaNum 32 | b64enc</c>.
    /// </remarks>
    public const int CsrfKeyLength = 32;

    /// <summary>The symbols a generated credential is drawn from.</summary>
    /// <remarks>
    ///     ⚠ <b>Alphanumeric and nothing else, which is narrower than a password generator would
    ///     ordinarily pick and is forced.</b> These values travel through a <c>ConfigMap</c>'s YAML, a
    ///     PostgreSQL connection string and a <c>docker login</c> argument; a shell metacharacter or a
    ///     YAML special is a quoting bug in whichever of the three the author did not think about. The
    ///     entropy lost is bought back by <see cref="PasswordLength" />.
    /// </remarks>
    public const string PasswordAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    /// <summary>Generates one registry's full credential set.</summary>
    /// <remarks>
    ///     ⚠ <b>NOT IDEMPOTENT, AND THE PASS THAT CALLS IT STILL IS.</b> This returns different values
    ///     every call, which reads like a violation of docs/plan/08 § The reconcile loop's clause 1 and
    ///     is not: the reconciler offers them to <see cref="ISecretWriter.MintAsync" />, whose
    ///     <c>cas=0</c> keeps the first set and discards every later candidate, and then <i>resolves</i>
    ///     what the vault actually holds before rendering anything. The alternative — deriving a
    ///     password from the resource id so that it is reproducible — is an administrator credential
    ///     anyone who knows a GUID can compute.
    ///     <para>
    ///         ⚠ <see cref="RandomNumberGenerator.GetString" /> and not <c>Random.Shared</c>. These are
    ///         credentials; a non-cryptographic generator seeded from a clock is the classic way to
    ///         make one guessable.
    ///     </para>
    /// </remarks>
    public static Dictionary<string, string> GenerateCredentials() =>
        new(StringComparer.Ordinal) {
            [AdminPasswordField] = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength),
            [CoreSecretField] = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength),
            [CsrfKeyField] = RandomNumberGenerator.GetString(PasswordAlphabet, CsrfKeyLength),
            [JobServiceSecretField] = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength),
            [RegistryHttpSecretField] = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength),
            [DatabasePasswordField] = RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength)
        };

    // ── The constraint vocabularies ───────────────────────────────────────────────────────────

    /// <inheritdoc cref="KubeQuantity.Pattern" />
    /// <remarks>
    ///     ⚠ Pointed at <see cref="KubeQuantity" /> rather than copied — <c>QuantityParserTests</c>
    ///     fails if a further copy or a second suffix table appears.
    /// </remarks>
    public const string QuantityPattern = KubeQuantity.Pattern;

    /// <inheritdoc cref="KubeQuantity.OptionalPattern" />
    public const string OptionalQuantityPattern = KubeQuantity.OptionalPattern;

    /// <summary>The sizing presets of docs/plan/12 § Sizing vocabulary, <c>s1</c> family.</summary>
    /// <remarks>
    ///     ⚠ <b>This sizes the <i>registry</i> pod and nothing else.</b> Core, the portal, the job
    ///     service, the database and Redis are sized by <see cref="ControlPlaneCpu" />, which makes this
    ///     the third type in the catalogue whose quota meters are a sum over <i>heterogeneous</i>
    ///     components — after <c>CyberCloud.Storage/accounts</c> and <c>CyberCloud.Search/services</c>.
    ///     <para>
    ///         ⚠ <b><c>s1</c> rather than <c>c1</c>, and the choice is about the workload rather than
    ///         about the table.</b> A registry is bound by disk and by network; docs/plan/12's five
    ///         families are burstable, CPU-bound, general, memory-bound and latency-sensitive, and none
    ///         of them is that. <c>s1</c> — <i>"1:4 · General"</i> — is the closest, and taking it
    ///         rather than inventing a sixth family is the point of having a vocabulary.
    ///     </para>
    /// </remarks>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string Cpu, string Memory)>(StringComparer.Ordinal) {
            ["s1.nano"] = ("250m", "1Gi"),
            ["s1.micro"] = ("500m", "2Gi"),
            ["s1.small"] = ("1", "4Gi"),
            ["s1.medium"] = ("2", "8Gi"),
            ["s1.large"] = ("4", "16Gi"),
            ["s1.xlarge"] = ("8", "32Gi"),
            ["s1.2xlarge"] = ("16", "64Gi"),
            ["s1.4xlarge"] = ("32", "128Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>The CPU every component that is not the registry requests.</summary>
    /// <remarks>
    ///     ⚠ <b>A constant rather than a property, and it still costs quota.</b> A registry with two
    ///     replicas runs eight pods — core, portal and job service twice each, plus the database, Redis
    ///     and the registry — and a meter that counted only the registry would under-reserve by seven
    ///     of them on the default body.
    /// </remarks>
    public const string ControlPlaneCpu = "250m";

    /// <summary>The memory every component that is not the registry requests.</summary>
    /// <remarks>
    ///     ⚠ 512Mi is generous for the portal (an nginx serving static files) and tight for core, and
    ///     it is one figure rather than six because a per-component sizing table would be six
    ///     properties nobody would ever set. If a component needs its own rung, that is a schema change
    ///     and a meter term, in that order.
    /// </remarks>
    public const string ControlPlaneMemory = "512Mi";

    /// <summary>The database's data volume. ⚠ A constant, for the reason <see cref="ControlPlaneCpu" /> gives.</summary>
    /// <remarks>
    ///     ⚠ Harbor's PostgreSQL holds metadata — projects, repositories, tags, audit rows — and not
    ///     image layers. It grows with the <i>number</i> of artifacts rather than with their size, so a
    ///     tenant sizing it would be a tenant guessing at something the platform can state.
    /// </remarks>
    public const string DatabaseVolumeSize = "10Gi";

    /// <summary>Redis' data volume.</summary>
    /// <remarks>
    ///     ⚠ It exists at all because Harbor's job service keeps its queue in Redis: losing it loses
    ///     every in-flight garbage collection and replication job. It is small because nothing else is
    ///     in there.
    /// </remarks>
    public const string RedisVolumeSize = "1Gi";

    /// <summary>The volume name and mount path the registry's images live at.</summary>
    public const string RegistryVolume = "storage";

    /// <summary>Where <see cref="RegistryVolume" /> is mounted — <c>distribution</c>'s <c>rootdirectory</c>.</summary>
    public const string RegistryMountPath = "/storage";

    /// <summary>The key the registry's configuration is filed under in the <c>ConfigMap</c>.</summary>
    public const string RegistryConfigKey = "registry-config.yml";

    /// <summary>The key the job service's configuration is filed under.</summary>
    public const string JobServiceConfigKey = "jobservice-config.yml";

    /// <summary>
    ///     The body shape at <see cref="V2026" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Every default here is the chart's default, spelled as JSON — charts/README.md § The
    ///     annotation format. There is no <c>@default</c> directive because the chart's default
    ///     <i>is</i> the YAML literal on the annotated line.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the registry is billed in."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The registry's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster whose namespace holds the registry."
                ) {
                    Format = SchemaFormat.Uuid,
                    Widget = WidgetHint.Cluster,
                    Immutable = true
                },

                // ── The chart's API surface, in the chart's own declaration order ───────────────
                new(
                    "/properties/version",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Harbor minor version. The platform pins the patch, because Harbor "
                    + "publishes image tags per patch and a bare minor resolves to nothing. A minor "
                    + "leaving support is a portal notice and a 120-day window — docs/plan/12 "
                    + "§ Cross-cutting decisions."
                ) {
                    AllowedValues = ["2.14", "2.15"],
                    DefaultJson = "\"2.15\""
                },
                new(
                    "/properties/replicas",
                    SchemaKind.WholeNumber,
                    Required: true,
                    Description: "How many replicas of each stateless component — the API core, the "
                    + "web portal and the job service — run. Two is the smallest count that survives a "
                    + "node drain. The registry itself, the database and Redis each own a volume and "
                    + "run one replica whatever this says."
                ) {
                    Minimum = 1,
                    Maximum = 10,
                    DefaultJson = "2"
                },
                new(
                    "/properties/sizing",
                    SchemaKind.Nested,
                    Description: "CPU and memory for the registry pod, either by preset or explicitly. "
                    + "The other five components are sized by the platform and are not affected."
                ),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "A sizing preset from docs/plan/12. The registry uses the s1 family, "
                    + "which is 1 vCPU to 4 GiB."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"s1.small\""
                },
                new(
                    "/properties/sizing/cpu",
                    SchemaKind.Text,
                    Description: "Explicit vCPU quantity in Kubernetes form, for example 500m or 2. "
                    + "Empty means take it from the preset."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/sizing/memory",
                    SchemaKind.Text,
                    Description: "Explicit memory quantity in Kubernetes form, for example 4Gi. Empty "
                    + "means take it from the preset."
                ) {
                    Pattern = OptionalQuantityPattern,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/storage",
                    SchemaKind.Nested,
                    Description: "Where the image layers live."
                ),
                new(
                    "/properties/storage/size",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Image storage, in Kubernetes quantity form. Grows online; never "
                    + "shrinks. ⚠ This is a filesystem volume rather than the tenant's object-storage "
                    + "bucket, which is what docs/plan/13 asks for and what the platform cannot yet "
                    + "give it — see the registry's own documentation."
                ) {
                    Pattern = QuantityPattern,
                    DefaultJson = "\"100Gi\"",
                    ExampleJson = "\"100Gi\""
                },
                new(
                    "/properties/storage/class",
                    SchemaKind.Text,
                    Description: "StorageClass name for the image volume. Empty means the cluster "
                    + "default."
                ) {
                    Widget = WidgetHint.StorageClass,
                    Immutable = true,
                    DefaultJson = "\"\""
                },
                new(
                    "/properties/monitoring",
                    SchemaKind.Nested,
                    Description: "What the platform scrapes."
                ),
                new(
                    "/properties/monitoring/enabled",
                    SchemaKind.Boolean,
                    Description: "Whether Harbor's core exports Prometheus metrics and a PodMonitor "
                    + "selects them. On by default — docs/plan/12: \"a managed service the tenant "
                    + "cannot see the health of is a black box they will not trust with production\". "
                    + "Turning it off removes the metrics port as well as the scrape, so nothing is "
                    + "left listening on an unscraped address."
                ) {
                    DefaultJson = "true"
                }
            ]
        );

    /// <summary>
    ///     What a <c>POST …/listCredentials</c> returns.
    /// </summary>
    public static ResourceSchema ListCredentialsResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/endpoint",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The in-cluster registry endpoint — what docker login takes. ⚠ http "
                    + "rather than https, and no external address is returned, because there is none."
                ),
                new(
                    "/portalUrl",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Harbor's web UI, which is a different Service from the API endpoint "
                    + "because this row renders no single front door."
                ),
                new(
                    "/username",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The administrator's username. Always admin — Harbor seeds that row "
                    + "and nothing renames it."
                ),
                new(
                    "/password",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The administrator's password, read from the tenant's Vault for this "
                    + "call only. Minted at create; never derived from the resource id."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The Harbor minor a body asks for.</summary>
    public static string Version(JsonElement desired) =>
        Root(desired, "version") is { ValueKind: JsonValueKind.String } value
        && value.GetString() is { Length: > 0 } declared
            ? declared
            : DefaultVersion;

    /// <summary>The image tag a body's version pins to.</summary>
    /// <returns>
    ///     ⚠ Falls back to the default minor's tag rather than to the raw value: an unrecognised minor
    ///     must not become an image reference nothing publishes.
    /// </returns>
    public static string ImageTag(JsonElement desired) =>
        PinnedPatch.TryGetValue(Version(desired), out var pinned) ? pinned : PinnedPatch[DefaultVersion];

    /// <summary>The image reference one Harbor component runs.</summary>
    /// <param name="repository">The repository under <see cref="ImageRegistry" />.</param>
    /// <param name="desired">The validated desired body.</param>
    public static string Image(string repository, JsonElement desired) =>
        ImageRegistry + "/" + repository + ":" + ImageTag(desired);

    /// <summary>The replica count of each stateless component.</summary>
    public static int Replicas(JsonElement desired) => Number(desired, "replicas", DefaultReplicas);

    /// <summary>The image-storage size a body asks for.</summary>
    public static string StorageSize(JsonElement desired) =>
        Text(desired, "storage", "size", DefaultStorageSize);

    /// <summary>Whether the desired body asks for metrics and a scrape.</summary>
    public static bool MonitoringEnabled(JsonElement desired) =>
        Flag(desired, "monitoring", "enabled", true);

    /// <summary>
    ///     The CPU and memory the registry pod asks for: the explicit quantities when both are given,
    ///     otherwise the preset's.
    /// </summary>
    public static (string Cpu, string Memory) Resources(JsonElement desired) {
        var preset = Text(desired, "sizing", "preset", DefaultPreset);
        var fallback = Presets.TryGetValue(preset, out var found)
            ? found
            : (Cpu: string.Empty, Memory: string.Empty);

        var cpu = Text(desired, "sizing", "cpu", string.Empty);
        var memory = Text(desired, "sizing", "memory", string.Empty);

        return (cpu.Length > 0 ? cpu : fallback.Cpu, memory.Length > 0 ? memory : fallback.Memory);
    }

    // ── The objects a desired body becomes ────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>Secret</c> every component reads its credentials out of.
    /// </summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="credentials">The credentials, as the vault holds them.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THE ONE OBJECT THIS PROVIDER RENDERS THAT CARRIES SECRET VALUES, AND IT IS BUILT
    ///         FROM WHAT THE VAULT RETURNED RATHER THAN FROM DESIRED STATE.</b> The registry's body has
    ///         no credential property and must not grow one: docs/plan/00 § Non-negotiables keeps
    ///         secrets out of grain state, and a body is grain state.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>data</c> with the base64 written out rather than <c>stringData</c>, and the
    ///         reason is the read-back.</b> <c>stringData</c> is write-only: the API server folds it
    ///         into <c>data</c> and never returns it, so an object applied with one field and read back
    ///         with another is an object <see cref="Matches" /> would have to accept in two shapes, one
    ///         of which no real cluster ever produces.
    ///     </para>
    /// </remarks>
    public static string CredentialsSecretJson(string name, IReadOnlyDictionary<string, string> credentials) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentNullException.ThrowIfNull(credentials);

        var data = new JsonObject();

        foreach (var field in CredentialFields) {
            data[field] = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(credentials.TryGetValue(field, out var value) ? value : string.Empty)
            );
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = CredentialsSecretName(name) },
            ["type"] = "Opaque",
            ["data"] = data
        }.ToJsonString();
    }

    /// <summary>The <c>ConfigMap</c> holding the registry's and the job service's YAML.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>NEITHER DOCUMENT CARRIES A CREDENTIAL, which is why they can live in a
    ///     <c>ConfigMap</c>.</b> <c>distribution</c> reads its HTTP secret from
    ///     <c>REGISTRY_HTTP_SECRET</c> and the job service reads its from <c>CORE_SECRET</c>, both
    ///     environment variables sourced from <see cref="CredentialsSecretName" /> — so the two files
    ///     here are configuration and the <c>Secret</c> is the only object with a value in it. A
    ///     credential inlined into this YAML would be readable by anyone holding <c>get configmaps</c>
    ///     in the namespace, which is a strictly weaker right than <c>get secrets</c>.
    /// </remarks>
    public static string ConfigMapJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = ConfigMapName(name) },
            ["data"] = new JsonObject {
                [RegistryConfigKey] = RegistryConfigYaml(),
                [JobServiceConfigKey] = JobServiceConfigYaml(name, desired)
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     <c>distribution</c>'s <c>config.yml</c>.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THERE IS NO <c>auth:</c> BLOCK, AND THAT IS THE LARGEST KNOWN GAP IN THIS ROW.</b>
    ///         <c>goharbor/harbor-helm</c> renders <c>auth: htpasswd</c> here and computes the file
    ///         with Sprig's <c>htpasswd</c>, which is <b>bcrypt</b>; <c>distribution</c>'s htpasswd
    ///         backend accepts bcrypt and nothing else. .NET ships no bcrypt and this repository
    ///         references no package that does, so the hash cannot be produced inside a reconcile pass.
    ///         The registry therefore accepts unauthenticated requests from anything that can reach its
    ///         <c>Service</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>What bounds it, stated rather than assumed.</b> The <c>Service</c> is
    ///         <c>ClusterIP</c> in <c>ReconcileDriver.NamespaceFor</c>'s
    ///         <c>{subscriptionId:N}-{resourceGroup}</c>, which holds one tenant's resources and no
    ///         other tenant's — so the exposure is to the tenant's own workloads rather than across
    ///         tenants, and Harbor's own RBAC still gates every path a client reaches through core.
    ///         What is genuinely lost is defence in depth: a tenant workload that should only be able to
    ///         <i>pull</i> can <i>push</i>, by addressing the registry directly and skipping core.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is written down rather than papered over</b> —
    ///         <c>charts/managed/harbor/conformance.yaml § owed</c>,
    ///         <c>registry-accepts-unauthenticated-callers-from-inside-the-namespace</c> — with the two
    ///         things that close it: a bcrypt implementation the platform owns, or a
    ///         <c>NetworkPolicy</c> that lets only core's pods reach the registry's, which is a
    ///         sixteenth object and a Cilium/Kube-OVN decision rather than a provider's.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>storage.filesystem.rootdirectory</c> and not <c>storage.s3</c>. docs/plan/13 says
    ///         the backend <i>"is the tenant's SeaweedFS bucket"</i>; that bucket is another provider's
    ///         resource and <c>ReconcileContext</c> has no reader for one — module-layering.txt records
    ///         this family as the third to want the same missing line.
    ///     </para>
    ///     <para>
    ///         ⚠ <c>delete.enabled: true</c>, because without it Harbor's garbage collection deletes
    ///         nothing and a tenant's storage only ever grows. It is off by default in
    ///         <c>distribution</c>, which is the kind of default an operator would have supplied and
    ///         there is no operator.
    ///     </para>
    /// </remarks>
    public static string RegistryConfigYaml() =>
        "version: 0.1\n"
        + "log:\n"
        + "  level: info\n"
        + "  fields:\n"
        + "    service: registry\n"
        + "storage:\n"
        + "  filesystem:\n"
        + "    rootdirectory: " + RegistryMountPath + "\n"
        + "  cache:\n"
        + "    layerinfo: redis\n"
        + "  maintenance:\n"
        + "    uploadpurging:\n"
        + "      enabled: false\n"
        + "  delete:\n"
        + "    enabled: true\n"
        + "  redirect:\n"
        + "    disable: true\n"
        + "http:\n"
        + "  addr: :" + Text(RegistryPort) + "\n"
        + "  relativeurls: false\n"
        + "  debug:\n"
        + "    addr: localhost:5001\n"
        + "health:\n"
        + "  storagedriver:\n"
        + "    enabled: true\n"
        + "    interval: 30s\n"
        + "    threshold: 3\n";

    /// <summary>Harbor's job service <c>config.yml</c>.</summary>
    /// <remarks>
    ///     ⚠ <c>job_loggers</c> is <c>STD_OUTPUT</c> rather than <c>FILE</c>, which is a decision this
    ///     row has to take because there is no operator to take it. The file logger writes into the
    ///     pod's writable layer and Harbor's UI reads job logs back over the job service's own API — so
    ///     a restart loses them either way, and the standard-output logger at least puts them somewhere
    ///     docs/plan/16's collector already reads.
    /// </remarks>
    public static string JobServiceConfigYaml(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        return "protocol: \"http\"\n"
            + "port: " + Text(JobServicePort) + "\n"
            + "worker_pool:\n"
            + "  workers: 10\n"
            + "  backend: \"redis\"\n"
            + "  redis_pool:\n"
            + "    redis_url: \"redis://" + RedisName(name) + ":" + Text(RedisPort) + "/1\"\n"
            + "    namespace: \"harbor_job_service_namespace\"\n"
            + "    idle_timeout_second: 3600\n"
            + "job_loggers:\n"
            + "  - name: \"STD_OUTPUT\"\n"
            + "    level: \"INFO\"\n"
            + "metric:\n"
            + "  enabled: " + (MonitoringEnabled(desired) ? "true" : "false") + "\n"
            + "  path: /metrics\n"
            + "  port: " + Text(MetricsPort) + "\n";
    }

    // ── The workloads ─────────────────────────────────────────────────────────────────────────

    /// <summary>The database <c>StatefulSet</c> — Harbor's own PostgreSQL build.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>One replica, and it is a single point of failure that is written down rather than
    ///     hidden.</b> Harbor's <c>harbor-db</c> image is a plain PostgreSQL with Harbor's bootstrap in
    ///     it and has no replication of its own; making it highly available means CloudNativePG, which
    ///     is <c>CyberCloud.DBforPostgreSQL/servers</c> and therefore another provider's resource.
    ///     Third entry in the same list — <c>conformance.yaml § owed</c>,
    ///     <c>the-database-is-a-single-point-of-failure</c>.
    /// </remarks>
    public static string DatabaseSetJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var container = new JsonObject {
            ["name"] = "database",
            ["image"] = Image(DatabaseImageRepository, desired),
            ["ports"] = new JsonArray { ContainerPort("postgres", DatabasePort) },
            ["env"] = new JsonArray {
                SecretEnv("POSTGRES_PASSWORD", name, DatabasePasswordField),
                new JsonObject { ["name"] = "PGDATA", ["value"] = "/var/lib/postgresql/data/pgdata" }
            },
            ["volumeMounts"] = new JsonArray {
                new JsonObject { ["name"] = "data", ["mountPath"] = "/var/lib/postgresql/data" }
            },
            ["resources"] = ControlPlaneResources(),
            // ⚠ `pg_isready` and not a TCP probe. PostgreSQL binds its port before it has finished
            // recovery, so a TCP check reports a database that refuses every connection as ready — and
            // core's first action is a schema migration.
            ["readinessProbe"] = ExecProbe(["pg_isready", "-U", DatabaseUsername], 10, 3)
        };

        return WorkloadJson(
            DatabaseName(name),
            name,
            DatabaseComponent,
            replicas: 1,
            containers: [container],
            volumes: null,
            claim: ClaimTemplate("data", DatabaseVolumeSize, StorageClass(desired)),
            serviceName: DatabaseName(name)
        );
    }

    /// <summary>The Redis <c>StatefulSet</c> — the job service's queue.</summary>
    public static string RedisSetJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var container = new JsonObject {
            ["name"] = "redis",
            ["image"] = Image(RedisImageRepository, desired),
            ["ports"] = new JsonArray { ContainerPort("redis", RedisPort) },
            ["volumeMounts"] = new JsonArray {
                new JsonObject { ["name"] = "data", ["mountPath"] = "/var/lib/redis" }
            },
            ["resources"] = ControlPlaneResources(),
            ["readinessProbe"] = TcpProbe(RedisPort, 10, 3)
        };

        return WorkloadJson(
            RedisName(name),
            name,
            RedisComponent,
            replicas: 1,
            containers: [container],
            volumes: null,
            claim: ClaimTemplate("data", RedisVolumeSize, StorageClass(desired)),
            serviceName: RedisName(name)
        );
    }

    /// <summary>The registry <c>StatefulSet</c> — <c>distribution</c> and <c>registryctl</c>.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>TWO CONTAINERS IN ONE POD, AND THE SECOND ONE IS WHY GARBAGE COLLECTION WORKS.</b>
    ///         <c>registryctl</c> is Harbor's control sidecar: the job service calls it to run
    ///         <c>registry garbage-collect</c>, which has to happen in the same filesystem namespace as
    ///         the registry's storage. A <c>Deployment</c> of its own would be a second pod with a
    ///         second copy of a <c>ReadWriteOnce</c> volume, which does not schedule.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>One replica, and the reason is the volume rather than a preference.</b> The claim is
    ///         <c>ReadWriteOnce</c>, so a second replica would stay <c>Pending</c> forever on most
    ///         storage classes. That is also why <c>/properties/replicas</c> says in its own description
    ///         that it does not apply here: a property that silently did nothing for one of six
    ///         components would be worse than one that says so.
    ///     </para>
    /// </remarks>
    public static string RegistrySetJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var (cpu, memory) = Resources(desired);

        var registry = new JsonObject {
            ["name"] = "registry",
            ["image"] = Image("registry-photon", desired),
            ["ports"] = new JsonArray { ContainerPort("registry", RegistryPort) },
            ["env"] = new JsonArray {
                SecretEnv("REGISTRY_HTTP_SECRET", name, RegistryHttpSecretField),
                new JsonObject {
                    ["name"] = "REGISTRY_REDIS_ADDR",
                    ["value"] = RedisName(name) + ":" + Text(RedisPort)
                }
            },
            ["volumeMounts"] = new JsonArray {
                new JsonObject {
                    ["name"] = "config",
                    ["mountPath"] = "/etc/registry/config.yml",
                    ["subPath"] = RegistryConfigKey
                },
                new JsonObject { ["name"] = RegistryVolume, ["mountPath"] = RegistryMountPath }
            },
            ["readinessProbe"] = HttpProbe("/", RegistryPort, 10, 3)
        };

        if (cpu.Length > 0 && memory.Length > 0) {
            var quantities = new JsonObject { ["cpu"] = cpu, ["memory"] = memory };
            registry["resources"] = new JsonObject {
                ["requests"] = quantities.DeepClone(), ["limits"] = quantities
            };
        }

        var controller = new JsonObject {
            ["name"] = "registryctl",
            ["image"] = Image("harbor-registryctl", desired),
            ["ports"] = new JsonArray { ContainerPort("controller", RegistryControllerPort) },
            ["env"] = new JsonArray {
                SecretEnv("CORE_SECRET", name, CoreSecretField),
                SecretEnv("JOBSERVICE_SECRET", name, JobServiceSecretField)
            },
            ["volumeMounts"] = new JsonArray {
                new JsonObject {
                    ["name"] = "config",
                    ["mountPath"] = "/etc/registry/config.yml",
                    ["subPath"] = RegistryConfigKey
                },
                new JsonObject { ["name"] = RegistryVolume, ["mountPath"] = RegistryMountPath }
            },
            ["resources"] = ControlPlaneResources()
        };

        return WorkloadJson(
            RegistryName(name),
            name,
            RegistryComponent,
            replicas: 1,
            containers: [registry, controller],
            volumes: ConfigVolume(name),
            claim: ClaimTemplate(RegistryVolume, StorageSize(desired), StorageClass(desired)),
            serviceName: RegistryName(name)
        );
    }

    /// <summary>The core <c>Deployment</c> — Harbor's API, its auth and its <c>/v2/</c> proxy.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     ⚠ <b>Every credential arrives as a <c>secretKeyRef</c> and none as a literal.</b> That is
    ///     what keeps this document byte-identical whatever the vault holds, which in turn is what makes
    ///     the pass idempotent — see <see cref="GenerateCredentials" />. A rendered value would also put
    ///     the administrator's password into the <c>Deployment</c>'s own spec, readable by anyone
    ///     holding <c>get deployments</c>.
    /// </remarks>
    public static string CoreDeploymentJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var monitoring = MonitoringEnabled(desired);

        var ports = new JsonArray { ContainerPort("http", CorePort) };
        if (monitoring) {
            ports.Add(ContainerPort("metrics", MetricsPort));
        }

        var env = new JsonArray {
            SecretEnv("HARBOR_ADMIN_PASSWORD", name, AdminPasswordField),
            SecretEnv("CORE_SECRET", name, CoreSecretField),
            SecretEnv("CSRF_KEY", name, CsrfKeyField),
            SecretEnv("JOBSERVICE_SECRET", name, JobServiceSecretField),
            SecretEnv("REGISTRY_HTTP_SECRET", name, RegistryHttpSecretField),
            SecretEnv("POSTGRESQL_PASSWORD", name, DatabasePasswordField),
            Env("POSTGRESQL_HOST", DatabaseName(name)),
            Env("POSTGRESQL_PORT", Text(DatabasePort)),
            Env("POSTGRESQL_USERNAME", DatabaseUsername),
            Env("POSTGRESQL_DATABASE", SchemaDatabase),
            Env("POSTGRESQL_SSLMODE", "disable"),
            Env("_REDIS_URL_CORE", "redis://" + RedisName(name) + ":" + Text(RedisPort) + "/0"),
            Env("_REDIS_URL_REG", "redis://" + RedisName(name) + ":" + Text(RedisPort) + "/2"),
            Env("REGISTRY_URL", "http://" + RegistryName(name) + ":" + Text(RegistryPort)),
            Env(
                "REGISTRY_CONTROLLER_URL",
                "http://" + RegistryName(name) + ":" + Text(RegistryControllerPort)
            ),
            Env("PORTAL_URL", "http://" + PortalName(name) + ":" + Text(PortalPort)),
            Env("JOBSERVICE_URL", "http://" + JobServiceName(name) + ":" + Text(JobServicePort)),
            Env("TOKEN_SERVICE_URL", "http://" + CoreName(name) + ":" + Text(CorePort) + "/service/token"),
            Env("CORE_URL", "http://" + CoreName(name) + ":" + Text(CorePort)),
            // ⚠ WITH_TRIVY is false and the property that would turn it on does not exist. Scanning is
            // docs/plan/01's M2 row; a flag whose only honest value is `false` is a flag that publishes
            // a feature nobody can have.
            Env("WITH_TRIVY", "false"),
            Env("LOG_LEVEL", "info"),
            Env("METRIC_ENABLE", monitoring ? "true" : "false"),
            Env("METRIC_PATH", "/metrics"),
            Env("METRIC_PORT", Text(MetricsPort))
        };

        var container = new JsonObject {
            ["name"] = "core",
            ["image"] = Image("harbor-core", desired),
            ["ports"] = ports,
            ["env"] = env,
            ["resources"] = ControlPlaneResources(),
            // ⚠ /api/v2.0/ping and not /. Core answers `/` with a redirect before its database
            // migration has run, so a readiness probe on the root would put a core that cannot serve a
            // single API call into the Service.
            ["readinessProbe"] = HttpProbe("/api/v2.0/ping", CorePort, 10, 3),
            ["livenessProbe"] = HttpProbe("/api/v2.0/ping", CorePort, 20, 5)
        };

        return WorkloadJson(
            CoreName(name),
            name,
            CoreComponent,
            Replicas(desired),
            [container],
            volumes: null,
            claim: null,
            serviceName: null
        );
    }

    /// <summary>The portal <c>Deployment</c> — an nginx serving Harbor's web UI.</summary>
    public static string PortalDeploymentJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var container = new JsonObject {
            ["name"] = "portal",
            ["image"] = Image("harbor-portal", desired),
            ["ports"] = new JsonArray { ContainerPort("http", PortalPort) },
            ["resources"] = ControlPlaneResources(),
            ["readinessProbe"] = HttpProbe("/", PortalPort, 10, 3)
        };

        return WorkloadJson(
            PortalName(name),
            name,
            PortalComponent,
            Replicas(desired),
            [container],
            volumes: null,
            claim: null,
            serviceName: null
        );
    }

    /// <summary>The job service <c>Deployment</c> — garbage collection, replication, scan jobs.</summary>
    public static string JobServiceDeploymentJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var container = new JsonObject {
            ["name"] = "jobservice",
            ["image"] = Image("harbor-jobservice", desired),
            ["ports"] = new JsonArray { ContainerPort("http", JobServicePort) },
            ["env"] = new JsonArray {
                SecretEnv("CORE_SECRET", name, CoreSecretField),
                SecretEnv("JOBSERVICE_SECRET", name, JobServiceSecretField),
                Env("CORE_URL", "http://" + CoreName(name) + ":" + Text(CorePort)),
                Env(
                    "REGISTRY_CONTROLLER_URL",
                    "http://" + RegistryName(name) + ":" + Text(RegistryControllerPort)
                ),
                Env("CONFIG_PATH", "/etc/jobservice/config.yml")
            },
            ["volumeMounts"] = new JsonArray {
                new JsonObject {
                    ["name"] = "config",
                    ["mountPath"] = "/etc/jobservice/config.yml",
                    ["subPath"] = JobServiceConfigKey
                }
            },
            ["resources"] = ControlPlaneResources(),
            ["readinessProbe"] = HttpProbe("/api/v1/stats", JobServicePort, 10, 3)
        };

        return WorkloadJson(
            JobServiceName(name),
            name,
            JobServiceComponent,
            Replicas(desired),
            [container],
            volumes: ConfigVolume(name),
            claim: null,
            serviceName: null
        );
    }

    // ── The services ──────────────────────────────────────────────────────────────────────────
    //
    // ⚠ EVERY ONE OF THE SIX IS ClusterIP AND NONE OF THEM IS A LoadBalancer, AND THAT IS THE THIRD
    // ROW IN THE CATALOGUE TO DECLARE NO EXPOSURE AXIS AT ALL. docs/plan/12 § Cross-cutting decisions
    // requires an explicit CIDR allow-list on any external exposure; a Kubernetes `Service` has
    // `loadBalancerSourceRanges` and would carry one, so unlike charts/managed/seaweedfs and
    // charts/managed/kubernetes the blocker here is NOT a missing upstream field. It is that the thing
    // a tenant would expose is a REGISTRY over plain HTTP with the internal-auth gap
    // RegistryConfigYaml records — and publishing that is the one thing that paragraph forbids in as
    // many words. conformance.yaml § owed, `one-front-door`, is where both halves close together.

    /// <summary>The front-door <c>Service</c> — core, under the resource's own name.</summary>
    public static string CoreServiceJson(string name, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var ports = new JsonArray { ServicePort("http", CorePort) };
        if (MonitoringEnabled(desired)) {
            ports.Add(ServicePort("metrics", MetricsPort));
        }

        return ServiceJson(name, name, CoreComponent, ports);
    }

    /// <summary>The portal's <c>Service</c>.</summary>
    public static string PortalServiceJson(string name) =>
        ServiceJson(PortalName(name), name, PortalComponent, [ServicePort("http", PortalPort)]);

    /// <summary>The job service's <c>Service</c>.</summary>
    public static string JobServiceServiceJson(string name) =>
        ServiceJson(
            JobServiceName(name),
            name,
            JobServiceComponent,
            [ServicePort("http", JobServicePort)]
        );

    /// <summary>The registry's <c>Service</c> — reached by core and by nothing outside the namespace.</summary>
    public static string RegistryServiceJson(string name) =>
        ServiceJson(
            RegistryName(name),
            name,
            RegistryComponent,
            [ServicePort("registry", RegistryPort), ServicePort("controller", RegistryControllerPort)]
        );

    /// <summary>The database's <c>Service</c>.</summary>
    public static string DatabaseServiceJson(string name) =>
        ServiceJson(DatabaseName(name), name, DatabaseComponent, [ServicePort("postgres", DatabasePort)]);

    /// <summary>Redis' <c>Service</c>.</summary>
    public static string RedisServiceJson(string name) =>
        ServiceJson(RedisName(name), name, RedisComponent, [ServicePort("redis", RedisPort)]);

    /// <summary>The <c>PodMonitor</c> that scrapes core's metrics endpoint.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>Core's pods and not every component's.</b> Harbor's exporter — a seventh component this
    ///     row does not render — is what turns the database's and the registry's internals into
    ///     metrics; core exports its own API and job counters without it. Selecting all six components
    ///     would scrape five ports nothing is listening on, which is a <c>PodMonitor</c> that looks
    ///     healthy and reports five targets down forever.
    /// </remarks>
    public static string PodMonitorJson(string name) {
        ArgumentException.ThrowIfNullOrEmpty(name);

        var selector = new JsonObject();
        foreach (var (key, value) in PodLabels(name, CoreComponent)) {
            selector[key] = value;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["spec"] = new JsonObject {
                ["selector"] = new JsonObject { ["matchLabels"] = selector },
                ["podMetricsEndpoints"] = new JsonArray {
                    new JsonObject { ["port"] = "metrics", ["path"] = "/metrics" }
                }
            }
        }.ToJsonString();
    }

    /// <summary>
    ///     Whether an object read back from a cluster carries what the desired body asks for.
    /// </summary>
    /// <param name="objectJson">The object's JSON, exactly as the API server returned it.</param>
    /// <param name="desired">The desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>CONTAINMENT, AND FOR THE REASON <c>NatsClusters</c> FOUND RATHER THAN THE ONE FIVE
    ///         OTHER PROVIDERS GIVE.</b> The usual argument is a CRD's <c>+kubebuilder:default</c>
    ///         markers or an operator's mutating webhook. Neither applies: the archived operator's CRDs
    ///         are not installed and are not what this row renders. What forces containment is that
    ///         <b>five of the six kinds here are built-in</b>, and a built-in kind is the most heavily
    ///         defaulted object in Kubernetes — a <c>Deployment</c> comes back with a
    ///         <c>strategy</c>, a <c>revisionHistoryLimit</c>, a <c>terminationGracePeriodSeconds</c>,
    ///         a <c>dnsPolicy</c>, a <c>schedulerName</c> and an <c>imagePullPolicy</c> nothing here
    ///         sent, and a <c>Service</c> comes back with a <c>clusterIP</c>, a <c>sessionAffinity</c>
    ///         and an <c>ipFamilyPolicy</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The archived operator makes this the FIRST family where the usual argument is not
    ///         merely false but unavailable</b>, which is a distinction worth keeping:
    ///         <c>KafkaClusters</c> and <c>ClickHouseClusters</c> found CRDs that declared no defaults,
    ///         and could at least look. Here there is no CRD to look at.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It dispatches on kind AND on the component label, and the component label is why
    ///         this works at all.</b> Six <c>Service</c>s and three <c>Deployment</c>s reach this
    ///         function as nine documents with no address attached — see § The component vocabulary.
    ///         An unrecognised document is <see langword="false" /> rather than assumed.
    ///     </para>
    /// </remarks>
    public static bool Matches(string objectJson, JsonElement desired) {
        JsonNode? parsed;
        try {
            parsed = JsonNode.Parse(objectJson);
        } catch (JsonException) {
            return false;
        }

        if (parsed is not JsonObject document) {
            return false;
        }

        return (document["kind"]?.GetValue<string>() ?? Classify(document)) switch {
            "Secret" => MatchesCredentialsSecret(document),
            "ConfigMap" => MatchesConfigMap(document),
            "Service" => MatchesService(document, desired),
            "PodMonitor" => MatchesPodMonitor(document),
            "Deployment" or "StatefulSet" => MatchesWorkload(document, desired),
            _ => false
        };
    }

    /// <summary>
    ///     What kind a document rendered by this provider is, when it has not been applied yet.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>A RENDERED BODY CARRIES NO <c>kind</c> AND SIX DIFFERENT KINDS ARRIVE HERE, WHICH IS
    ///         A SHAPE NO EARLIER FAMILY HAD.</b> <c>KubeCommandBuilder</c> injects <c>kind</c> from the
    ///         <see cref="GroupVersionKind" /> on the apply path, so a document only carries one after
    ///         it has been applied and read back. Every provider before this one owned at most two
    ///         kinds, so <c>null or "TheirKind"</c> was an honest single case; here it would have to
    ///         mean six things at once.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>So the fallback reads the document's own SHAPE, and each test is a field only that
    ///         kind has.</b> It is deliberately not a guess: a document matching none of them returns
    ///         the empty string and <see cref="Matches" /> answers <see langword="false" />, which is
    ///         the same answer an unrecognised <c>kind</c> gets.
    ///     </para>
    /// </remarks>
    static string Classify(JsonObject document) {
        if (document["data"] is JsonObject) {
            // ⚠ `type: Opaque` is what separates the credentials Secret from the ConfigMap — both carry
            // a `data` map and nothing else does.
            return document["type"] is not null ? "Secret" : "ConfigMap";
        }

        if (document["spec"] is not JsonObject spec) {
            return string.Empty;
        }

        if (spec["podMetricsEndpoints"] is JsonArray) {
            return "PodMonitor";
        }

        if (spec["template"] is JsonObject) {
            // ⚠ A `volumeClaimTemplates` array is the only structural difference between the two
            // workload kinds here, and the comparison does not depend on which it is — MatchesWorkload
            // branches on the COMPONENT rather than on the kind, for the reason its own remarks give.
            return spec["volumeClaimTemplates"] is JsonArray ? "StatefulSet" : "Deployment";
        }

        return spec["ports"] is JsonArray ? "Service" : string.Empty;
    }

    /// <summary>Whether the credentials <c>Secret</c> is there and carries every field.</summary>
    /// <remarks>
    ///     ⚠ <b>PRESENCE AND NOT CONTENT, AND THAT IS FORCED RATHER THAN LAZY.</b> This object's
    ///     content is deliberately not in the body — the credentials live in the vault and reach a
    ///     manifest for the length of one pass — so there is nothing here to compare against, and
    ///     inventing something would mean putting a credential in desired state.
    ///     <para>
    ///         ⚠ What it still catches is the failure that matters: a <c>Secret</c> deleted by a
    ///         well-meant <c>kubectl</c>, emptied by an admission policy, or never applied is six
    ///         components whose <c>secretKeyRef</c>s cannot resolve, which is a pod stuck in
    ///         <c>CreateContainerConfigError</c> and a registry that never finishes. All of those land
    ///         as a missing field and all of them make this false, so the next pass re-renders it from
    ///         the vault.
    ///     </para>
    /// </remarks>
    static bool MatchesCredentialsSecret(JsonObject document) {
        if (document["data"] is not JsonObject data) {
            return false;
        }

        foreach (var field in CredentialFields) {
            if (data[field]?.GetValue<string>() is not { Length: > 0 }) {
                return false;
            }
        }

        return true;
    }

    static bool MatchesConfigMap(JsonObject document) =>
        document["data"] is JsonObject data
        && data[RegistryConfigKey]?.GetValue<string>() is { Length: > 0 }
        && data[JobServiceConfigKey]?.GetValue<string>() is { Length: > 0 };

    static bool MatchesService(JsonObject document, JsonElement desired) {
        if (document["spec"] is not JsonObject spec
            || spec["selector"] is not JsonObject selector
            || selector["app.kubernetes.io/component"]?.GetValue<string>() is not { } component) {
            return false;
        }

        // ⚠ The metrics port is the only thing a body moves on any Service, so this is the only
        // Service comparison that reads the body at all. The other five are a presence check on the
        // selector — which is what a Service that was recreated by hand with the wrong selector fails,
        // and that is a registry whose front door routes to nothing.
        if (!string.Equals(component, CoreComponent, StringComparison.Ordinal)) {
            return true;
        }

        var wanted = MonitoringEnabled(desired) ? 2 : 1;

        return spec["ports"] is JsonArray ports && ports.Count == wanted;
    }

    static bool MatchesPodMonitor(JsonObject document) =>
        document["spec"] is JsonObject spec
        && spec["selector"] is JsonObject selector
        && selector["matchLabels"] is JsonObject labels
        && labels["app.kubernetes.io/component"]?.GetValue<string>() == CoreComponent;

    /// <summary>Whether a <c>Deployment</c> or a <c>StatefulSet</c> carries the desired spec.</summary>
    /// <remarks>
    ///     ⚠ <b>The three stateless components are compared on their replica count and the three
    ///     volume-owning ones are not</b>, because <c>/properties/replicas</c> genuinely does not
    ///     reach them — see <see cref="RegistrySetJson" />. Comparing all six against
    ///     <see cref="Replicas" /> would report permanent drift on the database the moment a tenant
    ///     asked for two replicas of anything.
    /// </remarks>
    static bool MatchesWorkload(JsonObject document, JsonElement desired) {
        if (document["spec"] is not JsonObject spec
            || spec["template"] is not JsonObject template
            || template["metadata"] is not JsonObject metadata
            || metadata["labels"] is not JsonObject labels
            || labels["app.kubernetes.io/component"]?.GetValue<string>() is not { } component) {
            return false;
        }

        if (template["spec"] is not JsonObject pod || pod["containers"] is not JsonArray containers) {
            return false;
        }

        // ⚠ THE IMAGE TAG IS COMPARED ON EVERY COMPONENT, which is what makes a version change
        // observable. All six images are pinned from the same body property, so a workload still
        // running the old tag is the drift an upgrade has to converge away.
        var tag = ":" + ImageTag(desired);

        foreach (var container in containers) {
            if (container is not JsonObject entry
                || entry["image"]?.GetValue<string>() is not { } image
                || !image.EndsWith(tag, StringComparison.Ordinal)) {
                return false;
            }
        }

        var stateless = component is CoreComponent or PortalComponent or JobServiceComponent;

        if (stateless && spec["replicas"]?.GetValue<int>() != Replicas(desired)) {
            return false;
        }

        if (!string.Equals(component, RegistryComponent, StringComparison.Ordinal)) {
            return true;
        }

        // The registry's claim template is where the tenant's storage size lands.
        return spec["volumeClaimTemplates"] is JsonArray claims
            && claims.Count == 1
            && claims[0] is JsonObject claim
            && (claim["spec"] as JsonObject)?["resources"] is JsonObject resources
            && (resources["requests"] as JsonObject)?["storage"]?.GetValue<string>()
            == StorageSize(desired);
    }

    // ── A body, for tests, fixtures and the conformance case ──────────────────────────────────

    /// <summary>Builds a body that satisfies <see cref="Schema2026" />.</summary>
    /// <param name="clusterId">The cluster to place the registry in.</param>
    /// <param name="replicas">How many replicas of each stateless component.</param>
    /// <param name="storageSize">The image volume's size.</param>
    /// <param name="version">The Harbor minor.</param>
    /// <param name="location">The region.</param>
    /// <remarks>
    ///     ⚠ Every property it writes is a <b>leaf</b>. <c>ResourceSchema.Project</c> skips a
    ///     <see cref="SchemaKind.Nested" /> container and rebuilds it from whichever leaf lands first,
    ///     so a body carrying an empty object would not survive the read-back the conformance suite
    ///     compares canonically.
    /// </remarks>
    public static string Body(
        Guid clusterId,
        int replicas = 2,
        string storageSize = "100Gi",
        string version = DefaultVersion,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["version"] = version,
                ["replicas"] = replicas,
                ["storage"] = new JsonObject { ["size"] = storageSize },
                ["monitoring"] = new JsonObject { ["enabled"] = true }
            }
        }.ToJsonString();

    // ── The schema's own defaults, once ───────────────────────────────────────────────────────
    //
    // ⚠ These are the same literals as the `DefaultJson` values above, and they exist because the
    // write path stores a body AS SENT — SchemaProperty.DefaultJson's own remarks say the validator
    // does not substitute. So every reader above has to know what an absent property means, and a
    // reader that spelled it inline would be a second place the default lives.

    /// <summary>The Harbor minor an absent <c>version</c> means.</summary>
    public const string DefaultVersion = "2.15";

    const string DefaultPreset = "s1.small";
    const string DefaultStorageSize = "100Gi";
    const int DefaultReplicas = 2;

    // ── Rendering helpers ─────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     One workload document — a <c>Deployment</c> or a <c>StatefulSet</c>, shaped the same way.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>ONE FUNCTION FOR SIX WORKLOADS, and the alternative was six near-copies that drift.</b>
    ///     What every one of them must get right is the same three things — the selector, the pod
    ///     template's labels and the container list — and the first two are written from
    ///     <see cref="PodLabels" /> exactly once here. Six hand-written copies is six chances for a
    ///     selector to disagree with the template it selects, which the API server accepts and which
    ///     produces a workload that owns no pods.
    ///     <para>
    ///         ⚠ No labels, no annotations and no namespace here. ADR-013's seven labels and two
    ///         annotations are injected by <c>KubeCommand</c> non-overridably — the builder is the one
    ///         place a key and a value are syntax-checked.
    ///     </para>
    /// </remarks>
    static string WorkloadJson(
        string objectName,
        string resourceName,
        string component,
        int replicas,
        JsonArray containers,
        JsonArray? volumes,
        JsonObject? claim,
        string? serviceName
    ) {
        var labels = new JsonObject();
        foreach (var (key, value) in PodLabels(resourceName, component)) {
            labels[key] = value;
        }

        var pod = new JsonObject { ["containers"] = containers };
        if (volumes is not null) {
            pod["volumes"] = volumes;
        }

        var spec = new JsonObject {
            ["replicas"] = replicas,
            ["selector"] = new JsonObject { ["matchLabels"] = labels.DeepClone() },
            ["template"] = new JsonObject {
                ["metadata"] = new JsonObject { ["labels"] = labels }, ["spec"] = pod
            }
        };

        if (serviceName is not null) {
            // ⚠ A StatefulSet's serviceName is required and names a Service that must exist for its
            // pods to get DNS records. All three here point at the component's own Service, which this
            // provider applies BEFORE the workload — see the reconciler's apply order.
            spec["serviceName"] = serviceName;
        }

        if (claim is not null) {
            spec["volumeClaimTemplates"] = new JsonArray { claim };
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = objectName }, ["spec"] = spec
        }.ToJsonString();
    }

    static string ServiceJson(string objectName, string resourceName, string component, JsonArray ports) {
        var selector = new JsonObject();
        foreach (var (key, value) in PodLabels(resourceName, component)) {
            selector[key] = value;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = objectName },
            ["spec"] = new JsonObject {
                ["type"] = "ClusterIP", ["selector"] = selector, ["ports"] = ports
            }
        }.ToJsonString();
    }

    static JsonObject ClaimTemplate(string volumeName, string size, string storageClass) {
        var spec = new JsonObject {
            ["accessModes"] = new JsonArray { "ReadWriteOnce" },
            ["resources"] = new JsonObject {
                ["requests"] = new JsonObject { ["storage"] = size }
            }
        };

        if (storageClass.Length > 0) {
            spec["storageClassName"] = storageClass;
        }

        return new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = volumeName }, ["spec"] = spec
        };
    }

    static JsonArray ConfigVolume(string name) => [
        new JsonObject {
            ["name"] = "config",
            ["configMap"] = new JsonObject { ["name"] = ConfigMapName(name) }
        }
    ];

    static JsonObject ControlPlaneResources() {
        var quantities = new JsonObject { ["cpu"] = ControlPlaneCpu, ["memory"] = ControlPlaneMemory };

        return new JsonObject { ["requests"] = quantities.DeepClone(), ["limits"] = quantities };
    }

    static JsonObject Env(string variable, string value) =>
        new() { ["name"] = variable, ["value"] = value };

    /// <summary>An environment variable sourced from the credentials <c>Secret</c>.</summary>
    static JsonObject SecretEnv(string variable, string name, string field) =>
        new() {
            ["name"] = variable,
            ["valueFrom"] = new JsonObject {
                ["secretKeyRef"] = new JsonObject {
                    ["name"] = CredentialsSecretName(name), ["key"] = field
                }
            }
        };

    static JsonObject ContainerPort(string portName, int port) =>
        new() { ["name"] = portName, ["containerPort"] = port };

    static JsonObject ServicePort(string portName, int port) =>
        new() { ["name"] = portName, ["port"] = port, ["targetPort"] = portName };

    static JsonObject HttpProbe(string path, int port, int period, int failures) =>
        new() {
            ["httpGet"] = new JsonObject { ["path"] = path, ["port"] = port },
            ["periodSeconds"] = period,
            ["failureThreshold"] = failures
        };

    static JsonObject TcpProbe(int port, int period, int failures) =>
        new() {
            ["tcpSocket"] = new JsonObject { ["port"] = port },
            ["periodSeconds"] = period,
            ["failureThreshold"] = failures
        };

    static JsonObject ExecProbe(string[] command, int period, int failures) {
        var argv = new JsonArray();
        foreach (var argument in command) {
            argv.Add(argument);
        }

        return new JsonObject {
            ["exec"] = new JsonObject { ["command"] = argv },
            ["periodSeconds"] = period,
            ["failureThreshold"] = failures
        };
    }

    static string Text(int value) => value.ToString(CultureInfo.InvariantCulture);

    static string StorageClass(JsonElement desired) => Text(desired, "storage", "class", string.Empty);

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

    static int Number(JsonElement desired, string name, int fallback) =>
        Root(desired, name) is { ValueKind: JsonValueKind.Number } value && value.TryGetInt32(out var found)
            ? found
            : fallback;

    static bool Flag(JsonElement desired, string parent, string name, bool fallback) =>
        Member(desired, parent, name) switch {
            { ValueKind: JsonValueKind.True } => true,
            { ValueKind: JsonValueKind.False } => false,
            _ => fallback
        };
}
