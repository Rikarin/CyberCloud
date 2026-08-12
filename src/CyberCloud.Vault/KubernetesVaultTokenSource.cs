using CyberCloud.Core.Time;
using System.Net.Http.Json;
using System.Text.Json;

namespace CyberCloud.Vault;

/// <summary>
///     Logs the silo in to OpenBao with the projected service-account token its own pod carries.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>THIS IS THE ANSWER TO "WHAT CREDENTIAL DOES THE PLATFORM USE", AND THE POINT IS THAT
///         THERE ISN'T ONE.</b> A vault client needs a credential, and a credential in configuration
///         is the problem the vault exists to solve. The way out is that a pod already holds one it
///         did not have to be given: the kubelet projects a signed service-account token into
///         <see cref="VaultOptions.TokenFilePath" />, rotates it, and never writes it anywhere a
///         backup or a manifest can reach. OpenBao verifies that token against the cluster's own
///         <c>TokenReview</c> API and issues a lease. So the platform's identity is <i>which pod it
///         is</i>, which is a fact about the cluster rather than a string somebody has to keep
///         secret.
///     </para>
///     <para>
///         ⚠ <b>Kubernetes auth rather than JWT/OIDC against our own identity system, and
///         docs/plan/18 § Shape's Auth row is about the other direction.</b> That row —
///         <i>"JWT/OIDC against our identity system. Managed identities (11) map to OpenBao
///         roles"</i> — describes how a <b>tenant's</b> workload reaches the tenant-facing vault
///         product. Using it here would be circular: <c>CyberCloud.Identity</c> keeps TOTP shared
///         secrets and application client secrets behind <see cref="SecretRef" /> handles
///         (<c>UnavailableTotpSecrets</c> says so), so an identity system that had to be reachable
///         before the platform could read a secret would have to be reachable before it could verify
///         a credential. Kubernetes auth has no such loop: the trust root is the cluster the silo is
///         already running in, which <c>UseKubernetesHosting</c> (ADR-004) makes the silo depend on
///         anyway.
///     </para>
///     <para>
///         ⚠ <b>Not <c>HttpClusterOidcDiscovery</c> or <c>ProjectedTokenValidator</c>, and it is
///         worth saying why they do not fit.</b> Those verify a projected token that somebody
///         <i>sent us</i> — <c>ManagedIdentityGrain</c> checking a tenant workload's token against
///         the tenant cluster's OIDC issuer, inside the grain, on the raw token. This is the mirror
///         image: the platform <i>presents</i> its own token and OpenBao does the verifying. Nothing
///         in that pair is reusable here, because none of it is about presenting a credential. The
///         closest existing code is <c>CyberCloud.Sdk</c>'s <c>WorkloadIdentityCredential</c>, which
///         is client-side and reads its path from an environment variable.
///     </para>
///     <para>
///         ⚠ <b>The token file is re-read on every login rather than held.</b> Kubernetes rewrites a
///         projected token in place as it rotates, and a silo that read it once at startup would keep
///         presenting an expired assertion until it was restarted — which, for a process designed to
///         run for weeks, means "forever". <c>WorkloadIdentityCredential</c> re-reads for the same
///         reason and records it.
///     </para>
/// </remarks>
/// <param name="http">
///     The client to reach OpenBao with. Its <c>BaseAddress</c> is unused — every request is built
///     from <see cref="VaultOptions.Address" /> so that a misconfigured base address cannot silently
///     redirect a login.
/// </param>
/// <param name="options">Where OpenBao is and which role to log in as.</param>
/// <param name="clock">
///     Reads the current time. A test needs the lease arithmetic to be checkable without waiting
///     out a lease.
/// </param>
public sealed class KubernetesVaultTokenSource(HttpClient http, VaultOptions options, IClock clock)
    : IVaultTokenSource, IDisposable {
    readonly SemaphoreSlim gate = new(1, 1);

    VaultToken? cached;

    /// <summary>Releases the login gate.</summary>
    /// <remarks>
    ///     ⚠ Present because the gate is a <see cref="SemaphoreSlim" /> rather than a
    ///     <see cref="Lock" />, and it has to be: the login it guards is asynchronous, and a lock
    ///     cannot be held across an <c>await</c>. The container disposes this with the silo; nothing
    ///     else should.
    /// </remarks>
    public void Dispose() => gate.Dispose();

    /// <inheritdoc />
    public async ValueTask<Result<VaultToken>> GetAsync(CancellationToken cancellationToken = default) {
        // ⚠ The fast path reads the field without the gate, and that is safe for the reason a
        // reference read is always safe on .NET: it is atomic, so this either sees the previous
        // token or a newer one, never a torn one. A stale-but-unexpired token here is the correct
        // answer, not a race.
        var current = cached;
        if (current is not null && current.ExpiresAt > clock.UtcNow) {
            return Result<VaultToken>.Success(current);
        }

        await gate.WaitAsync(cancellationToken);

        try {
            // Re-checked inside the gate: a hundred concurrent reconcile passes finding an expired
            // token must produce one login, not a hundred. Each of those would be an audit entry in
            // OpenBao and a TokenReview call against the cluster's API server.
            current = cached;
            if (current is not null && current.ExpiresAt > clock.UtcNow) {
                return Result<VaultToken>.Success(current);
            }

            var login = await LoginAsync(cancellationToken);
            if (login.IsFailure) {
                return login;
            }

            cached = login.GetValueOrThrow();

            return login;
        } finally {
            gate.Release();
        }
    }

    /// <inheritdoc />
    public void Invalidate(VaultToken stale) {
        ArgumentNullException.ThrowIfNull(stale);

        // ⚠ ReferenceEquals rather than value equality, and rather than an unconditional clear. Two
        // resolves racing on a revoked token both call this; the first drops it, the second must not
        // drop whatever the first has since fetched. Value equality would work here too — the token
        // string differs — but reference identity is the property being relied on and saying so
        // keeps the next reader from "simplifying" it into an unconditional `cached = null`.
        if (ReferenceEquals(cached, stale)) {
            cached = null;
        }
    }

    async Task<Result<VaultToken>> LoginAsync(CancellationToken cancellationToken) {
        if (options.Role.Length == 0) {
            return Fail(
                VaultFailures.AuthenticationFailed(
                    "no role is configured. Set CyberCloud:Vault:Role to the OpenBao role this "
                    + "silo's service account is bound to."
                )
            );
        }

        string jwt;

        try {
            // ⚠ Read on every login, never cached. See the remarks on rotation.
            jwt = (await File.ReadAllTextAsync(options.TokenFilePath, cancellationToken)).Trim();
        } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) {
            return Fail(
                VaultFailures.AuthenticationFailed(
                    $"the projected service-account token at '{options.TokenFilePath}' could not be "
                    + $"read ({exception.GetType().Name}). A silo outside Kubernetes has no such "
                    + "file; a silo inside one that cannot read it has "
                    + "automountServiceAccountToken disabled, or projects the token somewhere else."
                )
            );
        }

        if (jwt.Length == 0) {
            return Fail(
                VaultFailures.AuthenticationFailed(
                    $"the projected service-account token at '{options.TokenFilePath}' is empty. "
                    + "This refuses rather than attempting a login with no assertion."
                )
            );
        }

        var url = $"{options.Address.TrimEnd('/')}/v1/auth/{options.AuthMountPath}/login";

        HttpResponseMessage response;

        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) {
                // ⚠ The one place in this assembly that puts a credential into a request body. It is
                // an assertion about this pod, it is signed, it is short-lived, and it goes over TLS
                // to one address. It is never logged and never assigned to a field.
                Content = JsonContent.Create(new LoginRequest(options.Role, jwt)),
            };

            if (options.Namespace.Length > 0) {
                request.Headers.Add(VaultHeaders.Namespace, options.Namespace);
            }

            response = await http.SendAsync(request, cancellationToken);
        } catch (HttpRequestException exception) {
            return Fail(
                VaultFailures.AuthenticationFailed(
                    $"OpenBao at {options.Address} could not be reached: {exception.Message}"
                )
            );
        } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return Fail(
                VaultFailures.AuthenticationFailed(
                    $"OpenBao at {options.Address} did not answer the login within "
                    + $"{options.RequestTimeout.TotalSeconds:0.#}s."
                )
            );
        }

        using (response) {
            if (!response.IsSuccessStatusCode) {
                return Fail(
                    VaultFailures.AuthenticationFailed(
                        $"OpenBao at {options.Address} refused the login as role '{options.Role}' "
                        + $"with HTTP {(int)response.StatusCode}. A 403 here means the pod's service "
                        + "account is not one the role is bound to, or the cluster's TokenReview "
                        + "call did not confirm the token; a 400 means the auth method is mounted "
                        + $"somewhere other than '{options.AuthMountPath}'. ⚠ Nothing about the "
                        + "request body is reproduced here, because it is the pod's token."
                    )
                );
            }

            LoginResponse? body;

            try {
                body = await response.Content.ReadFromJsonAsync<LoginResponse>(cancellationToken);
            } catch (Exception exception) when (exception is JsonException or NotSupportedException) {
                return Fail(
                    VaultFailures.AuthenticationFailed(
                        $"OpenBao at {options.Address} answered the login with something that is "
                        + "not the documented JSON envelope."
                    )
                );
            }

            var token = body?.Auth?.ClientToken ?? string.Empty;

            if (token.Length == 0) {
                return Fail(
                    VaultFailures.AuthenticationFailed(
                        $"OpenBao at {options.Address} answered the login with no auth.client_token. "
                        + "⚠ This refuses rather than carrying on with an empty token, which would "
                        + "turn one authentication fault into a permission-denied on every secret."
                    )
                );
            }

            // ⚠ Clamped at zero rather than trusted. A lease shorter than the skew — a role
            // configured with a 30-second TTL, say — would otherwise produce an expiry in the past
            // and a fresh login on every single resolve, which is a self-inflicted denial of service
            // against the auth mount. Zero means "use it once and log in again", which is the honest
            // behaviour for a lease that short.
            var lease = TimeSpan.FromSeconds(Math.Max(0, body!.Auth!.LeaseDuration));
            var usable = lease > options.TokenExpirySkew ? lease - options.TokenExpirySkew : TimeSpan.Zero;

            return Result<VaultToken>.Success(new(token, clock.UtcNow + usable));
        }
    }

    static Result<VaultToken> Fail(VaultRefusal refusal) =>
        Result<VaultToken>.Failure(refusal.Code, refusal.OperatorDetail);

    /// <summary>The documented login payload — OpenBao's Kubernetes auth method, <c>Login</c>.</summary>
    /// <param name="Role">The role name.</param>
    /// <param name="Jwt">The projected service-account token.</param>
    sealed record LoginRequest(
        [property: System.Text.Json.Serialization.JsonPropertyName("role")] string Role,
        [property: System.Text.Json.Serialization.JsonPropertyName("jwt")] string Jwt
    );

    /// <summary>The documented login response, narrowed to the three fields this client uses.</summary>
    /// <param name="Auth">The auth envelope.</param>
    /// <remarks>
    ///     ⚠ Narrowed on purpose. <c>policies</c>, <c>metadata</c> and <c>accessor</c> all come back
    ///     and none is read: <c>metadata</c> names the pod's service account and
    ///     <c>accessor</c> is a handle to the token, and a type with fields for them is a type
    ///     something will eventually log.
    /// </remarks>
    sealed record LoginResponse(
        [property: System.Text.Json.Serialization.JsonPropertyName("auth")] AuthEnvelope? Auth
    );

    /// <summary>The <c>auth</c> object of a login response.</summary>
    /// <param name="ClientToken">The token to send as <c>X-Vault-Token</c>.</param>
    /// <param name="LeaseDuration">How many seconds the token is good for.</param>
    sealed record AuthEnvelope(
        [property: System.Text.Json.Serialization.JsonPropertyName("client_token")] string? ClientToken,
        [property: System.Text.Json.Serialization.JsonPropertyName("lease_duration")] int LeaseDuration
    );
}

/// <summary>The OpenBao request headers this client sends.</summary>
/// <remarks>
///     ⚠ Spelled <c>X-Vault-*</c> and not <c>X-Bao-*</c>, which looks like a leftover and is not:
///     OpenBao is a fork and kept the header names, so its own API documentation and every example
///     against it use the <c>Vault</c> spelling. Renaming them here would stop the client working.
/// </remarks>
public static class VaultHeaders {
    /// <summary>The token header.</summary>
    public const string Token = "X-Vault-Token";

    /// <summary>The namespace header — docs/plan/18 § Shape, namespace per tenant.</summary>
    public const string Namespace = "X-Vault-Namespace";
}
