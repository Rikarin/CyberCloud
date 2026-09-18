using System.Text.Json.Serialization;

namespace CyberCloud.Kubernetes.Contracts.Tunnel;

/// <summary>
///     A <see cref="KubeCommand" /> as JSON, for the tunnel.
/// </summary>
/// <remarks>
///     ⚠ <b>In this assembly because the command's constructor is <c>internal</c>, and that is the
///     type-state chain's guarantee rather than an obstacle to route around.</b> Only
///     <c>KubeCommandBuilder</c> may mint a command, so nothing outside this assembly can build one
///     from JSON either — which is right: a command that crossed the tunnel was built by the
///     builder on the platform side, labels injected and checked, and what the agent applies is
///     that command and not one it assembled. <see cref="FromJson" /> is the one other way in, and
///     it exists for the agent alone.
/// </remarks>
public static class KubeCommandJson {
    /// <summary>The command's fields, flat, with the names the wire uses.</summary>
    sealed record Wire {
        [JsonPropertyName("tenantId")]
        public Guid TenantId { get; init; }

        [JsonPropertyName("subscriptionId")]
        public Guid SubscriptionId { get; init; }

        [JsonPropertyName("resourceId")]
        public Guid ResourceId { get; init; }

        [JsonPropertyName("target")]
        public ObjectRef Target { get; init; } = new();

        [JsonPropertyName("body")]
        public string Body { get; init; } = "{}";

        [JsonPropertyName("fieldManager")]
        public string FieldManager { get; init; } = string.Empty;

        [JsonPropertyName("labels")]
        public Dictionary<string, string> Labels { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("annotations")]
        public Dictionary<string, string> Annotations { get; init; } = new(StringComparer.Ordinal);

        [JsonPropertyName("reconcileHash")]
        public string ReconcileHash { get; init; } = string.Empty;

        [JsonPropertyName("force")]
        public bool Force { get; init; }

        [JsonPropertyName("resourcePath")]
        public string ResourcePath { get; init; } = string.Empty;

        [JsonPropertyName("ownerResourceId")]
        public Guid OwnerResourceId { get; init; }
    }

    /// <summary>Serializes a built command.</summary>
    /// <param name="command">The command.</param>
    public static string ToJson(KubeCommand command) {
        ArgumentNullException.ThrowIfNull(command);

        return TunnelCodec.Serialize(
            new Wire {
                TenantId = command.TenantId,
                SubscriptionId = command.SubscriptionId,
                ResourceId = command.ResourceId,
                Target = command.Target,
                Body = command.Body,
                FieldManager = command.FieldManager,
                Labels = new(command.Labels, StringComparer.Ordinal),
                Annotations = new(command.Annotations, StringComparer.Ordinal),
                ReconcileHash = command.ReconcileHash,
                Force = command.Force,
                ResourcePath = command.ResourcePath,
                OwnerResourceId = command.OwnerResourceId
            }
        );
    }

    /// <summary>Rebuilds a command the platform serialized.</summary>
    /// <param name="json">What <see cref="ToJson" /> produced.</param>
    /// <returns>The command, or a failure naming what was wrong with the JSON.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ The seven mandatory labels are checked again here, on the agent's side. The platform
    ///         injected them, but the agent is the last thing between a frame and a tenant's API
    ///         server, and a frame that lost its labels in transit would be an unlabelled object — the
    ///         one thing ADR-013 makes impossible to build.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A co-owned command carries no labels by design, and the check does not
    ///         invert — it is replaced.</b> The seven are the owner's and stay on the object, so a
    ///         co-owned command that did carry labels would be a co-writer claiming the owner's
    ///         identity and is refused. But "no labels" alone is not a guard: a frame with
    ///         <c>ownerResourceId</c> set and nothing else right would be an unlabelled body applied
    ///         under any manager onto any object, with the label check switched off for it. So the
    ///         agent runs <see cref="KubeCommand.CheckCoOwnedShape" /> instead — the manager derived
    ///         from the owner it names, the live <c>resourceVersion</c> in the body, the fragment
    ///         bookkeeping present on an apply and absent on a withdrawal — and the client it hands
    ///         the command to checks the owner against the object it reads
    ///         (<see cref="KubeCommand.CheckCoOwnedAgainst" />).
    ///     </para>
    /// </remarks>
    public static Result<KubeCommand> FromJson(string json) {
        var wire = TunnelCodec.Deserialize<Wire>(json);

        if (wire.TryGetError(out var error)) {
            return Result<KubeCommand>.Failure(error);
        }

        var value = wire.GetValueOrThrow();

        if (value.OwnerResourceId == Guid.Empty) {
            foreach (var label in KubeLabels.Mandatory) {
                if (!value.Labels.ContainsKey(label)) {
                    return Result<KubeCommand>.Failure(
                        ErrorCode.InvalidRequestBody,
                        $"A command arrived over the tunnel without the '{label}' label. Every object "
                        + "the platform applies carries the seven cybercloud.io/* labels (ADR-013), and "
                        + "the agent refuses one that does not rather than applying it."
                    );
                }
            }
        }

        var command = new KubeCommand {
            TenantId = value.TenantId,
            SubscriptionId = value.SubscriptionId,
            ResourceId = value.ResourceId,
            Target = value.Target,
            Body = value.Body,
            FieldManager = value.FieldManager,
            Labels = value.Labels,
            Annotations = value.Annotations,
            ReconcileHash = value.ReconcileHash,
            Force = value.Force,
            ResourcePath = value.ResourcePath,
            OwnerResourceId = value.OwnerResourceId
        };

        var shape = command.CheckCoOwnedShape();

        return shape.TryGetError(out var shapeError)
            ? Result<KubeCommand>.Failure(
                shapeError.Code,
                shapeError.Message + " The agent refuses the shape over the tunnel rather than applying it."
            )
            : Result<KubeCommand>.Success(command);
    }
}
