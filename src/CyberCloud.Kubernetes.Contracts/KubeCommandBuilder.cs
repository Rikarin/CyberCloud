using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     The one implementation of all three stages of the ADR-013 type-state chain.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b><see langword="internal" />, and that is load-bearing.</b> The chain's guarantee is
///         that <c>Build()</c> is unreachable until both <c>WithTenantId</c> and <c>WithResourceId</c>
///         have been called. One class implements all three interfaces, so if the class itself were
///         public a caller could write <c>((KubeCommandBuilder)KubeCommand.For(c)).Build()</c> and
///         walk straight past the chain. Being internal makes that cast unspellable outside this
///         assembly — the type-state is a property of the public surface rather than a convention.
///     </para>
///     <para>
///         The three interfaces are implemented <b>explicitly</b> for the same reason: even inside
///         this assembly, an instance typed as the concrete class exposes nothing.
///     </para>
/// </remarks>
sealed class KubeCommandBuilder(IKubeClusterConnection connection, IChartRenderer? charts)
    : IKubeCommandNeedsTenant, IKubeCommandNeedsResource, IKubeCommandBuilder {
    /// <summary>
    ///     The api-version stamped when a caller does not say. docs/plan/08 § Versioning makes
    ///     <c>api-version</c> a required query parameter on every request, so in production this is
    ///     always overridden by <see cref="IKubeCommandBuilder.WithApiVersion" /> from the request
    ///     that caused the reconcile; the constant exists so that the label is never absent.
    /// </summary>
    internal const string DefaultApiVersion = "2026-08-01";

    /// <summary>The field manager used when a provider does not name itself.</summary>
    internal const string DefaultFieldManager = "cybercloud/unspecified";

    readonly Dictionary<string, string> extraLabels = new(StringComparer.Ordinal);
    readonly Dictionary<string, string> extraAnnotations = new(StringComparer.Ordinal);
    readonly List<JsonObject> ownerReferences = [];

    Guid tenantId;
    Guid? subscriptionOverride;
    ResourceId resource;
    bool resourceSet;
    string? ns;
    GroupVersionKind? kind;
    string? fieldManager;
    string apiVersion = DefaultApiVersion;
    string? body;
    string? chartName;
    JsonElement chartValues;
    bool chartRequested;

    // ── The build ──────────────────────────────────────────────────────────────────────────────

    static readonly JsonSerializerOptions ObjectJsonOptions = new(JsonSerializerDefaults.Web) {
        // Kubernetes rejects an explicit null where it expects an absent field far more often than
        // it treats the two alike, and an apply patch's nulls are a *deletion* instruction under
        // server-side apply — writing `"replicas": null` would hand the field back to another
        // manager. Omitting them is the only safe default here.
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // ── Stage 1 ────────────────────────────────────────────────────────────────────────────────

    IKubeCommandNeedsResource IKubeCommandNeedsTenant.WithTenantId(Guid id) {
        tenantId = id;
        return this;
    }

    // ── Stage 2 ────────────────────────────────────────────────────────────────────────────────

    IKubeCommandBuilder IKubeCommandNeedsResource.WithResourceId(ResourceId id) {
        resource = id;
        resourceSet = true;
        return this;
    }

    // ── Stage 3 — the fully-qualified builder ──────────────────────────────────────────────────

    IKubeCommandBuilder IKubeCommandBuilder.WithSubscriptionId(Guid id) {
        subscriptionOverride = id;
        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.InNamespace(string value) {
        ns = value;
        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.WithKind(GroupVersionKind value) {
        kind = value;
        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.WithFieldManager(string manager) {
        fieldManager = manager;
        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.WithApiVersion(string value) {
        apiVersion = value;
        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.WithLabels(params (string Key, string Value)[] extra) {
        ArgumentNullException.ThrowIfNull(extra);

        foreach (var (key, value) in extra) {
            // ⚠ THE "non-overridable" HALF OF ADR-013, enforced rather than documented. Silently
            // dropping the override would leave the caller believing it took effect.
            if (KubeLabels.IsMandatory(key)) {
                throw new ArgumentException(
                    $"'{key}' is one of the seven mandatory labels and is injected by the builder, "
                    + "so it cannot be set or replaced by a caller. ADR-013 makes the set 'injected "
                    + "and non-overridable' because these labels are how billing attributes a pod, "
                    + "how the reconciler finds orphans and how deletion is complete — a reconciler "
                    + "that could overwrite one could detach its own objects from the platform. "
                    + "Use a different key.",
                    nameof(extra)
                );
            }

            Require(LabelSyntax.ValidateKey(key), nameof(extra));
            Require(LabelSyntax.ValidateValue(value, key), nameof(extra));

            extraLabels[key] = value;
        }

        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.WithAnnotations(params (string Key, string Value)[] extra) {
        ArgumentNullException.ThrowIfNull(extra);

        foreach (var (key, value) in extra) {
            if (KubeLabels.IsMandatoryAnnotation(key)) {
                throw new ArgumentException(
                    $"'{key}' is a mandatory annotation injected by the builder and cannot be "
                    + "replaced. docs/plan/09 § The command builder.",
                    nameof(extra)
                );
            }

            // An annotation KEY obeys the label-key rule; an annotation VALUE does not obey the
            // label-value rule at all — that is the whole reason the resource path is an annotation
            // and the resource id is a label. So the key is checked and the value is not.
            Require(LabelSyntax.ValidateKey(key), nameof(extra));

            extraAnnotations[key] = value ?? string.Empty;
        }

        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.WithOwner(
        ResourceId parent,
        GroupVersionKind ownerKind,
        string ownerName,
        string ownerUid
    ) {
        ArgumentNullException.ThrowIfNull(ownerKind);
        ArgumentException.ThrowIfNullOrEmpty(ownerName);
        ArgumentException.ThrowIfNullOrEmpty(ownerUid);

        ownerReferences.Add(
            new() {
                ["apiVersion"] = ownerKind.ApiVersion,
                ["kind"] = ownerKind.Kind,
                ["name"] = ownerName,
                ["uid"] = ownerUid,
                // Kubernetes garbage-collects the dependent when the owner goes. This is the "→
                // ownerReferences + cascade" of docs/plan/09 § The command builder.
                ["blockOwnerDeletion"] = true,
                ["controller"] = true
            }
        );

        // The parent's identity is recorded as an annotation so that a support engineer reading the
        // object in the cluster can get from the dependent back to the owning Cyber Cloud resource
        // without a uid lookup. The uid is Kubernetes' identity; this is ours.
        extraAnnotations[KubeLabels.Prefix + "/owner-resource-id"] =
            parent.Id.ToString("D", CultureInfo.InvariantCulture);

        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.Chart(string chart, JsonElement values) {
        chartRequested = true;
        chartName = chart;
        chartValues = values;
        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.Object<T>(T obj) {
        ArgumentNullException.ThrowIfNull(obj);
        body = JsonSerializer.Serialize(obj, ObjectJsonOptions);
        return this;
    }

    IKubeCommandBuilder IKubeCommandBuilder.ObjectJson(string json) {
        ArgumentException.ThrowIfNullOrEmpty(json);
        body = json;
        return this;
    }

    KubeCommand IKubeCommandBuilder.Build() {
        var built = BuildCore();
        if (built.TryGetError(out var error)) {
            throw new InvalidOperationException(error.Message);
        }

        return built.GetValueOrThrow();
    }

    Result<KubeCommand> IKubeCommandBuilder.TryBuild() => BuildCore();

    async Task<Result<ApplyOutcome>> IKubeCommandBuilder.ApplyAsync(CancellationToken cancellationToken) {
        var built = BuildCore();
        return built.TryGetError(out var error)
            ? Result<ApplyOutcome>.Failure(error)
            : await connection.ApplyAsync(built.GetValueOrThrow(), cancellationToken)
                .ConfigureAwait(false);
    }

    async Task<Result> IKubeCommandBuilder.DeleteAsync(
        CascadePolicy policy,
        CancellationToken cancellationToken
    ) {
        var built = BuildCore();
        return built.TryGetError(out var error)
            ? Result.Failure(error)
            : await connection.DeleteAsync(built.GetValueOrThrow(), policy, cancellationToken)
                .ConfigureAwait(false);
    }

    Result<KubeCommand> BuildCore() {
        if (!resourceSet) {
            // Unreachable through the public chain — WithResourceId is what returns this interface.
            // Kept because the class implements all three stages and this is the invariant that
            // makes that safe.
            return Invalid(
                "no resource id was set. This should be unreachable: WithResourceId is "
                + "what produces an IKubeCommandBuilder."
            );
        }

        if (chartRequested) {
            if (charts is null) {
                return Invalid(
                    $"the command renders the chart '{chartName}', and no IChartRenderer is "
                    + "registered. Helm rendering lives in CyberCloud.Kubernetes.Charts "
                    + "(docs/plan/03 § src), which is a separate assembly and is not built yet. "
                    + "docs/plan/09 § The command builder describes the intended behaviour: render "
                    + "in-process and apply the resulting objects with server-side apply — no "
                    + "HelmRelease and no Flux, because desired state must not live in the target "
                    + "cluster's etcd (ADR-001). Pass a renderer to KubeCommand.For(connection, "
                    + "charts) once one exists, or use Object/ObjectJson."
                );
            }

            var rendered = charts.Render(
                chartName!,
                chartValues,
                ns ?? string.Empty,
                resource.Name
            );

            if (rendered.TryGetError(out var renderError)) {
                return Result<KubeCommand>.Failure(renderError);
            }

            var documents = rendered.GetValueOrThrow();
            if (documents.Count != 1) {
                return Invalid(
                    $"the chart '{chartName}' rendered {documents.Count.ToString(CultureInfo.InvariantCulture)} "
                    + "objects and one KubeCommand addresses exactly one object. A multi-object "
                    + "chart is applied as one command per object; that fan-out belongs to "
                    + "CyberCloud.Kubernetes.Charts, which owns the release-level concerns "
                    + "(ordering, hooks, pruning) this type deliberately does not."
                );
            }

            body = documents[0];
        }

        if (string.IsNullOrEmpty(body)) {
            return Invalid("no object was set. Call Object(...), ObjectJson(...) or Chart(...) before applying.");
        }

        if (kind is null) {
            return Invalid(
                "no kind was set. Call WithKind(new GroupVersionKind { Group, Version, Kind, Plural }). "
                + "The plural is required and cannot be derived from the kind — see the remarks on "
                + "GroupVersionKind. docs/plan/09 § Cluster connections omits it from its "
                + "GroupVersionKind sketch, and every Kubernetes REST path needs it."
            );
        }

        if (!kind.IsComplete) {
            return Invalid(
                $"the kind '{kind}' is incomplete: Version, Kind and Plural are all required "
                + "(Group is empty for the core group, which is legal)."
            );
        }

        JsonObject document;
        try {
            document = JsonNode.Parse(body!) as JsonObject
                ?? throw new JsonException("the body is not a JSON object");
        } catch (JsonException ex) {
            return Invalid($"the object body is not valid JSON: {ex.Message}");
        }

        // The reconcile hash is over the body as the caller supplied it, BEFORE injection. Hashing
        // after injection would fold the hash annotation's own absence/presence into the hash and
        // make every second apply look different. docs/plan/09 § The command builder wants "of the
        // desired body".
        var reconcileHash = KubeLabels.ReconcileHash(Canonical(document));

        var subscriptionId = subscriptionOverride ?? resource.SubscriptionId;
        var labels = MandatoryLabels(subscriptionId);

        foreach (var (key, value) in labels) {
            var problem = LabelSyntax.ValidateValue(value, key);
            if (problem.TryGetError(out var labelError)) {
                return Result<KubeCommand>.Failure(WithLabelContext(labelError, key, value));
            }
        }

        foreach (var (key, value) in extraLabels) {
            labels[key] = value;
        }

        var annotations = new Dictionary<string, string>(StringComparer.Ordinal) {
            [KubeLabels.ResourcePathAnnotation] = resource.Path, [KubeLabels.ReconcileHashAnnotation] = reconcileHash
        };

        foreach (var (key, value) in extraAnnotations) {
            annotations[key] = value;
        }

        var name = ResolveName(document);
        if (name is null) {
            return Invalid(
                "the object has no metadata.name. Server-side apply addresses an object by name, so "
                + "there is nothing to PATCH."
            );
        }

        Inject(document, kind, ns, name, labels, annotations, ownerReferences);

        return Result<KubeCommand>.Success(
            new() {
                TenantId = tenantId,
                SubscriptionId = subscriptionId,
                ResourceId = resource.Id,
                Target = new() { Kind = kind, Namespace = ns ?? string.Empty, Name = name },
                Body = document.ToJsonString(),
                FieldManager = fieldManager ?? FieldManagerFor(resource.Type.Namespace),
                Labels = labels,
                Annotations = annotations,
                ReconcileHash = reconcileHash,
                Force = false,
                ResourcePath = resource.Path
            }
        );
    }

    /// <summary>The seven, in ADR-013's order.</summary>
    Dictionary<string, string> MandatoryLabels(Guid subscriptionId) =>
        new(StringComparer.Ordinal) {
            [KubeLabels.TenantId] = KubeLabels.GuidValue(tenantId),
            [KubeLabels.SubscriptionId] = KubeLabels.GuidValue(subscriptionId),
            [KubeLabels.ResourceGroup] = resource.ResourceGroup,
            [KubeLabels.ResourceId] = KubeLabels.GuidValue(resource.Id),
            [KubeLabels.ResourceType] = KubeLabels.ResourceTypeValue(resource.Type),
            [KubeLabels.ApiVersion] = apiVersion,
            [KubeLabels.ManagedBy] = KubeLabels.ManagedByValue
        };

    string? ResolveName(JsonObject document) {
        if (document["metadata"] is JsonObject metadata
            && metadata["name"]?.GetValue<string>() is { Length: > 0 } fromBody) {
            return fromBody;
        }

        // Falling back to the resource's own name is safe: ResourceNaming is the DNS-1123 label rule,
        // which is exactly what a Kubernetes object name must satisfy.
        return resource.Name.Length > 0 ? resource.Name : null;
    }

    static void Inject(
        JsonObject document,
        GroupVersionKind kind,
        string? ns,
        string name,
        Dictionary<string, string> labels,
        Dictionary<string, string> annotations,
        List<JsonObject> owners
    ) {
        // apiVersion and kind are set from the GVK rather than trusted from the body: an apply patch
        // whose apiVersion disagrees with the URL is rejected, and the URL is built from the GVK.
        document["apiVersion"] = kind.ApiVersion;
        document["kind"] = kind.Kind;

        if (document["metadata"] is not JsonObject metadata) {
            metadata = [];
            document["metadata"] = metadata;
        }

        metadata["name"] = name;

        if (!string.IsNullOrEmpty(ns)) {
            metadata["namespace"] = ns;
        }

        metadata["labels"] = Merge(metadata["labels"] as JsonObject, labels);
        metadata["annotations"] = Merge(metadata["annotations"] as JsonObject, annotations);

        if (owners.Count > 0) {
            var array = new JsonArray();
            foreach (var owner in owners) {
                array.Add(owner.DeepClone());
            }

            metadata["ownerReferences"] = array;
        }
    }

    /// <summary>
    ///     Merges injected entries over whatever the body already carried. Ours win — that is what
    ///     "non-overridable" means when the override attempt is inside the rendered object rather
    ///     than in a <c>WithLabels</c> call.
    /// </summary>
    static JsonObject Merge(JsonObject? existing, Dictionary<string, string> injected) {
        var result = new JsonObject();

        if (existing is not null) {
            foreach (var (key, value) in existing) {
                result[key] = value?.DeepClone();
            }
        }

        foreach (var (key, value) in injected) {
            result[key] = value;
        }

        return result;
    }

    /// <summary>
    ///     A stable rendering of the desired body for hashing: property order is normalised so that
    ///     two serializations of the same desired state hash the same.
    /// </summary>
    /// <remarks>
    ///     ⚠ Without this the hash is a hash of <c>JsonSerializer</c>'s property ordering, which is
    ///     reflection order and is not contractually stable. A no-op detector that reports a change
    ///     because a field moved is worse than no detector: it turns every reconcile into an apply.
    /// </remarks>
    static string Canonical(JsonNode? node) {
        var writerOptions = new JsonWriterOptions { Indented = false };
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, writerOptions)) {
            WriteCanonical(node, writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    static void WriteCanonical(JsonNode? node, Utf8JsonWriter writer) {
        switch (node) {
            case null:
                writer.WriteNullValue();
                break;

            case JsonObject obj:
                writer.WriteStartObject();
                foreach (var (key, value) in obj.OrderBy(x => x.Key, StringComparer.Ordinal)) {
                    writer.WritePropertyName(key);
                    WriteCanonical(value, writer);
                }

                writer.WriteEndObject();
                break;

            case JsonArray array:
                writer.WriteStartArray();
                foreach (var item in array) {
                    WriteCanonical(item, writer);
                }

                writer.WriteEndArray();
                break;

            default:
                node.WriteTo(writer);
                break;
        }
    }

    static Error WithLabelContext(Error error, string key, string value) =>
        key == KubeLabels.ResourceType
            ? new(
                error.Code,
                error.Message
                + " ⚠ This is the resource TYPE label, whose value is derived from the resource "
                + "type and is the one member of ADR-013's seven that is not length-bounded by "
                + "construction: a ResourceTypeName may be a namespace of two or more 63-character "
                + "segments plus a type path of up to three, so '"
                + value
                + "' can exceed the 63-character label cap. It is rejected rather than "
                + "truncated, because truncation maps two distinct resource types onto one label "
                + "value and silently breaks orphan detection and billing attribution — the two "
                + "things ADR-013 says the labels exist for. Shorten the provider namespace or the "
                + "type path.",
                error.Target
            )
            : error;

    static void Require(Result outcome, string parameterName) {
        if (outcome.TryGetError(out var error)) {
            throw new ArgumentException(error.Message, parameterName);
        }
    }

    static Result<KubeCommand> Invalid(string message) =>
        Result<KubeCommand>.Failure(
            ErrorCode.InvalidRequestBody,
            "The Kubernetes command cannot be built: " + message
        );

    /// <summary>
    ///     <c>cybercloud/{provider}</c> — ADR-013's stable per-provider field manager.
    /// </summary>
    /// <param name="providerNamespace">
    ///     The provider namespace, for example <c>CyberCloud.DBforPostgreSQL</c>.
    /// </param>
    internal static string FieldManagerFor(string providerNamespace) =>
        "cybercloud/" + providerNamespace.ToLowerInvariant();
}
