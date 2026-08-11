namespace CyberCloud.Gateway.Host.Hubs;

/// <summary>
///     Whether a caller may hear about a resource.
/// </summary>
/// <remarks>
///     ⚠ <b>A question, not a decision.</b> docs/plan/10 § What the gateway must never do forbids the
///     gateway from performing authorization, and docs/plan/07 § The enforcement seam puts the one
///     seam inside the resource manager. This interface exists so the connection grain can <i>ask</i>
///     without importing an engine — its production implementation forwards to
///     <see cref="IResourceManager.ReadAsync" /> and copies the answer, which is why this assembly
///     names no <c>ICheckGrain</c> and references no authorization assembly.
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
sealed class ResourceManagerInterestAuthorizer(IResourceManager manager) : IInterestAuthorizer {
    /// <inheritdoc />
    public async Task<Result> CanReadAsync(
        CallerContext caller,
        string resourcePath,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(caller);

        var read = await manager.ReadAsync(
            new WriteRequest { Path = resourcePath, ApiVersion = "", Caller = caller },
            cancellationToken
        );

        return read.ToResult();
    }
}
