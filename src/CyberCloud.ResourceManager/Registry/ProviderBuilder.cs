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
    public IResourceTypeBuilder Action(string name, ActionKind kind, string permission, bool secret = false) {
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

        draft.Actions.Add(new(name, kind, permission, secret));
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
    public IResourceTypeBuilder RequiresCluster() {
        Open(nameof(RequiresCluster)).RequiresCluster = true;
        return this;
    }

    /// <summary>Freezes the drafts into registrations, checking what only the whole can check.</summary>
    /// <exception cref="InvalidOperationException">A type declares no api-version.</exception>
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
                    RequiresCluster = draft.RequiresCluster
                }
            );
        }

        return built.DrainToImmutable();
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
    }
}
