using System.Collections.Immutable;
using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts.Registry;

/// <summary>One declared action on a resource type. docs/plan/08 § The provider registry.</summary>
/// <param name="Name">The action, for example <c>restart</c>.</param>
/// <param name="Kind">How it is invoked.</param>
/// <param name="Permission">The ReBAC permission it needs.</param>
/// <param name="Secret">Whether the response carries secret material. Always audited.</param>
/// <remarks>
///     <para>
///         ⚠ <b><see cref="Request" /> and <see cref="Response" /> are the reason
///         <c>listKeys</c> stopped returning "secrets of unknown shape".</b> Before them an action
///         carried a name, a kind, a permission and a flag, so every generated surface had to describe
///         its result as an unconstrained object: the OpenAPI emitter wrote <c>schema: {}</c> and said
///         so out loud, an SDK could only hand back a <c>JsonElement</c>, and a portal had nothing to
///         render. An action is a <c>POST</c> with a body and a result like any other operation, and
///         the registry can now say what they are.
///     </para>
///     <para>
///         ⚠ <b>Both are <see langword="null" /> when undeclared, and that is not the same as
///         <see cref="ResourceSchema.Empty" />.</b> Empty means "an object with no members, and an
///         unknown member is refused" — a real, checkable shape. Null means the provider has not said,
///         and the emitters render that as the unconstrained schema it is rather than pretending the
///         action takes nothing.
///     </para>
/// </remarks>
public readonly record struct ActionRegistration(
    string Name,
    ActionKind Kind,
    string Permission,
    bool Secret
) {
    /// <summary>
    ///     The shape of the <c>POST</c> body, or <see langword="null" /> when the action declares none.
    /// </summary>
    /// <remarks>
    ///     ⚠ Validated by the write path against the same <see cref="ResourceSchema.Validate" /> a
    ///     resource body goes through, so an action's parameters are refused on the same terms as a
    ///     resource's properties rather than on each handler's care.
    /// </remarks>
    public ResourceSchema? Request { get; init; }

    /// <summary>
    ///     The shape of the <c>200</c> body, or <see langword="null" /> when the action declares none.
    /// </summary>
    /// <remarks>
    ///     ⚠ For a <see cref="Secret" /> action this is <b>the</b> description of what leaves the
    ///     platform. docs/plan/08 § The provider registry makes a <c>secret: true</c> action the only
    ///     path a secret value leaves by; a response schema is what lets a reviewer see which values
    ///     those are without reading the handler.
    /// </remarks>
    public ResourceSchema? Response { get; init; }

    /// <summary>
    ///     Whether the action returns <c>202</c> and an operation to poll rather than <c>200</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Not derivable, and guessing it wrong is a generated client that hangs or one that
    ///     returns too early.</b> <c>restart</c> takes a minute and is long-running; <c>listKeys</c>
    ///     reads two strings and is not. Both are <see cref="ActionKind.Post" /> and nothing else in
    ///     the registration distinguishes them, so before this the emitter had to declare one shape
    ///     for both — and it chose <c>200</c>, which is wrong for every action that does work.
    /// </remarks>
    public bool LongRunning { get; init; }
}

/// <summary>
///     What a resource type is called, for the surfaces a human reads.
/// </summary>
/// <param name="Name">
///     The singular display name — <c>PostgreSQL server</c>. Empty when the provider declared none.
/// </param>
/// <param name="Plural">The plural — <c>PostgreSQL servers</c>. A portal list's heading.</param>
/// <param name="Alias">
///     The short form docs/plan/21 § Grammar's alias table maps — <c>postgres</c> for
///     <c>dbforpostgresql server</c>. ⚠ Declared by the provider that owns the type rather than kept
///     in the CLI, so the "only hand-maintained part of the CLI's surface" is one table the registry
///     generates instead of one a human edits per provider.
/// </param>
/// <param name="Summary">One sentence about the type, for CLI group help and a portal blade header.</param>
/// <remarks>
///     <para>
///         ⚠ <b>A CLI verb tree and a portal breadcrumb had nothing to draw on.</b> The registry knew
///         a type's path (<c>servers/databases</c>) and nothing else, so every surface that shows a
///         name to a person would have had to invent one — and two surfaces inventing independently is
///         two names for one thing.
///     </para>
///     <para>
///         <b>Empty is honest and is what the fallback keys on.</b> A type that declares no display
///         metadata gets its path segment in the generated surfaces, which is what those surfaces
///         would have used anyway; the difference is that the fallback is in one place and is visible
///         in the emitted document, so "nobody has named this type yet" is a fact you can grep for.
///     </para>
/// </remarks>
public readonly record struct DisplayMetadata(
    string Name = "",
    string Plural = "",
    string Alias = "",
    string Summary = ""
) {
    // ⚠ Normalised on read because `default(DisplayMetadata)` skips the constructor's defaults and
    // leaves every string null. A type that declares no Display is exactly that default, so IsEmpty
    // — the first thing anything asks — would throw for the common case. The same reason
    // SchemaProperty normalises its own optional members.

    /// <inheritdoc cref="DisplayMetadata(string,string,string,string)" />
    public string Name {
        get => field ?? string.Empty;
        init => field = value ?? string.Empty;
    } = Name;

    /// <inheritdoc cref="DisplayMetadata(string,string,string,string)" />
    public string Plural {
        get => field ?? string.Empty;
        init => field = value ?? string.Empty;
    } = Plural;

    /// <inheritdoc cref="DisplayMetadata(string,string,string,string)" />
    public string Alias {
        get => field ?? string.Empty;
        init => field = value ?? string.Empty;
    } = Alias;

    /// <inheritdoc cref="DisplayMetadata(string,string,string,string)" />
    public string Summary {
        get => field ?? string.Empty;
        init => field = value ?? string.Empty;
    } = Summary;

    /// <summary>Whether the provider declared anything at all.</summary>
    public bool IsEmpty =>
        Name.Length == 0 && Plural.Length == 0 && Alias.Length == 0 && Summary.Length == 0;
}

/// <summary>
///     One declared quota draw: which meter, and how much of it a body draws.
/// </summary>
/// <param name="Meter">Which limit to draw against.</param>
/// <param name="AmountPointer">
///     An RFC 6901 pointer to a number in the body, <c>""</c> for a flat amount, or <c>""</c> with a
///     <see cref="Derivation" /> when the amount is computed.
/// </param>
/// <param name="Fallback">
///     What to reserve when <see cref="AmountPointer" /> is absent from the body, or
///     <see langword="null" /> to refuse the write.
/// </param>
/// <remarks>
///     ⚠ <b><paramref name="Fallback" /> is nullable, and <see langword="null" /> is the default a
///     provider gets.</b> It used to be <c>decimal</c> defaulting to <c>1</c>, so a pointer that
///     stopped resolving — a property renamed, an api-version bumped, a schema and a meter that drifted
///     apart — quietly reserved one unit. Quota passed, the resource provisioned, and the meter that
///     was supposed to bound it recorded a number nobody chose. A meter that cannot determine its
///     amount now refuses the write; declaring a fallback is how a provider says a value genuinely has
///     a server default, and it is a thing said out loud rather than a thing inherited.
/// </remarks>
public readonly record struct MeterRegistration(
    QuotaMeter Meter,
    string AmountPointer,
    decimal? Fallback
) {
    /// <summary>
    ///     How the amount is computed, when it is not a number at <see cref="AmountPointer" />.
    /// </summary>
    /// <remarks>
    ///     ⚠ Supersedes <see cref="AmountPointer" /> and <see cref="Fallback" /> entirely when present —
    ///     a derivation states its own absent case. See <see cref="MeterDerivation" /> for why the
    ///     function has to be pure in the body it is handed.
    /// </remarks>
    public MeterDerivation? Derivation { get; init; }

    /// <summary>What this meter draws, in one line, for a generated document.</summary>
    /// <remarks>
    ///     ⚠ Every meter has one, including the flat and pointer cases, so
    ///     <c>OpenApiEmitter</c> publishes one shape rather than branching — and so
    ///     "what moves this quota" is answerable from the document for every type.
    /// </remarks>
    public string Expression =>
        Derivation?.Expression
        ?? (AmountPointer.Length > 0
            ? AmountPointer
            : (Fallback ?? 1m).ToString(CultureInfo.InvariantCulture));

    /// <summary>Every pointer the amount reads. Empty for a flat amount.</summary>
    public ImmutableArray<string> Reads =>
        Derivation?.Reads ?? (AmountPointer.Length > 0 ? [AmountPointer] : []);
}

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
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The manager reads this, and the branch it drives is the whole of docs/plan/08
    ///         § Soft delete.</b> A positive window makes <c>DELETE</c> park the resource instead of
    ///         tearing it down: the index entry becomes
    ///         <see cref="IndexEntryState.SoftDeleted" /> so the old address answers the canonical
    ///         <c>404</c>, the ReBAC parent edge moves to the subscription, and the committed quota
    ///         stays committed until a purge. Zero is the hard delete every type had before.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>It is the resource's retention and there is no other source of one.</b> The delete
    ///         path stamps <see cref="IndexEntry.RecoverableUntil" /> from this number and the clock,
    ///         never from the body — which is how docs/plan/08's <i>"retention is set at creation and
    ///         immutable afterwards"</i> is satisfied without a property a caller could shorten.
    ///     </para>
    /// </remarks>
    public int SoftDeleteDays { get; init; }

    /// <summary>
    ///     The permission a <b>purge</b> needs. Empty for a type with no recovery window.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>A fourth permission, because "may delete" and "may destroy permanently" are separable
    ///     rights.</b> docs/plan/08 § Soft delete takes that from Azure, which puts
    ///     <c>deletedVaults/purge/action</c> in Key Vault Contributor's <c>notActions</c>. Checking
    ///     <see cref="DeletePermission" /> for a purge would mean anybody who could delete could also
    ///     destroy, and the window would protect against nothing.
    /// </remarks>
    public string PurgePermission { get; init; } = string.Empty;

    /// <summary>
    ///     Where in the body the purge-protection flag is, for a type that offers one. Empty
    ///     otherwise.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Once <see langword="true" /> it may not be set back to <see langword="false" />, and
    ///     the write path enforces that.</b> docs/plan/08 § Soft delete: <i>"Purge protection is a
    ///     further opt-in flag that cannot be turned off once on, which is the only version of it that
    ///     is worth anything."</i> A flag an attacker can clear and then purge is one round-trip of
    ///     protection.
    /// </remarks>
    public string PurgeProtectionPointer { get; init; } = string.Empty;

    /// <summary>Whether this type carries tags.</summary>
    /// <remarks>
    ///     ⚠ <b>This flag now has a shape, and it did not before.</b> It makes the write path accept a
    ///     root-level <c>tags</c> object (<see cref="ResourceSchema.Validate" />'s <c>allowTags</c>)
    ///     <i>and</i> makes the emitters declare one (<see cref="TagRules" />). While it made only the
    ///     first happen, the published document said <c>additionalProperties: false</c> over a body
    ///     that accepted <c>tags</c> — the runtime and the contract disagreeing, in the published
    ///     direction.
    /// </remarks>
    public bool SupportsTags { get; init; }

    /// <summary>Whether this type is placed into a cluster.</summary>
    /// <remarks>
    ///     ⚠ Read <see cref="ClusterIdPointer" /> with it: the flag says a cluster is needed and the
    ///     pointer says where the request supplies it.
    /// </remarks>
    public bool RequiresCluster { get; init; }

    /// <summary>
    ///     Where in the body the cluster id is, for a type that declares
    ///     <see cref="RequiresCluster" />. Empty for one that does not.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The second flag with no schema consequence, and its failure mode was worse than
    ///         the tag one.</b> <c>RequiresCluster()</c> made the reconcile driver refuse a pass with
    ///         no connection, and the connection was resolved by reading <c>/properties/clusterId</c>
    ///         out of the body — a pointer hard-coded in <c>ResourceManagerService</c> and declared, if
    ///         at all, by each provider's schema. Nothing connected the two. A provider that declared
    ///         <c>RequiresCluster</c> and forgot the property compiled, started, accepted a
    ///         <c>PUT</c>, returned <c>202</c>, and then failed <i>per resource</i> at reconcile time
    ///         — the worst possible place to find out, because the caller has already been told yes.
    ///     </para>
    ///     <para>
    ///         The repair is that the pointer is a registry fact: the builder records it, the builder's
    ///         <c>Build</c> refuses a type whose every api-version does not declare it as a required
    ///         string, and the write path reads the resource's own pointer rather than a constant. A
    ///         provider that forgets now fails at silo start, which is a deploy that does not roll out.
    ///     </para>
    /// </remarks>
    public string ClusterIdPointer { get; init; } = string.Empty;

    /// <summary>What this type is called, for the surfaces a human reads.</summary>
    public DisplayMetadata Display { get; init; }

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
///         <b>All four emitters are written and read this object.</b> The OpenAPI emitter reads it
///         directly; the <c>cyc</c> verb tree, the .NET SDK and the portal forms are generated from
///         the document it produces — docs/plan/21 § Generation's one hop, so the compatibility gate
///         on a published api-version protects all four at once. What is <i>not</i> written is the
///         TypeScript, Python and Go SDKs and the Terraform provider (docs/plan/21 § Other SDKs).
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
