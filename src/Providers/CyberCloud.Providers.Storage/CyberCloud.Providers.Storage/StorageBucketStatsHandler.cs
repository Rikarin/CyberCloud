// ⚠ For `Result<string>`. See StorageAccountListKeysHandler for why this import is safe beside the
// ErrorCode alias.

using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Serves <c>POST …/accounts/{account}/buckets/{name}/stats</c>: the object count and byte total
///     the operator last sampled, and when.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             THE FIRST HANDLER THAT CAME OFF <c>actions-without-handlers.txt</c> BY READING THE
///             OPERATOR RATHER THAN BY BUILDING A PIPELINE.
///         </b> The row said the figures had no source —
///         no S3 admin client, nothing scraping the metrics port — and that was true of this tree and
///         false of the operator it depends on: <c>bucket_usage.go</c> asks the master for
///         <c>collection.list</c> every five minutes and writes the per-bucket answer into
///         <c>status.usage</c>. A bucket's object is what <see cref="StorageBucketReconciler" />
///         already reads on every pass, so this handler is one <c>GetAsync</c> and a projection.
///     </para>
///     <para>
///         ⚠ <b>A bucket the refresher has not reached yet is refused, not answered with zeros.</b>
///         <see cref="StorageBuckets.StatsResponse" /> makes all three fields required, and a zero
///         count with a timestamp nobody sampled would be an answer a caller reads as "empty". The
///         refusal is <see cref="ErrorCode.OperationInProgress" /> — the resource exists, the
///         operator's half is still coming — with the interval in the message.
///     </para>
///     <para>
///         ⚠ <b><c>sampledAt</c> is the operator's clock, not this process's.</b> <c>lastUpdated</c> is
///         when <c>collection.list</c> was read; substituting <c>IClock.UtcNow</c> would be the exact
///         thing the response schema's own description warns against — a sampled number presented as
///         live.
///     </para>
/// </remarks>
public sealed class StorageBucketStatsHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => StorageBuckets.Type;

    /// <inheritdoc />
    public string Action => StorageBuckets.StatsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a bucket's stats are read off its "
                + "Bucket in the cluster. The type declares RequiresCluster, so ActionDispatcher should "
                + "have refused this call."
            );
        }

        var target = StorageBuckets.BucketRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(target, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        if (StorageBuckets.UsageOf(read.GetValueOrThrow().Json) is not { } usage) {
            return Result<string>.Failure(
                ErrorCode.OperationInProgress,
                $"'{target}' has no status.usage yet: the SeaweedFS operator samples every bucket's "
                + "object count and size from collection.list every five minutes, and this bucket has "
                + "not been sampled since it was created. Ask again after the next refresh."
            );
        }

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it.
        return Result<string>.Success(
            new JsonObject {
                ["objectCount"] = usage.ObjectCount, ["sizeBytes"] = usage.SizeBytes, ["sampledAt"] = usage.SampledAt
            }.ToJsonString()
        );
    }
}
