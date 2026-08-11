using System.Globalization;
using CyberCloud.Core;
using CyberCloud.Core.Resources;

namespace CyberCloud.Authorization.Contracts;

/// <summary>
///     An object — <c>type:id</c>. docs/plan/07 § The model, concept one.
/// </summary>
/// <remarks>
///     <para>
///         The components are validated by <see cref="RelationNaming" />, which is also what
///         <c>GrainKeys</c> re-validates when it parses <c>rel/obj/{type}/{id}</c> back. That is not
///         belt and braces: it is the only way "one object, one grain key, one activation" survives
///         a value that arrived from outside.
///     </para>
///     <para>
///         ⚠ <b>Nothing here checks the type against the schema.</b> A well-formed
///         <see cref="ObjectRef" /> may name a type no schema defines; that is caught at check time
///         with <see cref="ErrorCode.SchemaInvalid" />. Wire validation is about the grammar, the
///         schema is about the vocabulary, and conflating them would put the schema in the
///         <c>.Contracts</c> assembly where the gateway would have to carry it.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.ObjectRef")]
public sealed record ObjectRef
{
    /// <summary>The object type — <c>resourceGroup</c>, <c>user</c>, <c>group</c>.</summary>
    [Id(0)]
    public string Type { get; init; } = string.Empty;

    /// <summary>The object id. Normally a GUID's 32-digit <c>N</c> form.</summary>
    [Id(1)]
    public string Id { get; init; } = string.Empty;

    /// <summary>Whether this reference is well formed.</summary>
    public bool IsValid => RelationNaming.IsName(Type) && RelationNaming.IsId(Id);

    /// <summary>Builds a reference, or explains why the parts are not one.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    public static Result<ObjectRef> Create(string? type, string? id)
    {
        var validType = RelationNaming.ValidateName(type, "object type");
        if (validType.TryGetError(out var typeError))
        {
            return Result<ObjectRef>.Failure(typeError);
        }

        var validId = RelationNaming.ValidateId(id);
        return validId.TryGetError(out var idError)
            ? Result<ObjectRef>.Failure(idError)
            : Result<ObjectRef>.Success(new ObjectRef { Type = type!, Id = id! });
    }

    /// <summary>Builds a reference from parts that are known to be legal.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    /// <exception cref="ArgumentException">Either part is not legal.</exception>
    public static ObjectRef Of(string type, string id)
    {
        var created = Create(type, id);
        return created.TryGetError(out var error)
            ? throw new ArgumentException(error.Message, nameof(type))
            : created.GetValueOrThrow();
    }

    /// <summary>Builds a reference to an object identified by a GUID.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    public static ObjectRef Of(string type, Guid id) =>
        Of(type, id.ToString("N", CultureInfo.InvariantCulture));

    /// <summary>Parses <c>type:id</c>.</summary>
    /// <param name="value">The text.</param>
    public static Result<ObjectRef> Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result<ObjectRef>.Failure(
                ErrorCode.InvalidRequestBody, "An object reference is 'type:id' and this is empty.");
        }

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? Result<ObjectRef>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{value}' is not an object reference: it has no ':'. The form is 'type:id' — "
                + "docs/plan/07 § The model.")
            : Create(value[..colon], value[(colon + 1)..]);
    }

    /// <summary>Renders <c>type:id</c>.</summary>
    public override string ToString() => Type + ":" + Id;
}

/// <summary>
///     A subject — either an object (<c>user:alice</c>) or a <b>userset</b>
///     (<c>group:eng#member</c>). docs/plan/07 § The model, concept two.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.SubjectRef")]
public sealed record SubjectRef
{
    /// <summary>The subject's object type.</summary>
    [Id(0)]
    public string Type { get; init; } = string.Empty;

    /// <summary>The subject's object id.</summary>
    [Id(1)]
    public string Id { get; init; } = string.Empty;

    /// <summary>
    ///     The userset relation, or empty when the subject is a concrete object. <c>member</c> in
    ///     <c>group:eng#member</c>.
    /// </summary>
    [Id(2)]
    public string Relation { get; init; } = string.Empty;

    /// <summary>Whether this subject is a userset rather than a concrete object.</summary>
    public bool IsUserset => Relation.Length > 0;

    /// <summary>The object half of this subject, ignoring any userset relation.</summary>
    public ObjectRef Object => new() { Type = Type, Id = Id };

    /// <summary>Whether this reference is well formed.</summary>
    public bool IsValid =>
        RelationNaming.IsName(Type)
        && RelationNaming.IsId(Id)
        && (Relation.Length == 0 || RelationNaming.IsName(Relation));

    /// <summary>Builds a subject, or explains why the parts are not one.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    /// <param name="relation">The userset relation, or <see langword="null" /> for a concrete object.</param>
    public static Result<SubjectRef> Create(string? type, string? id, string? relation = null)
    {
        var validObject = ObjectRef.Create(type, id);
        if (validObject.TryGetError(out var objectError))
        {
            return Result<SubjectRef>.Failure(objectError);
        }

        if (string.IsNullOrEmpty(relation))
        {
            return Result<SubjectRef>.Success(new SubjectRef { Type = type!, Id = id! });
        }

        var validRelation = RelationNaming.ValidateName(relation, "userset relation");
        return validRelation.TryGetError(out var relationError)
            ? Result<SubjectRef>.Failure(relationError)
            : Result<SubjectRef>.Success(
                new SubjectRef { Type = type!, Id = id!, Relation = relation });
    }

    /// <summary>A concrete subject.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    /// <exception cref="ArgumentException">The parts are not legal.</exception>
    public static SubjectRef Of(string type, string id) => Build(type, id, null);

    /// <summary>A concrete subject identified by a GUID.</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    public static SubjectRef Of(string type, Guid id) =>
        Build(type, id.ToString("N", CultureInfo.InvariantCulture), null);

    /// <summary>A userset — "the <paramref name="relation" /> of <paramref name="type" />:<paramref name="id" />".</summary>
    /// <param name="type">The object type.</param>
    /// <param name="id">The object id.</param>
    /// <param name="relation">The userset relation.</param>
    /// <exception cref="ArgumentException">The parts are not legal.</exception>
    public static SubjectRef Userset(string type, string id, string relation) =>
        Build(type, id, relation);

    /// <summary>Parses <c>type:id</c> or <c>type:id#relation</c>.</summary>
    /// <param name="value">The text.</param>
    public static Result<SubjectRef> Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result<SubjectRef>.Failure(
                ErrorCode.InvalidRequestBody,
                "A subject is 'type:id' or 'type:id#relation' and this is empty.");
        }

        var hash = value.IndexOf('#', StringComparison.Ordinal);
        var objectPart = hash < 0 ? value : value[..hash];
        var relation = hash < 0 ? null : value[(hash + 1)..];

        var colon = objectPart.IndexOf(':', StringComparison.Ordinal);
        return colon < 0
            ? Result<SubjectRef>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{value}' is not a subject: it has no ':'. The form is 'type:id' or "
                + "'type:id#relation' — docs/plan/07 § The model.")
            : Create(objectPart[..colon], objectPart[(colon + 1)..], relation);
    }

    /// <summary>Renders <c>type:id</c> or <c>type:id#relation</c>.</summary>
    public override string ToString() =>
        IsUserset ? Type + ":" + Id + "#" + Relation : Type + ":" + Id;

    static SubjectRef Build(string type, string id, string? relation)
    {
        var created = Create(type, id, relation);
        return created.TryGetError(out var error)
            ? throw new ArgumentException(error.Message, nameof(type))
            : created.GetValueOrThrow();
    }
}

/// <summary>
///     A relation tuple — <c>object#relation@subject</c>. docs/plan/07 § The model, concept two.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.RelationTuple")]
public sealed record RelationTuple
{
    /// <summary>The object the relation is on.</summary>
    [Id(0)]
    public ObjectRef Object { get; init; } = new();

    /// <summary>The relation.</summary>
    [Id(1)]
    public string Relation { get; init; } = string.Empty;

    /// <summary>The subject.</summary>
    [Id(2)]
    public SubjectRef Subject { get; init; } = new();

    /// <summary>Whether the tuple is well formed.</summary>
    public bool IsValid => Object.IsValid && RelationNaming.IsName(Relation) && Subject.IsValid;

    /// <summary>Builds a tuple, or explains why the parts are not one.</summary>
    /// <param name="object">The object.</param>
    /// <param name="relation">The relation.</param>
    /// <param name="subject">The subject.</param>
    public static Result<RelationTuple> Create(ObjectRef @object, string? relation, SubjectRef subject)
    {
        ArgumentNullException.ThrowIfNull(@object);
        ArgumentNullException.ThrowIfNull(subject);

        if (!@object.IsValid)
        {
            return Result<RelationTuple>.Failure(
                ErrorCode.InvalidRequestBody, $"'{@object}' is not a well-formed object reference.");
        }

        if (!subject.IsValid)
        {
            return Result<RelationTuple>.Failure(
                ErrorCode.InvalidRequestBody, $"'{subject}' is not a well-formed subject.");
        }

        var validRelation = RelationNaming.ValidateName(relation, "relation");
        return validRelation.TryGetError(out var error)
            ? Result<RelationTuple>.Failure(error)
            : Result<RelationTuple>.Success(
                new RelationTuple { Object = @object, Relation = relation!, Subject = subject });
    }

    /// <summary>
    ///     Parses <c>object#relation@subject</c> — the notation docs/plan/07 § The model writes
    ///     tuples in, and the notation the regression corpus is checked in as.
    /// </summary>
    /// <param name="value">The text, for example <c>resourceGroup:prod#owner@user:alice</c>.</param>
    /// <remarks>
    ///     ⚠ <b>The grammar is unambiguous only because <see cref="RelationNaming" /> excludes
    ///     <c>#</c> and <c>@</c> from every component.</b> The subject half may itself contain a
    ///     <c>#</c> (a userset), so the split is on the <b>first</b> <c>#</c> and the <b>first</b>
    ///     <c>@</c> after it — and the object half can contain neither.
    /// </remarks>
    public static Result<RelationTuple> Parse(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return Result<RelationTuple>.Failure(
                ErrorCode.InvalidRequestBody,
                "A tuple is 'object#relation@subject' and this is empty.");
        }

        var hash = value.IndexOf('#', StringComparison.Ordinal);
        if (hash < 0)
        {
            return Result<RelationTuple>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{value}' is not a tuple: it has no '#'. The form is 'object#relation@subject'.");
        }

        var at = value.IndexOf('@', hash + 1);
        if (at < 0)
        {
            return Result<RelationTuple>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{value}' is not a tuple: it has no '@' after the '#'. The form is "
                + "'object#relation@subject'.");
        }

        var parsedObject = ObjectRef.Parse(value[..hash]);
        if (parsedObject.TryGetError(out var objectError))
        {
            return Result<RelationTuple>.Failure(objectError);
        }

        var parsedSubject = SubjectRef.Parse(value[(at + 1)..]);
        return parsedSubject.TryGetError(out var subjectError)
            ? Result<RelationTuple>.Failure(subjectError)
            : Create(
                parsedObject.GetValueOrThrow(),
                value[(hash + 1)..at],
                parsedSubject.GetValueOrThrow());
    }

    /// <summary>Renders <c>object#relation@subject</c>.</summary>
    public override string ToString() => Object + "#" + Relation + "@" + Subject;
}

/// <summary>
///     Zanzibar's zookie: a per-tenant monotonic version, returned by every tuple write and accepted
///     by every check. docs/plan/07 § Consistency.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.ConsistencyToken")]
public sealed record ConsistencyToken
{
    /// <summary>The tenant this token is about.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The tenant's relation version at the moment the token was minted.</summary>
    [Id(1)]
    public long Version { get; init; }

    /// <summary>Renders the token as the opaque string an API would hand out.</summary>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{TenantId:N}.{Version}");
}

/// <summary>
///     What consistency a caller is asking for — the mode, plus the token
///     <see cref="ConsistencyMode.AtLeastAsFresh" /> needs.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.Consistency")]
public sealed record Consistency
{
    /// <summary>The mode.</summary>
    [Id(0)]
    public ConsistencyMode Mode { get; init; } = ConsistencyMode.MinimizeLatency;

    /// <summary>The token, for <see cref="ConsistencyMode.AtLeastAsFresh" />.</summary>
    [Id(1)]
    public ConsistencyToken? Token { get; init; }

    /// <summary>The default — any cached result. docs/plan/07 § Consistency, row 1.</summary>
    public static Consistency MinimizeLatency { get; } = new();

    /// <summary>Everything that is destructive. Row 3.</summary>
    public static Consistency FullyConsistent { get; } =
        new() { Mode = ConsistencyMode.FullyConsistent };

    /// <summary>Row 2 — no cache entry older than <paramref name="token" />.</summary>
    /// <param name="token">The token a write returned.</param>
    public static Consistency AtLeastAsFresh(ConsistencyToken token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return new Consistency { Mode = ConsistencyMode.AtLeastAsFresh, Token = token };
    }
}

/// <summary>
///     The answer to a check, and enough about how it was reached to tell a genuine deny from a
///     truncated one.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.CheckResult")]
public sealed record CheckResult
{
    /// <summary>The decision. <b>Fail-closed</b>: false whenever the outcome is not allowed.</summary>
    [Id(0)]
    public bool Allowed { get; init; }

    /// <summary>Why — see <see cref="CheckOutcome" />.</summary>
    [Id(1)]
    public CheckOutcome Outcome { get; init; } = CheckOutcome.Unknown;

    /// <summary>The tenant relation version this answer reflects.</summary>
    [Id(2)]
    public ConsistencyToken Token { get; init; } = new();

    /// <summary>Whether the answer came from the hot-tier check cache rather than a walk.</summary>
    [Id(3)]
    public bool FromCache { get; init; }

    /// <summary>How many <c>(object, relation, subject)</c> triples the walk visited.</summary>
    [Id(4)]
    public int TriplesVisited { get; init; }

    /// <summary>The deepest object hop the walk reached.</summary>
    [Id(5)]
    public int MaxDepthReached { get; init; }

    /// <summary>
    ///     Where a cap was hit, in words, or empty. Present only when <see cref="Outcome" /> is one
    ///     of the two cap outcomes.
    /// </summary>
    [Id(6)]
    public string CapDetail { get; init; } = string.Empty;

    /// <summary>Whether a cap truncated the walk — a deny that may be wrong.</summary>
    public bool WasTruncated =>
        Outcome is CheckOutcome.DepthCapExceeded or CheckOutcome.BreadthCapExceeded;
}

/// <summary>
///     One Azure-shaped role assignment, rendered from tuples. docs/plan/07 § Azure RBAC, expressed
///     in it.
/// </summary>
/// <remarks>
///     ⚠ <b><see cref="Inherited" /> is the whole argument for <c>From(…)</c>.</b> An assignment
///     with <see cref="Inherited" /> = <see langword="true" /> has <b>no tuple on
///     <see cref="Scope" /></b>: it is a tuple on <see cref="InheritedFrom" /> that this scope picks
///     up through the <c>From("parent", …)</c> rewrite. docs/plan/07's third table row —
///     "Inheritance sub → rg → resource | The <c>From("parent", …)</c> rewrites; no tuples written"
///     — is exactly this field.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.RoleAssignment")]
public sealed record RoleAssignment
{
    /// <summary>The scope the assignment is being viewed at.</summary>
    [Id(0)]
    public ObjectRef Scope { get; init; } = new();

    /// <summary>The role — the relation name. <c>owner</c>, <c>contributor</c>, <c>reader</c>.</summary>
    [Id(1)]
    public string RoleName { get; init; } = string.Empty;

    /// <summary>Who holds it. A user, a service principal, or a group's userset.</summary>
    [Id(2)]
    public SubjectRef Principal { get; init; } = new();

    /// <summary>Whether this assignment is inherited rather than written at <see cref="Scope" />.</summary>
    [Id(3)]
    public bool Inherited { get; init; }

    /// <summary>The scope the tuple is actually written at. Equal to <see cref="Scope" /> when not inherited.</summary>
    [Id(4)]
    public ObjectRef InheritedFrom { get; init; } = new();
}

/// <summary>Every tuple whose object is one object, as <c>IObjectRelationsGrain</c> returns it.</summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.ObjectRelationsSnapshot")]
public sealed record ObjectRelationsSnapshot
{
    /// <summary>The object.</summary>
    [Id(0)]
    public ObjectRef Object { get; init; } = new();

    /// <summary>The tuples, as relation → subjects.</summary>
    [Id(1)]
    public IReadOnlyDictionary<string, IReadOnlyList<SubjectRef>> ByRelation { get; init; } =
        new Dictionary<string, IReadOnlyList<SubjectRef>>(StringComparer.Ordinal);

    /// <summary>How many tuples in total.</summary>
    [Id(2)]
    public int Count { get; init; }

    /// <summary>The subjects of one relation, or an empty list.</summary>
    /// <param name="relation">The relation.</param>
    public IReadOnlyList<SubjectRef> Subjects(string relation) =>
        ByRelation.TryGetValue(relation, out var subjects) ? subjects : [];
}

/// <summary>
///     One entry of the reverse index — a tuple, seen from its subject. docs/plan/07 § Storage,
///     row 2.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.SubjectIndexEntry")]
public sealed record SubjectIndexEntry
{
    /// <summary>The object the tuple is on.</summary>
    [Id(0)]
    public ObjectRef Object { get; init; } = new();

    /// <summary>The relation.</summary>
    [Id(1)]
    public string Relation { get; init; } = string.Empty;

    /// <summary>
    ///     The subject's own userset relation, or empty. <c>group:eng</c> and <c>group:eng#member</c>
    ///     share a grain and are told apart here.
    /// </summary>
    [Id(2)]
    public string SubjectRelation { get; init; } = string.Empty;
}

/// <summary>
///     What a sweep found and repaired. docs/plan/07 § Storage — "reconciled by a sweeper".
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Authorization.SweepReport")]
public sealed record SweepReport
{
    /// <summary>How many journal entries were outstanding when the sweep started.</summary>
    [Id(0)]
    public int Pending { get; init; }

    /// <summary>How many were reconciled into the reverse index.</summary>
    [Id(1)]
    public int Repaired { get; init; }

    /// <summary>How many could not be reconciled and remain in the journal.</summary>
    [Id(2)]
    public int Remaining { get; init; }
}
