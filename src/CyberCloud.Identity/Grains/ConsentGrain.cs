using CyberCloud.Core.Contracts;
using CyberCloud.Core.Time;

namespace CyberCloud.Identity.Grains;

/// <summary>
///     <see cref="IConsentGrain" /> — Entity, Durable, key <c>consent/{digest}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The key is a digest of the person and the client, so the first grant fixes both and
///         every later call is checked against them.</b> A caller that computed the key from one
///         pair and then handed in another would be recording somebody else's consent under this
///         person's key; the mismatch is refused rather than written. Nothing honest ever does that
///         — the identity host builds the key and the arguments from the same two values — which is
///         what makes the check cheap to keep.
///     </para>
///     <para>
///         <b>Scopes only widen</b> between a grant and a revocation: allowing <c>cyc.api</c> after
///         <c>openid profile</c> is a union, not a replacement, because the consent page shows the
///         scopes the <i>current</i> request asks for and a person who allowed more last month did
///         not withdraw it by answering a narrower question today. Withdrawing is
///         <see cref="RevokeAsync" />, which empties the set.
///     </para>
/// </remarks>
public sealed class ConsentGrain(
    [PersistentState("consent", StorageTiers.Durable)]
    IPersistentState<ConsentGrainState> state,
    IClock clock
)
    : Grain, IConsentGrain {
    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        _ = IdentityGrainKeys.Decode(this, GrainKeyKind.ConsentGrant);

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task<Result<ConsentGrant>> GrantAsync(Guid userId, string clientId, IReadOnlyList<string> scopes) {
        ArgumentNullException.ThrowIfNull(scopes);

        if (userId == Guid.Empty || string.IsNullOrEmpty(clientId)) {
            return Result<ConsentGrant>.Failure(
                ErrorCode.InvalidRequestBody,
                "A consent grant names a person and a client; one of them is empty."
            );
        }

        if (state.State.UserId != Guid.Empty
            && (state.State.UserId != userId || !string.Equals(state.State.ClientId, clientId, StringComparison.Ordinal))) {
            return Result<ConsentGrant>.Failure(
                ErrorCode.Conflict,
                "This consent record belongs to another person or another client. The key is a "
                + "digest of both, so a caller that reaches this grain with a different pair built "
                + "the key from one pair and the arguments from another."
            );
        }

        var now = clock.UtcNow;

        if (!state.State.Granted) {
            state.State.UserId = userId;
            state.State.ClientId = clientId;
            state.State.Scopes = [];
            state.State.GrantedAt = now;
            state.State.Granted = true;
        }

        foreach (var scope in scopes) {
            if (!string.IsNullOrEmpty(scope) && !state.State.Scopes.Contains(scope, StringComparer.Ordinal)) {
                state.State.Scopes.Add(scope);
            }
        }

        state.State.UpdatedAt = now;
        await state.WriteStateAsync();

        return Result<ConsentGrant>.Success(Descriptor());
    }

    /// <inheritdoc />
    public Task<Result<ConsentGrant>> GetAsync() =>
        Task.FromResult(
            state.State.Granted
                ? Result<ConsentGrant>.Success(Descriptor())
                : Result<ConsentGrant>.Failure(ErrorCode.ResourceNotFound, "No consent is on record for this person and client.")
        );

    /// <inheritdoc />
    public async Task<Result> RevokeAsync() {
        if (!state.State.Granted) {
            return Result.Success;
        }

        state.State.Granted = false;
        state.State.Scopes = [];
        state.State.UpdatedAt = clock.UtcNow;
        await state.WriteStateAsync();

        return Result.Success;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();

        return Task.CompletedTask;
    }

    ConsentGrant Descriptor() =>
        new() {
            UserId = state.State.UserId,
            ClientId = state.State.ClientId,
            Scopes = [.. state.State.Scopes],
            GrantedAt = state.State.GrantedAt,
            UpdatedAt = state.State.UpdatedAt
        };
}
