using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace CyberCloud.ResourceManager.Conformance;

/// <summary>
///     An object store in a dictionary. For conformance runs, the feeds host's protocol suite and
///     provider tests — never for a host.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NEVER REGISTER THIS IN A HOST.</b> Every artefact it is given lives in process memory
///         and dies with the process. It exists because <see cref="IObjectStore" />'s only real
///         implementation talks to an S3 endpoint over HTTP, and a type whose data plane keeps bytes
///         there — <c>CyberCloud.ContainerRegistry/feeds</c> — cannot converge, tear down or serve a
///         single package in any suite without something that answers.
///         <c>UnavailableObjectStore</c> stays the <c>TryAdd</c> default and a host has to opt into
///         <c>AddS3ObjectStore</c>; nothing wires this except a test that names it.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in a test project</b> for the reason <see cref="InMemorySecretVault" />
///         gives: the shared provider suite, the provider's own tests and the feeds host's suite live
///         in projects that do not reference each other, and three dictionaries is three chances to
///         be wrong about the one semantic worth testing — that a listing under a prefix returns
///         exactly the keys a put left there and a delete removed, in key order, the way an S3
///         <c>ListObjectsV2</c> does.
///     </para>
/// </remarks>
public sealed class InMemoryObjectStore : IObjectStore {
    readonly ConcurrentDictionary<string, (byte[] Bytes, string ContentType)> objects = new(StringComparer.Ordinal);

    /// <summary>How many objects the store holds right now.</summary>
    public int Count => objects.Count;

    /// <summary>Whether every write fails, for testing what a push does when the store is down.</summary>
    public bool RefuseWrites { get; set; }

    /// <inheritdoc />
    public Task<Result> PutAsync(
        string key,
        ReadOnlyMemory<byte> content,
        string contentType,
        CancellationToken cancellationToken = default
    ) {
        if (ObjectKeys.Validate(key).TryGetError(out var invalid)) {
            return Task.FromResult(Result.Failure(invalid));
        }

        if (RefuseWrites) {
            return Task.FromResult(Result.Failure(ErrorCode.InternalError, "the object store refused the write"));
        }

        objects[key] = (content.ToArray(), contentType);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result<StoredObject>> GetAsync(string key, CancellationToken cancellationToken = default) {
        if (ObjectKeys.Validate(key).TryGetError(out var invalid)) {
            return Task.FromResult(Result<StoredObject>.Failure(invalid));
        }

        return Task.FromResult(
            objects.TryGetValue(key, out var stored)
                ? Result<StoredObject>.Success(new(new MemoryStream(stored.Bytes, writable: false), stored.Bytes.Length, stored.ContentType))
                : Result<StoredObject>.Failure(ErrorCode.ResourceNotFound, $"'{key}' holds nothing.")
        );
    }

    /// <inheritdoc />
    public Task<Result> DeleteAsync(string key, CancellationToken cancellationToken = default) {
        if (ObjectKeys.Validate(key).TryGetError(out var invalid)) {
            return Task.FromResult(Result.Failure(invalid));
        }

        _ = objects.TryRemove(key, out _);
        return Task.FromResult(Result.Success);
    }

    /// <inheritdoc />
    public Task<Result<ImmutableArray<string>>> ListAsync(string prefix, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(prefix);

        return Task.FromResult(
            Result<ImmutableArray<string>>.Success(
                [
                    .. objects.Keys
                        .Where(x => x.StartsWith(prefix, StringComparison.Ordinal))
                        .Order(StringComparer.Ordinal)
                ]
            )
        );
    }

    /// <summary>Empties the store.</summary>
    public void Reset() {
        objects.Clear();
        RefuseWrites = false;
    }
}
