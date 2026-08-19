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

        // ⚠ A BODY WHOSE EVERY PROPERTY IS SECRET HAS NO READABLE PROJECTION, AND THAT LEAKS.
        //
        // ResourceManagerService.ReadablePointers withholds the Secret pointers by handing the grain
        // the others, and ResourceGrain.Project reads an empty list as "project the whole superset" —
        // the escape hatch the delete path and the reconcile driver need. So a body like this would
        // project everything it meant to withhold, and do it silently.
        //
        // ⚠ The check is HERE and not in ResourceSchema.Of, because a schema does not know what it is
        // for. An action's response schema is legitimately all-secret — the `listKeys` case in
        // docs/plan/08 § The provider registry is exactly that — and it never goes near the projection.
        // Only a resource-type body does, and this is the one place a schema is declared to be one.
        if (!schema.Properties.IsDefaultOrEmpty && schema.Properties.All(x => x.Secret)) {
            throw new ArgumentException(
                $"Every property of '{draft.Type}' at '{version}' is Secret, so a read has nothing to "
                + "project and would fall through to the whole stored superset — the opposite of what "
                + "Secret asks for. A resource type needs at least one property a caller can read back.",
                nameof(schema)
            );
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
            AddMeter(meter, string.Empty, 1m, null);
        }

        return this;
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Meter(QuotaMeter meter, string amountPointer, decimal? fallback = null) {
        ArgumentException.ThrowIfNullOrEmpty(amountPointer);

        if (amountPointer[0] != '/') {
            throw new ArgumentException(
                $"'{amountPointer}' is not a JSON Pointer. A meter's amount is addressed the same way "
                + "an error target is — docs/plan/08 § Errors — so it begins with '/', for example "
                + "'/properties/sku/vcpu'.",
                nameof(amountPointer)
            );
        }

        return AddMeter(meter, amountPointer, fallback, null);
    }

    /// <inheritdoc />
    public IResourceTypeBuilder Meter(QuotaMeter meter, MeterDerivation derivation) {
        ArgumentNullException.ThrowIfNull(derivation);

        return AddMeter(meter, string.Empty, null, derivation);
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
        bool longRunning = false,
        Type? handler = null
    ) {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);

        // ⚠ Checked here rather than where the handler is resolved, and the difference is when you
        // find out. A Type that does not implement the interface is a typo in a declaration, and a
        // declaration runs at silo start; leaving it to ActionDispatcher would turn it into a 500 on
        // the first call to that action, which for `listKeys` means the first time a tenant asks for
        // the credential they cannot otherwise obtain.
        if (handler is not null && !typeof(IResourceActionHandler).IsAssignableFrom(handler)) {
            throw new ArgumentException(
                $"'{handler.FullName}' is named as the handler for '{name}' and does not implement "
                + "IResourceActionHandler.",
                nameof(handler)
            );
        }

        // ⚠ REFUSED RATHER THAN IGNORED, BECAUSE IGNORING IT IS A TRAP WITH NO SYMPTOM.
        // ResourceManagerService.ActionAsync runs a handler only on the SYNCHRONOUS branch; a
        // long-running action still starts an operation, and OperationGrain drives the type's
        // RECONCILER for it. So a provider that declared both would ship an action whose handler
        // never runs, whose 202 looks correct, and whose effect is a reconcile pass. Refusing here
        // makes that a silo-start failure with a sentence in it. Closing it properly is a change to
        // OperationGrain.DriveAsync — see the owed note on ActionRegistration.LongRunning.
        if (handler is not null && longRunning) {
            throw new ArgumentException(
                $"'{name}' is declared long-running and names the handler '{handler.FullName}', and "
                + "the platform cannot run one. A handler is invoked on the synchronous action path "
                + "only; a long-running action starts an operation, and the operation grain drives "
                + "the resource type's reconciler rather than an action handler. Declare the action "
                + "synchronous, or leave the handler off until the operation path can run one.",
                nameof(handler)
            );
        }

        // ⚠ THE TWO NAMES THE PLATFORM OWNS, REFUSED HERE RATHER THAN OVERWRITTEN IN Build.
        //
        // `restore` and `purge` are synthesised for every type that declares a window (see Build), and
        // ResourceManagerService.ActionAsync hands both to RestoreAsync/PurgeAsync instead of running
        // the ordinary action path. A provider that declared its own `restore` would therefore ship an
        // action whose declared permission, body and handler are all ignored — the same silent trap the
        // long-running-handler refusal above exists for, with the extra edge that the two declarations
        // would be indistinguishable in the generated document. So it is a silo-start failure with a
        // sentence in it, and it is refused for EVERY type rather than only for the ones with a window:
        // a type that declares `restore` today and adds SupportsSoftDelete tomorrow would otherwise
        // break on the second change, which is not where anybody would look.
        if (SoftDeletePolicy.IsReserved(name)) {
            throw new ArgumentException(
                $"'{name}' is reserved. The platform declares '{SoftDeletePolicy.RestoreAction}' and "
                + $"'{SoftDeletePolicy.PurgeAction}' on every type that declares SupportsSoftDelete, and "
                + "the resource manager dispatches both to its own soft-delete path rather than to an "
                + "action handler — so a provider's declaration of either would publish a permission, a "
                + "body and a handler that nothing reads. docs/plan/08 § Soft delete.",
                nameof(name)
            );
        }

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
                LongRunning = longRunning,
                HandlerType = handler
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
    public IResourceTypeBuilder SupportsSoftDelete(
        int days,
        string purgePermission = SoftDeletePolicy.DefaultPurgePermission,
        string purgeProtectionPointer = ""
    ) {
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(days, 0);
        ArgumentException.ThrowIfNullOrWhiteSpace(purgePermission);
        ArgumentNullException.ThrowIfNull(purgeProtectionPointer);

        if (purgeProtectionPointer.Length > 0 && purgeProtectionPointer[0] != '/') {
            throw new ArgumentException(
                $"'{purgeProtectionPointer}' is not an RFC 6901 pointer: it must start with '/'. "
                + "Purge protection is read out of the resource's own body, so the registry has to be "
                + "told where — see IResourceTypeBuilder.SupportsSoftDelete.",
                nameof(purgeProtectionPointer)
            );
        }

        var draft = Open(nameof(SupportsSoftDelete));
        draft.SoftDeleteDays = days;
        draft.PurgePermission = purgePermission;
        draft.PurgeProtectionPointer = purgeProtectionPointer;
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
            CheckPurgeProtection(draft);

            built.Add(
                new() {
                    Type = draft.Type,
                    // Oldest first, so `Newest` is the last entry and a lexicographic sort and a
                    // chronological one agree — see ApiVersion's remarks.
                    ApiVersions = [.. draft.ApiVersions.OrderBy(x => x.Version)],
                    ReconcilerType = draft.ReconcilerType,
                    Meters = [.. draft.Meters],
                    Actions = SoftDeleteActionsOf(draft),
                    ReadPermission = draft.ReadPermission,
                    WritePermission = draft.WritePermission,
                    DeletePermission = draft.DeletePermission,
                    Chart = draft.Chart,
                    SoftDeleteDays = draft.SoftDeleteDays,
                    // ⚠ Both are zeroed for a type with no window, which is the same shape
                    // ClusterIdPointer takes one line down: a fact that only means something
                    // alongside its flag is not carried when the flag is off. It keeps "does this
                    // type have a purge permission" and "does this type have a recovery window" from
                    // being two questions that can disagree.
                    PurgePermission = draft.SoftDeleteDays > 0 ? draft.PurgePermission : string.Empty,
                    PurgeProtectionPointer =
                        draft.SoftDeleteDays > 0 ? draft.PurgeProtectionPointer : string.Empty,
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
    ///     The type's declared actions, plus <c>restore</c> and <c>purge</c> when it declares a window.
    /// </summary>
    /// <param name="draft">The type being frozen.</param>
    /// <returns>The declared actions unchanged, or those actions followed by the two.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS IS THE WHOLE OF THE HTTP BINDING FOR SOFT DELETE, AND IT IS A REGISTRY FACT
    ///         RATHER THAN A ROUTE.</b> docs/plan/08 § Soft delete records that
    ///         <c>RestoreAsync</c> and <c>PurgeAsync</c> <i>"exist, are implemented on
    ///         <c>ResourceManagerService</c>, and are covered by <c>SoftDeletePathTests</c> — and
    ///         neither has an HTTP route"</i>. Two lines here give them one, because a <c>POST</c> to
    ///         <c>{resource}/{action}</c> already routes: <c>GatewayRouter.ResolveAction</c> parses the
    ///         path, <c>RouteStage</c> answers <c>404</c> unless
    ///         <see cref="ResourceTypeRegistration.TryGetAction" /> knows the name, and
    ///         <c>DispatchStage</c> hands it to <c>IResourceManager.ActionAsync</c>. Declaring the
    ///         names is what opens all three, for exactly the types that have a window.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>And it reaches ADR-012's four surfaces without any of them learning what soft
    ///         delete is.</b> <c>OpenApiEmitter</c> emits one path per entry in this array,
    ///         <c>DocumentReader</c> reads them back off <c>x-cybercloud-action</c>, and the CLI verb,
    ///         the SDK method and the portal's action button follow. Adding paths to a published
    ///         api-version is additive, so the compatibility gate has nothing to say — it refuses
    ///         removals, not additions.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Both are long-running and neither names a handler, which is what
    ///         <see cref="Action" />'s own refusal requires.</b> A restore starts an
    ///         <c>OperationKind.Restore</c> over the stored body and a purge starts an
    ///         <c>OperationKind.Purge</c>; both answer <c>202</c> with an operation to poll. There is no
    ///         handler to name because neither runs on the action path at all — see
    ///         <see cref="SoftDeletePolicy.RestoreAction" />.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>The permissions are the type's own and are deliberately different from each
    ///         other.</b> A restore takes <c>WritePermission</c> — it puts a resource back — and a purge
    ///         takes <c>PurgePermission</c>, which
    ///         <see cref="SoftDeletePolicy.DefaultPurgePermission" /> keeps out of <c>delete</c> so that
    ///         the window protects against the caller who could already delete. Publishing them under
    ///         one permission would make the generated document describe a separation the manager
    ///         enforces and the surfaces deny.
    ///     </para>
    /// </remarks>
    static ImmutableArray<ActionRegistration> SoftDeleteActionsOf(TypeDraft draft) {
        if (draft.SoftDeleteDays <= 0) {
            return [.. draft.Actions];
        }

        return [
            .. draft.Actions,
            new(SoftDeletePolicy.RestoreAction, ActionKind.Post, draft.WritePermission, false) { LongRunning = true },
            new(SoftDeletePolicy.PurgeAction, ActionKind.Post, draft.PurgePermission, false) { LongRunning = true }
        ];
    }

    /// <summary>
    ///     Gives the purge-protection pointer its schema consequence: every api-version must declare
    ///     it, as a boolean.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The same check <see cref="CheckClusterPlacement" /> is, for the same reason, and the
    ///         failure it prevents is quieter.</b> A cluster pointer that names nothing fails every
    ///         reconcile, loudly, one resource at a time. A purge-protection pointer that names nothing
    ///         reads as <see langword="false" /> forever: the flag can never be set, so protection never
    ///         engages, so nothing ever fails — the resource is simply purgeable when its owner believes
    ///         it is not. A protection that silently does not exist is worse than one that is absent,
    ///         because only the second is visible in the generated document.
    ///     </para>
    ///     <para>
    ///         <b>Every version, not just the newest</b>, because an api-version is served forever
    ///         (docs/plan/08 § The provider registry) and the manager reads one pointer for all of them.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Optional rather than required, which is the opposite of the cluster id.</b> A
    ///         cluster id has no sensible default and a body without one cannot be placed. Purge
    ///         protection defaults to off — that is what "opt-in" means — so requiring every caller to
    ///         send <c>false</c> would be a required field whose only honest value is the default.
    ///     </para>
    /// </remarks>
    static void CheckPurgeProtection(TypeDraft draft) {
        if (draft.PurgeProtectionPointer.Length == 0) {
            return;
        }

        if (draft.SoftDeleteDays <= 0) {
            throw new InvalidOperationException(
                $"'{draft.Type}' declares a purge-protection pointer and no recovery window. Purge "
                + "protection refuses a purge, a purge ends a recovery window, and a type with no "
                + "window has neither — so the flag would be a property callers can set and nothing "
                + "reads. docs/plan/08 § Soft delete."
            );
        }

        foreach (var version in draft.ApiVersions) {
            SchemaProperty? declared = null;

            foreach (var property in version.Schema.Properties) {
                if (string.Equals(property.JsonPointer, draft.PurgeProtectionPointer, StringComparison.Ordinal)) {
                    declared = property;
                    break;
                }
            }

            if (declared is not { } flag) {
                throw new InvalidOperationException(
                    $"'{draft.Type}' declares SupportsSoftDelete(purgeProtectionPointer: "
                    + $"'{draft.PurgeProtectionPointer}') and its api-version '{version.Version}' does "
                    + "not declare that property. Nothing could ever set it, so the platform would read "
                    + "protection as off for every resource of this type and purge them all — a "
                    + "protection that fails silently open. docs/plan/08 § Soft delete."
                );
            }

            if (flag.Kind != SchemaKind.Boolean) {
                throw new InvalidOperationException(
                    $"'{draft.Type}' declares SupportsSoftDelete(purgeProtectionPointer: "
                    + $"'{draft.PurgeProtectionPointer}') and its api-version '{version.Version}' "
                    + $"declares that property as SchemaKind.{flag.Kind}. Purge protection is on or "
                    + "off; anything else is a value the write path would have to interpret, and an "
                    + "interpretation that got it wrong would fail open."
                );
            }
        }
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

    ProviderBuilder AddMeter(QuotaMeter meter, string pointer, decimal? fallback, MeterDerivation? derivation) {
        if (meter == QuotaMeter.Unknown) {
            throw new ArgumentException(
                "QuotaMeter.Unknown is not a meter — it is the zero value a default-constructed wire "
                + "type carries, and IQuotaGrain.TryReserveAsync refuses it. The families are at "
                + "docs/plan/06 § Quota.",
                nameof(meter)
            );
        }

        if (fallback is { } declared) {
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(declared, 0m);
        } else if (pointer.Length == 0 && derivation is null) {
            // ⚠ A meter with no pointer, no derivation and no fallback would reserve nothing on every
            // request — a declared meter that never moves. `Meters(meter)` is how a flat one unit is
            // spelled and it passes 1m; there is no way to reach this from the public surface, so it
            // is a guard against a future overload rather than against a caller.
            throw new ArgumentException(
                $"'{meter}' declares no pointer, no derivation and no fallback, so there is nothing "
                + "for it to reserve. A flat one unit is `Meters(meter)` — docs/plan/06 § Quota.",
                nameof(fallback)
            );
        }

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

        draft.Meters.Add(new(meter, pointer, fallback) { Derivation = derivation });
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

        public string PurgePermission { get; set; } = SoftDeletePolicy.DefaultPurgePermission;

        public string PurgeProtectionPointer { get; set; } = string.Empty;

        public bool SupportsTags { get; set; }

        public bool RequiresCluster { get; set; }

        public string ClusterIdPointer { get; set; } = ClusterPlacement.DefaultPointer;

        public DisplayMetadata Display { get; set; }
    }
}
