using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Globalization;
using System.Security.Cryptography;

namespace CyberCloud.ResourceManager.Conformance;

/// <summary>
///     Buckets and bucket-scoped keys in a dictionary. For conformance runs and provider tests —
///     never for a host.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NEVER REGISTER THIS IN A HOST</b>, for <see cref="InMemoryObjectStore" />'s reason: a
///         key it issues opens nothing anywhere. It exists so that a PostgreSQL server with backups on
///         — the default body — can converge in the Docker-free suites, where no SeaweedFS answers.
///         What it does keep honest is the bookkeeping the one real semantic depends on: every key it
///         has issued, which of them were withdrawn, and which buckets exist, so a test can assert
///         that a race's loser withdrew its key and that a second pass issued none.
///     </para>
/// </remarks>
public sealed class InMemoryObjectStoreGrants : IObjectStoreGrants {
    readonly ConcurrentDictionary<string, byte> buckets = new(StringComparer.Ordinal);
    readonly ConcurrentDictionary<string, string> live = new(StringComparer.Ordinal);
    readonly ConcurrentQueue<(string Principal, string AccessKeyId)> revoked = new();
    int issued;

    /// <summary>The endpoint the conformance harness renders into a server's backup section.</summary>
    public const string Endpoint = "http://objectstore.conformance.invalid:8333";

    /// <inheritdoc />
    public string DataPlaneEndpoint => Endpoint;

    /// <summary>Every bucket made so far.</summary>
    public ImmutableArray<string> Buckets => [.. buckets.Keys.Order(StringComparer.Ordinal)];

    /// <summary>How many keys have been issued in total.</summary>
    public int Issued => Volatile.Read(ref issued);

    /// <summary>Every key still live, access key id to principal.</summary>
    public ImmutableDictionary<string, string> Live => live.ToImmutableDictionary(StringComparer.Ordinal);

    /// <summary>Every withdrawal, in order.</summary>
    public ImmutableArray<(string Principal, string AccessKeyId)> Revoked => [.. revoked];

    /// <summary>Whether every call fails, for testing what a server does when the store is down.</summary>
    public bool Refuse { get; set; }

    /// <inheritdoc />
    public Task<Result> EnsureBucketAsync(string bucket, CancellationToken cancellationToken = default) {
        if (Refuse) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the object store refused the call"));
        }

        buckets.TryAdd(bucket, 0);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result<ObjectStoreKey>> IssueKeyAsync(
        string principal,
        string bucket,
        CancellationToken cancellationToken = default
    ) {
        if (Refuse) {
            return Task.FromResult(
                Result<ObjectStoreKey>.Failure(ErrorCode.InternalError, "the object store refused the call")
            );
        }

        var number = Interlocked.Increment(ref issued);
        var key = new ObjectStoreKey(
            "CONFORMANCE" + number.ToString("D9", CultureInfo.InvariantCulture),
            Convert.ToHexString(RandomNumberGenerator.GetBytes(20))
        );

        live[key.AccessKeyId] = principal;
        return Task.FromResult(Result<ObjectStoreKey>.Success(key));
    }

    /// <inheritdoc />
    public Task<Result> RevokeKeyAsync(
        string principal,
        string accessKeyId,
        CancellationToken cancellationToken = default
    ) {
        live.TryRemove(accessKeyId, out _);
        revoked.Enqueue((principal, accessKeyId));
        return Task.FromResult(Result.Success);
    }
}
