using CyberCloud.Gateway.Host.Http;

namespace CyberCloud.Gateway.Host.Pipeline;

/// <summary>
///     One of the eight stages that can stop a request. Stage 9 always runs and is
///     <see cref="ResponseWriter" />.
/// </summary>
/// <remarks>
///     ⚠ <b>A stage never writes to <c>HttpResponse</c>.</b> It returns an outcome or
///     <see langword="null" />, and stage 9 writes. That is what makes "the correlation id is on
///     every response, including errors" a property of the pipeline rather than of eight separate
///     pieces of care — docs/plan/10 § Request pipeline puts the header on <i>every</i> response and
///     the only way to get that reliably is to have one writer.
/// </remarks>
interface IGatewayStage {
    /// <summary>Which stage this is. The pipeline asserts the order from these.</summary>
    GatewayStage Stage { get; }

    /// <summary>Runs the stage.</summary>
    /// <param name="context">The request so far. A stage writes what later stages read.</param>
    /// <param name="cancellationToken">Cancels the stage.</param>
    /// <returns>
    ///     <see langword="null" /> to continue, or the outcome that ends the request. ⚠ An outcome
    ///     from stage 8 is the <i>success</i> path too — dispatch produces the <c>202</c>.
    /// </returns>
    Task<GatewayOutcome?> RunAsync(GatewayRequestContext context, CancellationToken cancellationToken = default);
}
