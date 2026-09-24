using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;
using CyberCloud.Tenancy.Contracts;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IApplicationGrain" /> — Entity, Durable, key <c>app/{applicationId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         This is what OpenIddict's application store reads through. ADR-015: "OpenIddict is a
///         library: it handles the protocol, we own the stores, and the stores are grains."
///     </para>
///     <para>
///         ⚠ <b>The <c>client_id</c> index is claimed here, not by the caller.</b> A registration is
///         keyed by its GUID, so nothing maps a <c>client_id</c> back to it without
///         <see cref="IClientIndexGrain" /> — the lookup the authorization-code flow needs and the
///         one ADR-015's degraded mode leaves to us. Claiming it inside create is what makes "two
///         applications cannot share a <c>client_id</c>" a property of the platform rather than a
///         convention, because the single-threaded index activation is the mutex.
///     </para>
///     <para>
///         ⚠ <b>There is no reaper for an orphaned application, so the grain settles itself.</b>
///         docs/plan/06 § Two-phase create sweeps a resource whose silo died between the write and
///         the confirm with a per-subscription reaper reminder. An application belongs to no
///         subscription and has no reminder, so the same window is closed on the next call instead:
///         <see cref="ApplicationGrainState.ClientIdConfirmed" /> records whether the confirm
///         happened, and every entry point runs <c>SettleAsync</c> first, which re-drives the claim
///         while the lease is live and sweeps the registration once another application has taken
///         the <c>client_id</c>. The same principle as the index's own lease — evaluated on read,
///         never by a timer whose silo can die.
///     </para>
/// </remarks>
public sealed class ApplicationGrain(
    [PersistentState("application", StorageTiers.Durable)]
    IPersistentState<ApplicationGrainState> state,
    IGrainFactory grains,
    IClock clock
)
    : Grain, IApplicationGrain {
    Guid applicationId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        applicationId = IdentityGrainKeys.Decode(this, GrainKeyKind.Application).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistration>> CreateAsync(ApplicationRegistration registration) {
        ArgumentNullException.ThrowIfNull(registration);

        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result<ApplicationRegistration>.Failure(unsettled);
        }

        if (state.State.Registration is not null) {
            return Result<ApplicationRegistration>.Failure(
                ErrorCode.Conflict,
                $"Application {applicationId:D} is already registered."
            );
        }

        var validated = Validate(registration);
        if (validated.TryGetError(out var error)) {
            return Result<ApplicationRegistration>.Failure(error);
        }

        var clientId = validated.GetValueOrThrow().ClientId;

        // ⚠ Listed before anything else is claimed, for IDirectoryIndexGrain's reason: a crash past
        // this line leaves an id the listing skips as "not found", never a registration nothing
        // lists. Issue #41.
        var listed = await Tenant()
            .GetGrain<IDirectoryIndexGrain>(GrainKeys.DirectoryIndex(GrainKeys.DirectoryApplications))
            .AddAsync(applicationId);

        if (listed.TryGetError(out var unlisted)) {
            return Result<ApplicationRegistration>.Failure(unlisted);
        }

        // ⚠ CLAIM THE CLIENT ID BEFORE WRITING STATE — docs/plan/06 § Two-phase create's ordering,
        // applied to a client-id index rather than a path index. A registration written first and a
        // claim that then failed would leave an application no lookup can reach; the claim first means
        // a taken client id is a 409 before this grain owns anything. TryClaim is idempotent for the
        // same application id, so a retried create re-claims its own id and succeeds.
        var index = ClientIndex(clientId);

        var claimed = await index.TryClaimAsync(clientId, applicationId);
        if (claimed.TryGetError(out var conflict)) {
            return Result<ApplicationRegistration>.Failure(conflict);
        }

        // ⚠ The ids come from the KEY, not from the body. A registration whose body named a different
        // application would otherwise write one grain's state under another's identity — and the body
        // is caller-supplied on a control-plane endpoint.
        state.State.Registration = validated.GetValueOrThrow() with {
            ApplicationId = applicationId, TenantId = tenantId, CreatedAt = clock.UtcNow, ClientSecretIssuedAt = null
        };
        state.State.ClientIdConfirmed = false;
        state.State.ClientSecretDigest = string.Empty;

        await state.WriteStateAsync();

        // ⚠ A CONFIRM THAT FAILS IS ROLLED BACK HERE AND NOT LEFT FOR SettleAsync. The only ways
        // it fails are the lease having expired or the id being bound elsewhere, and either way the
        // caller is about to be told the create did not happen. A registration left behind would
        // answer their retry with "already registered" — or, worse, be settled into a live one by
        // the next read, after the caller was told it failed. Only a silo dying between the write
        // and this line leaves the orphan, and that is the case SettleAsync is for.
        var confirmed = await index.ConfirmAsync(applicationId);
        if (confirmed.TryGetError(out var confirmError)) {
            state.State.Registration = null;
            await state.WriteStateAsync();
            return Result<ApplicationRegistration>.Failure(confirmError);
        }

        state.State.ClientIdConfirmed = true;
        await state.WriteStateAsync();

        return Result<ApplicationRegistration>.Success(state.State.Registration);
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistration>> GetAsync() {
        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result<ApplicationRegistration>.Failure(unsettled);
        }

        return state.State.Registration is { } registration
            ? Result<ApplicationRegistration>.Success(registration)
            : NotFound<ApplicationRegistration>();
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistration>> UpdateAsync(ApplicationRegistration registration) {
        ArgumentNullException.ThrowIfNull(registration);

        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result<ApplicationRegistration>.Failure(unsettled);
        }

        if (state.State.Registration is not { } existing) {
            return NotFound<ApplicationRegistration>();
        }

        var validated = Validate(registration);
        if (validated.TryGetError(out var error)) {
            return Result<ApplicationRegistration>.Failure(error);
        }

        state.State.Registration = validated.GetValueOrThrow() with {
            ApplicationId = applicationId,
            TenantId = tenantId,
            ClientId = existing.ClientId,
            CreatedAt = existing.CreatedAt,
            ClientSecretIssuedAt = existing.ClientSecretIssuedAt
        };

        await state.WriteStateAsync();
        return Result<ApplicationRegistration>.Success(state.State.Registration);
    }

    /// <inheritdoc />
    public async Task<Result<ApplicationRegistration>> IssueClientSecretAsync(string secret) {
        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result<ApplicationRegistration>.Failure(unsettled);
        }

        if (state.State.Registration is not { } registration) {
            return NotFound<ApplicationRegistration>();
        }

        if (registration.IsPublicClient) {
            return Result<ApplicationRegistration>.Failure(
                ErrorCode.InvalidRequestBody,
                "A public client holds no secret — a secret shipped in a browser or a CLI is public."
            );
        }

        // ⚠ A floor, not a policy: the caller mints 256 bits, and anything this short was typed.
        if (string.IsNullOrEmpty(secret) || secret.Length < 32) {
            return Result<ApplicationRegistration>.Failure(
                ErrorCode.InvalidRequestBody,
                "A client secret is minted by the platform and is at least 32 characters."
            );
        }

        state.State.ClientSecretDigest = CredentialDigest.Sha256(secret);
        state.State.Registration = registration with { ClientSecretIssuedAt = clock.UtcNow };

        await state.WriteStateAsync();
        return Result<ApplicationRegistration>.Success(state.State.Registration);
    }

    /// <inheritdoc />
    public async Task<Result<bool>> VerifyClientSecretAsync(string presented) {
        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result<bool>.Failure(unsettled);
        }

        if (state.State.Registration is null) {
            return NotFound<bool>();
        }

        // ⚠ Digest against digest, in constant time — CredentialDigest.FixedTimeEquals, as the
        // invitation grain compares a link's secret. No digest stored is false, never a match.
        return Result<bool>.Success(
            state.State.ClientSecretDigest.Length > 0
            && !string.IsNullOrEmpty(presented)
            && CredentialDigest.FixedTimeEquals(CredentialDigest.Sha256(presented), state.State.ClientSecretDigest)
        );
    }

    /// <inheritdoc />
    public async Task<Result<bool>> AllowsGrantAsync(GrantType grant) {
        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result<bool>.Failure(unsettled);
        }

        return state.State.Registration is { } registration
            ? Result<bool>.Success(registration.AllowedGrants.Contains(grant))
            : NotFound<bool>();
    }

    /// <inheritdoc />
    public async Task<Result<bool>> IsRegisteredRedirectUriAsync(string redirectUri) {
        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result<bool>.Failure(unsettled);
        }

        if (state.State.Registration is not { } registration) {
            return NotFound<bool>();
        }

        // ⚠ WHOLE-STRING, ORDINAL. Never StartsWith, never a wildcard, never a case-insensitive
        // compare on the path. A prefix match turns `https://app.example.com/cb` into a match for
        // `https://app.example.com/cb.attacker.test`, and the authorization code goes to whoever
        // asked. This one line is the difference between an authorization server and an open
        // redirect with extra steps.
        return Result<bool>.Success(
            registration.RedirectUris.Exists(x => string.Equals(x, redirectUri, StringComparison.Ordinal))
        );
    }

    /// <inheritdoc />
    public async Task<Result> DeleteAsync() {
        var settled = await SettleAsync();
        if (settled.TryGetError(out var unsettled)) {
            return Result.Failure(unsettled);
        }

        if (state.State.Registration is not { } registration) {
            return Result.Failure(ErrorCode.ResourceNotFound, $"Application {applicationId:D} does not exist.");
        }

        // ⚠ Release the index before dropping the state, so the client id is free the moment the
        // registration is gone. Release is idempotent and refuses a mismatched application id, so a
        // re-driven delete cannot hand away a client id the tenant re-registered in the meantime.
        //
        // ⚠ AND A REFUSED RELEASE IS NOT A REFUSED DELETE. The refusal means the index already
        // names another application, so this registration is reachable by its GUID and by nothing
        // else — a delete that failed here would leave it that way for good, holding a client id it
        // does not own. The other application keeps the id; this one's state goes. A registration
        // whose client id no index can carry — written before the index validated one — has nothing
        // to release for the same reason.
        if (ClientIndexOrNull(registration.ClientId) is { } index) {
            var released = await index.ReleaseAsync(applicationId);
            if (released.TryGetError(out var error) && error.Code != ErrorCode.Conflict) {
                return Result.Failure(error);
            }
        }

        state.State.Registration = null;
        state.State.ClientIdConfirmed = false;
        state.State.ClientSecretDigest = string.Empty;
        await state.WriteStateAsync();

        // Last, so a failure here leaves a listed id whose grain answers "not found" — which a
        // listing skips — rather than a live registration nothing lists. Issue #41.
        return await Tenant()
            .GetGrain<IDirectoryIndexGrain>(GrainKeys.DirectoryIndex(GrainKeys.DirectoryApplications))
            .RemoveAsync(applicationId);
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    /// <summary>
    ///     Finishes, or sweeps, a create whose silo died between the write and the confirm.
    /// </summary>
    /// <returns>Success once the state and the index agree, or the index's own refusal.</returns>
    /// <remarks>
    ///     <para>
    ///         A no-op for every settled grain — one with no registration, or one whose
    ///         <see cref="ApplicationGrainState.ClientIdConfirmed" /> is set — so the cost on the
    ///         steady state is one field read. Otherwise the claim is re-driven with this
    ///         application's own id, which <c>IndexClaimMachine</c> makes idempotent: a live lease is
    ///         renewed, a confirmed binding is left alone, an expired one is taken again. Then the
    ///         confirm, and the flag.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>A claim refused because another application holds the id is the sweep.</b> The
    ///         lease expired, somebody else registered the <c>client_id</c>, and this registration
    ///         is what docs/plan/06 § Two-phase create calls the orphan — durable state, no confirmed
    ///         index. Its create never returned success, so dropping it loses nothing anybody was
    ///         told they had; keeping it would be two registrations naming one <c>client_id</c>,
    ///         which is the property the index exists to refuse.
    ///     </para>
    ///     <para>
    ///         ⚠ State written before the flag existed reads as unconfirmed and is claimed on first
    ///         touch. Nothing has shipped — the wire-compatibility gate has no release tag to compare
    ///         against — so there is no such state, and the re-drive is what a backfill would do.
    ///     </para>
    /// </remarks>
    async Task<Result> SettleAsync() {
        if (state.State.Registration is not { } registration || state.State.ClientIdConfirmed) {
            return Result.Success;
        }

        if (ClientIndexOrNull(registration.ClientId) is not { } index) {
            // A client id no index can carry was never claimed, so there is nothing to settle;
            // DeleteAsync is the one call that can do anything with it.
            return Result.Success;
        }

        var claimed = await index.TryClaimAsync(registration.ClientId, applicationId);
        if (claimed.TryGetError(out var refused)) {
            if (refused.Code != ErrorCode.ResourceAlreadyExists) {
                return Result.Failure(refused);
            }

            state.State.Registration = null;
            await state.WriteStateAsync();
            return Result.Success;
        }

        var confirmed = await index.ConfirmAsync(applicationId);
        if (confirmed.TryGetError(out var unconfirmed)) {
            return Result.Failure(unconfirmed);
        }

        state.State.ClientIdConfirmed = true;
        await state.WriteStateAsync();
        return Result.Success;
    }

    /// <summary>
    ///     The client-id index grain for a client id in this application's tenant.
    /// </summary>
    /// <param name="clientId">The <c>client_id</c> being indexed. Must pass <see cref="GrainKeys.EnsureValidClientId" />.</param>
    /// <remarks>
    ///     ⚠ Through <c>ForTenant</c>, like every other grain reference in this module — the index
    ///     lives in tenancy but is tenant-qualified, so it is reached the way
    ///     <c>SignInService</c> reaches <c>IEmailIndexGrain</c> to resolve an address.
    /// </remarks>
    /// <exception cref="ArgumentException">
    ///     <paramref name="clientId" /> is one no key can carry. <see cref="Validate" /> refuses such
    ///     a value before a create reaches here, and <see cref="ClientIndexOrNull" /> is the form for
    ///     a stored value that may predate that check.
    /// </exception>
    IClientIndexGrain ClientIndex(string clientId) =>
        Tenant().GetGrain<IClientIndexGrain>(GrainKeys.ClientIndex(tenantId, clientId));

    TenantGrainFactory Tenant() => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    /// <summary>
    ///     <see cref="ClientIndex" /> for a stored client id, or <c>null</c> when no index can carry
    ///     it — a registration written before the index validated one.
    /// </summary>
    /// <param name="clientId">The <c>client_id</c> as stored.</param>
    IClientIndexGrain? ClientIndexOrNull(string clientId) =>
        GrainKeys.EnsureValidClientId(clientId).IsSuccess ? ClientIndex(clientId) : null;

    static Result<ApplicationRegistration> Validate(ApplicationRegistration registration) {
        // ⚠ THE INDEX'S OWN RULE, APPLIED HERE SO IT IS A 400 AND NOT A CRASH. GrainKeys.ClientIndex
        // throws for a client id a key cannot carry — internal white space, a control character,
        // more than 254 characters — and a throw out of a grain call is an exception at the caller,
        // not a Result. A registration body is caller-supplied, so every refusal in it has to come
        // back as one.
        var clientId = GrainKeys.EnsureValidClientId(registration.ClientId);
        if (clientId.TryGetError(out var invalidClientId)) {
            return Result<ApplicationRegistration>.Failure(invalidClientId);
        }

        foreach (var uri in registration.RedirectUris) {
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)) {
                return Result<ApplicationRegistration>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{uri}' is not an absolute redirect URI. Relative redirect URIs cannot be "
                    + "compared safely, because what they resolve against is the attacker's choice."
                );
            }

            // ⚠ AND `UriKind.Absolute` DOES NOT MEAN WHAT THE LINE ABOVE NEEDS IT TO MEAN ON UNIX.
            // Measured on .NET 10, macOS and Linux: `/callback` parses as `file:///callback` and
            // `//evil.example/x` parses as `file://evil.example/x`, both with TryCreate returning
            // true. On Windows both are refused. So the guard above held on the one platform nobody
            // runs this on and let the second string — the protocol-relative open-redirect payload
            // that ReturnUrl.Sanitize exists to refuse elsewhere in this tree — straight through.
            //
            // A `file:` redirect URI is never a legitimate one: the authorization response has
            // nowhere to go and the browser resolves `//host/path` against the page's own scheme,
            // which is exactly the "resolves against the attacker's choice" the message names. A
            // custom scheme is left alone — `com.example.app:/oauth` is how OAuth 2.1 says a native
            // client registers, and it parses correctly on every platform.
            if (parsed.IsFile) {
                return Result<ApplicationRegistration>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{uri}' has no scheme of its own, so it is a relative or protocol-relative "
                    + "reference that this platform's URI parser turned into a 'file:' URI. What it "
                    + "resolves against is the browser's context and therefore the attacker's choice."
                );
            }

            if (parsed.Fragment.Length > 0) {
                // ⚠ OAuth 2.1 forbids a fragment in a redirect URI, and the reason is mechanical: the
                // authorization response appends its own query or fragment, so a registered fragment
                // makes the comparison and the actual navigation disagree.
                return Result<ApplicationRegistration>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{uri}' carries a fragment. A redirect URI must not — the authorization "
                    + "response appends its own, so the registered value and the value the browser "
                    + "is sent to would differ."
                );
            }
        }

        // ⚠ A scope the identity host doesn't know would be registered here and refused at
        // /authorize, so it is refused here, where the person registering can fix it. Issue #41.
        foreach (var scope in registration.AllowedScopes) {
            if (!ApplicationPolicy.RegistrableScopes.Contains(scope, StringComparer.Ordinal)) {
                return Result<ApplicationRegistration>.Failure(
                    ErrorCode.InvalidRequestBody,
                    $"'{scope}' is not a scope a client can be registered for. The scopes are "
                    + $"{string.Join(", ", ApplicationPolicy.RegistrableScopes)}."
                );
            }
        }

        // ⚠ A public client with a stored secret is a contradiction that is worth failing on rather
        // than resolving: whichever way it is resolved, somebody's threat model is wrong.
        if (registration.IsPublicClient && !registration.ClientSecretRef.IsEmpty) {
            return Result<ApplicationRegistration>.Failure(
                ErrorCode.InvalidRequestBody,
                "A public client cannot hold a client secret — a secret shipped in a browser or a "
                + "CLI is public. Use PKCE, which is what OAuth 2.1 requires here anyway."
            );
        }

        return Result<ApplicationRegistration>.Success(registration);
    }

    Result<T> NotFound<T>()
        where T : notnull =>
        Result<T>.Failure(ErrorCode.ResourceNotFound, $"Application {applicationId:D} does not exist.");
}
