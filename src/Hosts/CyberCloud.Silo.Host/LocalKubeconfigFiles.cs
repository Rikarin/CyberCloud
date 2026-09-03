using CyberCloud.Core;
using System.Globalization;
using ErrorCode = CyberCloud.Core.ErrorCode;

namespace CyberCloud.Silo.Host;

/// <summary>
///     Resolves a cluster's <c>CredentialRef</c> to a kubeconfig held in a file on this machine.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>This closes the one seam that made every production silo unable to reach any
///         cluster.</b> <c>KubeApiClientFactory.ResolveKubeconfig</c> is a nullable delegate and its
///         null case is a refusal — <i>"No kubeconfig resolver is registered, so cluster … cannot be
///         reached … the resolver has to be supplied at registration time"</i> — and no host supplied
///         one. So <c>AddCyberCloudKubernetes</c> put <c>ClusterConnectionGrain</c> in the silo,
///         <see cref="SiloComposition" /> wired <c>GrainClusterConnectionFactory</c> over it, and the
///         first apply of the first reconcile still failed with an <c>InternalError</c> naming a
///         registration nobody had made. The refusal is correct; what was missing was any caller.
///     </para>
///     <para>
///         ⚠ <b>It is opt-in and it is rooted, because "read a kubeconfig off the filesystem" is a
///         capability rather than a convenience.</b> With no root configured this registers nothing
///         and the silo keeps the refusal above, which is the right posture for a cluster whose
///         kubeconfig belongs in Vault (docs/plan/09 § Cluster connections, docs/plan/18). With a root
///         configured, a <c>CredentialRef</c> must be a <c>file:</c> URI resolving <i>inside</i> that
///         root: a <c>ClusterConnectionDescriptor</c> is written by a reconciler rather than by a
///         tenant, but a credential reference that could name any path on the host would make the
///         blast radius of a provider bug the whole filesystem.
///     </para>
///     <para>
///         ⚠ <b>Local development is the caller that exists today.</b> <c>CyberCloud.AppHost</c> runs
///         k3s and bind-mounts its kubeconfig to <c>&lt;AppHost project&gt;/.k3s</c> (ADR-014); that
///         directory is what it passes as the root. The vault-backed resolver replaces this one when
///         <c>CyberCloud.KeyVault</c> lands — the shape of the seam does not change, only where the
///         bytes come from.
///     </para>
/// </remarks>
static class LocalKubeconfigFiles {
    /// <summary>The configuration key naming the directory kubeconfig files may be read from.</summary>
    public const string RootKey = "CyberCloud:Silo:KubeconfigRoot";

    /// <summary>The one credential-reference scheme this resolver answers.</summary>
    public const string Scheme = "file";

    /// <summary>
    ///     What a reference has to start with, checked before it is parsed.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Because comparing <c>Uri.Scheme</c> alone answers a narrower question than it looks
    ///     like it does, and the difference is per-platform.</b> <see cref="Uri" /> treats an
    ///     <i>implicit</i> file path as a <c>file:</c> URI, so on Linux and macOS a bare
    ///     <c>/etc/kubernetes/admin.conf</c> parsed as absolute, reported scheme <c>file</c> and was
    ///     resolved — while the same reference on Windows failed to parse and was refused as "not a
    ///     'file:' credential reference". A <c>CredentialRef</c> that means different things on
    ///     different hosts is not a reference, and the silo that reads it is the one running on
    ///     Linux. Every writer in this tree spells the scheme (<c>new Uri(path).AbsoluteUri</c>), so
    ///     requiring it costs nothing and removes the divergence.
    /// </remarks>
    const string SchemePrefix = Scheme + ":";

    /// <summary>
    ///     Builds the resolver <c>KubeApiClientFactory.ResolveKubeconfig</c> takes.
    /// </summary>
    /// <param name="root">The directory a reference must resolve inside.</param>
    /// <returns>A resolver that reads the file, or refuses and says why.</returns>
    /// <remarks>
    ///     ⚠ Every refusal names the reference or the path it resolved to, and the one that is about
    ///     the root names the root as well. A kubeconfig that cannot be found is otherwise
    ///     indistinguishable, from the reconciler's side, from a cluster that is down: both arrive as
    ///     a failed apply, hours after the create, in a log nobody is reading.
    ///     <para>
    ///         ⚠ <b>The root check is lexical and does not follow links.</b> It compares resolved
    ///         paths, so <c>..</c> cannot climb out of the root, but a symbolic link placed
    ///         <i>inside</i> the root is followed wherever it points. That is the right boundary for
    ///         what this guards — a <c>CredentialRef</c> written by a reconciler — and it is not a
    ///         boundary against whoever can write into the root directory, who is the operator that
    ///         configured it.
    ///     </para>
    /// </remarks>
    public static Func<string, CancellationToken, Task<Result<string>>> ResolverFor(string root) {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));

        return async (credentialRef, cancellationToken) => {
            if (string.IsNullOrWhiteSpace(credentialRef)) {
                return Result<string>.Failure(
                    ErrorCode.InvalidRequestBody,
                    "This cluster's connection carries no credential reference, so there is nothing "
                    + "to resolve a kubeconfig from."
                );
            }

            if (!credentialRef.StartsWith(SchemePrefix, StringComparison.Ordinal)
                || !Uri.TryCreate(credentialRef, UriKind.Absolute, out var uri)
                || !string.Equals(uri.Scheme, Scheme, StringComparison.Ordinal)) {
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{credentialRef}' is not a '{Scheme}:' credential reference, and this silo "
                        + $"resolves no other scheme. A kubeconfig kept anywhere else — a Kubernetes "
                        + $"Secret, a vault path — needs the resolver that reads it; see "
                        + $"KubeApiClientFactory.ResolveKubeconfig."
                    )
                );
            }

            var path = Path.GetFullPath(uri.LocalPath);

            // ⚠ The separator matters. Comparing the prefix without it lets '/k3s-elsewhere' pass a
            // root of '/k3s', which is the classic way a rooted path check is not one.
            if (!path.StartsWith(full + Path.DirectorySeparatorChar, StringComparison.Ordinal)) {
                return Result<string>.Failure(
                    ErrorCode.AuthorizationFailed,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"'{credentialRef}' resolves to '{path}', which is outside the kubeconfig "
                        + $"root '{full}' this silo was configured with ({RootKey})."
                    )
                );
            }

            if (!File.Exists(path)) {
                return Result<string>.Failure(
                    ErrorCode.ResourceNotFound,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"There is no kubeconfig at '{path}', so the cluster naming "
                        + $"'{credentialRef}' cannot be reached."
                    )
                );
            }

            try {
                return Result<string>.Success(await File.ReadAllTextAsync(path, cancellationToken));
            } catch (IOException unreadable) {
                return Result<string>.Failure(
                    ErrorCode.InternalError,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The kubeconfig at '{path}' could not be read: {unreadable.Message}"
                    )
                );
            } catch (UnauthorizedAccessException refused) {
                return Result<string>.Failure(
                    ErrorCode.AuthorizationFailed,
                    string.Create(
                        CultureInfo.InvariantCulture,
                        $"The kubeconfig at '{path}' could not be read: {refused.Message}"
                    )
                );
            }
        };
    }
}
