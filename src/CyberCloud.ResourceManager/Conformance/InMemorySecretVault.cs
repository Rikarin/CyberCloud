using System.Collections.Concurrent;

namespace CyberCloud.ResourceManager.Conformance;

/// <summary>
///     A vault in a dictionary: mints once, resolves what it minted. For conformance runs and
///     provider tests, never for a host.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>NEVER REGISTER THIS IN A HOST. IT HOLDS EVERY CREDENTIAL IT HAS EVER SEEN, IN
///         PLAINTEXT, IN PROCESS MEMORY, WITH NO POLICY, NO AUDIT AND NO EXPIRY.</b> It exists
///         because <see cref="ISecretWriter" />'s only real implementation talks to OpenBao over
///         HTTP, and a provider whose reconciler mints a credential cannot converge in any suite
///         without something that answers. <c>UnavailableSecretWriter</c> stays the
///         <c>TryAdd</c> default and a host has to opt into
///         <c>AddOpenBaoSecretResolver</c>; nothing wires this except a test that names it.
///     </para>
///     <para>
///         ⚠ <b>Here rather than in a test project because three suites need the same one.</b> The
///         shared provider conformance suite, the k3s-backed cluster conformance harness and each
///         provider's own unit tests all drive a reconciler that mints, and they live in projects
///         that do not reference each other. Three copies of a mint-once dictionary is three chances
///         for one of them to be wrong about the semantic under test.
///         <see cref="ReconcilerConformance" /> is the precedent for conformance support shipping in
///         this assembly.
///     </para>
///     <para>
///         ⚠ <b>It implements the mint-once rule for real, which is the only reason it is worth
///         having.</b> A double that overwrote on every call would make the idempotence assertion
///         pass against a writer that does not have the property — the test would be measuring
///         itself. <see cref="MintAsync" /> uses <c>TryAdd</c> on a concurrent dictionary, which is
///         the in-memory shape of OpenBao's <c>cas=0</c>: first writer wins, everybody else is told
///         so, and two threads racing produce one document rather than two.
///     </para>
/// </remarks>
public sealed class InMemorySecretVault : ISecretResolver, ISecretWriter {
    readonly ConcurrentDictionary<string, IReadOnlyDictionary<string, string>> documents = new(StringComparer.Ordinal);

    /// <summary>How many mints actually wrote. ⚠ Not how many were asked for.</summary>
    /// <remarks>
    ///     The number an idempotence test asserts on: drive two reconcile passes and this is still 1.
    /// </remarks>
    public int Writes { get; private set; }

    /// <summary>Whether the next mint fails, for testing the partial-failure order.</summary>
    /// <remarks>
    ///     ⚠ Set by a test that wants to prove the resource is not created when the credential cannot
    ///     be. It fails the <i>write</i> and not the read, because those are the two halves that can
    ///     fail independently.
    /// </remarks>
    public bool RefuseMint { get; set; }

    /// <inheritdoc />
    public Task<Result<SecretMint>> MintAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fields);

        if (RefuseMint) {
            return Task.FromResult(
                Result<SecretMint>.Failure(
                    ErrorCode.InternalError,
                    $"The test vault was told to refuse a mint at '{path}'."
                )
            );
        }

        var copy = new Dictionary<string, string>(fields, StringComparer.Ordinal);
        var minted = documents.TryAdd(path, copy);

        if (minted) {
            Writes++;
        }

        return Task.FromResult(Result<SecretMint>.Success(new(minted)));
    }

    /// <inheritdoc />
    public Task<Result<string>> ResolveAsync(
        SecretRef reference,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.IsEmpty) {
            return Task.FromResult(
                Result<string>.Failure(ErrorCode.InternalError, $"'{reference}' names nothing.")
            );
        }

        return Task.FromResult(
            documents.TryGetValue(reference.Path, out var document)
            && document.TryGetValue(reference.Field, out var value)
                ? Result<string>.Success(value)
                : Result<string>.Failure(
                    ErrorCode.ResourceNotFound,
                    $"The test vault holds nothing at '{reference}'."
                )
        );
    }

    /// <summary>Reads a field without going through a handle. For an assertion, not for a caller.</summary>
    /// <param name="path">The vault path.</param>
    /// <param name="field">The field.</param>
    /// <returns>The value, or <see langword="null" /> when there is none.</returns>
    public string? Peek(string path, string field) =>
        documents.TryGetValue(path, out var document) && document.TryGetValue(field, out var value)
            ? value
            : null;

    /// <summary>Whether anything has been minted at a path.</summary>
    /// <param name="path">The vault path.</param>
    public bool Holds(string path) => documents.ContainsKey(path);
}
