using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>
///     One entry of a long-running operation's progress array — <c>openapi/2026-08-01.json</c>
///     § OperationProgress.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The progress array is this platform's addition to Azure's LRO pattern</b>
///         (docs/plan/10 § Long-running operations, over HTTP), and docs/plan/21 § The .NET SDK's
///         conventions table gives it a row of its own with <b>Nobody's</b> in the "borrowed from"
///         column: <i>"Azure's LROs expose no progress; ours do and the SDK should not hide it."</i>
///         It is what makes a nine-minute cluster creation (docs/plan/09) tolerable instead of a blank
///         terminal.
///     </para>
///     <para>
///         ⚠ <b>Hand-written, not generated, even though the schema is in the OpenAPI document.</b>
///         <see cref="Operation{T}" /> parses it, and the poller cannot depend on a type the generator
///         may or may not have emitted this release. The generator must therefore <b>skip</b> the
///         <c>OperationProgress</c>, <c>OperationState</c>, <c>OperationStatus</c>, <c>Error</c>,
///         <c>ErrorCode</c> and <c>ErrorResponse</c> schemas — see EmitterContract.cs.
///     </para>
/// </remarks>
public sealed class OperationProgress {
    /// <summary>Creates a progress entry.</summary>
    /// <param name="at">When the reconciler reported it.</param>
    /// <param name="step">The phase — <c>applying</c>, <c>waiting-for-ready</c>.</param>
    /// <param name="message">What happened, naming the actual numbers, or <see langword="null" />.</param>
    /// <param name="percentComplete">0–100, or <see langword="null" /> when the step does not know.</param>
    public OperationProgress(DateTimeOffset at, string step, string? message = null, int? percentComplete = null) {
        At = at;
        Step = step;
        Message = message;
        PercentComplete = percentComplete;
    }

    /// <summary>When the reconciler reported this entry.</summary>
    public DateTimeOffset At { get; }

    /// <summary>The phase a reconciler reported — <c>applying</c>, <c>waiting-for-ready</c>.</summary>
    public string Step { get; }

    /// <summary>What happened, naming the actual numbers, or <see langword="null" />.</summary>
    public string? Message { get; }

    /// <summary>How far along, 0–100, or <see langword="null" /> when this step does not report one.</summary>
    public int? PercentComplete { get; }

    internal static OperationProgress? TryRead(JsonElement element) {
        if (element.ValueKind is not JsonValueKind.Object)
            return null;

        // `at` and `step` are the required members — openapi/2026-08-01.json § OperationProgress. An
        // entry missing either is skipped rather than surfaced with a default timestamp, because a
        // progress line claiming to have happened at 0001-01-01 is worse than one that is absent.
        if (!element.TryGetProperty("at", out var at) || !at.TryGetDateTimeOffset(out var timestamp))
            return null;

        if (!element.TryGetProperty("step", out var step) || step.ValueKind is not JsonValueKind.String)
            return null;

        var message = element.TryGetProperty("message", out var m) && m.ValueKind is JsonValueKind.String
            ? m.GetString()
            : null;

        var percent = element.TryGetProperty("percentComplete", out var p) && p.ValueKind is JsonValueKind.Number
            ? p.GetInt32()
            : (int?)null;

        return new OperationProgress(timestamp, step.GetString()!, message, percent);
    }

    /// <inheritdoc />
    public override string ToString()
        => PercentComplete is null ? $"{Step}: {Message}" : $"{PercentComplete}% {Step}: {Message}";
}
