using System.Collections.Immutable;

namespace CyberCloud.Gateway.Host.Pipeline;

/// <summary>
///     Which stages of docs/plan/10 § Request pipeline a request actually entered, in order.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This exists to make stage order a test rather than a review item</b>, exactly as
///         <c>WriteTrace</c> does for the write path. Rate limiting <i>before</i> dispatch is what
///         makes a flood cost Redis <c>INCR</c>s instead of grain activations, and tenant resolution
///         <i>before</i> routing is what makes a smuggled tenant a <c>404</c> rather than a
///         cross-tenant read. Neither property survives a reordering, and neither is visible in a
///         diff that moves one line.
///     </para>
///     <para>
///         <see cref="Reached" /> is append-only, so <see cref="IsCanonicalPrefix" /> is the whole
///         check: the recorded sequence must match <see cref="Canonical" /> from the start, with
///         <see cref="GatewayStage.ShapeResponse" /> allowed to follow any earlier stop because
///         stage 9 runs on every response, including the errors the first eight produce.
///     </para>
/// </remarks>
sealed record GatewayTrace {
    /// <summary>The canonical order — the nine stages as docs/plan/10 numbers them.</summary>
    public static ImmutableArray<GatewayStage> Canonical { get; } = [
        GatewayStage.Correlation,
        GatewayStage.Authenticate,
        GatewayStage.ResolveTenant,
        GatewayStage.RegionRouting,
        GatewayStage.RateLimit,
        GatewayStage.Route,
        GatewayStage.Validate,
        GatewayStage.Dispatch,
        GatewayStage.ShapeResponse
    ];

    /// <summary>The stages entered, in the order they were entered.</summary>
    public ImmutableArray<GatewayStage> Reached { get; init; } = [];

    /// <summary>The last stage before <see cref="GatewayStage.ShapeResponse" />, or the shape itself.</summary>
    public GatewayStage StoppedAt {
        get {
            if (Reached.IsDefaultOrEmpty) {
                return GatewayStage.None;
            }

            for (var i = Reached.Length - 1; i >= 0; i--) {
                if (Reached[i] != GatewayStage.ShapeResponse) {
                    return Reached[i];
                }
            }

            return GatewayStage.ShapeResponse;
        }
    }

    /// <summary>
    ///     Whether this trace is a strictly increasing prefix of <see cref="Canonical" />, with
    ///     <see cref="GatewayStage.ShapeResponse" /> permitted as the final entry.
    /// </summary>
    /// <returns>
    ///     <c>true</c> when no stage ran out of order, ran twice, or was skipped before the stage the
    ///     request stopped at.
    /// </returns>
    public bool IsCanonicalPrefix() {
        if (Reached.IsDefaultOrEmpty) {
            return true;
        }

        var body = Reached[^1] == GatewayStage.ShapeResponse ? Reached[..^1] : Reached;

        if (body.Length > Canonical.Length) {
            return false;
        }

        for (var i = 0; i < body.Length; i++) {
            if (body[i] != Canonical[i]) {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override string ToString() =>
        Reached.IsDefaultOrEmpty ? "(no stages)" : string.Join(" → ", Reached);
}

/// <summary>
///     Builds a <see cref="GatewayTrace" />, refusing to record a stage out of order.
/// </summary>
/// <remarks>
///     ⚠ <b>It throws rather than returning a failure.</b> A stage running out of order is a
///     programming error in the pipeline's own composition, not a request outcome — docs/plan/00
///     § Coding standards. Discovering it at the call site during a test is the entire value; a
///     <see cref="Result" /> would let it be ignored.
/// </remarks>
sealed class GatewayTraceBuilder {
    readonly ImmutableArray<GatewayStage>.Builder reached = ImmutableArray.CreateBuilder<GatewayStage>(9);

    /// <summary>The last stage entered.</summary>
    public GatewayStage Last => reached.Count == 0 ? GatewayStage.None : reached[^1];

    /// <summary>Records a stage.</summary>
    /// <param name="stage">The stage the pipeline is entering. Must be later than the last one.</param>
    /// <exception cref="InvalidOperationException">
    ///     The stage is not later than <see cref="Last" />, which means the pipeline's composition
    ///     reordered or repeated a step.
    /// </exception>
    public void Enter(GatewayStage stage) {
        if (stage <= Last) {
            throw new InvalidOperationException(
                $"Gateway stage {stage} was entered after {Last}. docs/plan/10 § Request pipeline: "
                + "\"Order matters and each step is here for a named reason\" — rate limiting after "
                + "dispatch would mean a flood costs grain activations, and routing before tenant "
                + "resolution would mean a path segment could select the tenant."
            );
        }

        reached.Add(stage);
    }

    /// <summary>The trace so far.</summary>
    public GatewayTrace Build() => new() { Reached = reached.ToImmutable() };
}
