// ⚠ For `Result<string>`. See StorageAccountListKeysHandler for why this import is safe beside the
// ErrorCode alias.

using CyberCloud.Core;
using System.Text.Json.Nodes;

namespace CyberCloud.Providers.Storage;

/// <summary>
///     Serves <c>POST …/accounts/{account}/fileShares/{name}/listMountTargets</c>: the claim a pod
///     names, and the filer, path and collection a VM's <c>weed mount</c> names.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>IT READS THE CLUSTER AND MINTS NOTHING</b>, which makes it the second shape of action
///         handler in this provider. <see cref="StorageAccountListKeysHandler" /> resolves a vault;
///         this one observes a claim, because the one fact a caller cannot compute — the filer path —
///         is decided by the CSI external-provisioner when it binds the claim, and lives in
///         <c>spec.volumeName</c>. Everything else is a function of the address.
///     </para>
///     <para>
///         ⚠ <b>A claim that is not yet bound is refused, not answered with an empty path.</b>
///         <see cref="StorageFileShares.ListMountTargetsResponse" /> makes every field required, and
///         the dispatcher validates the body against it — so an empty <c>path</c> would be a handler
///         failing its own contract. The refusal is <see cref="ErrorCode.OperationInProgress" />
///         (<c>409</c>) rather than <c>404</c>: the resource exists and reports <c>Succeeded</c>; what
///         has not happened yet is the operator's half, and the message says so.
///     </para>
/// </remarks>
public sealed class StorageFileShareListMountTargetsHandler : IResourceActionHandler {
    /// <inheritdoc />
    public ResourceTypeName Type => StorageFileShares.Type;

    /// <inheritdoc />
    public string Action => StorageFileShares.ListMountTargetsAction;

    /// <inheritdoc />
    public async Task<Result<string>> InvokeAsync(
        ActionContext context,
        CancellationToken cancellationToken = default
    ) {
        if (context.Cluster is not { } cluster) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{context.Id.Path}' has no cluster connection, and a share's mount target is read off "
                + "its claim in the cluster. The type declares RequiresCluster, so ActionDispatcher "
                + "should have refused this call."
            );
        }

        var claimRef = StorageFileShares.ClaimRef(context.Namespace, context.Id);
        var read = await cluster.GetAsync(claimRef, cancellationToken);

        if (read.TryGetError(out var readError)) {
            return Result<string>.Failure(readError);
        }

        var volumeName = StorageFileShares.VolumeNameOf(read.GetValueOrThrow().Json);

        if (volumeName.Length == 0) {
            return Result<string>.Failure(
                ErrorCode.OperationInProgress,
                $"'{claimRef}' is applied and not yet bound, so the share has no filer path to hand out. "
                + "The account's CSI driver provisions it once its controller is running; ask again in "
                + "a moment. A claim that stays unbound is a driver whose seaweedRef does not resolve "
                + "— see the SeaweedCSIDriver's ClusterReachable condition."
            );
        }

        var account = StorageFileShares.AccountOf(context.Id);

        // ⚠ The property names are the response schema's pointers with the slash removed, and the
        // dispatcher checks that rather than trusting it.
        return Result<string>.Success(
            new JsonObject {
                ["claimName"] = claimRef.Name,
                ["accessMode"] = StorageFileShares.AccessMode,
                ["filer"] = StorageFileShares.FilerAddress(context.Namespace, account),
                ["path"] = StorageFileShares.FilerPathOf(volumeName),
                // ⚠ The collection is the volume's name because that is what the CSI mounter passes —
                // `collection: path.Base(filerPath)` — and the quota is enforced per collection. A VM
                // mount naming a different collection would write outside the ceiling.
                ["collection"] = volumeName
            }.ToJsonString()
        );
    }
}
