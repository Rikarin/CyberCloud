using Microsoft.Extensions.Logging;
using Orleans.Multitenant;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Grains;

/// <summary>
///     Whether a caller may hear about a resource.
/// </summary>
/// <remarks>
///     ⚠ <b>A question, not a decision.</b> docs/plan/10 § What the gateway must never do forbids the
///     gateway from performing authorization, and docs/plan/07 § The enforcement seam puts the one
///     seam inside the resource manager. This interface exists so <see cref="ConnectionGrain" /> can
///     <i>ask</i> without importing an engine — its production implementation forwards to
///     <see cref="IResourceManager.ReadAsync" /> and copies the answer.
///     <para>
///         It moved here with the grain. While both lived in the gateway host it was the seam that
///         kept that assembly from naming <c>ICheckGrain</c>; here it is a seam a test can script,
///         which is what it is actually for now that the engine is one assembly away rather than one
///         reference away.
///     </para>
/// </remarks>
public interface IInterestAuthorizer {
    /// <summary>Whether the caller may read a resource.</summary>
    /// <param name="caller">Who is asking. Its tenant came from the token.</param>
    /// <param name="resourcePath">The resource, as docs/plan/06 § Identifiers spells it.</param>
    /// <param name="cancellationToken">Cancels the question.</param>
    /// <returns>
    ///     Success when readable. On refusal, whatever the seam said — which is
    ///     <see cref="ErrorCode.ResourceNotFound" /> for a resource the caller cannot see, never a
    ///     <c>403</c>.
    /// </returns>
    Task<Result> CanReadAsync(
        CallerContext caller,
        string resourcePath,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     Forwards the question to the resource manager's read path.
/// </summary>
/// <remarks>
///     ⚠ <b>"Can you read it?" and "read it" are the same call, on purpose.</b>
///     <see cref="IResourceManager.ReadAsync" /> runs the enforcement seam and returns the canonical
///     <c>404</c> for both absent and unauthorized, so using it as the authorization question costs
///     one read and gets the 404-never-403 property for free. Writing a separate "check" entry point
///     would be a second path through the seam, and the two would drift.
/// </remarks>
public sealed class ResourceManagerInterestAuthorizer(IResourceManager manager) : IInterestAuthorizer {
    /// <inheritdoc />
    public async Task<Result> CanReadAsync(
        CallerContext caller,
        string resourcePath,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);
        ArgumentNullException.ThrowIfNull(manager);

        var read = await manager.ReadAsync(
            new WriteRequest { Path = resourcePath, ApiVersion = "", Caller = caller },
            cancellationToken
        );

        return read.ToResult();
    }
}

/// <summary>
///     How many interests one connection may hold. docs/plan/10 § Rate limiting.
/// </summary>
/// <remarks>
///     ⚠ <b>The per-connection stream cap, and only that one.</b> docs/plan/10 § Rate limiting gives
///     the exempt classes two concurrency limits: connections per tenant and streams per connection.
///     The first is a per-pod socket budget and stays in the gateway with the hub that takes the
///     socket; this one bounds an <i>interest set</i>, which is the grain's own state, and enforcing
///     it anywhere else would be enforcing it on a number the enforcer cannot see. One connection
///     subscribing to a tenant's entire resource graph is the cheapest possible request for the caller
///     and an unbounded one for the platform: a stream subscription and a re-check per relation change
///     for every interest.
/// </remarks>
public sealed class ConnectionLimits {
    /// <summary>How many interests one connection may register. Default 200.</summary>
    public int StreamsPerConnection { get; init; } = 200;
}

/// <summary>
///     <see cref="IConnectionGrain" /> — Session, no storage, key <c>conn/{connectionId}</c>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Per-subscribe, never per-connect, and re-checked.</b> docs/plan/10 § SignalR is
///         explicit, and the reason is worth keeping in front of whoever edits this: a check that
///         runs once at connect passes at 09:00 and is still delivering that resource group's events
///         at 17:00 to somebody whose access was revoked at 09:05. The live channel would be an
///         authorization bypass with a nice UI, and it would be invisible — the REST API would
///         correctly answer <c>404</c> the whole time.
///     </para>
///     <para>
///         ⚠ <b>It holds no <c>[PersistentState]</c> and must not acquire one.</b> The interest set
///         cannot outlive the socket that created it (docs/plan/05 § Hot), so there is nothing to
///         rebuild and nothing to lose, and this type is absent from <c>durable-grains.txt</c> —
///         satisfying the Storage tier gate by absence rather than by exemption. The fields below are
///         plain instance state, which is what "dies with the connection" means when written down.
///     </para>
///     <para>
///         ⚠ <b>The tenant is read off the activation and is not taken from a caller.</b>
///         <see cref="OnActivateAsync" /> reads it through <c>GetTenantId()</c> and refuses an
///         activation that has none, so a grain reached without <c>ForTenant</c> fails on arrival
///         rather than serving a connection with no tenant. <see cref="AttachAsync" /> then refuses a
///         caller whose tenant disagrees: the key is the boundary, and a caller context that named a
///         different tenant would be a token and a grain describing two different people.
///     </para>
///     <para>
///         <b>Two things trigger a re-check.</b> <see cref="RecheckAsync" /> called from the tenant's
///         relation-version stream is the production path; calling it directly is what the revoke
///         test does. The stream subscription is deliberately <i>not</i> established here — it needs a
///         configured stream provider, and a grain that threw on activation when one is missing would
///         make every hub connection fail in a deployment that has not wired streams yet.
///         <c>ConnectionStreamBridge</c> is where that wiring belongs and it is owed; ⚠ what changed
///         with the move out of the gateway host is that it is now <i>possible</i> — a grain in a silo
///         can subscribe to the <c>Events</c> provider, and a type declared only in an Orleans client
///         could not.
///     </para>
/// </remarks>
public sealed class ConnectionGrain(
    IInterestAuthorizer authorizer,
    ConnectionLimits limits,
    ILogger<ConnectionGrain> logger
)
    : Grain, IConnectionGrain {
    readonly HashSet<ConnectionInterest> interests = [];

    CallerContext caller = new();
    string connectionId = "";
    string hub = "";
    Guid tenantId;

    /// <inheritdoc />
    public override Task OnActivateAsync(CancellationToken cancellationToken) {
        tenantId = ResourceManagerGrainKeys.TenantOf(this);

        var within = this.GetKeyWithinTenant();

        // ⚠ Not GrainKeys.Parse: a connection is not one of docs/plan/06 § Grain keys' entities — see
        // ConnectionGrainKeys on why it has no GrainKeyKind. The prefix is still checked, because a
        // grain activated under some other shape would be serving a key it does not understand.
        if (!within.StartsWith(ConnectionGrainKeys.Prefix, StringComparison.Ordinal)
            || within.Length == ConnectionGrainKeys.Prefix.Length) {
            throw new InvalidOperationException(
                $"{nameof(ConnectionGrain)} was activated with the key '{within}', which is not a "
                + $"'{ConnectionGrainKeys.Prefix}{{connectionId}}' key. Build it with "
                + $"{nameof(ConnectionGrainKeys)}.{nameof(ConnectionGrainKeys.Connection)}."
            );
        }

        connectionId = within[ConnectionGrainKeys.Prefix.Length..];

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public Task<Result> AttachAsync(CallerContext caller, string hub) {
        ArgumentNullException.ThrowIfNull(caller);

        // ⚠ The key's tenant wins, and a disagreement is refused rather than reconciled. The gateway
        // reaches this grain through ForTenant(token tenant) and builds the caller from the same
        // token, so the two can only differ if something composed one of them from somewhere else —
        // which is the case this refusal exists for.
        if (caller.TenantId != tenantId) {
            return Task.FromResult(
                Result.Failure(
                    ErrorCode.AuthorizationFailed,
                    "This connection grain belongs to a different tenant than the caller attaching to "
                    + "it. The tenant in the key comes from the token — docs/plan/00 § The "
                    + "tenant-separation row, corrected."
                )
            );
        }

        this.caller = caller;
        this.hub = hub;

        // ⚠ Nothing is authorized here. A connection is a socket, not a grant.
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public async Task<Result> SubscribeAsync(ConnectionInterest interest) {
        if (caller.TenantId == Guid.Empty) {
            return Result.Failure(
                ErrorCode.AuthorizationFailed,
                "This connection has no caller. Call AttachAsync before subscribing."
            );
        }

        // docs/plan/10 § Rate limiting — the concurrency limit the exempt classes get instead of a
        // request count. An unbounded interest set is a re-check per relation change per interest.
        if (!interests.Contains(interest) && interests.Count >= limits.StreamsPerConnection) {
            return Result.Failure(
                ErrorCode.QuotaExceeded,
                $"This connection already holds {interests.Count} subscriptions, which is the limit. "
                + "docs/plan/10 § Rate limiting: long-poll and SignalR get a concurrency limit "
                + "instead of a request count."
            );
        }

        var readable = await authorizer.CanReadAsync(caller, interest.ResourcePath);
        if (readable.TryGetError(out var error)) {
            // ⚠ Whatever the seam said, unchanged. It is a 404 for a resource the caller cannot see,
            // and turning it into a "subscription refused" here would be a second answer to a
            // question that already has one.
            return Result.Failure(error);
        }

        interests.Add(interest);
        return Result.Success;
    }

    /// <inheritdoc />
    public Task<Result> UnsubscribeAsync(ConnectionInterest interest) {
        interests.Remove(interest);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<ImmutableArray<ConnectionInterest>> InterestsAsync() =>
        Task.FromResult(interests.ToImmutableArray());

    /// <inheritdoc />
    public async Task<int> RecheckAsync() {
        if (interests.Count == 0) {
            return 0;
        }

        var dropped = 0;

        foreach (var interest in interests.ToArray()) {
            var readable = await authorizer.CanReadAsync(caller, interest.ResourcePath);
            if (!readable.TryGetError(out _)) {
                continue;
            }

            interests.Remove(interest);
            dropped++;

            logger.LogInformation(
                "Dropped interest {Interest} on hub {Hub} for {Caller} on connection {Connection}: "
                + "the caller can no longer read it. docs/plan/10 § SignalR — subscription "
                + "authorization is re-checked on relation changes.",
                interest,
                hub,
                caller,
                connectionId
            );
        }

        return dropped;
    }

    /// <inheritdoc />
    public Task DeactivateAsync() {
        DeactivateOnIdle();
        return Task.CompletedTask;
    }

    /// <summary>The caller this connection is bound to. For a hub that needs it back.</summary>
    public CallerContext Caller => caller;
}
