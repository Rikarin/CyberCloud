using CyberCloud.Core;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     The ids of one kind of directory object in a tenant — its users, its invitations or its
///     applications — so the identity administration pages have something to list. Issue #41.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Index · <b>Tier</b> Durable · <b>Key</b> <c>idx/dir/{collection}</c>,
///         tenant-qualified. Build it with <c>GrainKeys.DirectoryIndex</c>.
///     </para>
///     <para>
///         ⚠ <b>Every directory object is keyed by a random id, and this is the only thing that
///         enumerates them.</b> The email index answers "whose is this address", the client index
///         "whose is this <c>client_id</c>", and neither can answer "who is here". Without this
///         grain, listing a tenant's members is a scan of the durable store.
///     </para>
///     <para>
///         ⚠ <b>Written by the object's own grain, before the object exists.</b>
///         <c>UserGrain.CreateAsync</c>, <c>InvitationGrain.CreateAsync</c> and
///         <c>ApplicationGrain.CreateAsync</c> each add their id here first — the claim-first order
///         of docs/plan/06 § Two-phase create, where the index is written before the thing it
///         points at. So a crash between the two leaves an id whose grain answers "not found", and
///         a reader skips it. The other order would leave an object nothing can list, which is the
///         failure this grain exists to prevent. Every path that creates a user — sign-up, an
///         invitation, a test fixture — goes through the user grain, so none of them can forget.
///     </para>
///     <para>
///         ⚠ <b>An id stays after its object is gone, except an application's.</b> A user is never
///         deleted, only <c>Deprovisioned</c>, and the listing shows that status. An invitation is
///         kept for its history. A deleted application has no record to show, so
///         <c>ApplicationGrain.DeleteAsync</c> removes its id. Each list is capped at
///         <see cref="DirectoryIndexPolicy.MaxEntries" />, and the cap refuses rather than drops.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IDirectoryIndexGrain")]
public interface IDirectoryIndexGrain : IGrainWithStringKey {
    /// <summary>Records an id. A repeat changes nothing.</summary>
    /// <param name="id">The object's GUID — the id its own grain key carries.</param>
    /// <returns>
    ///     Success; <see cref="ErrorCode.QuotaExceeded" /> when the list already holds
    ///     <see cref="DirectoryIndexPolicy.MaxEntries" /> ids.
    /// </returns>
    Task<Result> AddAsync(Guid id);

    /// <summary>Forgets an id. Forgetting one that isn't here changes nothing.</summary>
    /// <param name="id">The object's GUID.</param>
    Task<Result> RemoveAsync(Guid id);

    /// <summary>Every id, oldest first.</summary>
    Task<Result<IReadOnlyList<Guid>>> ListAsync();

    /// <summary>Drops this activation.</summary>
    Task DeactivateAsync();
}

/// <summary>The numbers a directory index lives by.</summary>
public static class DirectoryIndexPolicy {
    /// <summary>
    ///     How many ids one list holds. Past it, <see cref="IDirectoryIndexGrain.AddAsync" /> refuses
    ///     and the create it serves fails.
    /// </summary>
    /// <remarks>
    ///     ⚠ Refused rather than trimmed. The user grain's session list drops its oldest id, because
    ///     a dropped session costs one sign-out. A dropped member would be a person no administrator
    ///     can see or remove. Ten thousand is far past the directory sizes M2 plans for; lifting it
    ///     is a paged store, not a bigger number.
    /// </remarks>
    public const int MaxEntries = 10_000;
}

/// <summary>
///     What a tenant-registered OAuth client may be — the closed sets the registration grain
///     checks a registration against. docs/plan/11 § Protocol. Issue #41.
/// </summary>
public static class ApplicationPolicy {
    /// <summary>
    ///     The scopes a tenant client may ask for: the four the identity host registers with
    ///     OpenIddict, which are also the four the first-party clients hold.
    /// </summary>
    /// <remarks>
    ///     ⚠ A scope outside this set would be registered and then refused at <c>/authorize</c> as
    ///     one the server doesn't know, so the registration refuses it first. The identity host's
    ///     <c>IdentityHostOpenIddict</c> registers the same four, and a fifth is an edit to both.
    /// </remarks>
    public static IReadOnlyList<string> RegistrableScopes { get; } = ["openid", "profile", "offline_access", "cyc.api"];

    /// <summary>
    ///     The grants a registration made through the administration API gets: the code grant and
    ///     the refresh grant, which is the one interactive flow docs/plan/11 § Protocol allows.
    /// </summary>
    public static IReadOnlyList<GrantType> InteractiveGrants { get; } = [GrantType.AuthorizationCode, GrantType.RefreshToken];

    /// <summary>The most redirect URIs one registration may carry.</summary>
    public const int MaxRedirectUris = 20;

    /// <summary>The longest display name a registration may carry — what the consent page prints.</summary>
    public const int MaxDisplayNameLength = 100;
}
