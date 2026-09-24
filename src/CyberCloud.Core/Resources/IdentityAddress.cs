namespace CyberCloud.Core.Resources;

/// <summary>What an <see cref="IdentityAddress" /> names.</summary>
public enum IdentityAddressKind {
    /// <summary><c>default(IdentityAddress)</c>. Not an address.</summary>
    None = 0,

    /// <summary><c>invitations</c> — <c>GET</c> lists them, <c>POST</c> sends one (#43).</summary>
    Invitations,

    /// <summary><c>invitations/{id:N}</c> — <c>DELETE</c> revokes it.</summary>
    Invitation,

    /// <summary><c>invitations/{id:N}/resend</c> — <c>POST</c> mails it again under a new link.</summary>
    InvitationResend,

    /// <summary><c>members</c> — <c>GET</c> lists the tenant's users.</summary>
    Members,

    /// <summary><c>members/{id:N}</c> — <c>DELETE</c> removes one.</summary>
    Member,

    /// <summary><c>applications</c> — <c>GET</c> lists the registered clients, <c>POST</c> registers one.</summary>
    Applications,

    /// <summary><c>applications/{id:N}</c> — <c>GET</c> reads one, <c>DELETE</c> deletes it.</summary>
    Application,

    /// <summary><c>applications/{id:N}/rotateSecret</c> — <c>POST</c> issues a new secret.</summary>
    ApplicationSecret,

    /// <summary><c>sessions</c> — <c>GET</c> lists the caller's own sessions.</summary>
    Sessions,

    /// <summary><c>sessions/{id:N}</c> — <c>DELETE</c> signs one of them out.</summary>
    Session
}

/// <summary>
///     An address under <c>/tenants/{t}/providers/CyberCloud.Identity/</c> — the invitations of
///     issue #43 and the rest of the identity administration API of issue #41: the tenant's members,
///     its registered OAuth clients, and the caller's own sessions. docs/plan/11 § The object model;
///     docs/plan/20 § The pages that are not generated.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The third reserved namespace, reserved for the reason the first two are.</b> The
///         shape is <see cref="ResourceGraphAddress" />'s — a tenant scope, then
///         <c>/providers/{namespace}/…</c> — so a provider that registered
///         <see cref="ProviderNamespace" /> would have every type it declared shadowed by a route the
///         gateway claims first. <c>ProviderRegistry.Build</c> refuses one, and under this namespace
///         the router asks this grammar and nothing else: a path that names the namespace and is
///         none of these addresses is a <c>400</c> that lists them.
///     </para>
///     <para>
///         ⚠ <b>Ids are the <c>N</c> form, and nothing else is accepted.</b> A member's id is the
///         principal id a role assignment names (<c>reader-user-{id:N}</c>) and the <c>sub</c> of
///         their token, so the address spells it the same way; a <c>D</c>-form id is a <c>400</c>,
///         not a second spelling of one member.
///     </para>
///     <para>
///         ⚠ <b>Two verbs are <c>POST</c>s on a sub-path, not actions.</b> <c>…/resend</c> and
///         <c>…/rotateSecret</c> change something that has no body of its own and return what the
///         change made — a link the invitee now holds, a secret the owner is shown once — so they
///         are the <c>POST</c> Azure's <c>addPassword</c> is. They are parsed here, before the
///         gateway's action grammar sees the path, which would read them as actions on a resource
///         of a type no provider serves.
///     </para>
///     <para>
///         ⚠ <b>The sessions are the caller's own, so the address names no user.</b>
///         <c>/tenants/{t}/…/sessions</c> is "mine", resolved from the token's subject; an address
///         that named a user would be a second way to ask for somebody else's list, and a check to
///         get wrong. Disjoint from every other grammar for the reason
///         <see cref="ResourceGraphAddress" /> gives: <c>providers</c> third, under a namespace of its
///         own.
///     </para>
/// </remarks>
/// <param name="TenantId">The tenant the address is in.</param>
/// <param name="Kind">What it names.</param>
/// <param name="Id">The item's id, for the item kinds; <see cref="Guid.Empty" /> for a collection.</param>
public readonly record struct IdentityAddress(Guid TenantId, IdentityAddressKind Kind, Guid Id = default) {
    /// <summary>
    ///     The provider namespace these addresses live under. ⚠ Reserved: no provider may register it.
    ///     The identity module's own name.
    /// </summary>
    public const string ProviderNamespace = "CyberCloud.Identity";

    /// <summary>The namespace as it appears in a path, slashes included.</summary>
    public const string NamespaceSegment = "/providers/" + ProviderNamespace + "/";

    /// <summary>The invitations collection's segment — the address #43 served first.</summary>
    public const string InvitationsSegment = "invitations";

    /// <summary>The members collection's segment.</summary>
    public const string MembersSegment = "members";

    /// <summary>The applications collection's segment.</summary>
    public const string ApplicationsSegment = "applications";

    /// <summary>The sessions collection's segment.</summary>
    public const string SessionsSegment = "sessions";

    /// <summary>The verb segment that resends an invitation.</summary>
    public const string ResendSegment = "resend";

    /// <summary>The verb segment that issues an application a new secret.</summary>
    public const string RotateSecretSegment = "rotateSecret";

    /// <summary>The invitations collection of <paramref name="tenantId" />.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static IdentityAddress Invitations(Guid tenantId) => new(tenantId, IdentityAddressKind.Invitations);

    /// <summary>The members collection of <paramref name="tenantId" />.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static IdentityAddress Members(Guid tenantId) => new(tenantId, IdentityAddressKind.Members);

    /// <summary>The applications collection of <paramref name="tenantId" />.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static IdentityAddress Applications(Guid tenantId) => new(tenantId, IdentityAddressKind.Applications);

    /// <summary>The caller's sessions in <paramref name="tenantId" />.</summary>
    /// <param name="tenantId">The tenant.</param>
    public static IdentityAddress Sessions(Guid tenantId) => new(tenantId, IdentityAddressKind.Sessions);

    /// <summary>The canonical path.</summary>
    public string Path {
        get {
            var prefix = ScopeId.Tenant(TenantId).Path + NamespaceSegment;
            var id = Id.ToString("N", System.Globalization.CultureInfo.InvariantCulture);

            return Kind switch {
                IdentityAddressKind.Invitations => prefix + InvitationsSegment,
                IdentityAddressKind.Invitation => prefix + InvitationsSegment + "/" + id,
                IdentityAddressKind.InvitationResend => prefix + InvitationsSegment + "/" + id + "/" + ResendSegment,
                IdentityAddressKind.Members => prefix + MembersSegment,
                IdentityAddressKind.Member => prefix + MembersSegment + "/" + id,
                IdentityAddressKind.Applications => prefix + ApplicationsSegment,
                IdentityAddressKind.Application => prefix + ApplicationsSegment + "/" + id,
                IdentityAddressKind.ApplicationSecret => prefix + ApplicationsSegment + "/" + id + "/" + RotateSecretSegment,
                IdentityAddressKind.Sessions => prefix + SessionsSegment,
                IdentityAddressKind.Session => prefix + SessionsSegment + "/" + id,
                _ => string.Empty
            };
        }
    }

    /// <summary>The address of one item in this collection.</summary>
    /// <param name="id">The item.</param>
    /// <exception cref="InvalidOperationException">This address is not a collection with items.</exception>
    public IdentityAddress Item(Guid id) =>
        Kind switch {
            IdentityAddressKind.Invitations => new(TenantId, IdentityAddressKind.Invitation, id),
            IdentityAddressKind.Members => new(TenantId, IdentityAddressKind.Member, id),
            IdentityAddressKind.Applications => new(TenantId, IdentityAddressKind.Application, id),
            IdentityAddressKind.Sessions => new(TenantId, IdentityAddressKind.Session, id),
            _ => throw new InvalidOperationException($"A {Kind} address has no items.")
        };

    /// <summary>
    ///     The type segment an item's <c>type</c> property carries — <c>CyberCloud.Identity/members</c>
    ///     and so on.
    /// </summary>
    public string ItemType =>
        ProviderNamespace
        + "/"
        + (Kind switch {
            IdentityAddressKind.Invitations or IdentityAddressKind.Invitation or IdentityAddressKind.InvitationResend =>
                InvitationsSegment,
            IdentityAddressKind.Members or IdentityAddressKind.Member => MembersSegment,
            IdentityAddressKind.Applications or IdentityAddressKind.Application or IdentityAddressKind.ApplicationSecret =>
                ApplicationsSegment,
            IdentityAddressKind.Sessions or IdentityAddressKind.Session => SessionsSegment,
            _ => string.Empty
        });

    /// <summary>Whether <paramref name="path" /> names this namespace at all — the router's first question.</summary>
    /// <param name="path">A request path.</param>
    public static bool IsUnderNamespace(string? path) =>
        path is not null && path.Contains(NamespaceSegment, StringComparison.OrdinalIgnoreCase);

    /// <summary>Parses an address, or names every address there is.</summary>
    /// <param name="path">A request path under <see cref="ProviderNamespace" />.</param>
    public static Result<IdentityAddress> ParsePath(string? path) {
        var at = path?.IndexOf(NamespaceSegment, StringComparison.OrdinalIgnoreCase) ?? -1;

        if (path is null || at <= 0) {
            return Invalid(path);
        }

        var scope = ScopeId.ParsePath(path[..at]);

        if (scope.IsFailure || scope.GetValueOrThrow().Kind != ScopeKind.Tenant) {
            return Result<IdentityAddress>.Failure(
                ErrorCode.InvalidResourceId,
                $"'{path}' is not an identity address: the segments before '{NamespaceSegment}' must be a "
                + "tenant, '/tenants/{t}'. A member, an invitation and an application belong to the "
                + "tenant, not to a subscription or a group — what a member may do there is a role "
                + "assignment on that scope."
            );
        }

        var tenantId = scope.GetValueOrThrow().TenantId;
        var segments = path[(at + NamespaceSegment.Length)..].Split('/');

        // ⚠ Ordinal, like every other literal the router matches: `Members` is not `members`, and a
        // second spelling of an address is a second cache key and a second audit row.
        var collection = segments[0] switch {
            InvitationsSegment => IdentityAddressKind.Invitations,
            MembersSegment => IdentityAddressKind.Members,
            ApplicationsSegment => IdentityAddressKind.Applications,
            SessionsSegment => IdentityAddressKind.Sessions,
            _ => IdentityAddressKind.None
        };

        if (collection == IdentityAddressKind.None) {
            return Invalid(path);
        }

        if (segments.Length == 1) {
            return Result<IdentityAddress>.Success(new(tenantId, collection));
        }

        // ⚠ The N form exactly as it formats — lower case too. GuidFormat.TryParseN takes either case,
        // and an upper-case id would be a second spelling of one member.
        if (segments.Length > 3
            || !GuidFormat.TryParseN(segments[1], out var id)
            || !string.Equals(segments[1], id.ToString("N", System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal)) {
            return Invalid(path);
        }

        var item = new IdentityAddress(tenantId, collection).Item(id);

        if (segments.Length == 2) {
            return Result<IdentityAddress>.Success(item);
        }

        return (collection, segments[2]) switch {
            (IdentityAddressKind.Invitations, ResendSegment) =>
                Result<IdentityAddress>.Success(new(tenantId, IdentityAddressKind.InvitationResend, id)),
            (IdentityAddressKind.Applications, RotateSecretSegment) =>
                Result<IdentityAddress>.Success(new(tenantId, IdentityAddressKind.ApplicationSecret, id)),
            _ => Invalid(path)
        };
    }

    /// <inheritdoc />
    public override string ToString() => Path;

    static Result<IdentityAddress> Invalid(string? path) =>
        Result<IdentityAddress>.Failure(
            ErrorCode.InvalidResourceId,
            $"'{path}' is not an identity address. Under '/tenants/{{t}}{NamespaceSegment}' the addresses are "
            + $"'{InvitationsSegment}', '{InvitationsSegment}/{{id}}', '{InvitationsSegment}/{{id}}/{ResendSegment}', "
            + $"'{MembersSegment}', '{MembersSegment}/{{id}}', '{ApplicationsSegment}', '{ApplicationsSegment}/{{id}}', "
            + $"'{ApplicationsSegment}/{{id}}/{RotateSecretSegment}', '{SessionsSegment}' and '{SessionsSegment}/{{id}}', "
            + "every id the 32-digit lower-case 'N' form of a GUID — docs/plan/11 § The object model."
        );
}
