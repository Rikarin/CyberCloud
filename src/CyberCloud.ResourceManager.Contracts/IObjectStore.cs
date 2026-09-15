using System.Collections.Immutable;
using System.Text;

namespace CyberCloud.ResourceManager.Contracts;

/// <summary>
///     The platform's own object storage — where a platform host keeps bytes that are neither grain
///     state nor a tenant's bucket. docs/plan/13 § Artifact feeds, docs/plan/15 § Object storage.
/// </summary>
/// <remarks>
///     <para>
///         ⚠
///         <b>
///             A platform seam beside <see cref="ISecretResolver" /> and <see cref="ISecretWriter" />,
///             and it arrived the way piece 5 of docs/plan/12 § The pattern, once did.
///         </b> The first type whose data plane is a platform host rather than an object in a
///         tenant's cluster — <c>CyberCloud.ContainerRegistry/feeds</c> — has artefacts to keep
///         somewhere, and docs/plan/13 says where: "a single .NET service backed by SeaweedFS".
///         That is the platform's SeaweedFS and not a tenant's <c>CyberCloud.Storage/accounts</c>,
///         which three catalogue rows already record they cannot reach from a reconciler
///         (<c>charts/managed/harbor/conformance.yaml § owed</c>,
///         <c>storage-backend-is-not-the-tenants-bucket</c>). A reconciler reaches this store the way
///         it reaches a vault: through <see cref="ReconcileContext.Objects" />, which the driver fills
///         from the host's registration and a hand-built context leaves refusing.
///     </para>
///     <para>
///         <b>What it is not:</b> a tenant-facing API, a second copy of any resource's desired
///         state, or a place for anything that fits in a grain. The feeds host writes a package's
///         bytes here and its metadata to a durable grain; a reconciler deletes a feed's whole prefix
///         on teardown and reads nothing else.
///     </para>
///     <para>
///         ⚠ <b>Keys are the caller's contract with itself.</b> <see cref="ObjectKeys.Validate" />
///         refuses what no S3-compatible store accepts; everything about the layout under a prefix
///         — that a feed's artefacts live under <c>{tenantId:N}/{feedId:N}/</c> and nothing else
///         does — is a promise the writer makes and the deleter relies on. A store cannot enforce
///         that promise and does not try.
///     </para>
///     <para>
///         The two implementations that answer with bytes are <c>InMemoryObjectStore</c> in the
///         conformance harness and <c>S3ObjectStore</c> in <c>CyberCloud.ObjectStorage</c>; a host
///         that registers neither gets <c>UnavailableObjectStore</c>, which refuses by name.
///     </para>
/// </remarks>
public interface IObjectStore {
    /// <summary>Writes one object, replacing whatever the key held.</summary>
    /// <param name="key">The object's key — see <see cref="ObjectKeys.Validate" />.</param>
    /// <param name="content">The bytes. Whole, because an artefact is hashed before it is stored.</param>
    /// <param name="contentType">The media type served back with the bytes.</param>
    /// <param name="cancellationToken">Cancels the write.</param>
    Task<Result> PutAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken = default
    );

    /// <summary>Reads one object.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>
    ///     The object, or <see cref="ErrorCode.ResourceNotFound" /> when the key holds nothing. ⚠ The
    ///     caller owns the returned <see cref="StoredObject.Content" /> and disposes it.
    /// </returns>
    Task<Result<StoredObject>> GetAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Removes one object. Removing a key that holds nothing succeeds.</summary>
    /// <param name="key">The object's key.</param>
    /// <param name="cancellationToken">Cancels the delete.</param>
    Task<Result> DeleteAsync(string key, CancellationToken cancellationToken = default);

    /// <summary>Every key under a prefix, in key order.</summary>
    /// <param name="prefix">The prefix — a key or a key's leading segments, ending in <c>/</c> or not.</param>
    /// <param name="cancellationToken">Cancels the listing.</param>
    /// <remarks>
    ///     ⚠ Whole, not paged. A store pages internally and this method loops; the callers it has
    ///     — a teardown deleting a feed, a test asserting one is gone — want every key or none, and
    ///     a paged seam would put a continuation token into a reconciler that has no state to keep
    ///     it in.
    /// </remarks>
    Task<Result<ImmutableArray<string>>> ListAsync(string prefix, CancellationToken cancellationToken = default);
}

/// <summary>One object read back from an <see cref="IObjectStore" />.</summary>
/// <param name="Content">The bytes. ⚠ The caller disposes it.</param>
/// <param name="Length">How many bytes <paramref name="Content" /> yields.</param>
/// <param name="ContentType">The media type stored beside the bytes.</param>
public sealed record StoredObject(Stream Content, long Length, string ContentType) : IDisposable {
    /// <inheritdoc />
    public void Dispose() => Content.Dispose();
}

/// <summary>
///     What every <see cref="IObjectStore" /> accepts as a key, stated once so the in-memory store
///     and the S3 one refuse the same things.
/// </summary>
public static class ObjectKeys {
    /// <summary>The longest key an S3-compatible store accepts, in UTF-8 bytes.</summary>
    public const int MaxBytes = 1024;

    /// <summary>Checks a key.</summary>
    /// <param name="key">The candidate.</param>
    /// <returns>
    ///     Success, or <see cref="ErrorCode.InvalidRequestBody" /> naming what was wrong. A key is
    ///     non-empty, at most <see cref="MaxBytes" /> bytes, has no leading slash, no empty segment,
    ///     no <c>.</c> or <c>..</c> segment, and no control character. ⚠ The segment rules are what
    ///     keep a caller-supplied name — a package id, a Maven path — from stepping out of the prefix
    ///     the writer put it under.
    /// </returns>
    public static Result Validate(string key) {
        if (string.IsNullOrEmpty(key)) {
            return Result.Failure(ErrorCode.InvalidRequestBody, "An object key cannot be empty.");
        }

        if (Encoding.UTF8.GetByteCount(key) > MaxBytes) {
            return Result.Failure(
                ErrorCode.InvalidRequestBody,
                $"An object key is at most {MaxBytes} bytes, and this one is longer."
            );
        }

        if (key[0] == '/') {
            return Result.Failure(ErrorCode.InvalidRequestBody, $"'{key}' starts with a slash; keys are relative to the bucket.");
        }

        foreach (var segment in key.Split('/')) {
            if (segment.Length == 0) {
                return Result.Failure(ErrorCode.InvalidRequestBody, $"'{key}' has an empty segment.");
            }

            if (segment is "." or "..") {
                return Result.Failure(ErrorCode.InvalidRequestBody, $"'{key}' has a '{segment}' segment.");
            }
        }

        foreach (var character in key) {
            if (char.IsControl(character)) {
                return Result.Failure(ErrorCode.InvalidRequestBody, "An object key cannot carry a control character.");
            }
        }

        return Result.Success;
    }
}
