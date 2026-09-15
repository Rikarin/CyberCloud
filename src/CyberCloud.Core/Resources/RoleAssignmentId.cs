namespace CyberCloud.Core.Resources;

/// <summary>
///     The three parts of a role assignment's name — <c>{role}-{principalType}-{principalId}</c>,
///     which is the tuple <c>scope#{role}@{principalType}:{principalId}</c> spelled as one path
///     segment.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE NAME IS DERIVED FROM THE TUPLE AND IS NOT THE CLIENT'S TO CHOOSE, WHICH IS THE
///             ONE PLACE THIS ADDRESS DEPARTS FROM AZURE'S.
///         </b> Azure's <c>roleAssignments/{name}</c> takes a
///         client-minted GUID and keeps a record beside the grant so that the GUID can be looked up
///         again. docs/plan/07 § Azure RBAC, expressed in it says the role-assignment API is a
///         <i>view</i> over tuples — <i>"the argument for building this rather than a role table"</i>
///         — and a record that maps a GUID to a tuple is a role table, however small. The tuple
///         already is the assignment: one object, one relation, one subject, and the set of tuples on
///         an object cannot hold the same one twice. So the name says which tuple, a repeated
///         <c>PUT</c> to the same name is the same tuple, and there is no second durable thing to
///         keep in step with the first. What that costs is a client that mints its own GUIDs — the
///         Bicep <c>guid(scope, principal, role)</c> idiom exists precisely because a deterministic
///         name is what people want from this address anyway.
///     </para>
///     <para>
///         ⚠ <b>The spellings are the ReBAC ones, verbatim and matched ordinally.</b>
///         <see cref="PrincipalType" /> is a subject type as the tuple store spells it —
///         <c>user</c>, <c>servicePrincipal</c>, <c>managedIdentity</c>, <c>group</c> — and not a
///         lower-cased or abbreviated copy that would be a second vocabulary agreeing with the first
///         by hand. This segment is therefore <b>not</b> a DNS-1123 name and is not validated as one;
///         the components are validated one at a time, and the whole is what
///         <see cref="Render" /> joins.
///     </para>
///     <para>
///         The split is unambiguous because a role name and a subject type contain no <c>-</c>
///         (<see cref="RelationNaming.NamePattern" />) and only the id may. The first two hyphens
///         are structural; every later one belongs to the id.
///     </para>
/// </remarks>
/// <param name="Role">The relation name — <c>owner</c>, <c>contributor</c> or <c>reader</c>.</param>
/// <param name="PrincipalType">The ReBAC subject type, as the tuple store spells it.</param>
/// <param name="PrincipalId">The subject id, per <see cref="RelationNaming.IsId" />.</param>
public readonly record struct RoleAssignmentName(string Role, string PrincipalType, string PrincipalId) {
    /// <summary>The joined form, <c>{role}-{principalType}-{principalId}</c>.</summary>
    public string Render() => Role + "-" + PrincipalType + "-" + PrincipalId;

    /// <summary>
    ///     Splits a name into its three parts and validates each. Which relations are roles and which
    ///     subject types exist is the schema's to say, not this type's — this checks the grammar.
    /// </summary>
    /// <param name="name">The path segment.</param>
    public static Result<RoleAssignmentName> Parse(string? name) {
        if (string.IsNullOrEmpty(name)) {
            return Invalid(
                "A role assignment name is required. It is '{role}-{principalType}-{principalId}' — "
                + "for example 'reader-user-7f3c2a1e0b4d4f6a8c9d1e2f3a4b5c6d' — and it names the "
                + "tuple the assignment is; see RoleAssignmentName."
            );
        }

        var first = name.IndexOf('-', StringComparison.Ordinal);
        var second = first < 0 ? -1 : name.IndexOf('-', first + 1);

        if (first <= 0 || second < 0 || second == first + 1 || second == name.Length - 1) {
            return Invalid(
                $"'{name}' is not a role assignment name: the form is "
                + "'{role}-{principalType}-{principalId}', three non-empty parts joined by '-'. The "
                + "id may contain '-'; the role and the principal type cannot."
            );
        }

        var role = name[..first];
        var principalType = name[(first + 1)..second];
        var principalId = name[(second + 1)..];

        if (!RelationNaming.IsName(role)) {
            return Invalid(
                $"'{role}' is not a role name: the rule is {RelationNaming.NamePattern}, as for any "
                + "relation — docs/plan/07 § The model."
            );
        }

        if (!RelationNaming.IsName(principalType)) {
            return Invalid(
                $"'{principalType}' is not a principal type: the rule is {RelationNaming.NamePattern}, "
                + "and the value is a ReBAC subject type spelled as the tuple store spells it — "
                + "'user', 'servicePrincipal', 'managedIdentity' or 'group'."
            );
        }

        var validId = RelationNaming.ValidateId(principalId);

        return validId.TryGetError(out var idError)
            ? Result<RoleAssignmentName>.Failure(
                idError.Code,
                $"The principal id in '{name}' is invalid: {idError.Message}"
            )
            : Result<RoleAssignmentName>.Success(new(role, principalType, principalId));
    }

    /// <inheritdoc />
    public override string ToString() => Render();

    static Result<RoleAssignmentName> Invalid(string message) =>
        Result<RoleAssignmentName>.Failure(ErrorCode.InvalidResourceId, message);
}

/// <summary>
///     The address of a role assignment —
///     <c>{scope}/providers/CyberCloud.Authorization/roleAssignments/{name}</c>, where the scope is
///     a tenant, a subscription, a resource group or a resource. Azure's shape; docs/plan/07 § Azure
///     RBAC, expressed in it.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             AN EXTENSION ADDRESS, AND THE ROUTER MUST TRY IT BEFORE BOTH OTHER GRAMMARS OR ONE
///             SHAPE OF IT IS UNREACHABLE.
///         </b> A resource-group-scoped assignment —
///         <c>/tenants/{t}/subscriptions/{s}/resourceGroups/{g}/providers/CyberCloud.Authorization/roleAssignments/{n}</c>
///         — is ten segments and satisfies <see cref="ResourceId.ParsePath" /> exactly, as a resource
///         of type <c>CyberCloud.Authorization/roleAssignments</c>. Tried second, it would reach the
///         resource manager, whose first step would refuse it as a type no provider serves. So this
///         grammar goes first and is disjoint by construction: nothing else ends in
///         <see cref="Suffix" />, because <see cref="ProviderNamespace" /> is reserved and no
///         provider may register a type under it — <c>ProviderRegistry.Build</c> throws on one, the
///         way it does for <c>KubeLabels.ReservedNamespace</c>.
///     </para>
///     <para>
///         ⚠ <b>The scope is one of two address types, and exactly one of them is set.</b>
///         <see cref="Scope" /> for a tenant, a subscription or a resource group;
///         <see cref="Resource" /> for a resource. <see cref="IsResourceScoped" /> is the
///         discriminator; a default <see cref="ScopeId" /> has <see cref="ScopeKind.Unknown" /> and
///         is what "not a scope" looks like. Two members rather than one because the two grammars
///         are two types with nothing in common but the tenant, and a union would have to be one of
///         them wearing the other's clothes.
///     </para>
///     <para>
///         ⚠ <b>The name is derived from the tuple, not chosen by the client</b> — see
///         <see cref="RoleAssignmentName" /> for the decision and what it costs.
///     </para>
/// </remarks>
/// <param name="Scope">The scope, for a tenant, a subscription or a resource group. Default otherwise.</param>
/// <param name="Resource">The resource, for a resource-scoped assignment. Default otherwise.</param>
/// <param name="Name">The parsed name — the tuple this assignment is.</param>
public readonly record struct RoleAssignmentId(ScopeId Scope, ResourceId Resource, RoleAssignmentName Name) {
    /// <summary>
    ///     The provider namespace this address lives under. ⚠ Reserved: no provider may register it.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Case-preserving on the way out and matched case-insensitively on the way in</b>, as
    ///     every structural literal of a resource path is — <see cref="ResourceId.ParsePath" />. The
    ///     reservation is enforced the same way, so a provider spelling it <c>cybercloud.authorization</c>
    ///     is refused too.
    /// </remarks>
    public const string ProviderNamespace = "CyberCloud.Authorization";

    /// <summary>The type segment, <c>roleAssignments</c>.</summary>
    public const string TypeSegment = "roleAssignments";

    /// <summary>
    ///     The Azure-shaped type string a response carries —
    ///     <c>CyberCloud.Authorization/roleAssignments</c>.
    /// </summary>
    public const string TypeName = ProviderNamespace + "/" + TypeSegment;

    /// <summary>
    ///     The four segments that follow the scope: <c>/providers/CyberCloud.Authorization/roleAssignments/</c>.
    /// </summary>
    public const string Suffix = "/providers/" + ProviderNamespace + "/" + TypeSegment + "/";

    /// <summary>
    ///     The two segments every address under the reserved namespace carries:
    ///     <c>/providers/CyberCloud.Authorization/</c>.
    /// </summary>
    public const string NamespaceSegment = "/providers/" + ProviderNamespace + "/";

    /// <summary>
    ///     Whether a path is under the reserved namespace at all — whether or not it is a
    ///     well-formed role assignment address.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     ⚠ <b>This is what makes the reservation total rather than a precedence rule.</b> A path
    ///     under the namespace that fails <see cref="ParsePath" /> — a name with no hyphens, a
    ///     trailing segment, the bare collection — would otherwise fall through to the resource and
    ///     collection grammars, both of which accept it as a type no provider serves and answer the
    ///     canonical <c>404</c>. That sends a client looking for a missing assignment when their URL
    ///     is wrong. The router asks this first, and under the namespace only
    ///     <see cref="ParsePath" />'s answer counts: a route or a <c>400</c> that names the grammar.
    /// </remarks>
    public static bool IsUnderNamespace(string? path) =>
        path is not null && path.Contains(NamespaceSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>An assignment on a tenant, a subscription or a resource group.</summary>
    /// <param name="scope">The scope. ⚠ <see cref="ScopeKind.Unknown" /> is not a scope and throws.</param>
    /// <param name="name">The name.</param>
    public static RoleAssignmentId OnScope(ScopeId scope, RoleAssignmentName name) =>
        scope.Kind == ScopeKind.Unknown
            ? throw new ArgumentException(
                "A role assignment needs a scope, and ScopeKind.Unknown is not one.",
                nameof(scope)
            )
            : new(scope, default, name);

    /// <summary>An assignment on a resource.</summary>
    /// <param name="resource">The resource. Its <see cref="ResourceId.Id" /> may still be unresolved.</param>
    /// <param name="name">The name.</param>
    public static RoleAssignmentId OnResource(ResourceId resource, RoleAssignmentName name) =>
        new(default, resource, name);

    /// <summary>Whether the scope is a resource rather than a tenant, a subscription or a group.</summary>
    public bool IsResourceScoped => Scope.Kind == ScopeKind.Unknown;

    /// <summary>The tenant, whichever of the two scope members carries it.</summary>
    public Guid TenantId => IsResourceScoped ? Resource.TenantId : Scope.TenantId;

    /// <summary>The scope's own path — everything before <see cref="Suffix" />.</summary>
    public string ScopePath => IsResourceScoped ? Resource.Path : Scope.Path;

    /// <summary>The full address.</summary>
    public string Path => ScopePath + Suffix + Name.Render();

    /// <summary>
    ///     The same address with the tenant replaced — what the router does with the token's tenant.
    /// </summary>
    /// <param name="tenantId">The tenant from the token.</param>
    public RoleAssignmentId WithTenant(Guid tenantId) =>
        IsResourceScoped
            ? this with { Resource = Resource with { TenantId = tenantId } }
            : this with { Scope = Scope with { TenantId = tenantId } };

    /// <summary>
    ///     Parses a role assignment address. Returns <see langword="false" /> for anything that is
    ///     not exactly one, and never throws.
    /// </summary>
    /// <param name="path">The candidate path. May be <see langword="null" />.</param>
    /// <param name="id">The parsed address on success.</param>
    public static bool TryParsePath(string? path, out RoleAssignmentId id) {
        id = default;
        var parsed = ParsePath(path);

        if (parsed.IsFailure) {
            return false;
        }

        id = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary>
    ///     <see cref="TryParsePath" /> with an explanation that names the offending value.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     <para>
    ///         The suffix is located by its <b>last</b> occurrence and matched case-insensitively on
    ///         its three literals, as <see cref="ResourceId.ParsePath" /> matches <c>providers</c>.
    ///         The prefix before it is then handed to <see cref="ScopeId.ParsePath" /> and, failing
    ///         that, to <see cref="ResourceId.ParsePath" /> — so the scope's own rules on GUID form
    ///         and naming apply unchanged, and a scope address this cannot parse is one neither of
    ///         those can either.
    ///     </para>
    ///     <para>
    ///         ⚠ A path that merely <i>contains</i> the suffix but does not end in a name — a
    ///         collection address, or the suffix followed by nothing — is not a role assignment and
    ///         is refused here rather than reinterpreted. There is no collection <c>GET</c> on this
    ///         path yet; <c>ICheckGrain.ListRoleAssignmentsAsync</c> is the read and it is not on the
    ///         wire.
    ///     </para>
    /// </remarks>
    public static Result<RoleAssignmentId> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(
                "A role assignment path is required. It looks like "
                + "'{scope}" + Suffix + "{role}-{principalType}-{principalId}', where the scope is a "
                + "tenant, a subscription, a resource group or a resource — docs/plan/07 § Azure RBAC, "
                + "expressed in it."
            );
        }

        var at = path.LastIndexOf(Suffix, StringComparison.OrdinalIgnoreCase);

        if (at <= 0) {
            return Invalid(
                $"'{path}' is not a role assignment path: it does not contain '{Suffix}' after a scope."
            );
        }

        var scopePath = path[..at];
        var name = path[(at + Suffix.Length)..];

        if (name.Length == 0 || name.Contains('/', StringComparison.Ordinal)) {
            return Invalid(
                $"'{path}' is not a role assignment path: '{Suffix}' must be followed by exactly one "
                + "segment, the assignment's name."
            );
        }

        var parsedName = RoleAssignmentName.Parse(name);
        if (parsedName.TryGetError(out var nameError)) {
            return Result<RoleAssignmentId>.Failure(nameError);
        }

        if (ScopeId.TryParsePath(scopePath, out var scope)) {
            return Result<RoleAssignmentId>.Success(OnScope(scope, parsedName.GetValueOrThrow()));
        }

        var resource = ResourceId.ParsePath(scopePath);

        return resource.TryGetError(out var resourceError)
            ? Invalid(
                $"'{scopePath}' — the scope of '{path}' — is neither a scope path nor a resource id "
                + $"path. As a resource id path: {resourceError.Message}"
            )
            : Result<RoleAssignmentId>.Success(OnResource(resource.GetValueOrThrow(), parsedName.GetValueOrThrow()));
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    static Result<RoleAssignmentId> Invalid(string message) =>
        Result<RoleAssignmentId>.Failure(ErrorCode.InvalidResourceId, message);
}
