namespace CyberCloud.Core.Resources;

/// <summary>Which of the policy engine's three objects an address names.</summary>
public enum PolicyObjectKind {
    /// <summary>Never assigned. Not an address.</summary>
    Unknown = 0,

    /// <summary><c>policyDefinitions</c> — a rule, at a tenant, a management group or a subscription.</summary>
    Definition = 1,

    /// <summary>
    ///     <c>policyAssignments</c> — a definition applied at a management group, a subscription or a
    ///     resource group.
    /// </summary>
    Assignment = 2,

    /// <summary>
    ///     <c>policyStates</c> — the compliance an audit recorded, per resource and assignment. A
    ///     collection only: a state is never addressed one at a time.
    /// </summary>
    State = 3
}

/// <summary>
///     The address of a policy object —
///     <c>{scope}/providers/CyberCloud.Policy/{policyDefinitions|policyAssignments|policyStates}[/{name}]</c>.
///     docs/plan/08 § Policy, issue #46.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             AN EXTENSION ADDRESS ON A SCOPE, LIKE A ROLE ASSIGNMENT, AND NOT A TYPE IN THE
///             REGISTRY — WHICH IS WHERE docs/plan/01 PUT <c>CyberCloud.Policy/policyDefinitions</c> AND
///             WHERE IT COULD NOT GO.
///         </b> Two facts decide it. An assignment lives on a management group, a
///         subscription or a resource group, and <c>ResourceId.ParsePath</c> has no shape above a
///         resource group — a registry type is always ten segments or more. And a definition's
///         <c>policyRule</c> is a recursive tree, which <c>ResourceSchema</c> deliberately cannot
///         express: the registry's schema is a flat pointer list whose arrays hold scalars only, so a
///         condition would have had to be a string of JSON inside the body, validated by nobody at
///         step 2 and invisible to every generated client. So the three objects are scope extensions
///         served by <c>IPolicyManager</c>, in the order <c>RoleAssignmentId</c> set.
///     </para>
///     <para>
///         ⚠ <b>The namespace is reserved, and the router asks this grammar before any other.</b> On a
///         resource group, <c>…/providers/CyberCloud.Policy/policyAssignments/{name}</c> is a
///         well-formed ten-segment resource path, so tried after the resource grammar it would reach
///         the resource manager as a type no provider serves. <c>ProviderRegistry.Build</c> refuses a
///         provider that claims <see cref="ProviderNamespace" />, and under the namespace only this
///         parser's answer counts — a malformed policy path is a <c>400</c> that names this grammar,
///         never a fall-through to a <c>404</c>.
///     </para>
///     <para>
///         <b>Which scope takes which object</b> is <see cref="AllowsScope" />: a definition at a
///         tenant, a management group or a subscription — the scopes whose owners write rules for
///         what sits beneath them; an assignment at a management group, a subscription or a resource
///         group — docs/plan/06 § The hierarchy's three levels that hold resources; and the states at
///         any of those three, because a question about compliance is asked of a scope.
///     </para>
/// </remarks>
/// <param name="Scope">The scope the object sits on. Its tenant is the address's tenant.</param>
/// <param name="Kind">Which object.</param>
/// <param name="Name">
///     The object's DNS-1123 name, or empty for the collection at <paramref name="Scope" />.
/// </param>
public readonly record struct PolicyAddress(ScopeId Scope, PolicyObjectKind Kind, string Name) {
    /// <summary>The provider namespace these addresses live under. ⚠ Reserved: no provider may register it.</summary>
    public const string ProviderNamespace = "CyberCloud.Policy";

    /// <summary>The definitions' type segment.</summary>
    public const string DefinitionsSegment = "policyDefinitions";

    /// <summary>The assignments' type segment.</summary>
    public const string AssignmentsSegment = "policyAssignments";

    /// <summary>The compliance states' type segment.</summary>
    public const string StatesSegment = "policyStates";

    /// <summary>The two segments every address carries after its scope, <c>/providers/CyberCloud.Policy/</c>.</summary>
    public const string NamespaceSegment = "/providers/" + ProviderNamespace + "/";

    /// <summary>The Azure-shaped type a definition's body carries.</summary>
    public const string DefinitionTypeName = ProviderNamespace + "/" + DefinitionsSegment;

    /// <summary>The Azure-shaped type an assignment's body carries.</summary>
    public const string AssignmentTypeName = ProviderNamespace + "/" + AssignmentsSegment;

    /// <summary>Whether this addresses a collection rather than one object.</summary>
    public bool IsCollection => Name.Length == 0;

    /// <summary>The tenant.</summary>
    public Guid TenantId => Scope.TenantId;

    /// <summary>The type segment for <see cref="Kind" />.</summary>
    public string Segment => SegmentOf(Kind);

    /// <summary>The address, spelled canonically — the key everything downstream stores it under.</summary>
    public string Path =>
        Kind == PolicyObjectKind.Unknown
            ? ""
            : Scope.Path + NamespaceSegment + Segment + (IsCollection ? "" : "/" + Name);

    /// <summary>The collection this object belongs to.</summary>
    public PolicyAddress Collection => this with { Name = "" };

    /// <summary>The same address with the tenant replaced — what the router does with the token's tenant.</summary>
    /// <param name="tenantId">The tenant from the token.</param>
    public PolicyAddress WithTenant(Guid tenantId) => this with { Scope = Scope with { TenantId = tenantId } };

    /// <summary>A definition at a scope.</summary>
    /// <param name="scope">A tenant, a management group or a subscription.</param>
    /// <param name="name">The definition's name.</param>
    public static PolicyAddress Definition(ScopeId scope, string name) => new(scope, PolicyObjectKind.Definition, name);

    /// <summary>An assignment at a scope.</summary>
    /// <param name="scope">A management group, a subscription or a resource group.</param>
    /// <param name="name">The assignment's name.</param>
    public static PolicyAddress Assignment(ScopeId scope, string name) => new(scope, PolicyObjectKind.Assignment, name);

    /// <summary>The compliance states at a scope.</summary>
    /// <param name="scope">A management group, a subscription or a resource group.</param>
    public static PolicyAddress States(ScopeId scope) => new(scope, PolicyObjectKind.State, "");

    /// <summary>Whether an object of this kind may sit on a scope of that kind — see the remarks.</summary>
    /// <param name="kind">The object.</param>
    /// <param name="scope">The scope.</param>
    public static bool AllowsScope(PolicyObjectKind kind, ScopeKind scope) =>
        kind switch {
            PolicyObjectKind.Definition => scope is ScopeKind.Tenant or ScopeKind.ManagementGroup or ScopeKind.Subscription,
            PolicyObjectKind.Assignment or PolicyObjectKind.State =>
                scope is ScopeKind.ManagementGroup or ScopeKind.Subscription or ScopeKind.ResourceGroup,
            _ => false
        };

    /// <summary>Whether a path is under the reserved namespace at all — whether or not it parses.</summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     ⚠ What makes the reservation total rather than a precedence rule, as
    ///     <see cref="RoleAssignmentId.IsUnderNamespace" /> is for its namespace.
    /// </remarks>
    public static bool IsUnderNamespace(string? path) =>
        path is not null && path.Contains(NamespaceSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses an address. Returns <see langword="false" /> for anything that is not one, and never throws.</summary>
    /// <param name="path">The candidate path.</param>
    /// <param name="address">The parsed address on success.</param>
    public static bool TryParsePath(string? path, out PolicyAddress address) {
        address = default;
        var parsed = ParsePath(path);

        if (parsed.IsFailure) {
            return false;
        }

        address = parsed.GetValueOrThrow();
        return true;
    }

    /// <summary>
    ///     <see cref="TryParsePath" /> with an explanation naming the offending part and the grammar.
    /// </summary>
    /// <param name="path">The candidate path.</param>
    /// <remarks>
    ///     The namespace and the type segment are matched case-insensitively, as every structural
    ///     literal of a resource path is; the address is re-spelled canonically in <see cref="Path" />.
    ///     A name is DNS-1123 (<see cref="ResourceNaming" />), which is also what keeps it from
    ///     carrying a <c>/</c> into anything keyed on it.
    /// </remarks>
    public static Result<PolicyAddress> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)) {
            return Invalid(Grammar);
        }

        var at = path.IndexOf(NamespaceSegment, StringComparison.OrdinalIgnoreCase);
        if (at <= 0) {
            return Invalid($"'{path}' is not a policy address. {Grammar}");
        }

        var scope = ScopeId.ParsePath(path[..at]);
        if (scope.TryGetError(out var scopeError)) {
            return Invalid($"'{path}' is not a policy address: {scopeError.Message} {Grammar}");
        }

        var rest = path[(at + NamespaceSegment.Length)..].Split('/');

        if (rest.Length is not (1 or 2) || rest.Any(static x => x.Length == 0)) {
            return Invalid($"'{path}' is not a policy address: after '{NamespaceSegment}' comes a type and, for one object, its name. {Grammar}");
        }

        var kind = KindOf(rest[0]);
        if (kind == PolicyObjectKind.Unknown) {
            return Invalid(
                $"'{rest[0]}' is not a policy type. The types under '{ProviderNamespace}' are "
                + $"'{DefinitionsSegment}', '{AssignmentsSegment}' and '{StatesSegment}'."
            );
        }

        var parsedScope = scope.GetValueOrThrow();

        if (!AllowsScope(kind, parsedScope.Kind)) {
            return Invalid(
                kind == PolicyObjectKind.Definition
                    ? $"A policy definition sits on a tenant, a management group or a subscription, and '{path}' puts one on a {parsedScope.Kind}."
                    : $"'{SegmentOf(kind)}' sits on a management group, a subscription or a resource group, and '{path}' puts it on a {parsedScope.Kind}."
            );
        }

        if (rest.Length == 1) {
            return Result<PolicyAddress>.Success(new(parsedScope, kind, ""));
        }

        if (kind == PolicyObjectKind.State) {
            return Invalid(
                $"'{path}' names one compliance state. States are read as a collection, "
                + $"'{{scope}}{NamespaceSegment}{StatesSegment}', and never one at a time."
            );
        }

        var named = ResourceNaming.Validate(rest[1], kind == PolicyObjectKind.Definition ? "policy definition name" : "policy assignment name");

        return named.TryGetError(out var nameError)
            ? Result<PolicyAddress>.Failure(nameError)
            : Result<PolicyAddress>.Success(new(parsedScope, kind, rest[1]));
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    const string Grammar =
        "A policy address is '{scope}" + NamespaceSegment + "{policyDefinitions|policyAssignments}/{name}', "
        + "the collection without the name, or '{scope}" + NamespaceSegment + "policyStates' — docs/plan/08 "
        + "§ Policy.";

    static PolicyObjectKind KindOf(string segment) =>
        segment switch {
            _ when string.Equals(segment, DefinitionsSegment, StringComparison.OrdinalIgnoreCase) => PolicyObjectKind.Definition,
            _ when string.Equals(segment, AssignmentsSegment, StringComparison.OrdinalIgnoreCase) => PolicyObjectKind.Assignment,
            _ when string.Equals(segment, StatesSegment, StringComparison.OrdinalIgnoreCase) => PolicyObjectKind.State,
            _ => PolicyObjectKind.Unknown
        };

    static string SegmentOf(PolicyObjectKind kind) =>
        kind switch {
            PolicyObjectKind.Definition => DefinitionsSegment,
            PolicyObjectKind.Assignment => AssignmentsSegment,
            PolicyObjectKind.State => StatesSegment,
            _ => ""
        };

    static Result<PolicyAddress> Invalid(string message) =>
        Result<PolicyAddress>.Failure(ErrorCode.InvalidResourceId, message);
}
