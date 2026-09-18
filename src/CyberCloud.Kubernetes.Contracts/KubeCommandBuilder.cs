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
    readonly List<string> templatePaths = [];

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

    // ⚠ The co-owned mode's one input beyond the ordinary chain. Non-null switches BuildCore to
    // BuildCoOwned, and everything the ordinary build injects from the applying resource's identity
    // is instead read off this object's labels — see IKubeCommandBuilder.CoWriting.
    KubeObject? coWriting;

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

    IKubeCommandBuilder IKubeCommandBuilder.WithTemplateLabels(params string[] paths) {
        ArgumentNullException.ThrowIfNull(paths);

        foreach (var path in paths) {
            if (string.IsNullOrEmpty(path) || path.Split('/').Any(string.IsNullOrEmpty)) {
                throw new ArgumentException(
                    $"'{path}' is not a field path. A template path is one or more non-empty "
                    + "field names separated by '/', from the root of the body — for example "
                    + "'spec/volumeClaimTemplates'.",
                    nameof(paths)
                );
            }

            templatePaths.Add(path);
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

            if (KubeLabels.IsFragmentAnnotation(key)) {
                // ⚠ In either mode. A hand-written fragment annotation in the ordinary mode would
                // make the owner's apply claim a co-writer's bookkeeping; in the co-owned mode it
                // would let one co-writer forge what another applied, which the merge then trusts.
                throw new ArgumentException(
                    $"'{key}' is a per-fragment annotation the co-owned mode writes for itself "
                    + "(IKubeCommandBuilder.CoWriting) and cannot be set by a caller in either mode.",
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

    IKubeCommandBuilder IKubeCommandBuilder.CoWriting(KubeObject live) {
        ArgumentNullException.ThrowIfNull(live);
        coWriting = live;
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
        if (coWriting is not null) {
            // ⚠ A WITHDRAWAL, NOT A DELETE, AND IT GOES THROUGH ApplyAsync. The object is the
            // owner's. What this co-writer can take back is its own fragment, and the way to take
            // a fragment back under server-side apply is to apply without it: the shared manager's
            // new field set is the other co-writers' union, and the API server removes what the
            // manager owned and no longer applies. A reconciler that reaches for DeleteAsync out of
            // habit on teardown therefore withdraws rather than deleting both networks' VPCs.
            var withdrawal = BuildCoOwned(withdraw: true);
            if (withdrawal.TryGetError(out var withdrawError)) {
                return Result.Failure(withdrawError);
            }

            var applied = await connection.ApplyAsync(withdrawal.GetValueOrThrow(), cancellationToken)
                .ConfigureAwait(false);

            if (applied.TryGetError(out var applyError)) {
                return Result.Failure(applyError);
            }

            // ⚠ THE INTERFACE'S SHAPE IS A BARE Result, SO THE THREE "NOT APPLIED" OUTCOMES BECOME
            // CODED FAILURES RATHER THAN A SUCCESS THAT WITHDREW NOTHING. Stale is PreconditionFailed —
            // the object moved, read again (KubeCoWriter does); Conflict is Conflict — the union the
            // other co-writers hold now collides with a field somebody else owns, and nothing was
            // written; Suspended is OperationInProgress — the cluster is out of reach and the
            // withdrawal has not happened yet.
            var outcome = applied.GetValueOrThrow();

            return outcome.Result switch {
                ApplyResult.Stale => Result.Failure(
                    ErrorCode.PreconditionFailed,
                    $"'{outcome.Target}' moved between the read and the withdrawal; nothing was written. "
                    + "Read it again and withdraw again."
                ),
                ApplyResult.Conflict => Result.Failure(
                    ErrorCode.Conflict,
                    outcome.Drift?.Describe() ?? outcome.Message
                ),
                ApplyResult.Suspended => Result.Failure(
                    ErrorCode.OperationInProgress,
                    outcome.Message.Length > 0 ? outcome.Message : "the cluster is unreachable; the withdrawal has not happened yet"
                ),
                _ => Result.Success
            };
        }

        var built = BuildCore();
        return built.TryGetError(out var error)
            ? Result.Failure(error)
            : await connection.DeleteAsync(built.GetValueOrThrow(), policy, cancellationToken)
                .ConfigureAwait(false);
    }

    Result<KubeCommand> BuildCore() {
        if (coWriting is not null) {
            return BuildCoOwned(withdraw: false);
        }

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
        StampTemplates(document, templatePaths, labels);

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
                ResourcePath = resource.Path,
                ResourceGroup = resource.ResourceGroup
            }
        );
    }

    // ── The co-owned build — IKubeCommandBuilder.CoWriting ─────────────────────────────────────────

    /// <summary>
    ///     Builds a co-writer's command: the union of every co-writer's fragment, under the owner's
    ///     shared co-writer manager, carrying the live object's <c>resourceVersion</c> and no labels.
    /// </summary>
    /// <param name="withdraw">
    ///     <c>true</c> to leave this co-writer's fragment and annotations out — the teardown — and
    ///     apply only what the other co-writers hold.
    /// </param>
    Result<KubeCommand> BuildCoOwned(bool withdraw) {
        var live = coWriting!;

        // ── What the ordinary mode allows and this one refuses ────────────────────────────────────
        //
        // ⚠ Refused by name rather than ignored. Each of these would let a co-writer say something
        // about the OBJECT — whose it is, what owns it, what its templates are labelled — and every
        // one of those is the owner's to say. Ignoring the call would leave the caller believing it
        // took effect, which is the same objection WithLabels' mandatory-key check makes.
        if (extraLabels.Count > 0) {
            return Invalid(
                "WithLabels was called in the co-owned mode. Labels on an object are its owner's — the "
                + "seven mandatory ones name the owner, and an extra one would ride under the co-writers' "
                + "shared manager, where the next co-writer's apply prunes it. A co-writer adds only its "
                + "fragment. IKubeCommandBuilder.CoWriting."
            );
        }

        if (templatePaths.Count > 0) {
            return Invalid(
                "WithTemplateLabels was called in the co-owned mode. A nested template's labels are the "
                + "owner's, for the reason its own are. IKubeCommandBuilder.CoWriting."
            );
        }

        if (ownerReferences.Count > 0) {
            return Invalid(
                "WithOwner was called in the co-owned mode. An owner reference decides when the garbage "
                + "collector removes the object, and that is the object's owner's decision, not a "
                + "co-writer's. IKubeCommandBuilder.CoWriting."
            );
        }

        if (extraAnnotations.Count > 0) {
            // ⚠ Not because an annotation says whose the object is — it does not — but because it
            // would ride under the shared manager WITHOUT being in this co-writer's stored fragment,
            // so the next co-writer's apply, which is the union of the stored fragments, would prune
            // it. That is the clobber the mode exists to prevent, arriving by the side door.
            return Invalid(
                "WithAnnotations was called in the co-owned mode. An annotation applied beside a fragment "
                + "rides under the co-writers' shared manager without being part of the fragment the "
                + "other co-writers merge, so the next co-writer's apply would remove it. The three "
                + "per-fragment annotations are written by the builder, and the rest of an object's "
                + "metadata is its owner's. IKubeCommandBuilder.CoWriting."
            );
        }

        if (fieldManager is not null) {
            return Invalid(
                "WithFieldManager was called in the co-owned mode. Every co-writer of one object applies "
                + "under one manager named for the object's owner — KubeLabels.CoWriterFieldManager — "
                + "because the lists a co-writer reaches are atomic and a manager of its own would "
                + "conflict on the whole list forever. The manager is derived, not chosen."
            );
        }

        if (subscriptionOverride is not null) {
            return Invalid(
                "WithSubscriptionId was called in the co-owned mode. The subscription label is the "
                + "owner's and a co-writer writes no labels, so there is nothing for the override to "
                + "change. IKubeCommandBuilder.CoWriting."
            );
        }

        if (chartRequested) {
            return Invalid(
                "Chart was called in the co-owned mode. A chart renders whole objects with their own "
                + "identity, and a fragment is a slice of somebody else's. Render the slice and pass it "
                + "with ObjectJson. IKubeCommandBuilder.CoWriting."
            );
        }

        if (kind is null || !kind.IsComplete) {
            return Invalid(
                "no complete kind was set. The co-owned mode addresses the owner's object by the same "
                + "GroupVersionKind the read used; call WithKind with Version, Kind and Plural."
            );
        }

        if (live.Ref.Kind.IsComplete && live.Ref.Kind != kind) {
            return Invalid(
                $"WithKind names '{kind}' and the live object passed to CoWriting is a '{live.Ref.Kind}'. "
                + "A fragment is applied onto the object that was read, so the two must agree."
            );
        }

        // ── Who owns the object, read off the object ──────────────────────────────────────────────
        JsonObject liveDocument;
        try {
            liveDocument = JsonNode.Parse(live.Json) as JsonObject
                ?? throw new JsonException("the live object is not a JSON object");
        } catch (JsonException ex) {
            return Invalid($"the live object passed to CoWriting is not valid JSON: {ex.Message}");
        }

        var liveMetadata = liveDocument["metadata"] as JsonObject;
        var liveLabels = liveMetadata?["labels"] as JsonObject;

        foreach (var key in KubeLabels.Mandatory) {
            if (liveLabels?[key]?.GetValue<string>() is not { Length: > 0 }) {
                return Invalid(
                    $"the live object '{live.Ref}' carries no '{key}' label, so this platform does not own "
                    + "it. A co-writer writes only onto an object another Cyber Cloud resource rendered — "
                    + "the seven labels are how that object names its owner, and without them there is "
                    + "no owner to co-write with."
                );
            }
        }

        if (!string.Equals(liveLabels![KubeLabels.ManagedBy]!.GetValue<string>(), KubeLabels.ManagedByValue, StringComparison.Ordinal)) {
            return Invalid(
                $"the live object '{live.Ref}' is managed by "
                + $"'{liveLabels[KubeLabels.ManagedBy]!.GetValue<string>()}', not by this platform."
            );
        }

        var ownerTenant = liveLabels[KubeLabels.TenantId]!.GetValue<string>();
        if (!string.Equals(ownerTenant, KubeLabels.GuidValue(tenantId), StringComparison.Ordinal)) {
            // ⚠ THE TENANT BOUNDARY, CHECKED ON THE OBJECT RATHER THAN ON THE CALLER'S CLAIM. A
            // peering is VPC-to-VPC within a tenant (docs/plan/14) and there is no cross-tenant
            // co-write on this platform; a co-writer that reached another tenant's object would be
            // writing routes into somebody else's network.
            return Invalid(
                $"the live object '{live.Ref}' belongs to tenant {ownerTenant} and the co-writer is in "
                + $"tenant {KubeLabels.GuidValue(tenantId)}. A co-writer never reaches across a tenant."
            );
        }

        // ⚠ THE GROUP BOUNDARY, ON THE OBJECT'S LABELS FOR THE SAME REASON. The write path authorized
        // the caller on the co-writer's own address and nothing else; what lets that caller change
        // the owner's object is that write on the one implies write on the other, and docs/plan/07
        // grants roles on subscriptions and groups, so one group is the smallest scope where it does.
        // The tenant check alone was not enough: a Vpc's name is {sub}-{group}-{network} and both
        // halves admit hyphens, so `prod`'s network `a-b` and `prod-a`'s network `b` are one object
        // name, and a peering in `prod` naming `a-b` read `prod-a`'s router here and wrote onto it.
        var ownerSubscription = liveLabels[KubeLabels.SubscriptionId]!.GetValue<string>();
        if (!string.Equals(ownerSubscription, KubeLabels.GuidValue(resource.SubscriptionId), StringComparison.Ordinal)) {
            return Invalid(
                $"the live object '{live.Ref}' belongs to subscription {ownerSubscription} and the co-writer "
                + $"is in subscription {KubeLabels.GuidValue(resource.SubscriptionId)}. A co-writer never "
                + "reaches across a subscription: write on it was checked on its own address, and that "
                + "implies write on the owner's object inside one resource group only."
            );
        }

        var ownerGroup = liveLabels[KubeLabels.ResourceGroup]!.GetValue<string>();
        if (!string.Equals(ownerGroup, resource.ResourceGroup, StringComparison.Ordinal)) {
            return Invalid(
                $"the live object '{live.Ref}' belongs to resource group '{ownerGroup}' and the co-writer "
                + $"is in resource group '{resource.ResourceGroup}'. A co-writer never reaches across a "
                + "resource group: write on it was checked on its own address, and one group is the smallest "
                + "scope on which that implies write on the owner's object. Two groups that differ by a "
                + "hyphen can render one object name, which is how a co-writer arrives here."
            );
        }

        var ownerIdValue = liveLabels[KubeLabels.ResourceId]!.GetValue<string>();
        if (!Guid.TryParseExact(ownerIdValue, "D", out var ownerId) || ownerId == Guid.Empty) {
            return Invalid(
                $"the live object '{live.Ref}' carries '{ownerIdValue}' as its resource id, which is not "
                + "a GUID, so nothing can name the manager its co-writers share."
            );
        }

        if (ownerId == resource.Id) {
            return Invalid(
                $"resource {resource.Id:D} is co-writing an object it owns itself. The co-owned mode is "
                + "for a second writer; the owner applies with Object or ObjectJson and no CoWriting call."
            );
        }

        var ownerTypeValue = liveLabels[KubeLabels.ResourceType]!.GetValue<string>();

        // ── The optimistic lock ───────────────────────────────────────────────────────────────────
        var resourceVersion = live.ResourceVersion.Length > 0
            ? live.ResourceVersion
            : liveMetadata?["resourceVersion"]?.GetValue<string>() ?? string.Empty;

        if (resourceVersion.Length == 0) {
            return Invalid(
                $"the live object '{live.Ref}' carries no resourceVersion. A co-owned apply carries the "
                + "version it was computed from so that two co-writers racing onto one object lose "
                + "loudly (ApplyResult.Stale) rather than one applying a union computed from a version "
                + "the other has already replaced. Read the object with IKubeClusterConnection.GetAsync."
            );
        }

        // ── The other co-writers' fragments, off the object ───────────────────────────────────────
        var liveAnnotations = liveMetadata?["annotations"] as JsonObject;
        var fragments = new List<(Guid Writer, JsonObject Fragment)>();
        var annotations = new Dictionary<string, string>(StringComparer.Ordinal);

        if (liveAnnotations is not null) {
            foreach (var (key, value) in liveAnnotations) {
                if (!KubeLabels.TryReadFragmentWriter(key, out var writer) || writer == resource.Id) {
                    continue;
                }

                var stored = value?.GetValue<string>();
                JsonObject? fragment = null;
                try {
                    fragment = stored is null ? null : JsonNode.Parse(stored) as JsonObject;
                } catch (JsonException) {
                    // Fall through to the refusal below.
                }

                if (fragment is null) {
                    // ⚠ Refused rather than skipped. Skipping would apply a union WITHOUT that
                    // co-writer's fragment, which is exactly the prune this mode exists to prevent —
                    // and a corrupt bookkeeping annotation is somebody's hand edit, which is worth
                    // a person's attention rather than a silent repair.
                    return Invalid(
                        $"the live object '{live.Ref}' carries '{key}', which should hold a co-writer's "
                        + "fragment as JSON and does not. Applying without it would prune that co-writer's "
                        + $"slice of the object. Restore or remove the annotation; the co-writer is resource "
                        + $"{writer:D}."
                    );
                }

                fragments.Add((writer, fragment));

                // The other co-writers' three annotations ride along verbatim, because the shared
                // manager owns them and an apply that left one out would remove it.
                annotations[key] = stored!;

                foreach (var companion in new[] { KubeLabels.FragmentHashAnnotation(writer), KubeLabels.FragmentPathAnnotation(writer) }) {
                    if (liveAnnotations[companion]?.GetValue<string>() is { } companionValue) {
                        annotations[companion] = companionValue;
                    }
                }
            }
        }

        // ── This co-writer's fragment ─────────────────────────────────────────────────────────────
        var fragmentHash = string.Empty;
        var suppliedName = string.Empty;

        if (withdraw) {
            if (!string.IsNullOrEmpty(body)) {
                return Invalid(
                    "a co-owned DeleteAsync withdraws this co-writer's fragment, and a body was set. "
                    + "The withdrawal applies what the OTHER co-writers hold, read off the object; "
                    + "there is nothing of this co-writer's to apply. Drop the Object/ObjectJson call."
                );
            }
        } else {
            if (string.IsNullOrEmpty(body)) {
                return Invalid("no fragment was set. Call Object(...) or ObjectJson(...) with the slice this resource contributes.");
            }

            JsonObject supplied;
            try {
                supplied = JsonNode.Parse(body) as JsonObject
                    ?? throw new JsonException("the body is not a JSON object");
            } catch (JsonException ex) {
                return Invalid($"the fragment is not valid JSON: {ex.Message}");
            }

            suppliedName = (supplied["metadata"] as JsonObject)?["name"]?.GetValue<string>() ?? string.Empty;

            var shaped = ShapeFragment(supplied, live);
            if (shaped.TryGetError(out var shapeError)) {
                return Result<KubeCommand>.Failure(shapeError);
            }

            var ownFragment = shaped.GetValueOrThrow();

            // The hash is over THIS fragment alone, canonical — never over the union. A co-writer's
            // no-op question is "did my slice change", and folding the others in would answer it with
            // their changes.
            var canonical = Canonical(ownFragment);
            fragmentHash = KubeLabels.ReconcileHash(canonical);

            fragments.Add((resource.Id, ownFragment));
            annotations[KubeLabels.FragmentAnnotation(resource.Id)] = canonical;
            annotations[KubeLabels.FragmentHashAnnotation(resource.Id)] = fragmentHash;
            annotations[KubeLabels.FragmentPathAnnotation(resource.Id)] = resource.Path;
        }

        var merged = FragmentMerge.Merge(fragments);
        if (merged.TryGetError(out var mergeError)) {
            return Result<KubeCommand>.Failure(mergeError);
        }

        // ── The name — the OWNER's object's, never this resource's ────────────────────────────────
        //
        // ⚠ ResolveName's fallback to resource.Name is the trap here: a peering's name is not a
        // Vpc's, and applying a fragment under the co-writer's own name would create a new object.
        var name = live.Ref.Name;
        if (suppliedName.Length > 0) {
            if (name.Length > 0 && !string.Equals(name, suppliedName, StringComparison.Ordinal)) {
                return Invalid(
                    $"the fragment names '{suppliedName}' and the live object is '{name}'. A fragment is applied "
                    + "onto the object that was read; drop metadata.name from the fragment or read the right object."
                );
            }

            name = suppliedName;
        }

        if (name.Length == 0) {
            return Invalid(
                "the live object passed to CoWriting has no name in its Ref and the fragment carries none. "
                + "There is nothing to PATCH."
            );
        }

        var targetNamespace = ns ?? live.Ref.Namespace;
        var document = merged.GetValueOrThrow();

        document["apiVersion"] = kind.ApiVersion;
        document["kind"] = kind.Kind;

        var metadata = new JsonObject { ["name"] = name };
        if (!string.IsNullOrEmpty(targetNamespace)) {
            metadata["namespace"] = targetNamespace;
        }

        metadata["resourceVersion"] = resourceVersion;

        if (annotations.Count > 0) {
            metadata["annotations"] = Merge(null, annotations);
        }

        document["metadata"] = metadata;

        return Result<KubeCommand>.Success(
            new() {
                TenantId = tenantId,
                SubscriptionId = resource.SubscriptionId,
                ResourceId = resource.Id,
                Target = new() { Kind = kind, Namespace = targetNamespace, Name = name },
                Body = document.ToJsonString(),
                FieldManager = KubeLabels.CoWriterFieldManager(ownerTypeValue, ownerIdValue),
                Labels = new Dictionary<string, string>(StringComparer.Ordinal),
                Annotations = annotations,
                ReconcileHash = fragmentHash,
                Force = false,
                ResourcePath = resource.Path,
                ResourceGroup = resource.ResourceGroup,
                OwnerResourceId = ownerId
            }
        );
    }

    /// <summary>
    ///     Reduces a caller's body to the fragment it contributes: everything but the object's own
    ///     identity, which the fragment may not carry.
    /// </summary>
    /// <param name="supplied">The body as the caller passed it.</param>
    /// <param name="live">The owner's object, for the messages.</param>
    /// <remarks>
    ///     <c>apiVersion</c> and <c>kind</c> are dropped, because the command sets them from the
    ///     GroupVersionKind as the ordinary mode does. <c>metadata.name</c> and
    ///     <c>metadata.namespace</c> are dropped, because they address the object and the address is
    ///     checked against the live one. Anything else under <c>metadata</c> — labels, annotations,
    ///     owner references, finalizers — and anything under <c>status</c> is refused: those say
    ///     whose the object is or what its controller saw, and neither is a co-writer's to say.
    /// </remarks>
    static Result<JsonObject> ShapeFragment(JsonObject supplied, KubeObject live) {
        var fragment = new JsonObject();

        foreach (var (key, value) in supplied) {
            switch (key) {
                case "apiVersion":
                case "kind":
                    continue;

                case "status":
                    return Result<JsonObject>.Failure(
                        ErrorCode.InvalidRequestBody,
                        "The Kubernetes command cannot be built: the fragment carries 'status'. Status is "
                        + "the controller's report on the owner's object, and a co-writer applies desired "
                        + "state only."
                    );

                case "metadata": {
                    if (value is not JsonObject metadata) {
                        return Result<JsonObject>.Failure(
                            ErrorCode.InvalidRequestBody,
                            "The Kubernetes command cannot be built: the fragment's 'metadata' is not an object."
                        );
                    }

                    foreach (var member in metadata) {
                        if (member.Key is "name" or "namespace") {
                            continue;
                        }

                        return Result<JsonObject>.Failure(
                            ErrorCode.InvalidRequestBody,
                            $"The Kubernetes command cannot be built: the fragment carries 'metadata.{member.Key}' "
                            + $"for '{live.Ref}'. Labels, annotations, owner references and the rest of an "
                            + "object's metadata say whose it is, and that is its owner's to say. A co-writer "
                            + "adds only its own fragment — the annotations that record it are written by "
                            + "the builder. IKubeCommandBuilder.CoWriting."
                        );
                    }

                    continue;
                }

                default:
                    fragment[key] = value?.DeepClone();
                    break;
            }
        }

        if (fragment.Count == 0) {
            return Result<JsonObject>.Failure(
                ErrorCode.InvalidRequestBody,
                "The Kubernetes command cannot be built: the fragment is empty once the object's identity "
                + "is set aside. An empty fragment is a withdrawal, and a withdrawal is DeleteAsync in the "
                + "co-owned mode."
            );
        }

        return Result<JsonObject>.Success(fragment);
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
    ///     Writes <see cref="KubeLabels.LifetimeStable" /> into the <c>metadata.labels</c> of every
    ///     nested template the caller declared with <c>WithTemplateLabels</c>.
    /// </summary>
    /// <param name="document">The object, already injected at the top level.</param>
    /// <param name="paths">The declared field paths.</param>
    /// <param name="labels">
    ///     The command's labels. Only the <see cref="KubeLabels.LifetimeStable" /> members are taken
    ///     — never <see cref="KubeLabels.ApiVersion" />, and never a caller's extra label, because
    ///     neither is guaranteed to hold the same value on the next reconcile and the fields this
    ///     writes into are, on the one kind that matters, refused any change at all.
    /// </param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>Nothing here creates a key it has no value for.</b> A path that does not resolve,
    ///         resolves to a scalar, or resolves to an array of scalars is skipped in silence — that
    ///         is the <c>Deployment</c> arm of a render function whose <c>StatefulSet</c> arm has a
    ///         claim template, and it is <c>defaults.templates.dataVolumeClaimTemplate</c>, which is a
    ///         string naming a template rather than a template.
    ///     </para>
    ///     <para>
    ///         ⚠
    ///         <b>
    ///             A template that already carries a <c>metadata</c> that is not an object is left
    ///             alone rather than replaced.
    ///         </b> Overwriting it would turn a body the API server would
    ///         have rejected with a clear message into one it rejects with a confusing one.
    ///     </para>
    /// </remarks>
    static void StampTemplates(
        JsonObject document,
        List<string> paths,
        Dictionary<string, string> labels
    ) {
        if (paths.Count == 0) {
            return;
        }

        var stable = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var key in KubeLabels.LifetimeStable) {
            if (labels.TryGetValue(key, out var value)) {
                stable[key] = value;
            }
        }

        foreach (var path in paths) {
            switch (Resolve(document, path)) {
                case JsonArray array:
                    foreach (var item in array) {
                        StampTemplate(item as JsonObject, stable);
                    }

                    break;

                case JsonObject template:
                    StampTemplate(template, stable);
                    break;
            }
        }
    }

    /// <summary>Walks a <c>/</c>-separated field path, answering <see langword="null" /> if it ends.</summary>
    /// <param name="document">The object to walk from.</param>
    /// <param name="path">The path.</param>
    static JsonNode? Resolve(JsonObject document, string path) {
        JsonNode? current = document;

        foreach (var segment in path.Split('/')) {
            if (current is not JsonObject step) {
                return null;
            }

            current = step[segment];
        }

        return current;
    }

    /// <summary>Merges the labels into one template's <c>metadata.labels</c>.</summary>
    /// <param name="template">The template, or <see langword="null" /> for a non-object array entry.</param>
    /// <param name="labels">The lifetime-stable labels.</param>
    static void StampTemplate(JsonObject? template, Dictionary<string, string> labels) {
        if (template is null) {
            return;
        }

        if (template["metadata"] is not JsonObject metadata) {
            // ⚠ Present-but-not-an-object is somebody else's problem to report; absent is ours to
            // fill, and only because there is a value to put in it.
            if (template.ContainsKey("metadata")) {
                return;
            }

            metadata = [];
            template["metadata"] = metadata;
        }

        metadata["labels"] = Merge(metadata["labels"] as JsonObject, labels);
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
