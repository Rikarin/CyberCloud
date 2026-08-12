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

    // ── The child type ─────────────────────────────────────────────────────────────────────────

    /// <summary>The nested type's path. Its parent type is <see cref="TypePath" />.</summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This exists so the isolation suite can assert the shape of a <i>child's</i> parent
    ///         edge against the real writer, the real authorizer and the real schema, and so the
    ///         shared conformance suite has a child to run end to end.</b> No shipping provider
    ///         declares a nested type yet — docs/plan/12 § The catalogue lists
    ///         <c>servers/databases</c> and its siblings as owed — and <c>TestingProvider</c>'s
    ///         <c>widgets/gadgets</c> lives in <c>CyberCloud.ResourceManager.Tests</c>, where both
    ///         halves of the authorization seam are doubled. A double writes whatever tuple its author
    ///         believed in, which is exactly what a test of the edge's subject cannot use.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is a FULL type — reconciler, cluster, action, tags — and it was not, and the
    ///         difference is the whole of failure class (a).</b> It used to declare only a schema and
    ///         three permissions, because the only thing that addressed it was the parent-edge test.
    ///         A child registered that way cannot run the shared suite: it has no reconciler for
    ///         <c>TheTypeIsRegisteredWithAReconcilerAndAllThreePermissions</c>, owns no objects for
    ///         the four cluster-facing assertions, declares no action for the two POST ones, and
    ///         refuses tags. A case for it would have passed a suite that quietly asked less — which
    ///         is the recurring failure this harness has already been bitten by once, when a drift
    ///         test deleted one object of two and went green on the provider that rendered one.
    ///     </para>
    /// </remarks>
    public const string ChildTypePath = "probes/samples";

    /// <summary>The nested type.</summary>
    public static ResourceTypeName ChildType { get; } = new(ProviderNamespace, ChildTypePath);

    /// <summary>
    ///     The child's shape. ⚠ Nothing in it names the parent — the address does, and that is the
    ///     whole of docs/plan/12 § Child resources.
    /// </summary>
    public static ResourceSchema ChildSchema { get; } =
        ResourceSchema.Of(
            [
                new("/location", SchemaKind.Text, Required: true),
                new("/properties", SchemaKind.Nested),
                new("/properties/clusterId", SchemaKind.Text, Required: true),
                new("/properties/note", SchemaKind.Text, Required: true)
            ]
        );

    /// <summary>A valid child body.</summary>
    /// <param name="clusterId">The cluster to place the object in.</param>
    /// <param name="note">What the child says.</param>
    public static string ChildBody(Guid clusterId, string note = "first") =>
        new JsonObject {
            ["location"] = "eu-central",
            ["properties"] = new JsonObject {
                ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture), ["note"] = note
            }
        }.ToJsonString();

    /// <summary>A child body missing a required property.</summary>
    /// <param name="clusterId">The cluster.</param>
    public static string ChildBodyWithoutNote(Guid clusterId) =>
        new JsonObject {
            ["location"] = "eu-central",
            ["properties"] = new JsonObject { ["clusterId"] = clusterId.ToString("D", CultureInfo.InvariantCulture) }
        }.ToJsonString();

    /// <summary>
    ///     The name of the object a child renders: its ancestors' names and its own, joined.
    /// </summary>
    /// <param name="id">The child's address.</param>
    /// <remarks>
    ///     ⚠ <b>The parent's name is in the object name on purpose, and it is an assertion rather than
    ///     a convention.</b> Two children of two different parents may share a name — the address
    ///     distinguishes them and <c>ReconcileDriver.NamespaceFor</c> does not, because a namespace is
    ///     per <c>(subscription, resource group)</c> and a parent is inside one. So a renderer that
    ///     ignored <see cref="ResourceId.ParentNames" /> would have the two children fighting over one
    ///     ConfigMap, each converging by overwriting the other. Reading the ancestors here is also
    ///     what makes every cluster-facing assertion in the suite depend on the address having
    ///     carried them down: an id built without them renders a different name and
    ///     <c>WhatTheProviderAppliedIsInTheClusterAndMatchesTheDesiredBody</c> fails.
    /// </remarks>
    public static string ObjectNameOf(ResourceId id) =>
        id.ParentNames.Length == 0
            ? id.Name
            : id.ParentNames.Replace('/', '-') + "-" + id.Name;

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
            .RequiresCluster()
            // ⚠ THE CHILD, DECLARED EXACTLY AS ITS PARENT IS. Every capability here is one the shared
            // suite has an assertion for; dropping any of them would turn that assertion into a
            // branch the child skips. See Probes.ChildTypePath's remarks for the version of this type
            // that had only a schema, and what a conformance run against it would have been worth.
            .ResourceType(Probes.ChildTypePath)
            .ApiVersion(Probes.V2026, Probes.ChildSchema)
            .Reconciler<SampleReconciler>()
            .Meters(QuotaMeter.Resources)
            .Permissions("read", "write", "delete")
            .Action("ping", ActionKind.Post, "write")
            .SupportsTags()
            .RequiresCluster();
    }
}

/// <summary>
///     A conforming reconciler: idempotent, stateless, bounded, and it reads back.
/// </summary>
/// <remarks>
///     ⚠ <b>The type it serves is a constructor argument rather than a constant, and the two concrete
///     classes below are the reason.</b> <c>ProviderRegistry</c> stores each type's reconciler by
///     CONCRETE TYPE and <c>ReconcileDriver</c> resolves it from the container by that type, so a
///     parent and its child cannot be served by one class however identical their work is — the
///     registry would have two registrations pointing at one singleton whose <see cref="Type" /> can
///     only name one of them, and <c>ProviderRegistry.Build</c> refuses exactly that. Two thin
///     subclasses over one body is the honest shape: what differs between them really is only the
///     type they answer for.
/// </remarks>
/// <param name="clock">Stamps the observation.</param>
/// <param name="type">The type this reconciler serves.</param>
public abstract class ProbeReconcilerBase(IClock clock, ResourceTypeName type) : IResourceReconciler {
    /// <inheritdoc />
    public ResourceTypeName Type => type;

    /// <inheritdoc />
    public async Task<ReconcileOutcome> ReconcileAsync(
        ReconcileContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return ReconcileOutcome.Failed(ErrorCode.InternalError, "a probe needs a cluster.");
        }

        var name = Probes.ObjectNameOf(context.Id);

        context.Log.Report("applying", $"applying probe '{name}'", 50);

        var applied = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(Probes.Kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(Probes.ObjectJson(name, context.Desired))
            .ApplyAsync(cancellationToken);

        if (applied.TryGetError(out var applyError)) {
            return ReconcileOutcome.FromFailure(applyError);
        }

        if (applied.GetValueOrThrow().Result is ApplyResult.Suspended or ApplyResult.Conflict) {
            return ReconcileOutcome.InProgress("the cluster did not take the apply", TimeSpan.FromSeconds(10));
        }

        // ⚠ CLAUSE 4. Converged follows the read, never the apply.
        var read = await cluster.GetAsync(Target(context.Namespace, name), cancellationToken);
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

        var name = Probes.ObjectNameOf(context.Id);

        var deleted = await KubeCommand.For(cluster)
            .WithTenantId(context.Id.TenantId)
            .WithResourceId(context.Id)
            .InNamespace(context.Namespace)
            .WithKind(Probes.Kind)
            .WithApiVersion(context.ApiVersion)
            .ObjectJson(Probes.ObjectJson(name, context.Desired))
            .DeleteAsync(CascadePolicy.Background, cancellationToken);

        if (deleted.TryGetError(out var error) && error.Code != ErrorCode.ResourceNotFound) {
            return ReconcileOutcome.FromFailure(error);
        }

        var read = await cluster.GetAsync(Target(context.Namespace, name), cancellationToken);
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

        var read = await cluster.GetAsync(
            Target(context.Namespace, Probes.ObjectNameOf(context.Id)),
            cancellationToken
        );

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

/// <summary>The reconciler for <c>probes</c>.</summary>
/// <param name="clock">Stamps the observation.</param>
public sealed class ProbeReconciler(IClock clock) : ProbeReconcilerBase(clock, Probes.Type);

/// <summary>The reconciler for the child type, <c>probes/samples</c>.</summary>
/// <remarks>
///     ⚠ It reconciles a child exactly as its parent reconciles a top-level resource, and nothing in
///     it looks the parent up: docs/plan/12 § Child resources makes the parent a pure function of the
///     address, and the only place that matters here is
///     <c>Probes.ObjectNameOf</c> — which reads <c>ParentNames</c> off the id it was handed.
/// </remarks>
/// <param name="clock">Stamps the observation.</param>
public sealed class SampleReconciler(IClock clock) : ProbeReconcilerBase(clock, Probes.ChildType);

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
            return ReconcileOutcome.FromFailure(error);
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
