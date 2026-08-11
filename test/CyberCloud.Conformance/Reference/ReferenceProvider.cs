using CyberCloud.Core.Time;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CyberCloud.Conformance.Reference;

/// <summary>
///     A provider that exists only so the shared suite has something to run against.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Not a real provider, registered nowhere, and it must never become one.</b>
///         <c>CyberCloud.Conformance</c> deliberately references no provider — that is what makes it
///         shared — so without this the suite would compile, ship, and run zero tests until somebody
///         wrote a provider. A suite that is green because it ran nothing is the failure
///         <c>--minimum-expected-tests</c> exists to catch, one level down.
///     </para>
///     <para>
///         It also gives the suite something to be <i>wrong</i> against: <c>SuiteRejectionTests</c>
///         runs the reconciler contract over <see cref="AssumingProbeReconciler" /> and asserts the
///         findings. A conformance suite that has only ever seen conforming reconcilers has not been
///         shown to reject anything.
///     </para>
/// </remarks>
public static class Probes {
    /// <summary>The reference provider's namespace.</summary>
    public const string ProviderNamespace = "CyberCloud.ConformanceReference";

    /// <summary>The one type.</summary>
    public const string TypePath = "probes";

    /// <summary>The one api-version.</summary>
    public const string V2026 = "2026-08-01";

    /// <summary>The type.</summary>
    public static ResourceTypeName Type { get; } = new(ProviderNamespace, TypePath);

    /// <summary>The object a probe becomes.</summary>
    public static GroupVersionKind Kind { get; } =
        new() { Group = "", Version = "v1", Kind = "ConfigMap", Plural = "configmaps" };

    /// <summary>The body shape.</summary>
    public static ResourceSchema Schema { get; } =
        ResourceSchema.Of(
            [
                new("/location", SchemaKind.Text, Required: true),
                new("/properties", SchemaKind.Nested),
                new("/properties/clusterId", SchemaKind.Text, Required: true),
                new("/properties/note", SchemaKind.Text, Required: true)
            ]
        );

    /// <summary>A valid body.</summary>
    /// <param name="clusterId">The cluster to place the object in.</param>
    /// <param name="note">What the object says.</param>
    public static string Body(Guid clusterId, string note = "first") =>
        new JsonObject {
            ["location"] = "eu-central",
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture), ["note"] = note
            }
        }.ToJsonString();

    /// <summary>A body missing a required property.</summary>
    /// <param name="clusterId">The cluster.</param>
    public static string BodyWithoutNote(Guid clusterId) =>
        new JsonObject {
            ["location"] = "eu-central",
            ["properties"] = new JsonObject { ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture) }
        }.ToJsonString();

    /// <summary>The note a desired body carries.</summary>
    /// <param name="desired">The desired body.</param>
    public static string NoteOf(JsonElement desired) =>
        desired.ValueKind == JsonValueKind.Object
        && desired.TryGetProperty("properties", out var properties)
        && properties.TryGetProperty("note", out var note)
        && note.ValueKind == JsonValueKind.String
            ? note.GetString() ?? string.Empty
            : string.Empty;

    /// <summary>The object document a desired body becomes.</summary>
    /// <param name="name">The object's name.</param>
    /// <param name="desired">The desired body.</param>
    public static string ObjectJson(string name, JsonElement desired) =>
        new JsonObject {
            ["metadata"] = new JsonObject { ["name"] = name },
            ["data"] = new JsonObject { ["note"] = NoteOf(desired) }
        }.ToJsonString();

    /// <summary>Whether an object carries what a desired body asked for.</summary>
    /// <param name="objectJson">The object's JSON.</param>
    /// <param name="desiredJson">The desired body's JSON.</param>
    public static bool Matches(string objectJson, string desiredJson) {
        using var desired = JsonDocument.Parse(desiredJson);

        return JsonNode.Parse(objectJson) is JsonObject document
               && document["data"] is JsonObject data
               && data["note"]?.GetValue<string>() == NoteOf(desired.RootElement);
    }
}

/// <summary>The reference provider's declaration.</summary>
public sealed class ReferenceProvider : IResourceProvider {
    /// <inheritdoc />
    public string ProviderNamespace => Probes.ProviderNamespace;

    /// <inheritdoc />
    public void Describe(IProviderBuilder builder) {
        ArgumentNullException.ThrowIfNull(builder);

        builder
            .ResourceType(Probes.TypePath)
            .ApiVersion(Probes.V2026, Probes.Schema)
            .Reconciler<ProbeReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action("ping", ActionKind.Post, "write")
            .SupportsTags()
            .RequiresCluster();
    }
}

/// <summary>A conforming reconciler: idempotent, stateless, bounded, and it reads back.</summary>
/// <param name="clock">Stamps the observation.</param>
public sealed class ProbeReconciler(IClock clock) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => Probes.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(ErrorCode.InternalError, "a probe needs a cluster.");
        }

        context.Log.Report("applying", $"applying probe '{context.Id.Name}'", 50);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(Probes.Kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(Probes.ObjectJson(context.Id.Name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return ReconcileOutcome.Failed(applyError, true);
        }

        if (applied.GetValueOrThrow().Result is ApplyResult.Suspended or ApplyResult.Conflict) {
            return ReconcileOutcome.InProgress("the cluster did not take the apply", TimeSpan.FromSeconds(10));
        }

        // ⚠ CLAUSE 4. Converged follows the read, never the apply.
        var read = await cluster.GetAsync(Target(context.Namespace, context.Id.Name), cancellationToken);
        if (read.TryGetError(out _)) {
            return ReconcileOutcome.InProgress("the probe is not readable back yet", TimeSpan.FromSeconds(5));
        }

        if (!Probes.Matches(read.GetValueOrThrow().Json, context.Desired.GetRawText())) {
            return ReconcileOutcome.InProgress("the probe does not carry the desired note yet", TimeSpan.FromSeconds(5));
        }

        context.Log.Report("ready", "the probe reads back as desired", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Converged;
        }

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(Probes.Kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(Probes.ObjectJson(context.Id.Name, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var error) && error.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.Failed(error, true);
        }

        var read = await cluster.GetAsync(Target(context.Namespace, context.Id.Name), cancellationToken);
        if (read.IsSuccess) {
            return ReconcileOutcome.InProgress("the probe is still readable", TimeSpan.FromSeconds(5));
        }

        context.Log.Report("deleted", "the probe is gone", 100);
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public async Task<ObservedState> ObserveAsync(
        ObserveContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ObservedState.Absent;
        }

        var read = await cluster.GetAsync(Target(context.Namespace, context.Id.Name), cancellationToken);

        return read.IsFailure
            ? new() { Exists = false, ObservedAt = clock.UtcNow, Summary = "absent" }
            : new() {
                Exists = true,
                Json = read.GetValueOrThrow().Json,
                ObservedAt = clock.UtcNow,
                Summary = "present"
            };
    }

    static ObjectRef Target(string ns, string name) =>
        new() { Kind = Probes.Kind, Namespace = ns, Name = name };
}

/// <summary>
///     A reconciler that breaks clauses 2 and 4, so the suite can be shown to reject one.
/// </summary>
/// <remarks>
///     ⚠ <b>This exists to test the test.</b> <see cref="applied" /> is the violation twice over: a
///     mutable instance field (clause 2, which breaks when the grain moves silo) and the thing
///     <see cref="ReconcileAsync" /> consults instead of reading the world back (clause 4).
/// </remarks>
public sealed class AssumingProbeReconciler : IResourceReconciler {
    bool applied;

    /// <inheritdoc />
    public ResourceTypeName Type => Probes.Type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (applied) {
            // ⚠ CLAUSE 4, VIOLATED. "I applied this, so it is there" — without looking.
            return ReconcileOutcome.Converged;
        }

        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(ErrorCode.InternalError, "a probe needs a cluster.");
        }

        var result = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(Probes.Kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(Probes.ObjectJson(context.Id.Name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (result.TryGetError(out var error)) {
            return ReconcileOutcome.Failed(error, true);
        }

        applied = true;
        return ReconcileOutcome.Converged;
    }

    /// <inheritdoc />
    public Task<ReconcileOutcome> DeleteAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        applied = false;
        return Task.FromResult(ReconcileOutcome.Converged);
    }

    /// <inheritdoc />
    public Task<ObservedState> ObserveAsync(ObserveContext context, CancellationToken cancellationToken = default) =>
        Task.FromResult(new ObservedState { Exists = applied, Summary = "remembered" });
}
