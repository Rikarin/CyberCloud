using CyberCloud.ResourceManager.Contracts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text.Json;

namespace CyberCloud.Vault;

/// <summary>
///     Reads a <see cref="SecretRef" /> out of OpenBao's <c>kv-v2</c> engine. The one
///     <see cref="ISecretResolver" /> that returns a value.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/08 § What the resource manager deliberately does not do routes <c>Hold
///         secrets</c> to <c>ISecretResolver → OpenBao</c>, and this is that arrow. A reconciler asks
///         for a handle it found in desired state, gets the value, puts it into a Kubernetes
///         <c>Secret</c> it is applying, and the value is gone when the pass returns.
///     </para>
///     <para>
///         ⚠ <b>NOTHING IS CACHED, AND THAT IS A DECISION WITH A PRICE RATHER THAN AN OVERSIGHT.</b>
///         The obvious optimisation is a short-lived value cache — a reconcile pass may resolve the
///         same handle twice, and each resolve is an HTTPS round trip inside a grain turn bounded at
///         30 seconds. It is refused for three reasons, in order of how much they cost:
///     </para>
///     <list type="number">
///         <item>
///             <b>A cache is a place a plaintext secret lives.</b> Without one, a value exists in a
///             local for the length of one reconcile pass and in nothing that outlives the call. With
///             one it lives in a process-wide dictionary for the cache's lifetime, so a heap dump, a
///             crash dump or a debugger on any silo hands over every credential that silo has touched
///             since it started. That is a much larger blast radius than the round trip is worth.
///         </item>
///         <item>
///             <b>Rotation stops working the way docs/plan/18 § Rotation says it does.</b> Rotation
///             is "the new value is created, both are valid for a configured window, consumers roll,
///             the old is revoked". A consumer holding a cached value does not roll — it keeps
///             rendering the old credential until its entry expires, and the revocation at the end of
///             the window breaks it. A cache turns a graceful rotation into a delayed outage.
///         </item>
///         <item>
///             <b>Revocation stops working.</b> docs/plan/18's <c>getValue</c> is
///             <c>FullyConsistent</c> on the ReBAC check because <i>"a revoked user reading a secret
///             from a stale cache is exactly the incident this exists to prevent"</i>. A value cache
///             on the platform's side of that check re-introduces the same staleness one layer down.
///         </item>
///     </list>
///     <para>
///         ⚠ <b>The token <i>is</i> cached, which is the opposite decision, and the asymmetry is the
///         point.</b> <see cref="IVaultTokenSource" /> holds a login for its lease, because the thing
///         being cached there is a lease OpenBao itself issued with a duration it chose — caching it
///         respects the vault's own statement about validity, where caching a value overrides it.
///         What that costs is bounded and named: a revoked token is not noticed until a read is
///         refused, which is why <see cref="IVaultTokenSource.Invalidate" /> exists and why a
///         <c>403</c> is retried exactly once.
///     </para>
///     <para>
///         ⚠ <b>What this does not do: attribute a read to a caller.</b> docs/plan/18 makes the
///         platform hold "a broad token per namespace", so OpenBao's own audit log records that
///         <i>the platform</i> read a path and cannot record which tenant, user or request caused it.
///         The audit line below carries what this layer can honestly know — the path, the field, the
///         version, the outcome, and the ambient trace id — and never the value. Attributing a read
///         to a <i>user</i> is docs/plan/18's tenant-facing <c>getValue</c> action, which is ReBAC
///         checked at the resource manager and is a different piece of work.
///     </para>
/// </remarks>
/// <param name="http">The client to reach OpenBao with.</param>
/// <param name="tokens">Where the <c>X-Vault-Token</c> comes from.</param>
/// <param name="options">Where OpenBao is and which mounts to use.</param>
/// <param name="logger">
///     Where the audit line and the operator half of every refusal go. ⚠ Optional, and a silo with
///     no logger loses the audit trail rather than the refusal — the refusal is on the
///     <see cref="Result{T}" />.
/// </param>
public sealed class OpenBaoSecretResolver(
    HttpClient http,
    IVaultTokenSource tokens,
    VaultOptions options,
    ILogger<OpenBaoSecretResolver>? logger = null
) : ISecretResolver {
    /// <summary>
    ///     The header a joined audit trail needs on OpenBao's side.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>Sent unconditionally and audited only if OpenBao is told to audit it.</b> A request
    ///     header reaches an OpenBao audit entry only when it is listed under
    ///     <c>/sys/config/auditing/request-headers</c>; unlisted, it is dropped. So this is the
    ///     platform's half of a join that the vault's deployment has to complete, and
    ///     <c>deploy/</c> is where that belongs. Worth knowing while wiring it: OpenBao 2.4 refuses
    ///     to enable an audit device over the API at all — <i>"cannot enable audit device via API;
    ///     use declarative, config-based audit device management instead"</i> — so the device is part
    ///     of the server's configuration file, not something a bootstrap job turns on.
    /// </remarks>
    public const string CorrelationHeader = "X-Correlation-Id";

    /// <inheritdoc />
    public async Task<Result<string>> ResolveAsync(
        SecretRef reference,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(reference);

        if (reference.IsEmpty) {
            return Refuse(VaultFailures.EmptyHandle(reference));
        }

        var token = await tokens.GetAsync(cancellationToken);

        if (token.IsFailure) {
            // ⚠ The token source's failures are already operator-detail strings, so they are logged
            // and replaced rather than passed through. A login failure reaching a tenant verbatim
            // would name the vault address, the role and the token file path.
            return CannotAuthenticate(token.Error!);
        }

        var first = await ReadAsync(reference, token.GetValueOrThrow(), cancellationToken);

        // ⚠ RETRIED EXACTLY ONCE, AND ONLY ON A 403. A token OpenBao has stopped accepting and a
        // policy that never covered this path are the same status code, so the only way to tell them
        // apart is to log in again and ask once more. Once, not in a loop: a genuine policy denial
        // would otherwise re-login on every resolve forever, and each login is a TokenReview call
        // against the cluster's API server.
        if (first.Retryable) {
            tokens.Invalidate(token.GetValueOrThrow());

            var fresh = await tokens.GetAsync(cancellationToken);

            if (fresh.IsFailure) {
                return CannotAuthenticate(fresh.Error!);
            }

            return Complete(reference, await ReadAsync(reference, fresh.GetValueOrThrow(), cancellationToken));
        }

        return Complete(reference, first);
    }

    /// <summary>Logs a login failure and hands the caller the tenant-safe half of it.</summary>
    /// <param name="error">The token source's failure. Its message is operator detail.</param>
    Result<string> CannotAuthenticate(Error error) {
        logger?.LogError("{Detail}", error.Message);

        return Result<string>.Failure(
            error.Code,
            VaultFailures.AuthenticationFailed(string.Empty).TenantMessage
        );
    }

    Result<string> Complete(SecretRef reference, ReadOutcome outcome) {
        if (outcome.Refusal is not null) {
            return Refuse(outcome.Refusal);
        }

        // ⚠ THE AUDIT LINE, AND EVERY FIELD ON IT IS AN ADDRESS. docs/plan/18: audited "with the
        // caller, the correlation id and the secret name (never the value)". The caller is the piece
        // this layer cannot supply — see the remarks — so what is here is the handle, the vault and
        // the trace, and the omission is named rather than papered over with a guess.
        logger?.LogInformation(
            "Vault read succeeded. path={Path} field={Field} version={Version} mount={Mount} "
            + "address={Address} trace={TraceId}",
            reference.Path,
            reference.Field,
            reference.Version.Length == 0 ? "current" : reference.Version,
            options.KvMountPath,
            options.Address,
            Activity.Current?.TraceId.ToString() ?? "none"
        );

        return Result<string>.Success(outcome.Value!);
    }

    Result<string> Refuse(VaultRefusal refusal) {
        logger?.LogError("{Detail}", refusal.OperatorDetail);

        return Result<string>.Failure(refusal.Code, refusal.TenantMessage);
    }

    async Task<ReadOutcome> ReadAsync(
        SecretRef reference,
        VaultToken token,
        CancellationToken cancellationToken
    ) {
        HttpResponseMessage response;

        try {
            using var request = new HttpRequestMessage(HttpMethod.Get, Url(reference));

            request.Headers.Add(VaultHeaders.Token, token.Value);

            if (options.Namespace.Length > 0) {
                request.Headers.Add(VaultHeaders.Namespace, options.Namespace);
            }

            if (Activity.Current is { } activity) {
                request.Headers.Add(CorrelationHeader, activity.TraceId.ToString());
            }

            response = await http.SendAsync(request, cancellationToken);
        } catch (HttpRequestException exception) {
            return ReadOutcome.Failed(
                VaultFailures.Unreachable($"{options.Address} could not be reached: {exception.Message}")
            );
        } catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return ReadOutcome.Failed(
                VaultFailures.Unreachable(
                    $"{options.Address} did not answer within {options.RequestTimeout.TotalSeconds:0.#}s. "
                    + "A resolve runs inside a reconcile pass bounded at 30 seconds, so the client "
                    + "gives up well before the pass does."
                )
            );
        }

        using (response) {
            switch ((int)response.StatusCode) {
                case 404:
                    return ReadOutcome.Failed(
                        VaultFailures.NotFound(reference, options.Address, options.KvMountPath)
                    );

                case 403:
                    return ReadOutcome.Denied(
                        VaultFailures.PermissionDenied(
                            reference,
                            options.Address,
                            options.KvMountPath,
                            options.Role
                        )
                    );

                case >= 200 and < 300:
                    break;

                default:
                    return ReadOutcome.Failed(
                        VaultFailures.Unreachable(
                            $"{options.Address} answered HTTP {(int)response.StatusCode} for a read of "
                            + $"'{options.KvMountPath}/data/{reference.Path}'. A 503 here is a sealed "
                            + "vault, which docs/plan/18 § Shape auto-unseals from a transit engine in "
                            + "the root cluster."
                        )
                    );
            }

            JsonDocument document;

            try {
                await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

                document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            } catch (JsonException) {
                return ReadOutcome.Failed(VaultFailures.Unreadable("it is not JSON"));
            }

            using (document) {
                if (!document.RootElement.TryGetProperty("data", out var outer)
                    || !outer.TryGetProperty("data", out var data)
                    || data.ValueKind != JsonValueKind.Object) {
                    return ReadOutcome.Failed(
                        VaultFailures.Unreadable(
                            "it has no data.data object. A kv-v2 read nests the secret one level "
                            + "deeper than kv-v1, so this is what a kv-v1 mount configured as "
                            + "CyberCloud:Vault:KvMountPath looks like from here"
                        )
                    );
                }

                if (!data.TryGetProperty(reference.Field, out var field)
                    || field.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined) {
                    return ReadOutcome.Failed(
                        VaultFailures.FieldMissing(
                            reference,
                            options.Address,
                            options.KvMountPath,
                            data.EnumerateObject().Select(x => x.Name)
                        )
                    );
                }

                // ⚠ Strings only, and a number is refused rather than rendered. A kv-v2 value can be
                // any JSON scalar, and `GetRawText()` on a number would hand back something that
                // looks right and differs from what was written the moment a port is stored as 5432
                // rather than "5432". A credential is a string; anything else is a mistake at the
                // writing end and is better found here than in a rendered manifest.
                if (field.ValueKind != JsonValueKind.String) {
                    return ReadOutcome.Failed(
                        VaultFailures.Unreadable(
                            $"the '{reference.Field}' field is a JSON {field.ValueKind} rather than a "
                            + "string. ⚠ The value is not reproduced here"
                        )
                    );
                }

                var value = field.GetString()!;

                // ⚠ An empty string that OpenBao really holds is still refused, and this is the last
                // gate before a value reaches a manifest. Somebody wrote "" to that field — a
                // provisioning job that failed halfway, or a template that rendered nothing — and
                // handing it back would be exactly the outcome UnavailableSecretResolver exists to
                // prevent, arrived at by a different route.
                if (value.Length == 0) {
                    return ReadOutcome.Failed(
                        VaultFailures.FieldMissing(
                            reference,
                            options.Address,
                            options.KvMountPath,
                            data.EnumerateObject().Select(x => x.Name)
                        )
                    );
                }

                return ReadOutcome.Read(value);
            }
        }
    }

    /// <summary>Builds the <c>kv-v2</c> read URL for a handle.</summary>
    /// <param name="reference">The handle.</param>
    /// <remarks>
    ///     ⚠ <b>Each path segment is escaped separately, so the slashes survive and everything else
    ///     does not.</b> A <see cref="SecretRef.Path" /> is hierarchical —
    ///     <c>tenants/{tenantId}/postgres/main</c> — so escaping the whole string would turn its
    ///     structure into <c>%2F</c> and address a single secret whose name contains slashes.
    ///     Escaping nothing would let a path segment carrying <c>?</c> or <c>#</c> rewrite the query
    ///     the client thinks it is sending.
    /// </remarks>
    string Url(SecretRef reference) {
        var path = string.Join('/', reference.Path.Split('/').Select(Uri.EscapeDataString));
        var url =
            $"{options.Address.TrimEnd('/')}/v1/{Uri.EscapeDataString(options.KvMountPath)}/data/{path}";

        return reference.Version.Length == 0
            ? url
            : $"{url}?version={Uri.EscapeDataString(reference.Version)}";
    }

    /// <summary>What one read attempt produced.</summary>
    /// <param name="Value">The secret, or <see langword="null" /> when the attempt failed.</param>
    /// <param name="Refusal">Why it failed, or <see langword="null" /> when it did not.</param>
    /// <param name="Retryable">
    ///     Whether a fresh login is worth trying. ⚠ True only for a <c>403</c> on the read — see the
    ///     retry comment in <see cref="ResolveAsync" />.
    /// </param>
    readonly record struct ReadOutcome(string? Value, VaultRefusal? Refusal, bool Retryable) {
        public static ReadOutcome Read(string value) => new(value, null, false);

        public static ReadOutcome Failed(VaultRefusal refusal) => new(null, refusal, false);

        public static ReadOutcome Denied(VaultRefusal refusal) => new(null, refusal, true);
    }
}
