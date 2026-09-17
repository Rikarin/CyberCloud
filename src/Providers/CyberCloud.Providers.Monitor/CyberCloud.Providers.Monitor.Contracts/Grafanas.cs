using CyberCloud.Core.Contracts;
using System.Collections.Frozen;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Monitor.Contracts;

/// <summary>
///     Everything addressable about <c>CyberCloud.Dashboard/grafanas</c>: the type, its body shape,
///     the Grafana image it runs, and the <b>four</b> Kubernetes objects it becomes.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/16 § Managed Grafana · <b>M2 · 0.8 EM</b>: <i>"one instance per tenant … datasources
///         pre-wired to that tenant's workspace and nothing else"</i>. Issue #32's third noun of four.
///         A tenant gets an unmodified Grafana OSS in their own namespace, provisioned with the two
///         datasources their workspace's <c>listKeys</c> hands out, and a URL to reach it.
///     </para>
///     <para>
///         ⚠⚠
///         <b>
///             ADR-011, READ FOR A DEPLOYED COMPONENT: ALLOWED, ON A CONDITION THIS TYPE KEEPS, AND
///             THE PORTAL'S HALF IS A RULE ABOUT CODE THIS TYPE NEVER TOUCHES.
///         </b> ADR-011's row: <i>"Grafana | AGPL-3.0 | ⚠ Offerable as a managed instance (we
///         distribute, we do not modify). Our portal must not embed or link Grafana code — it embeds
///         rendered dashboards by URL"</i>. Three readings, each checked against what this type does:
///     </para>
///     <list type="number">
///         <item>
///             <b>Distributed, not modified.</b> The <c>Deployment</c> runs <c>grafana/grafana</c> by
///             digest, straight from upstream, and configures it the way upstream documents —
///             environment variables and a provisioning file. Nothing here builds an image, patches
///             a file inside one, or links a Grafana library into any platform assembly. AGPL's
///             network clause binds whoever <i>modifies</i> and serves; this platform serves upstream's
///             bytes and a tenant who wants the source is sent to upstream's.
///         </item>
///         <item>
///             <b>No Grafana code in the portal.</b> The <c>url</c> action hands back an address, and
///             <c>GF_SECURITY_ALLOW_EMBEDDING=true</c> is set so a rendered panel can sit in an
///             <c>iframe</c> by URL, which is the one integration ADR-011 permits. No Grafana
///             package is in <c>portal/</c> and none is added by this branch;
///             <c>GrafanaDeclarationTests</c> reads the portal's <c>package.json</c> to keep it that
///             way.
///         </item>
///         <item>
///             <b>The build gate's allow-list is for the bundle and this is not a bundle
///             component.</b> <c>build/Build.Licence.cs</c> scans <c>charts/bundle/</c> and the
///             platform's own images; a chart under <c>charts/managed/</c> that renders an upstream
///             image — <c>haproxy</c> before this, the collector and this beside it — is outside
///             both scans. So the exception ADR-011 § Enforcement asks for is written into
///             <c>LicenceExceptions</c> now, beside the image it excuses and with this reading as
///             its argument, so that the day the scan widens to workload images it finds the
///             argument rather than a red row; <c>charts/managed/grafana/conformance.yaml § owed</c>,
///             <c>licence-scan-does-not-read-workload-images</c>, says the scan has not widened.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>IT IS NOT <c>grafana-operator</c>, WHICH IS WHAT docs/plan/16 NAMES.</b> The operator
///         is a bundle component that does not exist, a CRD the cluster-backed harness would stub
///         with an open schema, and a second reconciler between this one and the pod. What it would
///         buy — dashboards as a versioned sub-resource — needs an api-version with an array of
///         objects the schema does not have. One <c>Deployment</c> from a pinned image is what the
///         operator would have rendered anyway; the sub-resource is <c>conformance.yaml § owed</c>,
///         <c>dashboards-are-not-a-sub-resource</c>.
///     </para>
///     <para>
///         ⚠ <b>IN THE MONITOR FAMILY'S ASSEMBLIES, UNDER ITS OWN PROVIDER NAMESPACE.</b>
///         docs/plan/03 § Providers lists <c>CyberCloud.Providers.Monitor/ # workspaces, collectors,
///         alerts, grafanas</c>, and this is the first provider that is not the only
///         <c>IResourceProvider</c> in its assembly. The alternative — a seventeenth family for one
///         type — would need a line to this family for the workspace's row keys, and rule 2 of
///         § Assembly graph rules refuses it. The namespace is what the catalogue says it is
///         (<c>CyberCloud.Dashboard</c>); the assembly is where docs/plan/03 puts it.
///     </para>
///     <para>
///         ⚠ <b>THE WORKSPACE IS A PROPERTY, NOT A PARENT, AND IT IS THE SECOND
///         <c>SchemaFormat.ResourceId</c> IN THE CATALOGUE.</b> A Grafana is not a child of a
///         workspace — it is another provider's type — so the address cannot carry the workspace and
///         a body property has to. Like the alert rule's action group, the reconciler checks what the
///         schema cannot: the path is this tenant's, it names a <c>CyberCloud.Monitor/workspaces</c>
///         resource, and it is in the same resource group — because the pod reads the workspace's
///         row and ingest key from its own namespace, and a <c>Secret</c> cannot be mounted across
///         namespaces.
///     </para>
/// </remarks>
public static class Grafanas {
    /// <summary>The provider namespace — docs/plan/01's catalogue row.</summary>
    public const string ProviderNamespace = "CyberCloud.Dashboard";

    /// <summary>The type's path under <see cref="ProviderNamespace" />.</summary>
    public const string TypePath = "grafanas";

    /// <summary>The api-version — the platform's one published version, shared with the workspace.</summary>
    public const string V2026 = MonitorWorkspaces.V2026;

    /// <summary>The chart that renders the same four objects.</summary>
    public const string ChartName = "managed/grafana";

    /// <summary>Where the body names the cluster the instance runs in — its workspace's.</summary>
    public const string ClusterIdPointer = ClusterPlacement.DefaultPointer;

    /// <summary>Where the body names the workspace the datasources point at.</summary>
    public const string WorkspacePointer = "/properties/workspace";

    /// <summary>The action that reports where the instance answers, and how to sign in.</summary>
    public const string UrlAction = "url";

    /// <summary>The permission <see cref="UrlAction" /> needs.</summary>
    /// <remarks>
    ///     Its own permission rather than <c>read</c>, because the response carries the admin
    ///     password — the shape <c>CyberCloud.Monitor/workspaces</c>' <c>listKeys</c> established for
    ///     a response with a credential in it.
    /// </remarks>
    public const string UrlPermission = "url";

    /// <summary>The type, as the registry names it.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    // ── The image ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Grafana OSS, upstream's own image, unmodified.</summary>
    public const string ImageRepository = "grafana/grafana";

    /// <summary>The release the digest was resolved for.</summary>
    public const string ImageTag = "13.2.2";

    /// <summary>
    ///     The manifest-list digest Docker Hub served for <see cref="ImageTag" /> on 2026-09-17.
    /// </summary>
    public const string ImageDigest = "sha256:ac461fb352abc50da10a51c7d02462e9c05488f11f53f14b3ad79a8145f638a0";

    /// <summary>The image reference the <c>Deployment</c> carries.</summary>
    public const string Image = ImageRepository + ":" + ImageTag + "@" + ImageDigest;

    /// <summary>The uid the image's <c>USER</c> instruction sets — <c>472</c>, read off its config.</summary>
    public const int GrafanaUid = 472;

    /// <summary>The port Grafana answers on.</summary>
    public const int Port = 3000;

    /// <summary>
    ///     The ClickHouse datasource plugin, and the version <c>GF_INSTALL_PLUGINS</c> asks for.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Fetched by <c>grafana-cli</c> at pod start, from grafana.com, and not pinned by
    ///     digest.</b> Grafana OSS has no built-in ClickHouse datasource; the plugin is Grafana Labs'
    ///     own, Apache-2.0 (its LICENSE read on 2026-09-17), and the image installs it on first
    ///     start into the writable plugins directory. A cluster with no egress starts a Grafana with
    ///     one datasource of two. Both halves are <c>conformance.yaml § owed</c>,
    ///     <c>clickhouse-plugin-is-fetched-at-start</c>.
    /// </remarks>
    public const string ClickHousePlugin = "grafana-clickhouse-datasource";

    /// <summary>See <see cref="ClickHousePlugin" />.</summary>
    public const string ClickHousePluginVersion = "4.21.3";

    /// <summary>Where Grafana's provisioning tree lives, and where the datasources file is mounted.</summary>
    public const string ProvisioningDirectory = "/etc/grafana/provisioning/datasources";

    /// <summary>The provisioning file's name.</summary>
    public const string DatasourcesFile = "datasources.yaml";

    /// <summary>Where Grafana keeps its database and its plugins — an <c>emptyDir</c> here.</summary>
    /// <remarks>
    ///     ⚠ <b>Ephemeral, on purpose, and that is what makes the provisioning file the source of
    ///     truth.</b> A dashboard a tenant saves in the UI dies with the pod; the datasources come
    ///     back from the file on every start. Persistent dashboards are the sub-resource docs/plan/16
    ///     asks for and this type owes — see the class remarks.
    /// </remarks>
    public const string DataDirectory = "/var/lib/grafana";

    // ── Sizing ────────────────────────────────────────────────────────────────────────────────

    /// <summary>The preset a body gets when it names none.</summary>
    public const string DefaultPreset = "c1.small";

    /// <summary>What each preset gives the Grafana pod. The same table is in the chart.</summary>
    public static FrozenDictionary<string, (string Cpu, string Memory)> Presets { get; } =
        new Dictionary<string, (string, string)>(StringComparer.Ordinal) {
            ["c1.small"] = ("250m", "512Mi"), ["c1.medium"] = ("500m", "1Gi"), ["c1.large"] = ("1", "2Gi")
        }.ToFrozenDictionary(StringComparer.Ordinal);

    /// <summary>What a body's preset costs.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static (string Cpu, string Memory) Resources(JsonElement desired) =>
        Presets.TryGetValue(Preset(desired), out var chosen) ? chosen : Presets[DefaultPreset];

    // ── The kinds ─────────────────────────────────────────────────────────────────────────────

    /// <summary>The admin credential.</summary>
    public static GroupVersionKind SecretKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    /// <summary>The datasource provisioning file.</summary>
    public static GroupVersionKind ConfigMapKind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>Grafana itself.</summary>
    public static GroupVersionKind DeploymentKind { get; } =
        new() { Group = "apps", Version = "v1", Kind = "Deployment", Plural = "deployments" };

    /// <summary>The stable address the URL names.</summary>
    public static GroupVersionKind ServiceKind { get; } =
        new() { Group = "", Version = "v1", Kind = "Service", Plural = "services" };

    // ── Names ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>The name the Deployment, the Service and the ConfigMap take.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ObjectNameOf(string name) => "grafana-" + name;

    /// <summary>The name of the <c>Secret</c> carrying the admin credential.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string AdminSecretName(string name) => "grafana-" + name + "-admin";

    /// <summary>The admin <c>Secret</c> an instance owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef AdminSecretRef(string ns, string name) =>
        new() { Kind = SecretKind, Namespace = ns, Name = AdminSecretName(name) };

    /// <summary>The provisioning <c>ConfigMap</c> an instance owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ConfigMapRef(string ns, string name) =>
        new() { Kind = ConfigMapKind, Namespace = ns, Name = ObjectNameOf(name) };

    /// <summary>The <c>Deployment</c> an instance owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef DeploymentRef(string ns, string name) =>
        new() { Kind = DeploymentKind, Namespace = ns, Name = ObjectNameOf(name) };

    /// <summary>The <c>Service</c> an instance owns.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    public static ObjectRef ServiceRef(string ns, string name) =>
        new() { Kind = ServiceKind, Namespace = ns, Name = ObjectNameOf(name) };

    /// <summary>Every object an instance owns, in apply order.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     The credential and the provisioning file first, because the pod mounts both; the Service
    ///     last, because the URL is not an address until there is a pod behind it.
    /// </remarks>
    public static ImmutableArray<ObjectRef> Objects(string ns, string name) =>
        [AdminSecretRef(ns, name), ConfigMapRef(ns, name), DeploymentRef(ns, name), ServiceRef(ns, name)];

    /// <summary>The pod-template annotation carrying the provisioning file's hash.</summary>
    public const string ConfigChecksumAnnotation = "cybercloud.io/grafana-provisioning";

    /// <summary>The label the Deployment and the Service select pods by.</summary>
    public const string NameLabel = "app.kubernetes.io/name";

    /// <summary>The label distinguishing one instance's pods from another's.</summary>
    public const string InstanceLabel = "app.kubernetes.io/instance";

    /// <summary>The value <see cref="NameLabel" /> carries.</summary>
    public const string NameLabelValue = "grafana";

    // ── The credential ────────────────────────────────────────────────────────────────────────

    /// <summary>The admin user's name, fixed.</summary>
    public const string AdminUser = "admin";

    /// <summary>The <c>Secret</c> key and vault field the admin password is under.</summary>
    public const string AdminPasswordField = "adminPassword";

    /// <summary>The <c>Secret</c> key the admin user's name is under.</summary>
    public const string AdminUserField = "adminUser";

    const string PasswordAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz123456789";

    const int PasswordLength = 32;

    /// <summary>A fresh admin password. Different on every call — see <see cref="MonitorWorkspaces.GenerateIngestKey" />.</summary>
    public static string GenerateAdminPassword() => RandomNumberGenerator.GetString(PasswordAlphabet, PasswordLength);

    /// <summary>Where an instance's secrets live in the tenant's vault.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static string SecretPath(ResourceId id) {
        ArgumentNullException.ThrowIfNull(id.Path);

        return string.Create(
            CultureInfo.InvariantCulture,
            $"tenants/{id.TenantId:D}/{ProviderNamespace}/{TypePath}/{id.Id:D}"
        );
    }

    /// <summary>The handle that reads the admin password back.</summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    public static SecretRef AdminPasswordRef(ResourceId id) => new() { Path = SecretPath(id), Field = AdminPasswordField };

    // ── The body ──────────────────────────────────────────────────────────────────────────────

    /// <summary>The body shape at <see cref="V2026" />.</summary>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The region the instance is billed in — its workspace's."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The instance's own settings."),
                new(
                    ClusterIdPointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The cluster the instance runs in. ⚠ It must be the cluster its "
                    + "workspace publishes into: Grafana reads the workspace's accountID, database and "
                    + "ingest key from the workspace's own objects in the same namespace."
                ) { Format = SchemaFormat.Uuid, Widget = WidgetHint.Cluster, Immutable = true },
                new(
                    WorkspacePointer,
                    SchemaKind.Text,
                    Required: true,
                    Description: "The CyberCloud.Monitor/workspaces resource the datasources point at, "
                    + "as its full resource id path. It must be in this tenant and in the same "
                    + "resource group as the instance; the instance sees that workspace and nothing else."
                ) {
                    Format = SchemaFormat.ResourceId,
                    Immutable = true,
                    MaxLength = 512,
                    ExampleJson = "\"/tenants/11111111-1111-4111-8111-111111111111/subscriptions/"
                    + "33333333-3333-4333-8333-333333333333/resourceGroups/prod/providers/"
                    + "CyberCloud.Monitor/workspaces/prod\""
                },
                new(
                    "/properties/anonymousViewers",
                    SchemaKind.Boolean,
                    Description: "Whether anybody who can reach the URL may view dashboards without "
                    + "signing in, as a Viewer. Off means every visit signs in as the admin user the "
                    + "url action returns. ⚠ A rendered panel embedded by URL in another page is a "
                    + "visit like any other, so embedding needs this on or a signed-in browser."
                ) { DefaultJson = "false" },
                new("/properties/sizing", SchemaKind.Nested, Description: "CPU and memory for the instance."),
                new(
                    "/properties/sizing/preset",
                    SchemaKind.Text,
                    Description: "How much the pod gets. Grafana renders in the browser and queries "
                    + "the workspace's stores, so the small row serves a team; the larger rows are "
                    + "for many concurrent dashboards."
                ) {
                    AllowedValues = [.. Presets.Keys.Order(StringComparer.Ordinal)],
                    Widget = WidgetHint.CozyPreset,
                    DefaultJson = "\"" + DefaultPreset + "\""
                }
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } = [.. Schema2026.Properties.Select(x => x.JsonPointer)];

    /// <summary>What a <c>url</c> returns.</summary>
    public static ResourceSchema UrlResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/url",
                    SchemaKind.Text,
                    Required: true,
                    Description: "Where the instance answers inside the cluster. A dashboard or a "
                    + "panel is embedded by appending Grafana's own /d/… or /d-solo/… path to it — "
                    + "ADR-011's one permitted integration."
                ),
                new(
                    "/adminUser",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The administrator's user name."
                ),
                new(
                    "/adminPassword",
                    SchemaKind.Text,
                    Required: true,
                    Secret: true,
                    Description: "The administrator's password, read from the tenant's vault for this "
                    + "call only. Minted once when the instance was created."
                ),
                new(
                    "/workspace",
                    SchemaKind.Text,
                    Required: true,
                    Description: "The workspace the two provisioned datasources read from."
                )
            ]
        );

    // ── Reading a body ────────────────────────────────────────────────────────────────────────

    /// <summary>The workspace path the body names, unchecked.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string WorkspacePath(JsonElement desired) => Text(desired, "workspace").Trim();

    /// <summary>Whether the body asks for anonymous viewing.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static bool AnonymousViewers(JsonElement desired) => Flag(desired, "anonymousViewers", false);

    /// <summary>The sizing preset the body asks for, or the default.</summary>
    /// <param name="desired">The validated desired body.</param>
    public static string Preset(JsonElement desired) =>
        Nested(desired, "sizing", "preset") is { Length: > 0 } preset && Presets.ContainsKey(preset)
            ? preset
            : DefaultPreset;

    /// <summary>
    ///     The workspace the body names, checked against the instance's own address.
    /// </summary>
    /// <param name="id">The instance's address.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <returns>The workspace's address, or the refusal naming what is wrong with it.</returns>
    /// <remarks>
    ///     ⚠ <b>Three checks the schema cannot make, and the third is this type's own.</b> Tenant and
    ///     type are the alert rule's checks, for its reason. Subscription and resource group are new:
    ///     the pod reads the workspace's row and ingest key from its own namespace, and
    ///     <c>ReconcileDriver.NamespaceFor</c> is <c>{subscriptionId:N}-{resourceGroup}</c>, so a
    ///     workspace in another group is objects in another namespace, which a pod cannot mount. A
    ///     refusal here is a body the API accepted — the same limit <c>MonitorWorkspaceReconciler</c>
    ///     counts — and it is terminal, because nothing about it changes by waiting.
    /// </remarks>
    public static Result<ResourceId> WorkspaceOf(ResourceId id, JsonElement desired) {
        var path = WorkspacePath(desired);

        if (!ResourceId.TryParsePath(path, out var workspace)) {
            return Refuse("The workspace is not a resource id path. Write the full path of the CyberCloud.Monitor/workspaces resource — docs/plan/06 § Identifiers.");
        }

        if (workspace.TenantId != id.TenantId) {
            return Refuse(
                $"The workspace belongs to tenant {workspace.TenantId:D} and this instance to tenant "
                + $"{id.TenantId:D}. An instance shows its own tenant's workspace only."
            );
        }

        if (!string.Equals(workspace.Type.Namespace, MonitorWorkspaces.ProviderNamespace, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(workspace.Type.Type, MonitorWorkspaces.TypePath, StringComparison.OrdinalIgnoreCase)
            || workspace.Parent is not null) {
            return Refuse(
                $"The workspace is a {workspace.Type} resource. It must be a "
                + $"{MonitorWorkspaces.ProviderNamespace}/{MonitorWorkspaces.TypePath} resource — the tenancy whose stores the dashboards read."
            );
        }

        if (workspace.SubscriptionId != id.SubscriptionId
            || !string.Equals(workspace.ResourceGroup, id.ResourceGroup, StringComparison.Ordinal)) {
            return Refuse(
                $"The workspace is in resource group '{workspace.ResourceGroup}' of subscription "
                + $"{workspace.SubscriptionId:D} and this instance in '{id.ResourceGroup}' of "
                + $"{id.SubscriptionId:D}. Grafana reads the workspace's ingest key from a Secret in "
                + "its own namespace, and a pod cannot mount a Secret from another namespace, so the "
                + "workspace and the instance must share a resource group."
            );
        }

        return Result<ResourceId>.Success(workspace);
    }

    static Result<ResourceId> Refuse(string message) =>
        Result<ResourceId>.Failure(ErrorCode.InvalidRequestBody, message, WorkspacePointer);

    // ── The rendered provisioning file ────────────────────────────────────────────────────────

    /// <summary>The datasource provisioning file, as YAML.</summary>
    /// <param name="workspace">The workspace's own name.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b><c>$VAR</c> is Grafana's own provisioning-file interpolation and it happens at
    ///         start</b>, so the accountID, the database and the key never appear in this text — the
    ///         same arrangement the collector has with <c>${env:…}</c>, for the same reason
    ///         (<see cref="MonitorWorkspaces.WorkspaceEnv" />). Grafana expands <c>$NAME</c> in
    ///         every provisioning value; a literal dollar would be <c>$$</c>, and none is needed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Two datasources, both <c>editable: false</c>, both pointing at this workspace and
    ///         nothing else</b> — docs/plan/16's <i>"and nothing else"</i>. The Prometheus one is
    ///         Grafana's built-in, at the workspace's read-only PromQL endpoint, authenticated as the
    ///         workspace's <c>VMUser</c>. The ClickHouse one is the plugin's, over HTTP at the
    ///         workspace's SQL endpoint with its database as the default. Their <c>uid</c>s are fixed
    ///         so a dashboard JSON a tenant exports from one instance imports into another.
    ///     </para>
    /// </remarks>
    public static string DatasourcesYaml(string workspace) {
        ArgumentException.ThrowIfNullOrEmpty(workspace);

        var user = MonitorWorkspaces.VmUserName(workspace);
        var builder = new StringBuilder();

        builder.Append(
            CultureInfo.InvariantCulture,
            $$"""
            # Generated by CyberCloud from CyberCloud.Dashboard/grafanas.
            # Edits are overwritten on the next reconcile pass.
            apiVersion: 1
            datasources:
              - name: Metrics
                uid: cybercloud-metrics
                type: prometheus
                access: proxy
                isDefault: true
                editable: false
                url: {{MonitorWorkspaces.PromqlEndpoint("$" + MonitorWorkspaces.EnvAccountId)}}
                basicAuth: true
                basicAuthUser: {{user}}
                secureJsonData:
                  basicAuthPassword: ${{MonitorWorkspaces.EnvIngestKey}}
              - name: Logs and traces
                uid: cybercloud-logs
                type: {{ClickHousePlugin}}
                access: proxy
                editable: false
                jsonData:
                  host: {{MonitorWorkspaces.QueryHost}}
                  port: 443
                  protocol: http
                  secure: true
                  path: /sql/${{MonitorWorkspaces.EnvDatabase}}
                  defaultDatabase: ${{MonitorWorkspaces.EnvDatabase}}
                  username: {{user}}
                secureJsonData:
                  password: ${{MonitorWorkspaces.EnvIngestKey}}

            """
        );

        return builder.ToString();
    }

    /// <summary>The hash of the provisioning file, as the pod template carries it.</summary>
    /// <param name="workspace">The workspace's own name.</param>
    public static string ConfigHash(string workspace) => KubeLabels.ReconcileHash(DatasourcesYaml(workspace));

    // ── The four documents ────────────────────────────────────────────────────────────────────

    /// <summary>The <c>Secret</c> document the admin credential becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="adminPassword">The password, as read back from the vault.</param>
    /// <remarks>
    ///     <c>data</c> and not <c>stringData</c>, for <see cref="MonitorWorkspaces.KeySecretJson" />'s
    ///     reason: the convenience field never reads back.
    /// </remarks>
    public static string AdminSecretJson(string name, string adminPassword) {
        ArgumentException.ThrowIfNullOrEmpty(name);
        ArgumentException.ThrowIfNullOrEmpty(adminPassword);

        return new JsonObject {
            ["kind"] = SecretKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = AdminSecretName(name) },
            ["type"] = "Opaque",
            ["data"] = new JsonObject {
                [AdminUserField] = Convert.ToBase64String(Encoding.UTF8.GetBytes(AdminUser)),
                [AdminPasswordField] = Convert.ToBase64String(Encoding.UTF8.GetBytes(adminPassword))
            }
        }.ToJsonString();
    }

    /// <summary>The <c>ConfigMap</c> document the provisioning file becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="workspace">The workspace's own name.</param>
    public static string ConfigMapJson(string name, string workspace) =>
        new JsonObject {
            ["kind"] = ConfigMapKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = ObjectNameOf(name) },
            ["data"] = new JsonObject { [DatasourcesFile] = DatasourcesYaml(workspace) }
        }.ToJsonString();

    /// <summary>The <c>Deployment</c> document a desired body becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    /// <param name="workspace">The workspace's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>One replica, <c>Recreate</c>, and no <c>replicas</c> property.</b> Grafana OSS
    ///         keeps its state in SQLite on <see cref="DataDirectory" />; two pods would be two
    ///         instances with two sets of sessions behind one URL. A highly available instance is a
    ///         Postgres behind it, which is a different resource.
    ///     </para>
    ///     <para>
    ///         ⚠ <b><c>GF_SECURITY_ALLOW_EMBEDDING=true</c> is ADR-011's integration, spelled as
    ///         Grafana spells it.</b> Without it Grafana sends <c>X-Frame-Options: deny</c> and a panel
    ///         URL in an <c>iframe</c> renders nothing. It is unconditional because embedding by URL is
    ///         the only way this platform will ever show a Grafana panel.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The root filesystem is read-only and two <c>emptyDir</c>s make Grafana run
    ///         anyway.</b> Grafana writes its database and downloaded plugins under
    ///         <see cref="DataDirectory" /> and scratch under <c>/tmp</c>; everything else in the
    ///         image is read at start. An image rebuilt to write elsewhere fails to start, loudly,
    ///         rather than writing somewhere this spec did not anticipate.
    ///     </para>
    /// </remarks>
    public static string DeploymentJson(string name, string workspace, JsonElement desired) {
        var objectName = ObjectNameOf(name);
        var (cpu, memory) = Resources(desired);
        var selector = new JsonObject { [NameLabel] = NameLabelValue, [InstanceLabel] = objectName };

        var env = MonitorWorkspaces.WorkspaceEnv(workspace);
        env.Add(Env("GF_SECURITY_ADMIN_USER", AdminSecretName(name), AdminUserField));
        env.Add(Env("GF_SECURITY_ADMIN_PASSWORD", AdminSecretName(name), AdminPasswordField));
        env.Add(new JsonObject { ["name"] = "GF_SECURITY_ALLOW_EMBEDDING", ["value"] = "true" });
        env.Add(new JsonObject { ["name"] = "GF_AUTH_ANONYMOUS_ENABLED", ["value"] = AnonymousViewers(desired) ? "true" : "false" });
        env.Add(new JsonObject { ["name"] = "GF_AUTH_ANONYMOUS_ORG_ROLE", ["value"] = "Viewer" });
        env.Add(new JsonObject { ["name"] = "GF_INSTALL_PLUGINS", ["value"] = ClickHousePlugin + " " + ClickHousePluginVersion });
        env.Add(new JsonObject { ["name"] = "GF_PATHS_DATA", ["value"] = DataDirectory });
        env.Add(new JsonObject { ["name"] = "GF_PATHS_PLUGINS", ["value"] = DataDirectory + "/plugins" });

        return new JsonObject {
            ["kind"] = DeploymentKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = objectName },
            ["spec"] = new JsonObject {
                ["replicas"] = 1,
                ["strategy"] = new JsonObject { ["type"] = "Recreate" },
                ["selector"] = new JsonObject { ["matchLabels"] = selector.DeepClone() },
                ["template"] = new JsonObject {
                    ["metadata"] = new JsonObject {
                        ["labels"] = selector.DeepClone(),
                        ["annotations"] = new JsonObject { [ConfigChecksumAnnotation] = ConfigHash(workspace) }
                    },
                    ["spec"] = new JsonObject {
                        ["automountServiceAccountToken"] = false,
                        ["terminationGracePeriodSeconds"] = 30,
                        ["securityContext"] = new JsonObject {
                            ["runAsNonRoot"] = true,
                            ["runAsUser"] = GrafanaUid,
                            ["runAsGroup"] = GrafanaUid,
                            ["fsGroup"] = GrafanaUid,
                            ["seccompProfile"] = new JsonObject { ["type"] = "RuntimeDefault" }
                        },
                        ["containers"] = new JsonArray {
                            new JsonObject {
                                ["name"] = "grafana",
                                ["image"] = Image,
                                ["securityContext"] = new JsonObject {
                                    ["allowPrivilegeEscalation"] = false,
                                    ["readOnlyRootFilesystem"] = true,
                                    ["capabilities"] = new JsonObject { ["drop"] = new JsonArray { "ALL" } }
                                },
                                ["env"] = env,
                                ["ports"] = new JsonArray {
                                    new JsonObject { ["name"] = "http", ["containerPort"] = Port, ["protocol"] = "TCP" }
                                },
                                ["readinessProbe"] = new JsonObject {
                                    ["httpGet"] = new JsonObject { ["path"] = "/api/health", ["port"] = Port },
                                    ["periodSeconds"] = 5
                                },
                                ["resources"] = new JsonObject {
                                    ["requests"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory },
                                    ["limits"] = new JsonObject { ["cpu"] = cpu, ["memory"] = memory }
                                },
                                ["volumeMounts"] = new JsonArray {
                                    new JsonObject { ["name"] = "datasources", ["mountPath"] = ProvisioningDirectory, ["readOnly"] = true },
                                    new JsonObject { ["name"] = "data", ["mountPath"] = DataDirectory },
                                    new JsonObject { ["name"] = "tmp", ["mountPath"] = "/tmp" }
                                }
                            }
                        },
                        ["volumes"] = new JsonArray {
                            new JsonObject { ["name"] = "datasources", ["configMap"] = new JsonObject { ["name"] = objectName } },
                            new JsonObject { ["name"] = "data", ["emptyDir"] = new JsonObject() },
                            new JsonObject { ["name"] = "tmp", ["emptyDir"] = new JsonObject() }
                        }
                    }
                }
            }
        }.ToJsonString();
    }

    static JsonObject Env(string name, string secret, string key) =>
        new() {
            ["name"] = name,
            ["valueFrom"] = new JsonObject { ["secretKeyRef"] = new JsonObject { ["name"] = secret, ["key"] = key } }
        };

    /// <summary>The <c>Service</c> document an instance becomes.</summary>
    /// <param name="name">The resource's own name.</param>
    public static string ServiceJson(string name) {
        var objectName = ObjectNameOf(name);

        return new JsonObject {
            ["kind"] = ServiceKind.Kind,
            ["metadata"] = new JsonObject { ["name"] = objectName },
            ["spec"] = new JsonObject {
                ["type"] = "ClusterIP",
                ["selector"] = new JsonObject { [NameLabel] = NameLabelValue, [InstanceLabel] = objectName },
                ["ports"] = new JsonArray {
                    new JsonObject { ["name"] = "http", ["port"] = Port, ["targetPort"] = Port, ["protocol"] = "TCP" }
                }
            }
        }.ToJsonString();
    }

    // ── Reading an object back ───────────────────────────────────────────────────────────────

    /// <summary>Whether an object read out of the cluster carries what a desired body asked for.</summary>
    /// <param name="objectJson">The object, as the API server returned it.</param>
    /// <param name="workspace">The workspace's own name.</param>
    /// <param name="desired">The validated desired body.</param>
    /// <remarks>
    ///     Dispatches on <c>kind</c> and answers <see langword="false" /> for one it does not know —
    ///     this family's rule. The Secret is checked for the presence of both fields and not for the
    ///     password's value, for <see cref="MonitorWorkspaces.Matches" />' reason; the ConfigMap
    ///     exactly; the Deployment on the image, the hash and the anonymous-access switch, which are
    ///     the three things a body change can move; the Service on its one port.
    /// </remarks>
    public static bool Matches(string objectJson, string workspace, JsonElement desired) {
        ArgumentException.ThrowIfNullOrEmpty(objectJson);

        if (JsonNode.Parse(objectJson) is not JsonObject document) {
            return false;
        }

        return document["kind"]?.GetValue<string>() switch {
            "Secret" => document["data"] is JsonObject data
                && data[AdminUserField] is not null
                && data[AdminPasswordField] is not null,
            "ConfigMap" => document["data"]?[DatasourcesFile]?.GetValue<string>() == DatasourcesYaml(workspace),
            "Deployment" => MatchesDeployment(document, workspace, desired),
            "Service" => document["spec"]?["ports"] is JsonArray ports
                && ports.Count == 1
                && ports[0]?["port"]?.GetValue<int>() == Port,
            _ => false
        };
    }

    static bool MatchesDeployment(JsonObject document, string workspace, JsonElement desired) {
        if (document["spec"]?["template"] is not JsonObject template
            || template["spec"]?["containers"] is not JsonArray containers
            || containers.Count != 1
            || containers[0] is not JsonObject container
            || container["env"] is not JsonArray env) {
            return false;
        }

        var anonymous = env
            .OfType<JsonObject>()
            .FirstOrDefault(x => x["name"]?.GetValue<string>() == "GF_AUTH_ANONYMOUS_ENABLED")?["value"]
            ?.GetValue<string>();

        return container["image"]?.GetValue<string>() == Image
            && template["metadata"]?["annotations"]?[ConfigChecksumAnnotation]?.GetValue<string>() == ConfigHash(workspace)
            && anonymous == (AnonymousViewers(desired) ? "true" : "false");
    }

    // ── The URL ───────────────────────────────────────────────────────────────────────────────

    /// <summary>Where the instance answers inside the cluster.</summary>
    /// <param name="ns">The resource's namespace.</param>
    /// <param name="name">The resource's own name.</param>
    /// <remarks>
    ///     ⚠ <b>In-cluster only.</b> docs/plan/12 § Cross-cutting decisions requires an explicit
    ///     allow-list on any exposure and this type declares none — the decision
    ///     <c>charts/managed/monitor-workspace</c> took, for its reason. The portal reaches it the way
    ///     it reaches every other in-cluster address; <c>conformance.yaml § owed</c>,
    ///     <c>no-external-endpoint</c>.
    /// </remarks>
    public static string Url(string ns, string name) =>
        string.Create(CultureInfo.InvariantCulture, $"http://{ObjectNameOf(name)}.{ns}.svc:{Port}");

    // ── A body, for tests, the conformance case and the chart ────────────────────────────────

    /// <summary>A valid body at <see cref="V2026" />.</summary>
    /// <param name="clusterId">The cluster the instance runs in — its workspace's.</param>
    /// <param name="workspace">The workspace's resource id path.</param>
    /// <param name="anonymousViewers">Whether dashboards are viewable without signing in.</param>
    /// <param name="preset">The sizing preset.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        Guid clusterId,
        string workspace,
        bool anonymousViewers = false,
        string preset = DefaultPreset,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture),
                ["workspace"] = workspace,
                ["anonymousViewers"] = anonymousViewers,
                ["sizing"] = new JsonObject { ["preset"] = preset }
            }
        }.ToJsonString();

    // ── Reading JSON ─────────────────────────────────────────────────────────────────────────

    static JsonElement? Property(JsonElement desired, string name) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty(name, out var value)
            ? value
            : null;

    static string Text(JsonElement desired, string name) =>
        Property(desired, name) is { ValueKind: JsonValueKind.String } value ? value.GetString() ?? string.Empty : string.Empty;

    static bool Flag(JsonElement desired, string name, bool fallback) =>
        Property(desired, name) is { ValueKind: JsonValueKind.True or JsonValueKind.False } value
            ? value.GetBoolean()
            : fallback;

    static string Nested(JsonElement desired, string parent, string name) =>
        Property(desired, parent) is { ValueKind: JsonValueKind.Object } section
        && section.TryGetProperty(name, out var value)
        && value.ValueKind is JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
}
