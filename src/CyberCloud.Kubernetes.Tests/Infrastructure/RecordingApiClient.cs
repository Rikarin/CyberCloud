using CyberCloud.Kubernetes.Apply;
using System.Runtime.CompilerServices;

namespace CyberCloud.Kubernetes.Tests.Infrastructure;

/// <summary>
///     An <see cref="IKubeApiClient" /> that records what it was asked and answers from a script.
/// </summary>
/// <remarks>
///     ⚠ <b>This substitutes the API server, not server-side apply.</b> ADR-018 and docs/plan/09
///     § Testing the fabric put SSA semantics on a real k3s — <c>ServerSideApplyTests</c> — because
///     conflict detection is a property of the API server and a fake would only assert our belief
///     about it. What this fake is for is the opposite kind of question: "does the informer ask for
///     the right <c>resourceVersion</c>", "does it fall back on 410", "is the managed-by selector
///     always present". Those are properties of <i>our</i> code, and the only way to test the 410
///     branch against a real cluster would be to wait for etcd to compact.
/// </remarks>
public sealed class RecordingApiClient : IKubeApiClient {
    /// <summary>Every list call, in order.</summary>
    public List<(GroupVersionKind Kind, string Namespace, string Selector, string? ResourceVersion, string? Continue)>
        Lists { get; } = [];

    /// <summary>The pages to answer with, in order. Reused when exhausted.</summary>
    public Queue<Result<ListPage>> Pages { get; } = new();

    /// <summary>Answered when <see cref="Pages" /> is empty.</summary>
    public Result<ListPage> DefaultPage { get; set; } =
        Result<ListPage>.Success(new([], "rv-1", string.Empty));

    /// <summary>What <see cref="PingAsync" /> answers.</summary>
    public Result<string> PingResult { get; set; } = Result<string>.Success("v1.35.0");

    /// <summary>How many pings have been made.</summary>
    public int Pings { get; private set; }

    /// <summary>Events the watch yields.</summary>
    public List<KubeWatchEvent> WatchEvents { get; } = [];

    /// <inheritdoc />
    public Task<Result<string>> PingAsync(CancellationToken cancellationToken = default) {
        Pings++;
        return Task.FromResult(PingResult);
    }

    /// <inheritdoc />
    public Task<Result<KubeObject>> GetAsync(ObjectRef target, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result<KubeObject>.Failure(ErrorCode.ResourceNotFound, "not scripted"));

    /// <summary>
    ///     What <see cref="ApplyAsync" /> answers, or <see langword="null" /> for a plain
    ///     <see cref="ApplyResult.Created" />.
    /// </summary>
    /// <remarks>
    ///     Set by tests that need the connection grain to see a <i>refused</i> apply — the case where
    ///     the cluster answered and said no, which must not be folded into the health window. See
    ///     <c>ClusterConnectionGrain.Answered</c>.
    /// </remarks>
    public Result<ApplyOutcome>? NextApply { get; set; }

    /// <summary>
    ///     Thrown from <see cref="ApplyAsync" /> instead of answering, or <see langword="null" /> to
    ///     answer normally.
    /// </summary>
    /// <remarks>
    ///     ⚠ There to demonstrate the symptom the failure mapping exists to remove, and for nothing
    ///     else. An exception that escapes <c>KubeApiClient</c> escapes a grain call, and what the
    ///     caller sees is not the exception — see
    ///     <c>SuspendedReconcileTests.AnUnmappedClientExceptionReachesTheCallerAsACodecNotFoundException</c>.
    /// </remarks>
    public Exception? ThrowOnApply { get; set; }

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> ApplyAsync(KubeCommand command, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(command);

        if (ThrowOnApply is not null) {
            throw ThrowOnApply;
        }

        return Task.FromResult(
            NextApply
            ?? Result<ApplyOutcome>.Success(new() { Result = ApplyResult.Created, Target = command.Target })
        );
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(
        ObjectRef target,
        CascadePolicy policy,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result.Success);

    /// <inheritdoc />
    public Task<Result<ListPage>> ListAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string? resourceVersion = null,
        string? continueToken = null,
        int? limit = null,
        CancellationToken cancellationToken = default
    ) {
        Lists.Add((kind, ns, labelSelector, resourceVersion, continueToken));
        return Task.FromResult(Pages.Count > 0 ? Pages.Dequeue() : DefaultPage);
    }

    /// <inheritdoc />
    public async IAsyncEnumerable<KubeWatchEvent> WatchAsync(
        GroupVersionKind kind,
        string ns,
        string labelSelector,
        string resourceVersion,
        [EnumeratorCancellation] CancellationToken cancellationToken = default
    ) {
        foreach (var change in WatchEvents) {
            cancellationToken.ThrowIfCancellationRequested();
            yield return change;
        }

        await Task.CompletedTask;
    }

    /// <inheritdoc />
    public void Dispose() { }
}
