using System.Text.Json;

namespace CyberCloud.Sdk;

/// <summary>
///     Whether a call returns as soon as the operation is accepted, or waits for it to finish.
///     docs/plan/21 § The .NET SDK's worked example uses <see cref="Started" />.
/// </summary>
public enum WaitUntil {
    /// <summary>
    ///     Return as soon as the service accepts the request. The caller gets an
    ///     <see cref="Operation{T}" /> that has not completed, which is what makes
    ///     <see cref="Operation{T}.GetProgressAsync" /> worth having.
    /// </summary>
    Started,

    /// <summary>Poll until the operation reaches a terminal state, then return.</summary>
    Completed,
}

/// <summary>
///     An operation's state — <c>openapi/2026-08-01.json</c> § OperationState, and Azure's vocabulary
///     exactly (docs/plan/08 § Long-running operations).
/// </summary>
public enum OperationState {
    /// <summary>Accepted, not started. The operation grain exists and its reminder has not fired.</summary>
    NotStarted,

    /// <summary>Running. Keep polling.</summary>
    Running,

    /// <summary>Done. The resource is readable at its own URL.</summary>
    Succeeded,

    /// <summary>Failed. <see cref="OperationStatus.Error" /> says why.</summary>
    Failed,

    /// <summary>
    ///     Cancelled. ⚠ docs/plan/08 § Long-running operations: this state is reached only <i>after</i>
    ///     the delete path has run for everything already applied — <i>"a 'cancelled' create that
    ///     leaves resources running is a billing dispute waiting to happen, so cancellation completes
    ///     rather than abandoning"</i>. Nothing is left behind by the time a caller sees it.
    /// </summary>
    Canceled,
}

/// <summary>
///     One poll's answer — <c>openapi/2026-08-01.json</c> § OperationStatus, the body of
///     <c>GET /operations/{operationId}</c>.
/// </summary>
public sealed class OperationStatus {
    OperationStatus(OperationState state, int? percentComplete, IReadOnlyList<OperationProgress> progress, CyberCloudError? error) {
        State = state;
        PercentComplete = percentComplete;
        Progress = progress;
        Error = error;
    }

    /// <summary>The state. The only required member.</summary>
    public OperationState State { get; }

    /// <summary>How far along, 0–100, or <see langword="null" />.</summary>
    public int? PercentComplete { get; }

    /// <summary>
    ///     The progress array as of this poll. ⚠ It is cumulative — every poll returns what the
    ///     previous polls returned plus whatever is new — which is what lets
    ///     <see cref="Operation{T}.GetProgressAsync" /> stream by index without the service tracking a
    ///     cursor per client.
    /// </summary>
    public IReadOnlyList<OperationProgress> Progress { get; }

    /// <summary>Why it failed, when <see cref="State" /> is <see cref="OperationState.Failed" />.</summary>
    public CyberCloudError? Error { get; }

    /// <summary>Whether the state is terminal — nothing will change if it is polled again.</summary>
    public bool IsTerminal => State is OperationState.Succeeded or OperationState.Failed or OperationState.Canceled;

    /// <summary>Parses a poll response body.</summary>
    /// <param name="content">The body.</param>
    /// <exception cref="CyberCloudRequestFailedException">
    ///     The body is not an <c>OperationStatus</c>. ⚠ This throws where
    ///     <see cref="CyberCloudError.TryParse" /> returns <see langword="null" />, and the asymmetry
    ///     is deliberate: an unreadable <i>error</i> body still leaves the caller a status code to act
    ///     on, whereas an unreadable <i>status</i> body leaves the poller unable to decide whether to
    ///     keep polling — and silently treating it as <c>Running</c> is an infinite loop.
    /// </exception>
    public static OperationStatus Parse(ReadOnlyMemory<byte> content) {
        JsonDocument document;

        try {
            document = JsonDocument.Parse(content);
        } catch (JsonException e) {
            throw new CyberCloudRequestFailedException("The operation status response is not JSON.", e);
        }

        using (document) {
            var root = document.RootElement;

            if (root.ValueKind is not JsonValueKind.Object
                || !root.TryGetProperty("status", out var status)
                || status.ValueKind is not JsonValueKind.String
                || !Enum.TryParse<OperationState>(status.GetString(), ignoreCase: false, out var state))
                throw new CyberCloudRequestFailedException(
                    "The operation status response has no recognised 'status'. Expected one of "
                    + string.Join(", ", Enum.GetNames<OperationState>())
                    + " — openapi/2026-08-01.json § OperationState.");

            var percent = root.TryGetProperty("percentComplete", out var p) && p.ValueKind is JsonValueKind.Number
                ? p.GetInt32()
                : (int?)null;

            var error = root.TryGetProperty("error", out var e) && e.ValueKind is JsonValueKind.Object
                ? CyberCloudError.TryParse(Encoding.UTF8.GetBytes(e.GetRawText()))
                : null;

            return new OperationStatus(state, percent, ReadProgress(root), error);
        }
    }

    static List<OperationProgress> ReadProgress(JsonElement root) {
        if (!root.TryGetProperty("progress", out var array) || array.ValueKind is not JsonValueKind.Array)
            return [];

        var entries = new List<OperationProgress>(array.GetArrayLength());

        foreach (var item in array.EnumerateArray()) {
            if (OperationProgress.TryRead(item) is { } entry)
                entries.Add(entry);
        }

        return entries;
    }
}
