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
    ///     Builds the resolver <c>KubeApiClientFactory.ResolveKubeconfig</c> takes.
    /// </summary>
    /// <param name="root">The directory a reference must resolve inside.</param>
    /// <returns>A resolver that reads the file, or refuses and says why.</returns>
    /// <remarks>
    ///     ⚠ Every refusal names the reference and the root. A kubeconfig that cannot be found is
    ///     otherwise indistinguishable, from the reconciler's side, from a cluster that is down: both
    ///     arrive as a failed apply, hours after the create, in a log nobody is reading.
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

            if (!Uri.TryCreate(credentialRef, UriKind.Absolute, out var uri)
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
