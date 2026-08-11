using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>One declared action on a resource type. docs/plan/08 § The provider registry.</summary>
/// <param name="Name">The action, for example <c>restart</c>.</param>
/// <param name="Kind">How it is invoked.</param>
/// <param name="Permission">The ReBAC permission it needs.</param>
/// <param name="Secret">Whether the response carries secret material. Always audited.</param>
public readonly record struct ActionRegistration(
    string Name,
    ActionKind Kind,
    string Permission,
    bool Secret
);

/// <summary>
///     One declared quota draw: which meter, and where in the body the amount comes from.
/// </summary>
/// <param name="Meter">Which limit to draw against.</param>
/// <param name="AmountPointer">
///     An RFC 6901 pointer to a number in the body, or <c>""</c> for a flat one unit.
/// </param>
/// <param name="Fallback">What to reserve when the pointer is absent.</param>
public readonly record struct MeterRegistration(
    QuotaMeter Meter,
    string AmountPointer,
    decimal Fallback
);

/// <summary>
///     One api-version of one resource type: the date, the schema, and nothing else.
/// </summary>
/// <remarks>
///     ⚠ <b>Kept forever.</b> docs/plan/08 § The provider registry: <i>"The registry keeps every
///     version … Removing a version needs a 12-month notice window and a build gate that fails on a
///     version removed without one."</i> That gate is not written — see
///     <see cref="ResourceTypeRegistration.RetiredOn" /> for the half that is.
/// </remarks>
/// <param name="Version">The immutable date.</param>
/// <param name="Schema">The body shape at that date.</param>
public readonly record struct ApiVersionRegistration(ApiVersion Version, ResourceSchema Schema);

/// <summary>
///     Everything the registry knows about one resource type — what a request resolves to at step 1.
/// </summary>
/// <remarks>
///     ⚠ <b>This record is read on every request and is immutable.</b> It is built once at silo start
///     from a provider's <c>Describe</c> and never mutated, which is what lets the write path hold a
///     reference to it across awaits without copying.
/// </remarks>
public sealed record ResourceTypeRegistration {
    /// <summary>The fully qualified type.</summary>
    public ResourceTypeName Type { get; init; }

    /// <summary>Every api-version, oldest first.</summary>
    public ImmutableArray<ApiVersionRegistration> ApiVersions { get; init; } = [];

    /// <summary>The reconciler's CLR type, resolved from the container as a singleton.</summary>
    /// <remarks>
    ///     ⚠ <see langword="null" /> means "declared with no reconciler", which is legal only for a
    ///     type whose existence <i>is</i> the resource — a role assignment, a tag scope. The write
    ///     path converges such a type the moment desired state is written, and says so in progress.
    /// </remarks>
    public Type? ReconcilerType { get; init; }

    /// <summary>The quota meters a resource of this type draws on.</summary>
    public ImmutableArray<MeterRegistration> Meters { get; init; } = [];

    /// <summary>The declared actions.</summary>
    public ImmutableArray<ActionRegistration> Actions { get; init; } = [];

    /// <summary>The permission a read needs. ⚠ Also what decides <c>404</c> versus <c>403</c>.</summary>
    public string ReadPermission { get; init; } = "read";

    /// <summary>The permission a write needs.</summary>
    public string WritePermission { get; init; } = "write";

    /// <summary>The permission a delete needs.</summary>
    public string DeletePermission { get; init; } = "delete";

    /// <summary>The Helm chart, or empty for a type that renders none.</summary>
    public string Chart { get; init; } = string.Empty;

    /// <summary>How many days a deleted resource is recoverable, or 0 for no soft delete.</summary>
    public int SoftDeleteDays { get; init; }

    /// <summary>Whether this type carries tags.</summary>
    public bool SupportsTags { get; init; }

    /// <summary>Whether this type is placed into a cluster.</summary>
    public bool RequiresCluster { get; init; }

    /// <summary>
    ///     When each api-version stops being served, for versions under notice. Absent means "not
    ///     retired".
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Half of docs/plan/08 § The provider registry's rule, and the half that belongs here.</b>
    ///     The document requires "a 12-month notice window and a build gate that fails on a version
    ///     removed without one". This map is where the notice is recorded; <b>the build gate is not
    ///     written</b> — it belongs in <c>build/Build.Architecture.cs</c> with the other architecture
    ///     assertions, the same split <see cref="ErrorCode" /> records for the error-code registry.
    /// </remarks>
    public ImmutableDictionary<ApiVersion, DateOnly> RetiredOn { get; init; } =
        ImmutableDictionary<ApiVersion, DateOnly>.Empty;

    /// <summary>The newest api-version. ⚠ Not what a request gets unless it asked for it by date.</summary>
    public ApiVersion Newest =>
        ApiVersions.IsDefaultOrEmpty ? default : ApiVersions[^1].Version;

    /// <summary>Finds an api-version's schema.</summary>
    /// <param name="version">The version asked for.</param>
    /// <returns>
    ///     The schema, or an <see cref="ErrorCode.InvalidApiVersion" /> naming every version this type
    ///     does serve — a caller that guessed a date needs the list, not a "no".
    /// </returns>
    public Result<ResourceSchema> SchemaFor(ApiVersion version) {
        foreach (var candidate in ApiVersions) {
            if (candidate.Version == version) {
                return Result<ResourceSchema>.Success(candidate.Schema);
            }
        }

        return Result<ResourceSchema>.Failure(
            ErrorCode.InvalidApiVersion,
            $"'{version}' is not an api-version of '{Type}'. This type serves "
            + $"[{string.Join(", ", ApiVersions.Select(x => x.Version))}]. An api-version is immutable "
            + "and there is no 'latest' — docs/plan/08 § The provider registry."
        );
    }

    /// <summary>Finds a declared action by name, case-insensitively as a URL segment is.</summary>
    /// <param name="name">The action name from the URL.</param>
    /// <param name="action">The registration on success.</param>
    /// <returns><c>true</c> when this type declares an action with that name.</returns>
    public bool TryGetAction(string? name, out ActionRegistration action) {
        action = default;

        if (name is null) {
            return false;
        }

        foreach (var candidate in Actions) {
            if (string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase)) {
                action = candidate;
                return true;
            }
        }

        return false;
    }
}

/// <summary>
///     The built registry: what step 1 of docs/plan/08 § The write path, end to end looks a request up
///     in.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This is the same object ADR-012's emitters walk.</b>
///         docs/plan/08 § The provider registry: <i>"the same registry that generates the CLI is the
///         one that validates the request body. That identity is what makes drift impossible rather
///         than merely detectable."</i> Anything that reads a resource type's shape reads it from
///         here — there is no second description of the API anywhere in the platform, and a second
///         one would be the drift.
///     </para>
///     <para>
///         <b>The emitters are not written.</b> The registry is <i>shaped</i> so they are possible:
///         every type carries its versions, schemas, permissions, actions and meters, and every schema
///         property carries its pointer, kind, requiredness and description. Writing the OpenAPI, CLI,
///         SDK and form emitters is ADR-012 and a separate task.
///     </para>
/// </remarks>
public interface IProviderRegistry {
    /// <summary>Every registered type, ordered by <see cref="ResourceTypeRegistration.Type" />.</summary>
    ImmutableArray<ResourceTypeRegistration> Types { get; }

    /// <summary>Every provider namespace, as declared — case preserved for display.</summary>
    ImmutableArray<string> Namespaces { get; }

    /// <summary>Looks a type up, case-insensitively as Azure does.</summary>
    /// <param name="type">The type from the path.</param>
    /// <param name="registration">The registration on success.</param>
    /// <returns><c>true</c> when the platform serves this type.</returns>
    bool TryGetType(ResourceTypeName type, out ResourceTypeRegistration registration);

    /// <summary>
    ///     Step 1, whole: resolves a type and an api-version together, with the error a caller can act
    ///     on.
    /// </summary>
    /// <param name="type">The type from the path.</param>
    /// <param name="apiVersion">The <c>api-version</c> query parameter.</param>
    /// <returns>
    ///     The registration and the schema, or the first failure — <see cref="ErrorCode.InvalidResourceType" />
    ///     naming a type the platform does not serve, or <see cref="ErrorCode.InvalidApiVersion" />
    ///     naming the versions it does.
    /// </returns>
    Result<TypeResolution> Resolve(ResourceTypeName type, string? apiVersion);
}

/// <summary>What step 1 resolved to.</summary>
/// <param name="Registration">Everything the registry knows about the type.</param>
/// <param name="ApiVersion">The version the caller asked for, parsed.</param>
/// <param name="Schema">That version's body shape.</param>
public readonly record struct TypeResolution(
    ResourceTypeRegistration Registration,
    ApiVersion ApiVersion,
    ResourceSchema Schema
);
