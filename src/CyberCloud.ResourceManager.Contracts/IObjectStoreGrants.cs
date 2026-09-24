using System.Globalization;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     Buckets on the platform's object store, and access keys scoped to one bucket each — for a
///     data plane that writes there itself. docs/plan/15 § Backup as a service.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             The other half of <see cref="IObjectStore" />, and the reason it is a second seam rather
///             than three more members on the first.
///         </b> <see cref="IObjectStore" /> is the platform <i>writing</i> bytes through its own
///         credential into its own bucket. This is the platform handing a <i>workload</i> somewhere to
///         write: a CloudNativePG cluster archives WAL and base backups from its own pods, in the
///         tenant's cluster, with a key it reads from a Kubernetes <c>Secret</c>. That key must open
///         one bucket and nothing else, and it must never be the platform's own — a tenant can read
///         every <c>Secret</c> in their namespace.
///     </para>
///     <para>
///         ⚠ <b>The store issues keys, the vault holds them, and <see cref="ObjectStoreCredentials" />
///         is the one place the two meet.</b> A key is never a property of a resource body and never
///         grain state: it goes from <see cref="IssueKeyAsync" /> straight into
///         <see cref="ISecretWriter.MintAsync" />, and every pass after the first reads it back from
///         the vault.
///     </para>
///     <para>
///         The implementation that answers is <c>SeaweedFsObjectStoreGrants</c> in
///         <c>CyberCloud.ObjectStorage</c>; a host that registers none gets
///         <c>UnavailableObjectStoreGrants</c>, which refuses by name.
///     </para>
/// </remarks>
public interface IObjectStoreGrants {
    /// <summary>
    ///     The S3 endpoint a workload in a tenant's cluster reaches the store at — which is not
    ///     necessarily where the platform reaches it. Empty when this host wires no store: the two
    ///     refusing defaults answer empty, and a real store always has an endpoint.
    /// </summary>
    /// <remarks>
    ///     ⚠ A reconciler reads the empty endpoint as "this deployment cannot grant" and fails its pass
    ///     terminally, naming the way out. Retrying would not help, because no amount of waiting wires a
    ///     store — #30's review found every default-bodied PostgreSQL server on the AppHost retrying
    ///     forever against <c>UnavailableObjectStoreGrants</c>.
    /// </remarks>
    string DataPlaneEndpoint { get; }

    /// <summary>Creates a bucket, or finds it already there.</summary>
    /// <param name="bucket">The bucket name — see <see cref="ObjectStoreCredentials.BucketFor" />.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> EnsureBucketAsync(string bucket, CancellationToken cancellationToken = default);

    /// <summary>Issues a new access key for a principal, scoped to one bucket.</summary>
    /// <param name="principal">The store-side name the key belongs to. Created when absent.</param>
    /// <param name="bucket">The one bucket the key may read, write and list.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    /// <returns>
    ///     The key. ⚠ Its secret half goes into the vault and nowhere else — see
    ///     <see cref="ObjectStoreCredentials.EnsureAsync" />.
    /// </returns>
    Task<Result<ObjectStoreKey>> IssueKeyAsync(
        string principal,
        string bucket,
        CancellationToken cancellationToken = default
    );

    /// <summary>Withdraws one key. Withdrawing a key that is not there succeeds.</summary>
    /// <param name="principal">The principal the key was issued to.</param>
    /// <param name="accessKeyId">The key's public half.</param>
    /// <param name="cancellationToken">Cancels the call.</param>
    Task<Result> RevokeKeyAsync(string principal, string accessKeyId, CancellationToken cancellationToken = default);
}

/// <summary>An access key the store issued.</summary>
/// <param name="AccessKeyId">The public half.</param>
/// <param name="SecretAccessKey">
///     The secret half. ⚠ A value for the length of one pass: it is minted into the vault and read
///     back from there, and nothing that outlives the pass may hold it.
/// </param>
public sealed record ObjectStoreKey(string AccessKeyId, string SecretAccessKey) {
    /// <inheritdoc />
    /// <remarks>⚠ Overridden so a key that reaches a log line reaches it without its secret.</remarks>
    public override string ToString() => $"ObjectStoreKey {{ AccessKeyId = {AccessKeyId} }}";
}

/// <summary>
///     The one route from a store-issued key to a vault-held one, shared by every reconciler that
///     gives a workload a bucket.
/// </summary>
public static class ObjectStoreCredentials {
    /// <summary>The vault field the access key id is minted under.</summary>
    public const string AccessKeyIdField = "accessKeyId";

    /// <summary>The vault field the secret access key is minted under.</summary>
    public const string SecretAccessKeyField = "secretAccessKey";

    /// <summary>The bucket a resource's backups go to: <c>{prefix}-{resourceId:N}</c>.</summary>
    /// <param name="prefix">A short lower-case family prefix, for example <c>pg</c>.</param>
    /// <param name="resourceId">The resource's GUID.</param>
    /// <remarks>
    ///     ⚠ <b>The GUID and not the name</b>, because a name is reused: a server deleted and created
    ///     again under the same name is a new server, and CloudNativePG refuses to archive into a
    ///     location that already holds another cluster's WAL. The GUID is lower-case hex, so the
    ///     result is a legal S3 bucket name for any prefix of up to 28 such characters.
    /// </remarks>
    public static string BucketFor(string prefix, Guid resourceId) =>
        prefix + "-" + resourceId.ToString("N", CultureInfo.InvariantCulture);

    /// <summary>
    ///     The vault path a resource's store key is held at:
    ///     <c>platform/{type}/{tenantId}/{resourceId}/objectstore</c>.
    /// </summary>
    /// <param name="id">The resource, with its GUID resolved.</param>
    /// <remarks>
    ///     ⚠ <b>Under <c>platform/</c>, not <c>tenants/</c></b>, for the reason the key vault's root
    ///     moved there on issue-30-keyvault: a tenant can put a <c>tenants/…</c> path into a body for
    ///     the platform to resolve — <c>cloudInit.userData</c> is one — and a key the platform issued
    ///     for a workload is the platform's to hand out, through the <c>Secret</c> it renders, and not
    ///     the tenant's to name.
    /// </remarks>
    public static string VaultPathFor(ResourceId id) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"platform/{id.Type}/{id.TenantId:D}/{id.Id:D}/objectstore"
        );

    /// <summary>
    ///     Makes sure a bucket exists and the resource holds a key to it, minted once into the vault,
    ///     and returns the key the vault holds.
    /// </summary>
    /// <param name="grants">The store.</param>
    /// <param name="secrets">Where the key is read back from.</param>
    /// <param name="writer">Where it is minted.</param>
    /// <param name="vaultPath">The path — see <see cref="VaultPathFor" />.</param>
    /// <param name="principal">The store-side principal — the bucket name serves.</param>
    /// <param name="bucket">The bucket.</param>
    /// <param name="cancellationToken">Cancels the calls.</param>
    /// <returns>The key the vault holds, or the first failure.</returns>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>The vault decides who won, not this method.</b> Two silos reconciling one resource
    ///         both find the path empty and both ask the store for a key; <see cref="ISecretWriter" />'s
    ///         mint-once makes exactly one of them the key that is kept, and the other withdraws its
    ///         own at the store rather than leaving a live key nobody holds. The loser then reads the
    ///         winner's back, so both passes render the same <c>Secret</c>.
    ///     </para>
    ///     <para>
    ///         ⚠ <b>Issue, then mint — and a crash between the two leaves a key at the store that no
    ///         vault holds.</b> It opens only the resource's own bucket, and the next pass issues
    ///         another. What closes it is listing the principal's keys and withdrawing the ones the
    ///         vault does not name, which the seam has no member for; recorded as owed in
    ///         charts/managed/postgres/conformance.yaml § owed, <c>an-issued-key-can-outlive-a-crash</c>.
    ///     </para>
    /// </remarks>
    public static async Task<Result<ObjectStoreKey>> EnsureAsync(
        IObjectStoreGrants grants,
        ISecretResolver secrets,
        ISecretWriter writer,
        string vaultPath,
        string principal,
        string bucket,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(grants);
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentNullException.ThrowIfNull(writer);

        var bucketMade = await grants.EnsureBucketAsync(bucket, cancellationToken);
        if (bucketMade.TryGetError(out var bucketError)) {
            return Result<ObjectStoreKey>.Failure(bucketError);
        }

        var held = await ReadAsync(secrets, vaultPath, cancellationToken);
        if (held.IsSuccess || held.Error!.Code != ErrorCode.ResourceNotFound) {
            return held;
        }

        var issued = await grants.IssueKeyAsync(principal, bucket, cancellationToken);
        if (issued.TryGetError(out var issueError)) {
            return Result<ObjectStoreKey>.Failure(issueError);
        }

        var key = issued.GetValueOrThrow();
        var minted = await writer.MintAsync(
            vaultPath,
            new Dictionary<string, string>(StringComparer.Ordinal) {
                [AccessKeyIdField] = key.AccessKeyId,
                [SecretAccessKeyField] = key.SecretAccessKey
            },
            cancellationToken
        );

        if (minted.TryGetError(out var mintError)) {
            // The key is live at the store and held nowhere; withdrawing it is the tidy half, and a
            // failure to withdraw is the orphan the remarks describe rather than a second error.
            _ = await grants.RevokeKeyAsync(principal, key.AccessKeyId, cancellationToken);
            return Result<ObjectStoreKey>.Failure(mintError);
        }

        if (!minted.GetValueOrThrow().Minted) {
            _ = await grants.RevokeKeyAsync(principal, key.AccessKeyId, cancellationToken);
            return await ReadAsync(secrets, vaultPath, cancellationToken);
        }

        return Result<ObjectStoreKey>.Success(key);
    }

    /// <summary>
    ///     The key the vault already holds at a path, issuing nothing — for a reader of a bucket that
    ///     is not its own.
    /// </summary>
    /// <param name="secrets">Where the key is read from.</param>
    /// <param name="vaultPath">The path — see <see cref="VaultPathFor" />.</param>
    /// <param name="cancellationToken">Cancels the reads.</param>
    /// <returns>The key, or <see cref="ErrorCode.ResourceNotFound" /> when the path holds none.</returns>
    /// <remarks>
    ///     ⚠ <b>A restored PostgreSQL server reads its SOURCE's key this way</b> (#30's reclaim): the
    ///     source may be gone, its <c>Secret</c> with it, and the key outlives both in the vault until
    ///     a purge reclaims the path. <see cref="EnsureAsync" /> would be wrong there twice over — it
    ///     would make the source's bucket if it were missing and issue a second key to it.
    /// </remarks>
    public static async Task<Result<ObjectStoreKey>> HeldAsync(
        ISecretResolver secrets,
        string vaultPath,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(secrets);
        ArgumentException.ThrowIfNullOrEmpty(vaultPath);

        return await ReadAsync(secrets, vaultPath, cancellationToken);
    }

    static async Task<Result<ObjectStoreKey>> ReadAsync(
        ISecretResolver secrets,
        string vaultPath,
        CancellationToken cancellationToken
    ) {
        var id = await secrets.ResolveAsync(new() { Path = vaultPath, Field = AccessKeyIdField }, cancellationToken);
        if (id.TryGetError(out var idError)) {
            return Result<ObjectStoreKey>.Failure(idError);
        }

        var secret = await secrets.ResolveAsync(new() { Path = vaultPath, Field = SecretAccessKeyField }, cancellationToken);
        if (secret.TryGetError(out var secretError)) {
            return Result<ObjectStoreKey>.Failure(secretError);
        }

        return Result<ObjectStoreKey>.Success(new(id.GetValueOrThrow(), secret.GetValueOrThrow()));
    }
}

/// <summary>
///     The <see cref="IObjectStoreGrants" /> a hand-built <see cref="ReconcileContext" /> carries: it
///     refuses everything, naming the property to set.
/// </summary>
/// <remarks>
///     The sibling of <see cref="RefusingObjectStore" />, for the same reason: the registered
///     default, <c>UnavailableObjectStoreGrants</c>, lives in the manager's assembly and says more
///     about the wiring.
/// </remarks>
public sealed class RefusingObjectStoreGrants : IObjectStoreGrants {
    const string Because =
        "This reconcile context carries no object-store grants. A pass driven by ReconcileDriver always "
        + "carries the host's; a context built by hand has to supply them through ReconcileContext.Grants.";

    /// <inheritdoc />
    public string DataPlaneEndpoint => string.Empty;

    /// <inheritdoc />
    public Task<Result> EnsureBucketAsync(string bucket, CancellationToken cancellationToken = default) =>
        Task.FromResult(Result.Failure(ErrorCode.InternalError, Because));

    /// <inheritdoc />
    public Task<Result<ObjectStoreKey>> IssueKeyAsync(
        string principal,
        string bucket,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result<ObjectStoreKey>.Failure(ErrorCode.InternalError, Because));

    /// <inheritdoc />
    public Task<Result> RevokeKeyAsync(
        string principal,
        string accessKeyId,
        CancellationToken cancellationToken = default
    ) =>
        Task.FromResult(Result.Failure(ErrorCode.InternalError, Because));
}
