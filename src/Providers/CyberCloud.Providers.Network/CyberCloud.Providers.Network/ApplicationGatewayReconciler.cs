using CyberCloud.Core;
using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using System.Collections.Immutable;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Network;

/// <summary>
///     Converges one application gateway onto what it is: the configuration of a proxy and a WAF
///     agent, the pod that runs both, and the certificate the HTTPS listener serves.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="LoadBalancerReconciler" />'s shape plus two reads from outside the body.</b>
///         A certificate is a vault handle resolved on every pass, and a pool member may be a virtual
///         machine resolved through <see cref="ReconcileContext.View" /> to the address KubeVirt reports
///         for it. Both values end up in objects this reconciler applies and in nothing it stores.
///     </para>
///     <para>
///         The four clauses of docs/plan/08 § The reconcile loop:
///     </para>
///     <list type="number">
///         <item>
///             <b>Idempotent.</b> Every rendered document is a pure function of the namespace, the
///             address, the body, the resolved members and the certificate.
///         </item>
///         <item><b>No hidden state.</b> The only field is the primary constructor's <see cref="IClock" />.</item>
///         <item>
///             <b>Bounded.</b> Three applies and three reads, plus two view calls and one read per
///             machine member — ⚠ up to <see cref="ApplicationGateways.MaxEntries" /> machines, which
///             is the one place this pass could approach the 30-second budget; see
///             <c>charts/managed/application-gateway/conformance.yaml § owed</c>,
///             <c>machine-members-are-resolved-one-by-one</c>.
///         </item>
///         <item>
///             <b>Observes, never assumes.</b> Converged follows a read of every applied object.
///             ⚠ Converged does not mean serving — <c>showRouting</c>'s <c>readyReplicas</c> does.
///         </item>
///     </list>
/// </remarks>
/// <param name="clock">Stamps <see cref="ObservedState.ObservedAt" />.</param>
public sealed class ApplicationGatewayReconciler(IClock clock) : IResourceReconciler {
    /// <summary>KubeVirt's running-machine object, where the machine's address is reported.</summary>
    /// <remarks>
    ///     ⚠ Spelled here rather than taken from <c>CyberCloud.Providers.Compute.Contracts</c>, which
    ///     rule 2 of docs/plan/03 § Assembly graph rules keeps out. The <c>VirtualMachineInstance</c> is
    ///     KubeVirt's object rather than the Compute provider's — it carries none of ADR-013's labels,
    ///     which is why <c>IResourceView.RenderedObjectsAsync</c> does not list it and it is read by the
    ///     <c>VirtualMachine</c>'s name.
    /// </remarks>
    public static GroupVersionKind MachineInstanceKind { get; } =
        new() { Group = "kubevirt.io", Version = "v1", Kind = "VirtualMachineInstance", Plural = "virtualmachineinstances" };

    /// <inheritdoc />
    public ResourceTypeName Type => ApplicationGateways.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and an application gateway is a pod in a "
                + "cluster. The type declares RequiresCluster, so the driver should have refused this pass."
            );
        }

        if (ApplicationGateways.BodyProblem(context.Desired) is { } problem) {
            context.Log.Report("refused", problem);

            return ReconcileOutcome.Failed(ErrorCode.InvalidRequestBody, problem);
        }

        var name = ApplicationGateways.ObjectNameOf(context.Id);

        // ── The machines behind resource-id members ─────────────────────────────────────────────
        var resolution = await ResolveMachinesAsync(context, cluster, cancellationToken);

        if (resolution.Outcome is { } resolutionOutcome) {
            return resolutionOutcome;
        }

        foreach (var missing in resolution.Unresolved) {
            context.Log.Report("unresolved", missing);
        }

        // ── The certificate: resolved once, written to a Secret, never to a body ────────────────
        var tlsHash = string.Empty;
        string? pem = null;

        if (ApplicationGateways.HasHttps(context.Desired)) {
            var handle = ParseCertificateRef(ApplicationGateways.CertificateHandle(context.Desired), context.Id.TenantId);

            if (handle.TryGetError(out var handleError)) {
                return ReconcileOutcome.FromFailure(handleError);
            }

            var resolved = await context.Secrets.ResolveAsync(handle.GetValueOrThrow(), cancellationToken);

            if (resolved.TryGetError(out var resolveError)) {
                return ReconcileOutcome.FromFailure(resolveError);
            }

            pem = resolved.GetValueOrThrow();

            if (ApplicationGateways.PemProblem(pem) is { } pemProblem) {
                context.Log.Report("refused", pemProblem);

                return ReconcileOutcome.Failed(new Error(ErrorCode.InvalidRequestBody, pemProblem, "/properties/listeners/certificate"));
            }

            tlsHash = KubeLabels.ReconcileHash(pem);

            context.Log.Report("applying-certificate", $"writing the certificate of '{name}' into its Secret", 15);

            if (await Apply(context, cluster, KubeSecret.Kind, ApplicationGateways.TlsSecretJson(context.Id, pem), "the certificate", cancellationToken)
                is { } secretProblem) {
                return secretProblem;
            }
        }

        context.Log.Report("applying-config", $"applying the configuration of '{name}'", 30);

        if (await Apply(
                context,
                cluster,
                ApplicationGateways.ConfigMapKind,
                ApplicationGateways.ConfigMapJson(context.Id, context.Desired, resolution.Resolved),
                "the gateway configuration",
                cancellationToken
            ) is { } configProblem) {
            return configProblem;
        }

        context.Log.Report("applying-gateway", $"applying the gateway '{name}'", 60);

        if (await Apply(
                context,
                cluster,
                ApplicationGateways.DeploymentKind,
                ApplicationGateways.DeploymentJson(context.Namespace, context.Id, context.Desired, resolution.Resolved, tlsHash),
                "the gateway",
                cancellationToken
            ) is { } gatewayProblem) {
            return gatewayProblem;
        }

        // ── Clause 4 ─────────────────────────────────────────────────────────────────────────────
        foreach (var target in ApplicationGateways.Objects(context.Namespace, context.Id)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress($"'{target}' was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                    : ReconcileOutcome.FromFailure(readError);
            }

            var json = read.GetValueOrThrow().Json;

            if (!ApplicationGateways.Matches(json, context.Namespace, context.Id, context.Desired, resolution.Resolved)
                || (target.Kind.Kind == "Deployment" && TlsHashOf(json) != tlsHash)) {
                return ReconcileOutcome.InProgress(
                    $"'{target}' is readable and does not yet carry the desired spec",
                    TimeSpan.FromSeconds(5)
                );
            }
        }

        if (pem is not null) {
            var secret = await cluster.GetAsync(ApplicationGateways.TlsSecretRef(context.Namespace, context.Id), cancellationToken);

            if (secret.TryGetError(out var secretError)) {
                return secretError.Code == ErrorCode.ResourceNotFound
                    ? ReconcileOutcome.InProgress("the certificate Secret was applied and is not readable back yet", TimeSpan.FromSeconds(5))
                    : ReconcileOutcome.FromFailure(secretError);
            }

            var carried = KubeSecret.Value(secret.GetValueOrThrow(), ApplicationGateways.TlsFile);

            if (!carried.IsSuccess || carried.GetValueOrThrow() != pem) {
                return ReconcileOutcome.InProgress(
                    "the certificate Secret is readable and does not yet carry the vault's value",
                    TimeSpan.FromSeconds(5)
                );
            }
        } else if (await RemoveAsync(context, cluster, ApplicationGateways.TlsSecretRef(context.Namespace, context.Id), cancellationToken)
                   is { } staleProblem) {
            // ⚠ A certificate that was REMOVED from the body leaves a Secret nothing mounts. It holds a
            // private key, so it is deleted rather than left for a drift scan to find.
            return staleProblem;
        }

        context.Log.Report(
            "configured",
            $"the gateway '{name}' and its configuration are in the cluster"
            + (resolution.Unresolved.IsEmpty
                ? ""
                : $", with {resolution.Unresolved.Length.ToString(CultureInfo.InvariantCulture)} machine member(s) left out")
            + $". Whether it is serving is on POST …/{ApplicationGateways.RoutingAction}.",
            100
        );

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ The pod first, on <see cref="LoadBalancerReconciler.DeleteAsync" />' argument, and the
    ///     certificate last — whichever of the three a half-finished teardown leaves, it is never a
    ///     pod serving a configuration the platform can no longer see.
    /// </remarks>
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        context.Log.Report("deleting", $"removing the gateway '{ApplicationGateways.ObjectNameOf(context.Id)}'");

        foreach (var target in ApplicationGateways.AllObjects(context.Namespace, context.Id)) {
            if (await RemoveAsync(context, cluster, target, cancellationToken) is { } problem) {
                return problem;
            }
        }

        foreach (var target in ApplicationGateways.AllObjects(context.Namespace, context.Id)) {
            var read = await cluster.GetAsync(target, cancellationToken);

            if (read.IsSuccess) {
                return ReconcileOutcome.InProgress($"'{target}' is still readable", TimeSpan.FromSeconds(5));
            }

            if (read.Error!.Code != ErrorCode.ResourceNotFound) {
                return ReconcileOutcome.FromFailure(read.Error);
            }
        }

        context.Log.Report("deleted", "the gateway is gone and its frontend address is back in the subnet's pool", 100);

        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    /// <remarks>
    ///     ⚠ <b>Re-renders from the record the <c>ConfigMap</c> carries</b>
    ///     (<see cref="ApplicationGateways.ResolvedKey" />) because an observe has no view to resolve a
    ///     machine with. A hand edit to that record is caught too: the configuration beside it would
    ///     no longer be what the record renders.
    /// </remarks>
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var gateway = await cluster.GetAsync(ApplicationGateways.DeploymentRef(context.Namespace, context.Id), cancellationToken);

        if (gateway.TryGetError(out _)) {
            return new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "the gateway is absent" };
        }

        var found = gateway.GetValueOrThrow();
        var config = await cluster.GetAsync(ApplicationGateways.ConfigMapRef(context.Namespace, context.Id), cancellationToken);

        var matches = config.IsSuccess
            && ApplicationGateways.ResolvedOf(config.GetValueOrThrow().Json) is var resolved
            && ApplicationGateways.Matches(config.GetValueOrThrow().Json, context.Namespace, context.Id, context.Desired, resolved)
            && ApplicationGateways.Matches(found.Json, context.Namespace, context.Id, context.Desired, resolved);

        return new() {
            Exists = true,
            Json = found.Json,
            ObservedAt = clock.UtcNow,
            Revision = found.ResourceVersion,
            Summary = matches ? "the gateway carries the desired configuration" : "the gateway has drifted"
        };
    }

    // ── Machines ─────────────────────────────────────────────────────────────────────────────────

    /// <summary>What the machine members resolved to, which did not, or the outcome that ends the pass.</summary>
    /// <param name="Resolved">Resource id path to address.</param>
    /// <param name="Unresolved">One sentence per member left out of the configuration.</param>
    /// <param name="Outcome">A terminal or transient outcome, or <see langword="null" /> to carry on.</param>
    sealed record Resolution(
        ImmutableSortedDictionary<string, string> Resolved,
        ImmutableArray<string> Unresolved,
        ReconcileOutcome? Outcome
    );

    /// <summary>
    ///     Resolves every resource-id member to the address KubeVirt reports for it.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Three outcomes per machine, and only one of them ends the pass.</b> A machine on
    ///         another cluster or in another network is a body that can never work and is refused
    ///         terminally. A machine the view does not return — gone, or never granted to this gateway
    ///         — and a machine that is not running are <b>left out</b>: the pool is rendered without
    ///         them and <c>showRouting</c> names them, because blocking every other change to a gateway
    ///         on one stopped machine would make the gateway as fragile as its least healthy member.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>"In this network" is the machine's own body</b> — <c>/properties/network/virtualNetwork</c>
    ///         — plus the namespace its rendered <c>VirtualMachine</c> is in, which is this gateway's
    ///         exactly when the two share a subscription and a resource group. A network name alone is
    ///         unique only within a resource group.
    ///     </para>
    /// </remarks>
    async Task<Resolution> ResolveMachinesAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        CancellationToken cancellationToken
    ) {
        var resolved = ApplicationGateways.NoResolution.ToBuilder();
        var unresolved = ImmutableArray.CreateBuilder<string>();
        var network = ApplicationGateways.NetworkOf(context.Id);

        var targets = ApplicationGateways.Members(context.Desired)
            .Where(static x => x.IsResource)
            .Select(static x => x.Target)
            .Distinct(StringComparer.Ordinal);

        foreach (var target in targets) {
            var id = ResourceId.ParsePath(target).GetValueOrThrow();
            var read = await context.View.ReadAsync(id, cancellationToken);

            if (read.TryGetError(out var readError)) {
                if (readError.Code != ErrorCode.ResourceNotFound) {
                    return new(resolved.ToImmutable(), unresolved.ToImmutable(), ReconcileOutcome.FromFailure(readError));
                }

                unresolved.Add(
                    $"'{target}' does not exist, or this gateway has not been granted read on it — grant resource:"
                    + context.Id.Id.ToString("N", CultureInfo.InvariantCulture)
                    + " the reader role on the machine or its resource group, then PUT the gateway again."
                );

                continue;
            }

            var snapshot = read.GetValueOrThrow();

            if (snapshot.ClusterId != cluster.ClusterId) {
                return Refuse(
                    $"'{target}' runs on cluster {snapshot.ClusterId:D} and this gateway on {cluster.ClusterId:D}. "
                    + "A pool member has to be in the gateway's own network, which is on one cluster."
                );
            }

            var machineNetwork = NetworkNameOf(snapshot.Body);

            if (!string.Equals(machineNetwork, network, StringComparison.Ordinal)) {
                return Refuse(
                    $"'{target}' is attached to the network '{machineNetwork}' and this gateway is in '{network}'. "
                    + "There is no route between two virtual networks unless a peering adds one, and the "
                    + "gateway's pool members have to be reachable from its own subnet."
                );
            }

            var rendered = await context.View.RenderedObjectsAsync(id, cancellationToken);

            if (rendered.TryGetError(out var renderedError)) {
                return new(resolved.ToImmutable(), unresolved.ToImmutable(), ReconcileOutcome.FromFailure(renderedError));
            }

            var machine = rendered.GetValueOrThrow()
                .FirstOrDefault(static x => x.Kind.Group == MachineInstanceKind.Group && x.Kind.Kind == "VirtualMachine");

            if (machine is null) {
                unresolved.Add($"'{target}' has not rendered a KubeVirt VirtualMachine yet.");

                continue;
            }

            if (!string.Equals(machine.Namespace, context.Namespace, StringComparison.Ordinal)) {
                return Refuse(
                    $"'{target}' is in another resource group, so its network '{machineNetwork}' is not this "
                    + "gateway's network of the same name."
                );
            }

            var instance = await cluster.GetAsync(
                new() { Kind = MachineInstanceKind, Namespace = machine.Namespace, Name = machine.Name },
                cancellationToken
            );

            if (instance.TryGetError(out var instanceError)) {
                if (instanceError.Code != ErrorCode.ResourceNotFound) {
                    return new(resolved.ToImmutable(), unresolved.ToImmutable(), ReconcileOutcome.FromFailure(instanceError));
                }

                unresolved.Add($"'{target}' is not running, so it has no address.");

                continue;
            }

            if (AddressOf(instance.GetValueOrThrow().Json) is { } address) {
                resolved[target] = address;
            } else {
                unresolved.Add($"'{target}' is running and KubeVirt has not reported an address for it yet.");
            }
        }

        return new(resolved.ToImmutable(), unresolved.ToImmutable(), null);

        Resolution Refuse(string message) {
            context.Log.Report("refused", message);

            return new(
                resolved.ToImmutable(),
                unresolved.ToImmutable(),
                ReconcileOutcome.Failed(new Error(ErrorCode.InvalidRequestBody, message, "/properties/backendPools"))
            );
        }
    }

    /// <summary>The network a machine's body names, or empty.</summary>
    static string NetworkNameOf(string body) {
        try {
            return JsonNode.Parse(body)?["properties"]?["network"]?["virtualNetwork"]?.GetValue<string>() ?? "";
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return "";
        }
    }

    /// <summary>
    ///     The first usable address KubeVirt reports on a running machine's interfaces, in its canonical
    ///     spelling, or <see langword="null" />.
    /// </summary>
    /// <param name="instanceJson">The <c>VirtualMachineInstance</c>'s JSON.</param>
    /// <remarks>
    ///     ⚠ <b>The address is the guest agent's report, so it is the tenant's word</b>, and it is written
    ///     into the gateway's configuration. A machine reporting <c>127.0.0.1</c> would otherwise make the
    ///     gateway send its pool to its own pod — the firewall agent's port included. So it is held to
    ///     <see cref="ApplicationGateways.MemberAddressProblem" /> like an address a body names, and
    ///     rendered as <see cref="System.Net.IPAddress.ToString" /> spells it rather than as reported.
    /// </remarks>
    public static string? AddressOf(string instanceJson) {
        try {
            return JsonNode.Parse(instanceJson)?["status"]?["interfaces"] is JsonArray interfaces
                ? interfaces
                    .Select(static x => x?["ipAddress"]?.GetValue<string>())
                    .Select(static x => System.Net.IPAddress.TryParse(x, out var parsed) ? parsed : null)
                    .FirstOrDefault(static x => x is not null && ApplicationGateways.MemberAddressProblem(x) is null)
                    ?.ToString()
                : null;
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return null;
        }
    }

    // ── The certificate handle ───────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Parses <c>path#field[@version]</c>, refusing a path outside the tenant's own vault prefix.
    /// </summary>
    /// <param name="spelled">The handle as the body spells it.</param>
    /// <param name="tenantId">The tenant whose gateway names it — the only tenant whose paths it may name.</param>
    /// <remarks>
    ///     ⚠ <b>The tenancy check <c>VirtualMachines.ParseCloudInitRef</c> makes, for its reason</b>: the
    ///     resolver holds one platform-wide token, so the path is the only thing scoping the read, and
    ///     the value lands in a Secret the tenant's own pod mounts. Spelled again rather than shared
    ///     because rule 2 keeps one family's assembly out of another's.
    /// </remarks>
    public static Result<SecretRef> ParseCertificateRef(string spelled, Guid tenantId) {
        const string Pointer = "/properties/listeners/certificate";
        var hash = spelled.IndexOf('#', StringComparison.Ordinal);

        if (hash <= 0 || hash == spelled.Length - 1) {
            return Result<SecretRef>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{spelled}' is not a vault handle. Write path#field, optionally @version.",
                Pointer
            );
        }

        var path = spelled[..hash];
        var prefix = string.Create(CultureInfo.InvariantCulture, $"tenants/{tenantId:D}/");

        // ⚠ SecretRef.IsConfinedTo and not StartsWith alone: `tenants/{mine}/../{theirs}/cert` starts
        // with this tenant's prefix, and the resolver's HTTP client collapses the dot segments into the
        // other tenant's path — whose private key this gateway's Secret would then carry and its HTTPS
        // listener serve. Found by #31's review; the Compute and Mail copies of this check had already
        // been fixed on master for the same reason.
        if (!SecretRef.IsConfinedTo(path, prefix)) {
            return Result<SecretRef>.Failure(
                ErrorCode.AuthorizationFailed,
                $"listeners.certificate names '{path}', which is not under your tenant's vault prefix "
                + $"'{prefix}'. A gateway can only be given a certificate your own tenant holds, and the "
                + "path may not contain an empty, '.' or '..' segment.",
                Pointer
            );
        }

        var rest = spelled[(hash + 1)..];
        var at = rest.IndexOf('@', StringComparison.Ordinal);

        return Result<SecretRef>.Success(
            new() { Path = path, Field = at < 0 ? rest : rest[..at], Version = at < 0 ? string.Empty : rest[(at + 1)..] }
        );
    }

    static string TlsHashOf(string deploymentJson) {
        try {
            return JsonNode.Parse(deploymentJson)?["spec"]?["template"]?["metadata"]?["annotations"]?[
                ApplicationGateways.TlsChecksumAnnotation]?.GetValue<string>() ?? "";
        } catch (Exception ex) when (ex is JsonException or InvalidOperationException) {
            return "";
        }
    }

    // ── Applying and removing ────────────────────────────────────────────────────────────────────

    static async Task<ReconcileOutcome?> Apply(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        GroupVersionKind kind,
        string objectJson,
        string what,
        CancellationToken cancellationToken
    ) {
        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(objectJson)
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return ReconcileOutcome.FromFailure(applyError);
        }

        var outcome = applied.GetValueOrThrow();

        switch (outcome.Result) {
            case ApplyResult.Suspended:
                context.Log.Report("waiting-for-cluster", outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable",
                    TimeSpan.FromSeconds(30)
                );

            case ApplyResult.Conflict:
                // ⚠ Reported and retried, never forced — LoadBalancerReconciler's reason.
                context.Log.Report("conflict", outcome.Drift?.Describe() ?? outcome.Message);

                return ReconcileOutcome.InProgress(
                    outcome.Drift?.Describe() ?? $"another field manager owns part of {what} and it was not overwritten",
                    TimeSpan.FromSeconds(30)
                );

            default:
                return null;
        }
    }

    static async Task<ReconcileOutcome?> RemoveAsync(
        ReconcileContext context,
        IKubeClusterConnection cluster,
        ObjectRef target,
        CancellationToken cancellationToken
    ) {
        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(target.Kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(new JsonObject { ["metadata"] = new JsonObject { ["name"] = target.Name } }.ToJsonString())
            // ⚠ Foreground, for LoadBalancerReconciler's reason: a background cascade returns while the
            // pod behind the Deployment can still be serving.
            .DeleteAsync(CascadePolicy.Foreground, cancellationToken);

        return deleted.TryGetError(out var deleteError) && deleteError.Code != ErrorCode.ResourceNotFound
            ? ReconcileOutcome.FromFailure(deleteError)
            : null;
    }
}
