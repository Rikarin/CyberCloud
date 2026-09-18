using CyberCloud.Kubernetes.Contracts.Tunnel;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.ContainerService.Contracts;

/// <summary>
///     <c>CyberCloud.ContainerService/connectedClusters</c> — a cluster the tenant brought, reached
///     through an agent the tenant installs in it. docs/plan/09 § Cluster connections, the
///     <c>AgentInitiated</c> row, and issue #36.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE FIRST RESOURCE TYPE IN THE TREE THAT RENDERS NOTHING INTO ANY CLUSTER, AND THE
///             FIRST WHOSE CONVERGENCE IS AN EVENT THE TENANT CAUSES.
///         </b> A managed cluster is three Cluster API objects the platform applies and waits on.
///         A connected cluster is a promise: the tenant creates it, asks it for an install command,
///         runs that command in a cluster the platform cannot see, and the resource reaches
///         <c>Succeeded</c> when the agent's first heartbeat arrives. Nothing here applies, reads
///         back, or drifts in the docs/plan/08 sense, so the type declares no chart and no
///         <c>RequiresCluster</c>, and its reconciler holds no <c>IKubeClusterConnection</c> at all.
///     </para>
///     <para>
///         ⚠ <b>Its <c>clusterId</c> is its own resource id.</b> Once <c>Succeeded</c>, the id is
///         what every other resource names in <c>properties.clusterId</c> to be placed in this
///         cluster — <c>ReconcileDriver</c> attaches an <c>AgentInitiated</c> connection under it
///         on the pass that converges, the way it attaches an <c>InHouse</c> one for a managed
///         cluster. So a body has no cluster id to carry: the placement pointer other types
///         declare would point at the resource itself.
///     </para>
///     <para>
///         ⚠ <b>The credential leaves once, through <see cref="ListInstallCommandAction" />.</b> The
///         enrollment token is minted by the action, hashed into the tunnel grain, and returned in
///         one response body — never a status field, never an operation record. It admits one
///         connection and is spent; the agent keeps the credential it is handed in exchange in a
///         Secret in its own namespace. Re-running the action mints a fresh token and voids the
///         previous one.
///     </para>
///     <para>
///         ⚠
///         <b>
///             Sixty minutes to run the install command, and the number is the operation's rather
///             than this type's.
///         </b> docs/plan/08 § The reconcile loop caps an operation at an hour,
///         and a create that is still waiting for a heartbeat at that point is <c>Failed</c>. A
///         tenant who takes longer re-issues the <c>PUT</c>, which is idempotent, and gets a fresh
///         hour. <c>charts/agent/conformance.yaml § owed</c>, <c>an-hour-to-install</c>.
///     </para>
/// </remarks>
public static class ConnectedClusters {
    /// <summary>The provider namespace — <see cref="ManagedClusters.ProviderNamespace" />, shared.</summary>
    public const string ProviderNamespace = ManagedClusters.ProviderNamespace;

    /// <summary>The type path.</summary>
    public const string TypePath = "connectedClusters";

    /// <summary>The first api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The action that mints the install command: <c>POST …/connectedClusters/{name}/listInstallCommand</c>.</summary>
    public const string ListInstallCommandAction = "listInstallCommand";

    /// <summary>
    ///     The permission the action requires — its own, so that <c>read</c> on the cluster does not
    ///     mint a credential that admits an agent to it.
    /// </summary>
    public const string ListInstallCommandPermission = "listInstallCommand";

    /// <summary>The type name.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The Helm release name the install command uses.</summary>
    public const string ReleaseName = "cybercloud-agent";

    /// <summary>The namespace the install command creates the agent in.</summary>
    public const string AgentNamespace = "cybercloud-system";

    /// <summary>The default heartbeat interval, seconds. Six per staleness window.</summary>
    public const int DefaultHeartbeatSeconds = 15;

    /// <summary>The 2026-08-01 schema.</summary>
    /// <remarks>
    ///     ⚠ <b>No read-only status properties, although three were drafted</b> — the agent's version,
    ///     the API server's version, the last heartbeat. <c>SchemaProperty.ReadOnly</c> refuses a
    ///     write and nothing projects a value into a read: what the agent reports lives in the
    ///     resource's observed state, which <c>ConnectedClusterReconciler.ObserveAsync</c> fills and
    ///     the portal reads from there. A schema row nothing populates would be a field that reads
    ///     back empty forever.
    /// </remarks>
    public static ResourceSchema Schema2026 { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/location",
                    SchemaKind.Text,
                    true,
                    Description: "The region the cluster is billed in. ⚠ Where the cluster physically is "
                    + "is the tenant's business; this is the region whose gateway the agent dials and "
                    + "whose silos hold the connection."
                ) {
                    Format = SchemaFormat.Region,
                    Widget = WidgetHint.Region,
                    Immutable = true,
                    ExampleJson = "\"eu-central\""
                },
                new("/properties", SchemaKind.Nested, Description: "The connected cluster's own settings."),
                new(
                    "/properties/distribution",
                    SchemaKind.Text,
                    Description: "What the cluster runs — k3s, kubeadm, OpenShift, a hosted service. "
                    + "Informational: the agent works against any conformant API server and nothing "
                    + "here changes what it does."
                ) { MinLength = 1, MaxLength = 32, DefaultJson = "\"other\"", ExampleJson = "\"k3s\"" },
                new(
                    "/properties/heartbeatSeconds",
                    SchemaKind.WholeNumber,
                    Description: "How often the agent reports in. Read when listInstallCommand is "
                    + "called: the command passes it to the chart and the platform repeats it in the "
                    + "welcome the agent adopts on every connection. A change after that takes effect "
                    + "on the next listInstallCommand — no re-install; the running agent adopts it on "
                    + "its next connection. ⚠ The platform calls a cluster Degraded after ninety "
                    + "seconds without a heartbeat (docs/plan/09 § Cluster connections), so a value "
                    + "above thirty leaves fewer than three chances for a packet to arrive."
                ) { Minimum = 5, Maximum = 60, DefaultJson = "15" }
            ]
        );

    /// <summary>What <c>POST …/listInstallCommand</c> returns.</summary>
    /// <remarks>
    ///     ⚠ The whole response is a credential: <c>/command</c> embeds <c>/token</c>. Both are
    ///     <c>Secret</c>, so a generated form masks them and a read drops them, and the action is
    ///     declared <c>secret: true</c> so nothing records the response.
    /// </remarks>
    public static ResourceSchema ListInstallCommandResponse { get; } =
        ResourceSchema.Of(
            [
                new(
                    "/command",
                    SchemaKind.Text,
                    true,
                    Secret: true,
                    Description: "The helm command to run against the cluster being connected, with "
                    + "the one-time token inline. Run it from a workstation with cluster-admin on that "
                    + "cluster; the platform needs nothing from the cluster's side."
                ),
                new(
                    "/token",
                    SchemaKind.Text,
                    true,
                    Secret: true,
                    Description: "The one-time enrollment token, separately, for an install that does "
                    + "not use helm. It admits exactly one agent connection and is spent by it."
                ),
                new(
                    "/expiresAt",
                    SchemaKind.Text,
                    true,
                    Description: "When the token stops being accepted, RFC 3339. Twenty-four hours "
                    + "from the call; ask again for a fresh one."
                ) { Format = SchemaFormat.DateTime },
                new(
                    "/tunnelEndpoint",
                    SchemaKind.Text,
                    true,
                    Description: "The WebSocket URL the agent dials — wss://{gateway}/agent/v1/tunnel. "
                    + "The cluster needs outbound HTTPS to it and nothing inbound."
                ) { Format = SchemaFormat.Uri },
                new(
                    "/chart",
                    SchemaKind.Text,
                    true,
                    Description: "The chart reference the command installs: the OCI reference this "
                    + "deployment publishes the agent chart under, or charts/agent — the path in a "
                    + "checkout of the CyberCloud repository — when it has not published one, in "
                    + "which case run the command from that checkout."
                )
            ]
        );

    /// <summary>The pointers <see cref="Schema2026" /> declares, in declaration order.</summary>
    public static ImmutableArray<string> Pointers2026 { get; } =
        [.. Schema2026.Properties.Select(static x => x.JsonPointer)];

    // ── The desired body, read ────────────────────────────────────────────────────────────────

    /// <summary>The heartbeat interval a body asks for.</summary>
    public static int HeartbeatSeconds(JsonElement desired) =>
        desired.ValueKind is JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.ValueKind is JsonValueKind.Object
        && properties.TryGetProperty("heartbeatSeconds", out var value)
        && value.ValueKind is JsonValueKind.Number
        && value.TryGetInt32(out var seconds)
            ? seconds
            : DefaultHeartbeatSeconds;

    // ── The install command ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     The <c>helm upgrade --install</c> a tenant runs — one line, quoted for a POSIX shell.
    /// </summary>
    /// <param name="enrollment">
    ///     What the tunnel seam minted. Its <see cref="AgentEnrollment.HeartbeatInterval" /> is the
    ///     value the grain was armed with, so the chart and the welcome say the same number.
    /// </param>
    /// <remarks>
    ///     ⚠ <b><c>--set-string</c> for the token and the cluster id, never <c>--set</c>.</b> Helm's
    ///     <c>--set</c> parses its value: a token that happened to be all digits would become a
    ///     number, and a value with a comma would become a list. <c>--set-string</c> is the one
    ///     spelling that carries an opaque string through unchanged.
    /// </remarks>
    public static string InstallCommand(AgentEnrollment enrollment) {
        ArgumentNullException.ThrowIfNull(enrollment);

        var image = enrollment.AgentImage.Length > 0
            ? " --set-string image.reference=" + Quote(enrollment.AgentImage)
            : string.Empty;

        var heartbeatSeconds = enrollment.HeartbeatInterval > TimeSpan.Zero
            ? (int)enrollment.HeartbeatInterval.TotalSeconds
            : DefaultHeartbeatSeconds;

        return "helm upgrade --install "
            + ReleaseName
            + " "
            + Quote(enrollment.ChartReference)
            + " --namespace "
            + AgentNamespace
            + " --create-namespace"
            + " --set-string platform.tunnelEndpoint="
            + Quote(enrollment.TunnelEndpoint)
            + " --set-string cluster.id="
            + enrollment.ClusterId.ToString("D", CultureInfo.InvariantCulture)
            + " --set-string cluster.enrollmentToken="
            + Quote(enrollment.EnrollmentToken)
            + " --set agent.heartbeatSeconds="
            + heartbeatSeconds.ToString(CultureInfo.InvariantCulture)
            + image;
    }

    /// <summary>A body for the conformance suite.</summary>
    /// <param name="distribution">The distribution.</param>
    /// <param name="heartbeatSeconds">The heartbeat interval.</param>
    /// <param name="location">The region.</param>
    public static string Body(
        string distribution = "k3s",
        int heartbeatSeconds = DefaultHeartbeatSeconds,
        string location = "eu-central"
    ) =>
        new JsonObject {
            ["location"] = location,
            ["properties"] = new JsonObject { ["distribution"] = distribution, ["heartbeatSeconds"] = heartbeatSeconds }
        }.ToJsonString();

    static string Quote(string value) => "'" + value.Replace("'", """'\''""", StringComparison.Ordinal) + "'";
}
