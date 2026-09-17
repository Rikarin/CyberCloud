using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IAuthorizationCodeGrain" /> — Entity, <b>Hot</b>, key <c>code/{codeId:N}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The second hot-tier identity grain, and hot for the same reason the first is.</b>
///         <see cref="SessionGrain" />'s remarks carry the argument for sessions; this record is
///         shorter-lived still — it protects nothing once the code it names has expired, which is
///         <see cref="AccessTokenPolicy.AuthorizationCodeLifetime" /> after the code was minted —
///         and losing it early costs one replay window on one code, with PKCE still in front of it.
///     </para>
///     <para>
///         ⚠ <b>One turn does the whole check-and-record.</b> The grain is single-threaded, so two
///         exchanges of one code cannot both see an empty record: the first writes the token session
///         it was handed, the second reads it. That is the entire mechanism, and it is why the caller
///         mints the session id <i>before</i> it calls — <see cref="IAuthorizationCodeGrain" />'s
///         remarks say what breaks otherwise.
///     </para>
///     <para>
///         The record clears itself through a reminder registered at consumption, the way
///         <see cref="SignUpGrain" /> clears an abandoned sign-up: a record past
///         <see cref="AuthorizationCodeGrainState.ForgetAt" /> is cleared and the activation dropped.
///         A stale record that outlived its reminder — a silo restarted between the two — is
///         treated as absent on the next call too, so the reminder is housekeeping rather than
///         correctness.
///     </para>
/// </remarks>
public sealed class AuthorizationCodeGrain(
    [PersistentState("code", StorageTiers.Hot)]
    IPersistentState<AuthorizationCodeGrainState> state,
    IClock clock
)
    : Grain, IAuthorizationCodeGrain, IRemindable {
    /// <summary>The reminder that clears the record once the code it names has expired.</summary>
    public const string ForgetReminder = "code-forget";

    /// <summary>
    ///     How long past the code's own expiry the record is kept.
    /// </summary>
    /// <remarks>
    ///     ⚠ Longer than any clock skew between the host that minted the code and the silo that
    ///     holds this grain, and at least the reminder service's one-minute floor. A record
    ///     forgotten <i>before</i> the code expires would let a replay through in the last seconds
    ///     of a code's life; a record kept longer than this protects nothing, because OpenIddict
    ///     refuses the expired code before this grain is asked.
    /// </remarks>
    public static TimeSpan Grace { get; } = TimeSpan.FromMinutes(1);

    Guid codeId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        codeId = IdentityGrainKeys.Decode(this, GrainKeyKind.AuthorizationCode).Id;

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<CodeConsumption>> ConsumeAsync(Guid tokenSessionId, DateTimeOffset expiresAt) {
        if (tokenSessionId == Guid.Empty) {
            return Result<CodeConsumption>.Failure(
                ErrorCode.InvalidRequestBody,
                $"Code {codeId:D} cannot be consumed by an empty token session id: the record exists "
                + "to name the session a replay must revoke, and an empty id names none."
            );
        }

        var now = clock.UtcNow;

        if (Consumed && now >= state.State.ForgetAt) {
            // The reminder did not get here first — a silo restarted between consumption and its
            // tick, say. The code this record names is expired, so the record is stale and absent.
            await ForgetAsync();
        }

        if (Consumed) {
            return Result<CodeConsumption>.Success(new(false, state.State.TokenSessionId, state.State.ConsumedAt));
        }

        state.State.TokenSessionId = tokenSessionId;
        state.State.ConsumedAt = now;
        state.State.ForgetAt = (expiresAt > now ? expiresAt : now) + Grace;

        // ⚠ The reminder before the write, for the reason SignUpGrain gives: a crash between the two
        // leaves a reminder that fires, finds nothing, and unregisters itself, rather than a record
        // nothing will ever clear.
        var due = state.State.ForgetAt - now;
        await this.RegisterOrUpdateReminder(ForgetReminder, due, due);
        await state.WriteStateAsync();

        return Result<CodeConsumption>.Success(new(true, tokenSessionId, now));
    }

    /// <inheritdoc />
    public Task<Result<bool>> IsConsumedAsync() =>
        Task.FromResult(Result<bool>.Success(Consumed && clock.UtcNow < state.State.ForgetAt));

    /// <inheritdoc />
    public async Task ReceiveReminder(string reminderName, TickStatus status) {
        if (!string.Equals(reminderName, ForgetReminder, StringComparison.Ordinal)) {
            return;
        }

        if (!Consumed || clock.UtcNow >= state.State.ForgetAt) {
            await ForgetAsync();
            DeactivateOnIdle();
        }
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();

        return Task.CompletedTask;
    }

    bool Consumed => state.State.TokenSessionId != Guid.Empty;

    async Task ForgetAsync() {
        await state.ClearStateAsync();
        state.State = new();

        var reminder = await this.GetReminder(ForgetReminder);
        if (reminder is not null) {
            await this.UnregisterReminder(reminder);
        }
    }
}
