using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;
using CyberCloud.Identity.SignIn;
using CyberCloud.Tenancy.Contracts;
using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Globalization;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IInvitationGrain" /> — Entity, Durable, key <c>invite/{invitationId:N}</c>,
///     tenant-qualified. Issue #43, step 7.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Create is re-drivable and in the order docs/plan/06 § Two-phase create fixes.</b>
///         The email-index claim first, then the user, then the confirmation, then this grain's own
///         record, then the mail — so a crash between two steps leaves a leased address or an
///         invited user nobody was mailed. A call again with the same id and secret resumes it: the
///         claim answers the same entry, <see cref="IUserGrain.CreateAsync" /> is idempotent for the
///         same address, and the message is idempotent on the invitation id. The one refusal is
///         an address already bound to a member who is not merely invited — they are in the tenant
///         already, and a second user for them would be two people with one address.
///     </para>
///     <para>
///         ⚠ <b>Only a caller that kept the id and the secret can resume, and the gateway keeps
///         neither.</b> <c>GrainInvitationIssuer</c> mints both for each <c>POST</c> and returns
///         neither, so an owner's retry after a failed mail is a second invitation onto the same
///         invited user (<see cref="UserForAsync" /> reuses it). The first is left pending, a link
///         nobody received, and it expires in seven days. Two pending links for one user are safe
///         because accepting either one makes the other <see cref="InvitationStatus.Withdrawn" />. A
///         retry that resumes needs the id to be an idempotency key the sender supplies, and that's
///         still owed. Listing and revoking landed with #41, so an owner can now see the stray
///         pending link and revoke it rather than wait seven days.
///     </para>
///     <para>
///         ⚠ <b>Accept spends the link before it touches the user, and gives it back whenever the
///         user grain refused.</b> The other order — password first, then the burn — would let two
///         tabs holding the same link both set a password, the second silently replacing the first.
///         A crash after the burn and before the user call leaves an invited user and a used link,
///         which the sender repairs by inviting again onto the same user; that is the failure worth
///         having, because it is visible and costs one mail. The give-back is safe for every
///         refusal because a refused call changed nothing, and a link the user grain refused for
///         the user's status stays useless: it reads <see cref="InvitationStatus.Withdrawn" />.
///     </para>
///     <para>
///         ⚠ <b>The user's status is read, never assumed.</b> This grain once renamed the user, set
///         the password and made them <see cref="UserStatus.Active" /> as three calls with no check,
///         so a second pending link for a user who had joined through the first — or any link after
///         a suspension or a deprovision — brought the account back, reset its password and signed
///         it in past any second factor enrolled since. The review of #43 proved both with probes.
///         Now the page is told <see cref="InvitationStatus.Withdrawn" /> before anything is spent,
///         and <see cref="IUserGrain.AcceptInvitationAsync" /> checks <see cref="UserStatus.Invited" />
///         in the turn that writes, which is what holds against two links accepted at once.
///         <c>InvitationTests.ALinkCannotBringBackAMemberWhoWasSuspendedOrRemoved</c> pins it.
///     </para>
/// </remarks>
public sealed class InvitationGrain(
    [PersistentState("invitation", StorageTiers.Durable)]
    IPersistentState<InvitationGrainState> state,
    IGrainFactory grains,
    IInvitationDeliverySeam delivery,
    IClock clock,
    ILogger<InvitationGrain> logger
)
    : Grain, IInvitationGrain {
    Guid invitationId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        invitationId = IdentityGrainKeys.Decode(this, GrainKeyKind.Invitation).Id;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<Invitation>> CreateAsync(InvitationRequest request, string secret) {
        ArgumentNullException.ThrowIfNull(request);

        if (string.IsNullOrEmpty(secret) || request.InvitedBy == Guid.Empty) {
            return Result<Invitation>.Failure(
                ErrorCode.InvalidRequestBody,
                "An invitation names who sent it and carries a secret for its link; one of them is empty."
            );
        }

        var normalized = GrainKeys.NormalizeEmail(request.Email);

        if (normalized.TryGetError(out var invalid)) {
            return Result<Invitation>.Failure(invalid);
        }

        var email = normalized.GetValueOrThrow();
        var digest = CredentialDigest.Sha256(secret);

        if (Created) {
            // ⚠ A retry of this very invitation re-sends; anything else under this id is refused
            // rather than turned into a second invitation wearing the first one's key.
            if (!CredentialDigest.FixedTimeEquals(state.State.SecretDigest, digest)
                || !string.Equals(state.State.Email, email, StringComparison.Ordinal)) {
                return Result<Invitation>.Failure(
                    ErrorCode.Conflict,
                    $"Invitation {invitationId:D} already exists for another address or link."
                );
            }

            return await DeliverAsync(secret);
        }

        var userId = await UserForAsync(email);

        if (userId.TryGetError(out var taken)) {
            return Result<Invitation>.Failure(taken);
        }

        // ⚠ Listed before it is written, for IDirectoryIndexGrain's reason. Issue #41.
        var listed = await Tenant()
            .GetGrain<IDirectoryIndexGrain>(GrainKeys.DirectoryIndex(GrainKeys.DirectoryInvitations))
            .AddAsync(invitationId);

        if (listed.TryGetError(out var unlisted)) {
            return Result<Invitation>.Failure(unlisted);
        }

        var now = clock.UtcNow;

        state.State = new() {
            SecretDigest = digest,
            UserId = userId.GetValueOrThrow(),
            Email = email,
            ExpiresAt = now + InvitationPolicy.Lifetime,
            Status = InvitationStatus.Pending,
            InvitedBy = request.InvitedBy,
            TenantName = request.TenantName,
            CreatedAt = now,
            Sendings = 1,
            SentAt = now
        };

        await state.WriteStateAsync();

        return await DeliverAsync(secret);
    }

    /// <inheritdoc />
    public async Task<Result<Invitation>> ResendAsync(string secret) {
        if (!Created) {
            return Result<Invitation>.Failure(ErrorCode.ResourceNotFound, $"Invitation {invitationId:D} does not exist.");
        }

        if (string.IsNullOrEmpty(secret)) {
            return Result<Invitation>.Failure(ErrorCode.InvalidRequestBody, "A resent invitation carries a new secret for its link.");
        }

        var status = (await ViewAsync()).Status;

        if (status is not (InvitationStatus.Pending or InvitationStatus.Expired)) {
            return Result<Invitation>.Failure(
                ErrorCode.Conflict,
                $"Invitation {invitationId:D} is {status.ToString().ToLowerInvariant()}, so nobody is waiting "
                + "for its link. Invite the address again if they should still join."
            );
        }

        if (state.State.Sendings >= InvitationPolicy.MaxSendings) {
            return Result<Invitation>.Failure(
                ErrorCode.QuotaExceeded,
                $"Invitation {invitationId:D} has been sent {state.State.Sendings} times, the most one "
                + "invitation is sent. If the mail isn't arriving, check the address; revoke this one "
                + "and invite again to start over."
            );
        }

        // ⚠ The new link is in force before the mail goes — IInvitationGrain.ResendAsync's remarks.
        var now = clock.UtcNow;

        state.State.SecretDigest = CredentialDigest.Sha256(secret);
        state.State.ExpiresAt = now + InvitationPolicy.Lifetime;
        state.State.Status = InvitationStatus.Pending;
        state.State.Sendings++;
        state.State.SentAt = now;

        await state.WriteStateAsync();

        IdentityLog.InvitationResent(logger, tenantId, invitationId, state.State.Sendings);

        return await DeliverAsync(secret);
    }

    /// <inheritdoc />
    public async Task<Result<Invitation>> RevokeAsync() {
        if (!Created) {
            return Result<Invitation>.Failure(ErrorCode.ResourceNotFound, $"Invitation {invitationId:D} does not exist.");
        }

        switch (state.State.Status) {
            case InvitationStatus.Revoked:
                return Result<Invitation>.Success(Snapshot());
            case InvitationStatus.Accepted:
                return Result<Invitation>.Failure(
                    ErrorCode.Conflict,
                    $"Invitation {invitationId:D} was accepted: the person is a member now. Removing a "
                    + "member is DELETE on the member, not on the invitation."
                );
        }

        state.State.Status = InvitationStatus.Revoked;
        await state.WriteStateAsync();

        IdentityLog.InvitationRevoked(logger, tenantId, invitationId);

        return Result<Invitation>.Success(Snapshot());
    }

    /// <inheritdoc />
    public async Task<Result<Invitation>> DescribeAsync(string secret) =>
        Matches(secret) ? Result<Invitation>.Success(await ViewAsync()) : NotFound();

    /// <inheritdoc />
    public Task<Result<Invitation>> AcceptAsync(string secret, string displayName, string password) =>
        SpendAsync(
            secret,
            string.IsNullOrWhiteSpace(displayName) || string.IsNullOrEmpty(password) ? "Choose a name and a password." : null,
            user => user.AcceptInvitationAsync(displayName, password)
        );

    /// <inheritdoc />
    public Task<Result<Invitation>> AcceptWithHomeAccountAsync(string secret, string displayName, HomeAccount home) =>
        SpendAsync(
            secret,
            string.IsNullOrWhiteSpace(displayName) || home is null ? "The account to join with has no name." : null,
            user => user.JoinWithHomeAccountAsync(displayName, home!)
        );

    /// <summary>
    ///     Both ways of accepting: checks the link, spends it, hands the user grain its half, and
    ///     gives the link back when the user grain refused.
    /// </summary>
    /// <param name="secret">The secret from the link.</param>
    /// <param name="invalid">What is wrong with the person's input, or <see langword="null" />. Asked after the link's status, so a used link says so first.</param>
    /// <param name="join">The user grain call that makes the member.</param>
    async Task<Result<Invitation>> SpendAsync(
        string secret,
        string? invalid,
        Func<IUserGrain, Task<Result<UserProfile>>> join
    ) {
        if (!Matches(secret)) {
            return NotFound();
        }

        switch ((await ViewAsync()).Status) {
            case InvitationStatus.Accepted:
                return Result<Invitation>.Failure(
                    ErrorCode.Conflict,
                    "This invitation has already been used. Sign in instead, or ask for a new invitation."
                );
            case InvitationStatus.Expired:
                return Result<Invitation>.Failure(
                    ErrorCode.PreconditionFailed,
                    "This invitation has expired. Ask whoever sent it for a new one."
                );
            case InvitationStatus.Withdrawn:
            case InvitationStatus.Revoked:
                return Withdrawn();
        }

        if (invalid is not null) {
            return Result<Invitation>.Failure(ErrorCode.InvalidRequestBody, invalid);
        }

        // ── Spend the link first — see the type's remarks. ─────────────────────────────────────
        state.State.Status = InvitationStatus.Accepted;
        state.State.AcceptedAt = clock.UtcNow;
        await state.WriteStateAsync();

        var accepted = await join(Tenant().GetGrain<IUserGrain>(GrainKeys.User(state.State.UserId)));

        if (accepted.TryGetError(out var refused)) {
            // ⚠ The give-back: the user grain changed nothing, so the link is what it was before —
            // the person's to retry after a refused password, or withdrawn if the user stopped
            // being invited between the check above and this call.
            state.State.Status = InvitationStatus.Pending;
            state.State.AcceptedAt = null;
            await state.WriteStateAsync();

            if (refused.Code == ErrorCode.InvalidRequestBody) {
                return Result<Invitation>.Failure(ErrorCode.InvalidRequestBody, refused.Message);
            }

            return refused.Code == ErrorCode.PreconditionFailed ? Withdrawn() : Result<Invitation>.Failure(refused);
        }

        IdentityLog.InvitationAccepted(logger, tenantId, invitationId, state.State.UserId);

        return Result<Invitation>.Success(Snapshot());
    }

    /// <inheritdoc />
    public async Task<Result<Invitation>> GetAsync() =>
        Created ? Result<Invitation>.Success(await ViewAsync()) : NotFound();

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();

        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    bool Created => state.State.SecretDigest.Length > 0;

    bool Matches(string? secret) =>
        Created
        && !string.IsNullOrEmpty(secret)
        && CredentialDigest.FixedTimeEquals(CredentialDigest.Sha256(secret), state.State.SecretDigest);

    /// <summary>
    ///     The invited user for <paramref name="email" />: a fresh one claimed through the index, the
    ///     one a previous invitation left <see cref="UserStatus.Invited" />, or a conflict for a member.
    /// </summary>
    async Task<Result<Guid>> UserForAsync(string email) {
        var tenant = Tenant();
        var index = tenant.GetGrain<IEmailIndexGrain>(GrainKeys.EmailIndex(tenantId, email));
        var entry = await index.GetAsync();

        if (entry.IsSuccess && entry.GetValueOrThrow().State == IndexEntryState.Confirmed) {
            var existing = entry.GetValueOrThrow().BoundTo;
            var profile = await tenant.GetGrain<IUserGrain>(GrainKeys.User(existing)).GetAsync();

            return profile.IsSuccess && profile.GetValueOrThrow().Status == UserStatus.Invited
                ? Result<Guid>.Success(existing)
                : Result<Guid>.Failure(
                    ErrorCode.Conflict,
                    "That address already belongs to a member of this organisation."
                );
        }

        var userId = entry.IsSuccess && entry.GetValueOrThrow().State == IndexEntryState.Claimed
            ? entry.GetValueOrThrow().BoundTo
            : Guid.NewGuid();

        // docs/plan/06 § Two-phase create: the claim, the user, the confirmation.
        var claimed = await index.TryClaimAsync(email, userId);

        if (claimed.TryGetError(out var conflict)) {
            return Result<Guid>.Failure(conflict);
        }

        var created = await tenant.GetGrain<IUserGrain>(GrainKeys.User(userId))
            .CreateAsync(email, string.Empty, UserStatus.Invited);

        if (created.TryGetError(out var uncreated)) {
            return Result<Guid>.Failure(uncreated);
        }

        var confirmed = await index.ConfirmAsync(userId);

        return confirmed.TryGetError(out var unconfirmed)
            ? Result<Guid>.Failure(unconfirmed)
            : Result<Guid>.Success(userId);
    }

    async Task<Result<Invitation>> DeliverAsync(string secret) {
        var sent = await delivery.DeliverAsync(
            new() {
                TenantId = tenantId,
                InvitationId = invitationId,
                Email = state.State.Email,
                TenantName = state.State.TenantName,
                Secret = secret,
                ExpiresAt = state.State.ExpiresAt,
                Sending = Math.Max(1, state.State.Sendings)
            }
        );

        if (sent.TryGetError(out var undelivered)) {
            // ⚠ The record stays: the sender's retry with the same id and secret re-sends, which is
            // the repair for a relay that was down.
            IdentityLog.InvitationNotDelivered(logger, tenantId, invitationId, undelivered.Message);

            return Result<Invitation>.Failure(undelivered);
        }

        IdentityLog.InvitationSent(logger, tenantId, invitationId, state.State.InvitedBy);

        return Result<Invitation>.Success(Snapshot());
    }

    Invitation Snapshot() =>
        new() {
            InvitationId = invitationId,
            TenantId = tenantId,
            UserId = state.State.UserId,
            Email = state.State.Email,
            ExpiresAt = state.State.ExpiresAt,
            InvitedBy = state.State.InvitedBy,
            TenantName = state.State.TenantName,
            AcceptedAt = state.State.AcceptedAt,
            SentAt = state.State.SentAt == default ? state.State.CreatedAt : state.State.SentAt,
            Sendings = Math.Max(1, state.State.Sendings),
            Status = state.State.Status == InvitationStatus.Pending && clock.UtcNow >= state.State.ExpiresAt
                ? InvitationStatus.Expired
                : state.State.Status
        };

    /// <summary>
    ///     <see cref="Snapshot" />, with an unused invitation — pending or expired — whose user is no
    ///     longer <see cref="UserStatus.Invited" /> read as <see cref="InvitationStatus.Withdrawn" />.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         ⚠ Read from the user on every call rather than stored here: the status is the user
    ///         grain's, and a copy would go stale the moment an administrator suspended somebody
    ///         without knowing an invitation existed.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Expired is asked too, and Withdrawn wins over it.</b> The first cut asked only
    ///         for Pending, so an invitation that had expired read <c>expired</c> after its user was
    ///         removed or joined through another link, and <see cref="ResendAsync" /> — which resends
    ///         an expired one on purpose — mailed a fresh link to a removed member. The link opened
    ///         nothing, because <see cref="IUserGrain.AcceptInvitationAsync" /> checks Invited, but the
    ///         mail went. #41's review found it by reading, and
    ///         <c>InvitationExpiryTests.AnExpiredInvitationWhoseUserIsNoLongerInvitedIsWithdrawnAndNotResent</c>
    ///         pins it.
    ///     </para>
    /// </remarks>
    async Task<Invitation> ViewAsync() {
        var snapshot = Snapshot();

        if (snapshot.Status is not (InvitationStatus.Pending or InvitationStatus.Expired)) {
            return snapshot;
        }

        var user = await Tenant().GetGrain<IUserGrain>(GrainKeys.User(state.State.UserId)).GetAsync();

        return user.IsSuccess && user.GetValueOrThrow().Status == UserStatus.Invited
            ? snapshot
            : snapshot with { Status = InvitationStatus.Withdrawn };
    }

    static Result<Invitation> Withdrawn() =>
        Result<Invitation>.Failure(
            ErrorCode.Conflict,
            "This invitation can no longer be used. If you have joined already, sign in; otherwise ask "
            + "whoever sent it."
        );

    TenantGrainFactory Tenant() => grains.ForTenant(tenantId.ToString("D", CultureInfo.InvariantCulture));

    static Result<Invitation> NotFound() =>
        Result<Invitation>.Failure(ErrorCode.ResourceNotFound, "That invitation link is not valid.");
}
