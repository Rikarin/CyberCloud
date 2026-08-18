using System.Text;
using System.Text.Json;

namespace CyberCloud.Kubernetes.Contracts;

/// <summary>
///     Reads one value out of a <c>core/v1</c> <c>Secret</c> that something else wrote.
/// </summary>
/// <remarks>
///     <para>
///         <b>Every managed service in the catalogue whose credential an operator generates needs
///         this, and each of them was about to grow its own.</b> CloudNativePG writes
///         <c>{cluster}-app</c>, mariadb-operator writes <c>{server}-password</c>, the RabbitMQ
///         cluster-operator writes <c>{cluster}-default-user</c>, opensearch-operator writes
///         <c>{cluster}-admin-password</c>, Cluster API writes <c>{cluster}-kubeconfig</c>. A
///         <c>listKeys</c> handler on any of those types is the same three steps — address the
///         <c>Secret</c>, read it back, un-base64 one key — and three providers had already written
///         the <see cref="Kind" /> constant separately before the fourth needed it.
///     </para>
///     <para>
///         ⚠ <b><c>data</c> only, never <c>stringData</c>.</b> <c>stringData</c> is write-only: the
///         API server folds it into <c>data</c> and the field is absent from every read. A helper
///         that fell back to it would work against a hand-written fixture and find nothing against a
///         live cluster, which is the worst possible split.
///     </para>
///     <para>
///         ⚠ <b>A missing key is a failure that names the key and not the value.</b> The whole point
///         of this type is to be called with a credential on the other side of it, so a message
///         quoting what it found would put the credential in whatever logged the failure — the same
///         rule <c>ActionDispatcher</c> applies to a response that fails validation.
///     </para>
/// </remarks>
public static class KubeSecret {
    /// <summary>The <c>core/v1</c> <c>Secret</c> kind.</summary>
    /// <remarks>
    ///     ⚠ The core group, so <see cref="GroupVersionKind.Group" /> is empty and
    ///     <see cref="GroupVersionKind.IsCoreGroup" /> is what reads it. The plural is carried rather
    ///     than derived, for the reason that property gives.
    /// </remarks>
    public static GroupVersionKind Kind { get; } =
        new() { Group = "", Version = "v1", Kind = "Secret", Plural = "secrets" };

    /// <summary>Addresses a <c>Secret</c> by namespace and name.</summary>
    /// <param name="ns">The namespace the resource lives in.</param>
    /// <param name="name">The secret's own name.</param>
    public static ObjectRef Ref(string ns, string name) =>
        new() { Kind = Kind, Namespace = ns, Name = name };

    /// <summary>Reads one key's value out of a <c>Secret</c> read back from a cluster.</summary>
    /// <param name="secret">The object <c>IKubeClusterConnection.GetAsync</c> returned.</param>
    /// <param name="key">The key inside <c>data</c>, for example <c>password</c>.</param>
    /// <returns>
    ///     The decoded value, or a failure naming the secret and the key. ⚠ Never the value — see the
    ///     remarks on this type.
    /// </returns>
    public static Result<string> Value(KubeObject secret, string key) {
        ArgumentNullException.ThrowIfNull(secret);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        JsonDocument parsed;
        try {
            parsed = JsonDocument.Parse(secret.Json);
        } catch (JsonException) {
            return Result<string>.Failure(
                ErrorCode.InternalError,
                $"'{secret.Ref}' was read back and its body is not JSON, so '{key}' cannot be taken "
                + "out of it."
            );
        }

        using (parsed) {
            if (!parsed.RootElement.TryGetProperty("data", out var data)
                || data.ValueKind != JsonValueKind.Object) {
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"'{secret.Ref}' has no 'data' object, so '{key}' cannot be read from it. A Secret "
                    + "that was written through 'stringData' still reads back as 'data' — the API "
                    + "server folds the two — so an absent 'data' means the secret is empty rather "
                    + "than that it was written the other way."
                );
            }

            if (!data.TryGetProperty(key, out var encoded) || encoded.ValueKind != JsonValueKind.String) {
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"'{secret.Ref}' carries no string key '{key}'. Whatever writes that secret names "
                    + "its keys, and this is the name this platform expects — if the writer changed "
                    + "it, the expectation here is what is stale."
                );
            }

            byte[] bytes;
            try {
                bytes = Convert.FromBase64String(encoded.GetString() ?? string.Empty);
            } catch (FormatException) {
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    $"'{secret.Ref}' key '{key}' is not base64. Every value in a Secret's 'data' is, so "
                    + "this is a malformed object rather than a value this platform should try to use."
                );
            }

            return Result<string>.Success(Encoding.UTF8.GetString(bytes));
        }
    }
}
