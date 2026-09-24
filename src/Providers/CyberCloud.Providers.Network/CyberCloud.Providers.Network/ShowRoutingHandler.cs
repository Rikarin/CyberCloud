using CyberCloud.Core;
using CyberCloud.Core.Time;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Serves <c>POST …/applicationGateways/{name}/showRouting</c>: what the gateway routes, to which
///     addresses, behind which firewall policy, and whether its pod is up.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The machine addresses come from the <c>ConfigMap</c>'s own record</b>
///         (<see cref="ApplicationGateways.ResolvedKey" />) and not from a fresh resolution: an action
///         has no cross-resource view, and the question a tenant is asking is what the running gateway
///         was configured with, which is exactly that record.
///     </para>
///     <para>
///         ⚠ <b>Which members HAProxy believes are healthy is not here</b>, for
///         <see cref="ShowBackendsHandler" />' reason — it is on a socket inside the pod —
///         <c>charts/managed/application-gateway/conformance.yaml § owed</c>,
///         <c>member-health-is-not-observable</c>.
///     </para>
/// </remarks>
/// <param name="clock">Stamps <c>sampledAt</c>.</param>
public sealed class ShowRoutingHandler(IClock clock) : IResourceActionHandler {
    /// <summary>What the answer is, and is not, on every response.</summary>
    public const string Note =
        "The rules and members are what the platform configured the gateway with, in evaluation order, "
        + "rather than a reading of which members HAProxy currently believes are healthy. Machine "
        + "members show the address they resolved to when the gateway was last reconciled. "
        + "`readyReplicas: 0` means the gateway is not serving, whatever the rules below say.";

    /// <inheritdoc />
    public ResourceTypeName Type => ApplicationGateways.Type;

    /// <inheritdoc />
    public string Action => ApplicationGateways.RoutingAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(ActionContext context, CancellationToken cancellationToken = default) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and whether a gateway is running is read "
                + "from the Deployment object in a cluster."
            );
        }

        var deployment = await cluster.GetAsync(ApplicationGateways.DeploymentRef(context.Namespace, context.Id), cancellationToken);

        if (deployment.TryGetError(out var deploymentError)) {
            return Result<string>.Failure(deploymentError);
        }

        var config = await cluster.GetAsync(ApplicationGateways.ConfigMapRef(context.Namespace, context.Id), cancellationToken);

        var resolved = config.IsSuccess
            ? ApplicationGateways.ResolvedOf(config.GetValueOrThrow().Json)
            : ApplicationGateways.NoResolution;

        var desired = context.Desired;
        var frontend = ApplicationGateways.FrontendV6(desired) is { Length: > 0 } v6
            ? $"{ApplicationGateways.FrontendV4(desired)} and [{v6}]"
            : ApplicationGateways.FrontendV4(desired);

        var listeners = new JsonArray { $"{frontend}:{Int(ApplicationGateways.HttpPort(desired))} http" };

        if (ApplicationGateways.HasHttps(desired)) {
            listeners.Add(
                $"{frontend}:{Int(ApplicationGateways.HttpsPort(desired))} https, certificate "
                + ApplicationGateways.CertificateHandle(desired)
            );
        }

        var members = new JsonArray();
        var unresolved = new JsonArray();

        foreach (var member in ApplicationGateways.Members(desired)) {
            if (!member.IsResource) {
                members.Add(member.ToString());

                continue;
            }

            if (resolved.TryGetValue(member.Target, out var address)) {
                members.Add($"{member} -> {address}");
            } else {
                unresolved.Add(member.ToString());
            }
        }

        var waf = ApplicationGateways.WafMode(desired) == ApplicationGateways.WafOff
            ? "off"
            : $"{ApplicationGateways.WafMode(desired)}, OWASP CRS {ApplicationGateways.CrsVersion(desired)}, "
            + $"paranoia level {Int(ApplicationGateways.ParanoiaLevel(desired))}, "
            + $"{Int(ApplicationGateways.Exclusions(desired).Length)} exclusion(s), "
            + $"{Int(ApplicationGateways.CustomRules(desired).Length)} custom rule(s)";

        return Result<string>.Success(
            new JsonObject {
                ["listeners"] = listeners,
                ["rules"] = new JsonArray([.. ApplicationGateways.Rules(desired).Select(static x => (JsonNode)JsonValue.Create(x.ToString()))]),
                ["members"] = members,
                ["unresolved"] = unresolved,
                ["waf"] = waf,
                ["readyReplicas"] = ReadyReplicas(deployment.GetValueOrThrow().Json),
                ["note"] = Note,
                ["sampledAt"] = clock.UtcNow.ToString("O", CultureInfo.InvariantCulture)
            }.ToJsonString()
        );
    }

    static string Int(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>How many gateway pods are ready; absent is zero, for <see cref="ShowBackendsHandler" />' reason.</summary>
    static int ReadyReplicas(string objectJson) {
        try {
            return JsonNode.Parse(objectJson)?["status"]?["readyReplicas"] is JsonValue value && value.TryGetValue<int>(out var ready)
                ? ready
                : 0;
        } catch (JsonException) {
            return 0;
        }
    }
}
