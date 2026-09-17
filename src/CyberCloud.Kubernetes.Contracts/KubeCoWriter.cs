using System.Globalization;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     The seam a child reconciler co-writes onto a parent's — or a sibling's — object through:
///     read, apply the fragment under the co-owned mode, and read again when the object moved.
/// </summary>
/// <remarks>
///     <para>
///         <b>Why a seam and not three lines in every reconciler.</b> A co-owned apply
///         (<see cref="IKubeCommandBuilder.CoWriting" />) has a shape the ordinary apply does not:
///         it must be built from a read made a moment ago, and it is refused as
///         <see cref="ApplyResult.Stale" /> when the object moved in between, at which point the only
///         right answer is to read again. A peering writes onto <i>two</i> objects per pass, each of
///         which its owner and the other peerings are also writing, so the read-apply-again loop is
///         the common case rather than the edge. Writing it once, here, is what keeps the first
///         reconciler that gets it wrong from being the model for the second.
///     </para>
///     <para>
///         ⚠ <b>The retry is bounded and the last answer is handed back.</b> Three attempts, then the
///         <see cref="ApplyResult.Stale" /> outcome itself, which a reconciler reports as
///         <c>InProgress</c> with a short retry. An unbounded loop against an object a controller
///         writes on every pass would be a reconciler that never returns, which clause 3 of
///         docs/plan/08 § The reconcile loop forbids.
///     </para>
/// </remarks>
public interface IKubeCoWriter {
    /// <summary>
    ///     Applies <paramref name="fragmentJson" /> onto <paramref name="target" /> as a fragment of
    ///     <paramref name="writer" />'s, beside whatever other co-writers hold and under the owner's
    ///     labels.
    /// </summary>
    /// <param name="writer">The co-writing resource, with its GUID resolved.</param>
    /// <param name="target">The owner's object. Its kind must be complete; the plural is the REST path.</param>
    /// <param name="fragmentJson">
    ///     The slice this resource contributes — <c>spec</c>, <c>data</c>, and so on. No labels, no
    ///     annotations, no owner references, no status; see
    ///     <see cref="IKubeCommandBuilder.CoWriting" /> for what is refused and why.
    /// </param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    /// <returns>
    ///     The apply's outcome, or <see cref="ErrorCode.ResourceNotFound" /> when the owner's object
    ///     is not there. ⚠ Absence is a failure and never a create: a co-writer that created the
    ///     owner's object would create it without the seven labels, under the owner's name.
    /// </returns>
    Task<Result<ApplyOutcome>> ApplyFragmentAsync(
        ResourceId writer,
        ObjectRef target,
        string fragmentJson,
        CancellationToken cancellationToken = default
    );

    /// <summary>
    ///     Takes <paramref name="writer" />'s fragment back off <paramref name="target" />, leaving
    ///     the owner's object and every other co-writer's fragment standing.
    /// </summary>
    /// <param name="writer">The co-writing resource, with its GUID resolved.</param>
    /// <param name="target">The owner's object.</param>
    /// <param name="cancellationToken">The reconcile's token.</param>
    /// <returns>
    ///     The withdrawal's outcome. An object that is already gone is
    ///     <see cref="ApplyResult.Unchanged" /> with a message saying so: the owner's delete wins,
    ///     and a fragment on an object that no longer exists is as withdrawn as it can be.
    /// </returns>
    Task<Result<ApplyOutcome>> WithdrawFragmentAsync(
        ResourceId writer,
        ObjectRef target,
        CancellationToken cancellationToken = default
    );
}

/// <summary>
///     <see cref="IKubeCoWriter" /> over one cluster connection — what a
///     <c>ReconcileContext</c> carries when the pass has a cluster.
/// </summary>
/// <param name="cluster">The cluster the owner's object lives in.</param>
public sealed class KubeCoWriter(IKubeClusterConnection cluster) : IKubeCoWriter {
    /// <summary>How many times a stale read is retried before the stale outcome is handed back.</summary>
    public const int MaxAttempts = 3;

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> ApplyFragmentAsync(
        ResourceId writer,
        ObjectRef target,
        string fragmentJson,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentException.ThrowIfNullOrEmpty(fragmentJson);

        return RunAsync(
            writer,
            target,
            absent: () => Result<ApplyOutcome>.Failure(
                ErrorCode.ResourceNotFound,
                $"'{target}' is not in cluster {cluster.ClusterId:D}, so there is nothing for resource "
                + $"{writer.Id:D} to co-write onto. A co-writer never creates the owner's object — it "
                + "would be created under the owner's name without the seven labels — so the owner's "
                + "own reconcile has to have converged first."
            ),
            attempt: (live, ct) => KubeCommand.For(cluster)
                .WithTenantId(writer.TenantId)
                .WithResourceId(writer)
                .WithKind(target.Kind)
                .CoWriting(live)
                .ObjectJson(fragmentJson)
                .ApplyAsync(ct),
            cancellationToken
        );
    }

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> WithdrawFragmentAsync(
        ResourceId writer,
        ObjectRef target,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(target);

        return RunAsync(
            writer,
            target,
            absent: () => Result<ApplyOutcome>.Success(
                new() {
                    Result = ApplyResult.Unchanged,
                    Target = target,
                    Message = $"'{target}' is already gone from cluster {cluster.ClusterId:D}; the owner's "
                        + $"delete wins and resource {writer.Id:D}'s fragment went with it."
                }
            ),
            attempt: async (live, ct) => {
                var withdrawn = await KubeCommand.For(cluster)
                    .WithTenantId(writer.TenantId)
                    .WithResourceId(writer)
                    .WithKind(target.Kind)
                    .CoWriting(live)
                    .DeleteAsync(CascadePolicy.Background, ct)
                    .ConfigureAwait(false);

                // ⚠ The builder's co-owned DeleteAsync answers a bare Result, because that is the
                // interface's shape, and it codes the "not applied" outcomes: PreconditionFailed is
                // the stale race, which this loop answers by reading again. Everything else is
                // either done or a failure the reconciler has to see.
                if (withdrawn.TryGetError(out var error)) {
                    return error.Code == ErrorCode.PreconditionFailed
                        ? Result<ApplyOutcome>.Success(
                            new() { Result = ApplyResult.Stale, Target = target, Message = error.Message }
                        )
                        : Result<ApplyOutcome>.Failure(error);
                }

                return Result<ApplyOutcome>.Success(
                    new() {
                        Result = ApplyResult.Updated,
                        Target = target,
                        Message = $"resource {writer.Id:D}'s fragment was withdrawn from '{target}'."
                    }
                );
            },
            cancellationToken
        );
    }

    async Task<Result<ApplyOutcome>> RunAsync(
        ResourceId writer,
        ObjectRef target,
        Func<Result<ApplyOutcome>> absent,
        Func<KubeObject, CancellationToken, Task<Result<ApplyOutcome>>> attempt,
        CancellationToken cancellationToken
    ) {
        Result<ApplyOutcome>? last = null;

        for (var i = 0; i < MaxAttempts; i++) {
            var live = await cluster.GetAsync(target, cancellationToken).ConfigureAwait(false);

            if (live.TryGetError(out var readError)) {
                return readError.Code == ErrorCode.ResourceNotFound
                    ? absent()
                    : Result<ApplyOutcome>.Failure(readError);
            }

            last = await attempt(live.GetValueOrThrow(), cancellationToken).ConfigureAwait(false);

            if (last.Value.IsFailure || last.Value.GetValueOrThrow().Result != ApplyResult.Stale) {
                return last.Value;
            }
        }

        return last!.Value.IsSuccess
            ? Result<ApplyOutcome>.Success(
                last.Value.GetValueOrThrow() with {
                    Message = string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{target}' moved on every one of {MaxAttempts} read-then-apply attempts by resource "
                        + $"{writer.Id:D}; the next pass reads it again."
                    )
                }
            )
            : last.Value;
    }
}

/// <summary>
///     The <see cref="IKubeCoWriter" /> a <c>ReconcileContext</c> carries when the pass has no
///     cluster — every call fails by name.
/// </summary>
/// <remarks>
///     A clusterless provider has no object to co-write onto, so nothing legitimate reaches this;
///     what does reach it is a reconciler that declared no <c>RequiresCluster</c> and co-writes
///     anyway, and the message says which seam it should have declared.
/// </remarks>
public sealed class NoClusterCoWriter : IKubeCoWriter {
    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> ApplyFragmentAsync(
        ResourceId writer,
        ObjectRef target,
        string fragmentJson,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refuse(writer, target));

    /// <inheritdoc />
    public Task<Result<ApplyOutcome>> WithdrawFragmentAsync(
        ResourceId writer,
        ObjectRef target,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Refuse(writer, target));

    static Result<ApplyOutcome> Refuse(ResourceId writer, ObjectRef target) =>
        Result<ApplyOutcome>.Failure(
            ErrorCode.InternalError,
            $"'{writer.Path}' has no cluster connection and tried to co-write onto '{target}'. A "
            + "co-owned apply is a write into a cluster, so the type has to declare RequiresCluster "
            + "for the driver to hand its passes a connection — see ReconcileDriver."
        );
}
