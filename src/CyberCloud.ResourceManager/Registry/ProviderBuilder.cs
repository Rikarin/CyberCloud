using CyberCloud.ResourceManager.Contracts.Registry;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Registry;

/// <summary>
///     Collects one provider's <c>Describe</c> into <see cref="ResourceTypeRegistration" />s.
/// </summary>
/// <remarks>
///     ⚠ <b>The builder and the type builder are one object, which is what makes docs/plan/08's
///     example compile.</b> That example ends one type's declaration by starting the next one on the
///     same chain, so <see cref="IResourceTypeBuilder" /> extends <see cref="IProviderBuilder" /> and
///     this class implements both. The consequence to be aware of is that
///     <see cref="ResourceType" /> is the only thing that closes a type: everything else appends to
///     whichever type is currently open, and calling a type-scoped method before the first
///     <see cref="ResourceType" /> is a bug rather than a no-op — so it throws.
/// </remarks>
sealed class ProviderBuilder(string providerNamespace) : IResourceTypeBuilder {
    readonly List<TypeDraft> drafts = [];

    TypeDraft? current;

    /// <inheritdoc />
    public IResourceTypeBuilder ResourceType(string type) {
        ArgumentException.ThrowIfNullOrWhiteSpace(type);

        var name = ResourceTypeName.Create(providerNamespace, type);
        if (name.TryGetError(out var error)) {
            throw new ArgumentException(error.Message, nameof(type));
        }

        current = new(name.GetValueOrThrow());
        drafts.Add(current);
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder ApiVersion(string version, ResourceSchema schema) {
        ArgumentNullException.ThrowIfNull(schema);

        var draft = Open(nameof(ApiVersion));
        var parsed = Contracts.Registry.ApiVersion.Parse(version);

        foreach (var existing in draft.ApiVersions) {
            if (existing.Version == parsed) {
                throw new ArgumentException(
                    $"'{draft.Type}' declares api-version '{version}' twice. An api-version is "
                    + "immutable, so a second declaration is either a copy-paste or an attempt to "
                    + "change a published version — and the second is the thing "
                    + "docs/plan/08 § The provider registry forbids outright.",
                    nameof(version)
                );
            }
        }

        draft.ApiVersions.Add(new(parsed, schema));
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Reconciler<TReconciler>()
        where TReconciler : IResourceReconciler {
        var draft = Open(nameof(Reconciler));

        if (draft.ReconcilerType is not null) {
            throw new InvalidOperationException(
                $"'{draft.Type}' already declares the reconciler '{draft.ReconcilerType.Name}'. One "
                + "type converges one way; two reconcilers would race each other on the same objects."
            );
        }

        draft.ReconcilerType = typeof(TReconciler);
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Meters(params QuotaMeter[] meters) {
        ArgumentNullException.ThrowIfNull(meters);

        foreach (var meter in meters) {
            AddMeter(meter, string.Empty, 1m);
        }

        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Meter(QuotaMeter meter, string amountPointer, decimal fallback = 1m) {
        ArgumentException.ThrowIfNullOrEmpty(amountPointer);

        if (amountPointer[0] != '/') {
            throw new ArgumentException(
                $"'{amountPointer}' is not a JSON Pointer. A meter's amount is addressed the same way "
                + "an error target is — docs/plan/08 § Errors — so it begins with '/', for example "
                + "'/properties/sku/vcpu'.",
                nameof(amountPointer)
            );
        }

        return AddMeter(meter, amountPointer, fallback);
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Permissions(string read, string write, string delete) {
        ArgumentException.ThrowIfNullOrWhiteSpace(read);
        ArgumentException.ThrowIfNullOrWhiteSpace(write);
        ArgumentException.ThrowIfNullOrWhiteSpace(delete);

        var draft = Open(nameof(Permissions));
        draft.ReadPermission = read;
        draft.WritePermission = write;
        draft.DeletePermission = delete;
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Action(
        string name,
        ActionKind kind,
        string permission,
        bool secret = false,
        ResourceSchema? request = null,
        ResourceSchema? response = null,
        bool longRunning = false
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        var draft = Open(nameof(Action));

        foreach (var existing in draft.Actions) {
            if (string.Equals(existing.Name, name, StringComparison.OrdinalIgnoreCase)) {
                throw new ArgumentException(
                    $"'{draft.Type}' declares the action '{name}' twice. Actions are matched "
                    + "case-insensitively as a URL segment is, so 'listKeys' and 'listkeys' are one "
                    + "action and a second declaration would make which permission applies depend on "
                    + "iteration order.",
                    nameof(name)
                );
            }
        }

        // ⚠ A secret response with no declared shape is legal and is the honest rendering of "we have
        // not said". It is worth flagging in review rather than refusing here: docs/plan/08 § The
        // provider registry allows an action to exist before its response is modelled, and refusing
        // would make declaring the shape a prerequisite for having the action at all.
        draft.Actions.Add(
            new(name, kind, permission, secret) {
                Request = request,
                Response = response,
                LongRunning = longRunning
            }
        );

        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Display(string name, string plural, string shortName = "", string summary = "") {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(plural);

        var draft = Open(nameof(Display));

        if (!draft.Display.IsEmpty) {
            throw new InvalidOperationException(
                $"'{draft.Type}' is named twice. Two display names for one type would make which one a "
                + "portal breadcrumb shows depend on declaration order."
            );
        }

        draft.Display = new(name, plural, shortName, summary);
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Chart(string chart) {
        ArgumentException.ThrowIfNullOrWhiteSpace(chart);
        Open(nameof(Chart)).Chart = chart;
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder SupportsSoftDelete(int days) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(days, 0);
        Open(nameof(SupportsSoftDelete)).SoftDeleteDays = days;
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder SupportsTags() {
        Open(nameof(SupportsTags)).SupportsTags = true;
        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder RequiresCluster(string clusterIdPointer = ClusterPlacement.DefaultPointer) {
        ArgumentException.ThrowIfNullOrEmpty(clusterIdPointer);

        if (clusterIdPointer[0] != '/') {
            throw new ArgumentException(
                $"'{clusterIdPointer}' is not a JSON Pointer. The cluster id is addressed the same way "
                + "every other property is — see SchemaProperty.JsonPointer.",
                nameof(clusterIdPointer)
            );
        }

        var draft = Open(nameof(RequiresCluster));
        draft.RequiresCluster = true;
        draft.ClusterIdPointer = clusterIdPointer;
        return this;
    }

    /// <summary>Freezes the drafts into registrations, checking what only the whole can check.</summary>
    /// <exception cref="InvalidOperationException">
    ///     A type declares no api-version, or declares <c>RequiresCluster</c> without the property
    ///     that supplies the cluster id.
    /// </exception>
    public ImmutableArray<ResourceTypeRegistration> Build() {
        var built = ImmutableArray.CreateBuilder<ResourceTypeRegistration>(drafts.Count);

        foreach (var draft in drafts) {
            if (draft.ApiVersions.Count == 0) {
                throw new InvalidOperationException(
                    $"'{draft.Type}' declares no api-version. A type with no version cannot be "
                    + "requested — every request carries one and there is no 'latest' — so it would "
                    + "be a type nothing can reach. docs/plan/08 § The provider registry."
                );
            }

            CheckClusterPlacement(draft);

            built.Add(
                new() {
                    Type = draft.Type,
                    // Oldest first, so `Newest` is the last entry and a lexicographic sort and a
                    // chronological one agree — see ApiVersion's remarks.
                    ApiVersions = [.. draft.ApiVersions.OrderBy(x => x.Version)],
                    ReconcilerType = draft.ReconcilerType,
                    Meters = [.. draft.Meters],
                    Actions = [.. draft.Actions],
                    ReadPermission = draft.ReadPermission,
                    WritePermission = draft.WritePermission,
                    DeletePermission = draft.DeletePermission,
                    Chart = draft.Chart,
                    SoftDeleteDays = draft.SoftDeleteDays,
                    SupportsTags = draft.SupportsTags,
                    RequiresCluster = draft.RequiresCluster,
                    ClusterIdPointer = draft.RequiresCluster ? draft.ClusterIdPointer : string.Empty,
                    Display = draft.Display
                }
            );
        }

        return built.DrainToImmutable();
    }

    /// <summary>
    ///     Gives <c>RequiresCluster</c> its schema consequence: every api-version must declare the
    ///     property that supplies the cluster id, as a required string.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>This is the check whose absence made the flag dangerous.</b> Before it,
    ///         <c>RequiresCluster()</c> and the property that satisfies it lived in different files and
    ///         nothing connected them — the sample provider's own remarks said so. A type that declared
    ///         the flag and forgot the property started cleanly, accepted a <c>PUT</c>, answered
    ///         <c>202</c>, and then failed at reconcile time for every resource, one at a time, after
    ///         the caller had already been told the write was accepted.
    ///     </para>
    ///     <para>
    ///         <b>Every version, not just the newest.</b> An api-version is served forever
    ///         (docs/plan/08 § The provider registry), so a type that dropped the property in a later
    ///         version would still be reachable at the earlier one — and the manager reads one pointer
    ///         for all of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Required, not merely declared.</b> An optional cluster id is a body that validates
    ///         and then cannot be placed, which is the same failure one step later.
    ///     </para>
    /// </remarks>
    static void CheckClusterPlacement(TypeDraft draft) {
        if (!draft.RequiresCluster) {
            return;
        }

        foreach (var version in draft.ApiVersions) {
            SchemaProperty? declared = null;

            foreach (var property in version.Schema.Properties) {
                if (string.Equals(property.JsonPointer, draft.ClusterIdPointer, StringComparison.Ordinal)) {
                    declared = property;
                    break;
                }
            }

            if (declared is not { } clusterId) {
                throw new InvalidOperationException(
                    $"'{draft.Type}' declares RequiresCluster('{draft.ClusterIdPointer}') and its "
                    + $"api-version '{version.Version}' does not declare that property. The reconcile "
                    + "driver refuses a pass with no cluster connection and the manager resolves one by "
                    + "reading this pointer out of the body, so the type would accept every write and "
                    + "fail every reconcile — docs/plan/08 § The provider registry."
                );
            }

            if (clusterId.Kind is not SchemaKind.Text || !clusterId.Required) {
                throw new InvalidOperationException(
                    $"'{draft.Type}' declares RequiresCluster('{draft.ClusterIdPointer}') and its "
                    + $"api-version '{version.Version}' declares that property as "
                    + $"{(clusterId.Required ? "a required" : "an optional")} "
                    + $"SchemaKind.{clusterId.Kind}. A cluster id is a required string: "
                    + "an optional one is a body that validates and then cannot be placed, which is the "
                    + "same failure one step later."
                );
            }
        }
    }

    ProviderBuilder AddMeter(QuotaMeter meter, string pointer, decimal fallback) {
        if (meter == QuotaMeter.Unknown) {
            throw new ArgumentException(
                "QuotaMeter.Unknown is not a meter — it is the zero value a default-constructed wire "
                + "type carries, and IQuotaGrain.TryReserveAsync refuses it. The families are at "
                + "docs/plan/06 § Quota.",
                nameof(meter)
            );
        }

        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(fallback, 0m);

        var draft = Open(nameof(Meter));

        foreach (var existing in draft.Meters) {
            if (existing.Meter == meter) {
                throw new ArgumentException(
                    $"'{draft.Type}' declares the meter '{meter}' twice. Two declarations would "
                    + "reserve twice against one limit, which is a subscription that hits its quota "
                    + "at half its allowance.",
                    nameof(meter)
                );
            }
        }

        draft.Meters.Add(new(meter, pointer, fallback));
        return this;
    }

    TypeDraft Open(string method) =>
        current
        ?? throw new InvalidOperationException(
            $"'{method}' was called before any ResourceType. Everything a provider declares belongs "
            + "to a resource type, and the chain reads "
            + "`.ResourceType(\"servers\").ApiVersion(…)…` — docs/plan/08 § The provider registry."
        );

    sealed class TypeDraft(ResourceTypeName type) {
        public ResourceTypeName Type { get; } = type;

        public List<ApiVersionRegistration> ApiVersions { get; } = [];

        public List<MeterRegistration> Meters { get; } = [];

        public List<ActionRegistration> Actions { get; } = [];

        public Type? ReconcilerType { get; set; }

        public string ReadPermission { get; set; } = "read";

        public string WritePermission { get; set; } = "write";

        public string DeletePermission { get; set; } = "delete";

        public string Chart { get; set; } = string.Empty;

        public int SoftDeleteDays { get; set; }

        public bool SupportsTags { get; set; }

        public bool RequiresCluster { get; set; }

        public string ClusterIdPointer { get; set; } = ClusterPlacement.DefaultPointer;

        public DisplayMetadata Display { get; set; }
    }
}
