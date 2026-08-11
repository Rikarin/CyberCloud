using CyberCloud.Gateway.Host.Http;
using System.Globalization;

namespace CyberCloud.Gateway.Host.Pipeline.Stages;

/// <summary>
///     Stage 1 — <c>x-ms-correlation-request-id</c> in, <c>x-cybercloud-request-id</c> out.
///     docs/plan/10 § Request pipeline.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Two ids, not one, and they are not interchangeable.</b> The correlation id belongs to
///         the <i>caller's</i> unit of work and can span many requests — a CLI command that issues
///         six calls sends one. The request id belongs to this request and is minted here, always,
///         even when a correlation id arrived. Collapsing them means a support engineer given "the
///         id" cannot tell whether they are looking at one request or six.
///     </para>
///     <para>
///         ⚠ <b>A caller-supplied correlation id is length-capped and stripped of control
///         characters.</b> It is echoed into log lines and spans, and an unbounded caller-controlled
///         string that lands in a log is a log-injection primitive — a newline in it forges a log
///         entry.
///     </para>
/// </remarks>
sealed class CorrelationStage : IGatewayStage {
    /// <summary>The longest correlation id accepted. Longer ones are truncated, never rejected.</summary>
    public const int MaxCorrelationIdLength = 128;

    /// <inheritdoc />
    public GatewayStage Stage => GatewayStage.Correlation;

    /// <inheritdoc />
    public Task<GatewayOutcome?> RunAsync(
        GatewayRequestContext context,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(context);
        cancellationToken.ThrowIfCancellationRequested();

        context.RequestId = Guid.NewGuid().ToString("D", CultureInfo.InvariantCulture);

        var supplied = context.Http.Request.Headers.TryGetValue(
            GatewayHeaders.CorrelationRequestId,
            out var header
        )
            ? header.ToString()
            : "";

        context.CorrelationId = Sanitize(supplied) is { Length: > 0 } clean ? clean : context.RequestId;

        return Task.FromResult<GatewayOutcome?>(null);
    }

    /// <summary>Keeps printable ASCII, drops the rest, and caps the length.</summary>
    /// <param name="value">The caller's value.</param>
    internal static string Sanitize(string value) {
        if (value.Length == 0) {
            return "";
        }

        Span<char> buffer = stackalloc char[Math.Min(value.Length, MaxCorrelationIdLength)];
        var written = 0;

        foreach (var character in value) {
            if (written == buffer.Length) {
                break;
            }

            if (character is >= ' ' and <= '~') {
                buffer[written++] = character;
            }
        }

        return new(buffer[..written]);
    }
}
