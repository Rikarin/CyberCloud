namespace CyberCloud.Gateway.Host.Regions;

/// <summary>What stage 4 decided.</summary>
enum RegionAction {
    /// <summary>This region is the tenant's home. Carry on down the pipeline.</summary>
    Serve = 0,

    /// <summary>Not this region. Proxy to the home region, preserving the correlation id.</summary>
    Proxy,

    /// <summary>
    ///     A second hop was about to happen. ⚠ Refuse — docs/plan/10 § Request pipeline: <i>"One hop,
    ///     never two."</i>
    /// </summary>
    RefuseSecondHop
}

/// <summary>
///     Stage 4's decision. docs/plan/10 § Request pipeline.
/// </summary>
/// <param name="Action">What to do.</param>
/// <param name="HomeRegion">The tenant's home region, from the directory.</param>
/// <param name="ThisRegion">The region this pod runs in.</param>
readonly record struct RegionDecision(RegionAction Action, string HomeRegion, string ThisRegion);

/// <summary>
///     Where a request should be served. docs/plan/10 § Request pipeline, stage 4.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The decision is implemented and the hop is a seam, on purpose.</b> A real
///         cross-region HTTP hop needs two running regions and a resolvable name for each, which no
///         in-process test has. What <i>is</i> testable — and is what actually goes wrong — is the
///         decision and the loop guard, so both live here as pure functions and
///         <see cref="IRegionProxy" /> carries the socket.
///     </para>
///     <para>
///         ⚠ <b>The loop guard is the part that has teeth.</b> "One hop, never two" is not a
///         performance preference. Two regions whose directory snapshots disagree — which is the
///         normal state for a few seconds after a tenant moves — will each conclude the other is
///         home, and a request with no hop counter bounces until something times out. The symptom is
///         latency rather than an error, so it gets diagnosed slowly. A stamped
///         <c>x-cybercloud-forwarded-by-region</c> turns it into an immediate, attributable failure
///         that names both regions.
///     </para>
/// </remarks>
static class RegionRouting {
    /// <summary>Decides where to serve a request.</summary>
    /// <param name="homeRegion">The tenant's home region, from the directory entry.</param>
    /// <param name="thisRegion">This pod's region, from configuration.</param>
    /// <param name="alreadyForwardedBy">
    ///     The value of <c>x-cybercloud-forwarded-by-region</c>, or empty. Non-empty means some other
    ///     gateway already proxied this request once.
    /// </param>
    /// <returns>
    ///     <see cref="RegionAction.Serve" /> when the regions match or the tenant has no home region
    ///     recorded, <see cref="RegionAction.Proxy" /> for the first hop, and
    ///     <see cref="RegionAction.RefuseSecondHop" /> when a hop has already happened and this
    ///     region still is not home.
    /// </returns>
    public static RegionDecision Decide(string homeRegion, string thisRegion, string alreadyForwardedBy) {
        ArgumentNullException.ThrowIfNull(homeRegion);
        ArgumentNullException.ThrowIfNull(thisRegion);
        ArgumentNullException.ThrowIfNull(alreadyForwardedBy);

        // A tenant with no home region recorded is served here rather than refused. The directory is
        // the authority on placement and an empty value means "not placed yet"; refusing would make
        // tenant creation depend on a field written after it.
        if (homeRegion.Length == 0
            || thisRegion.Length == 0
            || string.Equals(homeRegion, thisRegion, StringComparison.OrdinalIgnoreCase)) {
            return new(RegionAction.Serve, homeRegion, thisRegion);
        }

        return new(
            alreadyForwardedBy.Length == 0 ? RegionAction.Proxy : RegionAction.RefuseSecondHop,
            homeRegion,
            thisRegion
        );
    }
}

/// <summary>
///     The cross-region hop. A seam, because a real one needs two regions.
/// </summary>
interface IRegionProxy {
    /// <summary>Forwards a request to the tenant's home region and copies the response back.</summary>
    /// <param name="context">The request. ⚠ The correlation id must survive the hop unchanged.</param>
    /// <param name="decision">Where to send it.</param>
    /// <param name="cancellationToken">Cancels the hop.</param>
    /// <returns>Success when the response has been copied back, or a failure to shape.</returns>
    Task<Result> ForwardAsync(
        HttpContext context,
        RegionDecision decision,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     The proxy a deployment with one region gets: it refuses, and says which region to call.
/// </summary>
/// <remarks>
///     ⚠ <b>It fails rather than serving locally, and that is the safe direction.</b> Serving another
///     region's tenant from here would mean reading its grains across a cluster boundary the
///     placement was chosen to avoid, and it would do so silently. A caller that is told which region
///     to call can act on it; a caller served the wrong thing cannot.
/// </remarks>
sealed class UnconfiguredRegionProxy : IRegionProxy {
    /// <inheritdoc />
    public Task<Result> ForwardAsync(
        HttpContext context,
        RegionDecision decision,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result.Failure(
            ErrorCode.InternalError,
            $"This gateway runs in region '{decision.ThisRegion}' and the tenant's home region is "
            + $"'{decision.HomeRegion}'. No cross-region proxy is configured, so the request cannot "
            + $"be served here. Call the '{decision.HomeRegion}' gateway — docs/plan/10 "
            + "§ Request pipeline."
        ));
}
