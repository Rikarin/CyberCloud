namespace CyberCloud.Core.Resources;

/// <summary>
///     The invitations address — <c>/tenants/{t}/providers/CyberCloud.Identity/invitations</c>, where
///     a tenant owner <c>POST</c>s the address of a colleague to invite. docs/plan/11 § Sign-up and
///     tenant creation, the invited path; issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The third reserved namespace, reserved for the reason the first two are.</b> The
///         shape is <see cref="ResourceGraphAddress" />'s — a tenant scope, then
///         <c>/providers/{namespace}/{type}</c> — so a provider that registered
///         <see cref="ProviderNamespace" /> would have every type it declared shadowed by a route the
///         gateway claims first. <c>ProviderRegistry.Build</c> refuses one, and under this namespace
///         the router asks this grammar and nothing else: a path that names the namespace and is not
///         this address is a <c>400</c> that says what the address is.
///     </para>
///     <para>
///         ⚠ <b>A <c>POST</c> to a collection, not a <c>PUT</c> to a name.</b> An invitation has no
///         name its sender chooses — its id is minted with the secret its link carries, and an id a
///         caller could pick would be an id a caller could collide with — so the verb is the one that
///         creates without an address, and the answer carries the id. It is, with
///         <see cref="ResourceGraphAddress" />, the second <c>POST</c> in this API that is not an
///         action on an existing resource; the router sees both before the action grammar for the
///         reason that type gives.
///     </para>
///     <para>
///         Disjoint from every other grammar for the reason <see cref="ResourceGraphAddress" />
///         gives: five segments with <c>providers</c> third, under a namespace of its own.
///     </para>
/// </remarks>
/// <param name="TenantId">The tenant invited into.</param>
public readonly record struct InvitationAddress(Guid TenantId) {
    /// <summary>
    ///     The provider namespace this address lives under. ⚠ Reserved: no provider may register it.
    ///     The identity module's own name — the directory it adds a member to.
    /// </summary>
    public const string ProviderNamespace = "CyberCloud.Identity";

    /// <summary>The one type segment under it.</summary>
    public const string TypeSegment = "invitations";

    /// <summary>The namespace as it appears in a path, slashes included.</summary>
    public const string NamespaceSegment = "/providers/" + ProviderNamespace + "/";

    /// <summary>Everything after the tenant scope.</summary>
    public const string Suffix = NamespaceSegment + TypeSegment;

    /// <summary>The canonical path.</summary>
    public string Path => ScopeId.Tenant(TenantId).Path + Suffix;

    /// <summary>Whether <paramref name="path" /> names this namespace at all — the router's first question.</summary>
    /// <param name="path">A request path.</param>
    public static bool IsUnderNamespace(string? path) =>
        path is not null && path.Contains(NamespaceSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses the address, or names what it should have been.</summary>
    /// <param name="path">A request path under <see cref="ProviderNamespace" />.</param>
    public static Result<InvitationAddress> ParsePath(string? path) {
        if (string.IsNullOrEmpty(path)
            || path.Length <= Suffix.Length
            || !path.EndsWith(Suffix, StringComparison.OrdinalIgnoreCase)) {
            return Invalid(
                $"'{path}' is not the invitations address. The only address under '{ProviderNamespace}' "
                + $"is '/tenants/{{t}}{Suffix}', and an invitation is a POST to it with "
                + """{ "email": "…" } as the body — docs/plan/11 § Sign-up and tenant creation."""
            );
        }

        var scope = ScopeId.ParsePath(path[..^Suffix.Length]);

        if (scope.IsFailure || scope.GetValueOrThrow().Kind != ScopeKind.Tenant) {
            return Invalid(
                $"'{path}' is not the invitations address: the segments before '{Suffix}' must be a "
                + "tenant, '/tenants/{t}'. A member belongs to the tenant, not to a subscription or a "
                + "group — what they may do there is a role assignment on that scope."
            );
        }

        return Result<InvitationAddress>.Success(new(scope.GetValueOrThrow().TenantId));
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    static Result<InvitationAddress> Invalid(string message) =>
        Result<InvitationAddress>.Failure(ErrorCode.InvalidResourceId, message);
}
