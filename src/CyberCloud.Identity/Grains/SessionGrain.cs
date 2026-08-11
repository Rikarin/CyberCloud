using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;
using CyberCloud.Identity.Credentials;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="ISessionGrain" /> — Entity, <b>Hot</b>, key <c>session/{sessionId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The only identity grain bound to the hot tier</b>, and therefore the only one not on
///         <c>durable-grains.txt</c>. docs/plan/05 § Hot lists sessions among what that tier holds:
///         losing one costs a sign-in, which is a warm-up rather than a loss. The other direction is
///         the argument that matters — sessions are the highest-write-rate object in this module
///         (every refresh is a write), and putting them on a synchronously-replicated PostgreSQL
///         shard would make the refresh endpoint the slowest thing in the platform.
///     </para>
///     <para>
///         ⚠ <b>The reuse rule is the reason this grain is interesting.</b> See
///         <see cref="RefreshAsync" />.
///     </para>
/// </remarks>
public sealed class SessionGrain(
    [PersistentState("session", StorageTiers.Hot)] IPersistentState<SessionGrainState> state,
    IClock clock
)
    : Grain, ISessionGrain {
    /// <summary>
    ///     How many retired handle digests the chain remembers.
    /// </summary>
    /// <remarks>
    ///     ⚠ Bounded, and the bound is a real trade-off rather than tidiness. Remembering every
    ///     generation forever would make a fourteen-day session's state grow with its refresh count;
    ///     remembering none would make reuse undetectable. Sixty-four covers well over a day of
    ///     ten-minute access tokens, which is far longer than a stolen handle stays useful — the
    ///     legitimate client rotates within minutes, and after that the attacker's copy is retired
    ///     and inside this window.
    /// </remarks>
    public const int MaxRetainedGenerations = 64;

    /// <summary>How much entropy a refresh handle carries. 256 bits.</summary>
    public const int HandleBytes = 32;

    Guid sessionId;
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = IdentityGrainKeys.TenantOf(this);
        sessionId = IdentityGrainKeys.Decode(this, GrainKeyKind.Session).Id;
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<RefreshRotation>> OpenAsync(
        Guid userId,
        string clientId,
        string deviceLabel,
        string clientAddressDigest,
        IReadOnlyList<AuthenticationMethod> methods
    ) {
        ArgumentNullException.ThrowIfNull(methods);

        if (state.State.UserId != Guid.Empty) {
            return Result<RefreshRotation>.Failure(
                ErrorCode.Conflict,
                $"Session {sessionId:D} is already open. Opening it again would mint a second chain "
                + "for one session, and the older chain would then be unrevokable."
            );
        }

        var now = clock.UtcNow;

        state.State.UserId = userId;
        state.State.ClientId = clientId ?? string.Empty;
        state.State.DeviceLabel = deviceLabel ?? string.Empty;
        state.State.ClientAddressDigest = clientAddressDigest ?? string.Empty;
        state.State.CreatedAt = now;
        state.State.AuthenticatedAt = now;
        state.State.LastRefreshedAt = now;
        state.State.Methods = [.. methods];
        state.State.IsLive = true;
        state.State.RevokedBecause = RevocationReason.None;

        var rotation = Mint(now);
        await state.WriteStateAsync();

        return Result<RefreshRotation>.Success(rotation);
    }

    /// <inheritdoc />
    public async Task<Result<RefreshRotation>> RefreshAsync(string presented) {
        if (state.State.UserId == Guid.Empty) {
            return Rejected();
        }

        var digest = CredentialDigest.Sha256(presented ?? string.Empty);

        // ── Case 2 first: the replay. ──────────────────────────────────────────────────────────
        //
        // ⚠ THIS IS THE RULE docs/plan/11 § Protocol ASKS FOR — "rotating, one-time-use, with reuse
        // detection → revoke the whole chain" — AND IT IS CHECKED BEFORE THE HAPPY PATH.
        //
        // Order matters. If the current-handle check ran first, a chain that had somehow retired and
        // re-minted the same digest would rotate instead of alarming. More importantly, checking the
        // retired set first means a replay is detected even when the session has already been
        // revoked for some other reason, which is exactly when an attacker's copy shows up.
        //
        // Why the whole chain and not the request: the replayed handle and the live handle are
        // indistinguishable by who sent them. Under the innocent reading (a client retried) the user
        // signs in again. Under the hostile reading (a handle was stolen) revoking only the replayed
        // generation would leave the thief holding a working session — the one outcome that must not
        // be possible.
        var retired = state.State.RetiredHandleDigests
            .Exists(x => CredentialDigest.FixedTimeEquals(x, digest));

        if (retired) {
            await RevokeInternalAsync(RevocationReason.RefreshReuseDetected);

            return Result<RefreshRotation>.Failure(
                ErrorCode.AuthorizationFailed,
                "That refresh token was already used. The session and every token in its chain have "
                + "been revoked; sign in again."
            );
        }

        // ── Case 3: never issued by this chain. ────────────────────────────────────────────────
        if (!state.State.IsLive
            || !CredentialDigest.FixedTimeEquals(state.State.CurrentHandleDigest, digest)) {
            return Rejected();
        }

        var now = clock.UtcNow;

        if (now >= state.State.RefreshExpiresAt || now >= state.State.CreatedAt + AccessTokenPolicy.AbsoluteSessionLifetime) {
            await RevokeInternalAsync(RevocationReason.Expired);
            return Rejected();
        }

        // ── Case 1: rotate. ───────────────────────────────────────────────────────────────────
        Retire(state.State.CurrentHandleDigest);
        state.State.LastRefreshedAt = now;

        var rotation = Mint(now);
        await state.WriteStateAsync();

        return Result<RefreshRotation>.Success(rotation);
    }

    /// <inheritdoc />
    public Task<Result<SessionDescriptor>> GetAsync() =>
        Task.FromResult(
            state.State.UserId == Guid.Empty
                ? Result<SessionDescriptor>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"Session {sessionId:D} does not exist."
                )
                : Result<SessionDescriptor>.Success(Descriptor())
        );

    /// <inheritdoc />
    public Task<Result<bool>> IsLiveAsync() =>
        Task.FromResult(
            Result<bool>.Success(
                state.State.IsLive
                && clock.UtcNow < state.State.CreatedAt + AccessTokenPolicy.AbsoluteSessionLifetime
            )
        );

    /// <inheritdoc />
    public async Task<Result> RevokeAsync(RevocationReason reason) {
        await RevokeInternalAsync(reason);
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result<int>> ChainLengthAsync() =>
        Task.FromResult(
            Result<int>.Success(
                state.State.RetiredHandleDigests.Count + (state.State.CurrentHandleDigest.Length > 0 ? 1 : 0)
            )
        );

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    // ── Internals ──────────────────────────────────────────────────────────────────────────────

    RefreshRotation Mint(DateTimeOffset now) {
        var handle = CredentialDigest.RandomHandle(HandleBytes);

        state.State.CurrentHandleDigest = CredentialDigest.Sha256(handle);
        state.State.Generation++;
        state.State.RefreshExpiresAt = now + AccessTokenPolicy.RefreshTokenLifetime;

        return new() {
            Handle = handle,
            SessionId = sessionId,
            Generation = state.State.Generation,
            ExpiresAt = state.State.RefreshExpiresAt
        };
    }

    void Retire(string digest) {
        if (digest.Length == 0) {
            return;
        }

        state.State.RetiredHandleDigests.Add(digest);

        if (state.State.RetiredHandleDigests.Count > MaxRetainedGenerations) {
            state.State.RetiredHandleDigests.RemoveRange(
                0,
                state.State.RetiredHandleDigests.Count - MaxRetainedGenerations
            );
        }
    }

    async Task RevokeInternalAsync(RevocationReason reason) {
        // ⚠ The current handle is retired rather than merely cleared, so that presenting it after
        // revocation is still recognised as a replay rather than as an unknown token. The difference
        // shows up in the audit trail, which is where a compromise is actually noticed.
        Retire(state.State.CurrentHandleDigest);

        state.State.CurrentHandleDigest = string.Empty;
        state.State.IsLive = false;
        state.State.RevokedBecause = reason;

        await state.WriteStateAsync();

        // ⚠ THIS DELIBERATELY DOES NOT CALL BACK INTO IUserGrain, AND THE REASON IS A DEADLOCK THAT
        // ACTUALLY HAPPENED.
        //
        // The obvious code here is `user.ForgetSessionAsync(sessionId)`, to keep the user's session
        // list tidy. It produces a cycle: UserGrain.SetStatusAsync (or SetPasswordAsync) iterates its
        // sessions and awaits RevokeAsync on each, and RevokeAsync then awaits a call back into the
        // same UserGrain — which is non-reentrant and is still executing the first call. The result
        // is not an exception; it is a 30-second Orleans response timeout on an administrator
        // suspending an account, which reads as "the cluster is broken" rather than as a cycle.
        // `RefreshReuseTests.SuspendingAUserKillsEverySessionBeforeTheCallReturns` is what caught it.
        //
        // So ownership runs one way: the USER owns its session list and prunes it (it clears the list
        // as part of the same write that revoked them), and the SESSION owns whether it is live. A
        // stale id in the user's list costs nothing — revoking an already-revoked session is
        // idempotent, and `TrackSessionAsync` caps the list so it cannot grow without bound. The
        // host calls `ForgetSessionAsync` on an explicit single-session sign-out, which is the one
        // path where no user-grain call is already in flight.
    }

    static Result<RefreshRotation> Rejected() =>
        Result<RefreshRotation>.Failure(
            ErrorCode.AuthorizationFailed,
            "That refresh token is not valid. Sign in again."
        );

    SessionDescriptor Descriptor() =>
        new() {
            SessionId = sessionId,
            UserId = state.State.UserId,
            TenantId = tenantId,
            ClientId = state.State.ClientId,
            DeviceLabel = state.State.DeviceLabel,
            ClientAddressDigest = state.State.ClientAddressDigest,
            CreatedAt = state.State.CreatedAt,
            LastRefreshedAt = state.State.LastRefreshedAt,
            AuthenticatedAt = state.State.AuthenticatedAt,
            Generation = state.State.Generation,
            IsLive = state.State.IsLive,
            RevokedBecause = state.State.RevokedBecause,
            Methods = [.. state.State.Methods]
        };
}
