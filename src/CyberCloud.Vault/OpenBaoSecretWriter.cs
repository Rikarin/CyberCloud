using CyberCloud.ResourceManager.Contracts;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;

namespace CyberCloud.Vault;

/// <summary>
///     Mints a credential into OpenBao's <c>kv-v2</c> engine, once. The write half of
///     <see cref="OpenBaoSecretResolver" />.
/// </summary>
/// <remarks>
///     <para>
///         docs/plan/12 § The pattern, once, piece 5 — "credential provisioning into the tenant's
///         Vault" — is this class. Nothing in the tree wrote to a vault before it, which is why
///         <c>CyberCloud.Storage/accounts</c> rendered a reference to a <c>Secret</c> nobody could
///         produce and its S3 gateway came up answering every request as an administrator.
///     </para>
///     <para>
///         ⚠ <b>MINT-ONCE IS <c>cas=0</c>, ENFORCED BY THE VAULT AND NOT BY A READ FIRST.</b> OpenBao's
///         <c>kv-v2</c> takes <c>options.cas</c> on a write and refuses when the current version is
///         not the one named; <c>cas: 0</c> means "write only if this path has never held anything".
///         A read-then-write here would look identical in a single-silo test and be wrong in
///         production: two silos reconciling the same resource would both read absent, both write, and
///         the tenant would hold whichever credential lost the race while the data plane honoured the
///         other. The vault is the only place that check can be atomic, so the check lives there.
///     </para>
///     <para>
///         ⚠ <b>A <c>cas</c> refusal is a SUCCESS carrying <c>Minted: false</c>.</b> The caller's goal
///         is that a credential exists at the path, and the second reconcile pass reaching that goal
///         without writing is the correct outcome. Reporting it as a conflict would fail every pass
///         after the first, which is every pass.
///     </para>
///     <para>
///         ⚠ <b><c>cas</c> only works when the mount requires it or the request asks for it, and this
///         asks.</b> A <c>kv-v2</c> mount has a <c>cas_required</c> setting that defaults to off;
///         sending <c>options.cas</c> per request works either way, so nothing here depends on how the
///         mount was configured. Worth knowing while deploying: with <c>cas_required</c> off and this
///         field omitted, a write silently creates version 2 — which is exactly the rotation-shaped
///         data loss this class exists to make unrepresentable.
///     </para>
///     <para>
///         ⚠ <b>Nothing is cached and nothing is logged but addresses</b>, for the three reasons
///         <see cref="OpenBaoSecretResolver" /> sets out at length. The audit line below names the
///         path, the field <i>names</i>, the mount and the trace, and never a value.
///     </para>
/// </remarks>
/// <param name="http">The client to reach OpenBao with.</param>
/// <param name="tokens">Where the <c>X-Vault-Token</c> comes from.</param>
/// <param name="options">Where OpenBao is and which mount to write to.</param>
/// <param name="logger">Where the audit line and the operator half of a refusal go.</param>
public sealed class OpenBaoSecretWriter(
    HttpClient http,
    IVaultTokenSource tokens,
    VaultOptions options,
    ILogger<OpenBaoSecretWriter>? logger = null
) : ISecretWriter {
    /// <inheritdoc />
    public async Task<Result<SecretMint>> MintAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        CancellationToken cancellationToken = default
    ) {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(fields);

        if (path.Length == 0) {
            return Refuse(
                ErrorCode.InternalError,
                "A credential cannot be minted at an empty vault path.",
                "MintAsync was called with an empty path. A path is an address and a caller that "
                + "passed none meant to pass a real one — the same rule SecretRef.IsEmpty states."
            );
        }

        // ⚠ An empty document is refused rather than written. A path that exists and holds no fields
        // reads back as a missing field from every consumer, which is indistinguishable from the mint
        // never having happened — except that cas=0 will now never fire again, so the credential can
        // never be minted at all.
        if (fields.Count == 0) {
            return Refuse(
                ErrorCode.InternalError,
                "A credential cannot be minted with no fields.",
                $"MintAsync was called for '{path}' with an empty field set. Writing it would occupy "
                + "the path with a document holding nothing, and cas=0 would then refuse every later "
                + "mint — so the credential could never be created."
            );
        }

        foreach (var pair in fields) {
            if (pair.Key.Length == 0 || pair.Value.Length == 0) {
                return Refuse(
                    ErrorCode.InternalError,
                    "A credential cannot be minted with an empty field name or value.",
                    $"MintAsync was called for '{path}' with an empty name or value. "
                    + "OpenBaoSecretResolver refuses an empty value on the way out — see the last gate "
                    + "in its ReadAsync — so writing one produces a secret nothing can ever read. "
                    + "⚠ The values are not reproduced here."
                );
            }
        }

        var token = await tokens.GetAsync(cancellationToken);

        if (token.IsFailure) {
            var error = token.Error!;
            logger?.LogError("{Detail}", error.Message);

            return Result<SecretMint>.Failure(
                error.Code,
                VaultFailures.AuthenticationFailed(string.Empty).TenantMessage
            );
        }

        var first = await WriteAsync(path, fields, token.GetValueOrThrow(), cancellationToken);

        // Retried exactly once and only on a 403, for the reason OpenBaoSecretResolver gives: a token
        // OpenBao has stopped accepting and a policy that never covered this path are the same status
        // code, and telling them apart costs one login.
        if (first.Retryable) {
            tokens.Invalidate(token.GetValueOrThrow());

            var fresh = await tokens.GetAsync(cancellationToken);

            if (fresh.IsFailure) {
                var refreshError = fresh.Error!;
                logger?.LogError("{Detail}", refreshError.Message);

                return Result<SecretMint>.Failure(
                    refreshError.Code,
                    VaultFailures.AuthenticationFailed(string.Empty).TenantMessage
                );
            }

            return Complete(path, fields, await WriteAsync(path, fields, fresh.GetValueOrThrow(), cancellationToken));
        }

        return Complete(path, fields, first);
    }

    Result<SecretMint> Complete(
        string path,
        IReadOnlyDictionary<string, string> fields,
        WriteOutcome outcome
    ) {
        if (outcome.Refusal is { } refusal) {
            logger?.LogError("{Detail}", refusal.OperatorDetail);

            return Result<SecretMint>.Failure(refusal.Code, refusal.TenantMessage);
        }

        // ⚠ THE AUDIT LINE, AND THE FIELD NAMES ARE ON IT BECAUSE THEY ARE ADDRESSES. What a mint
        // wrote is exactly what a later read will ask for by name, so an operator diagnosing "the
        // gateway says the field is missing" needs to see which names went in. The values never
        // appear — the same split OpenBaoSecretResolver's read line makes.
        logger?.LogInformation(
            "Vault mint {Result}. path={Path} fields=[{Fields}] mount={Mount} address={Address} "
            + "trace={TraceId}",
            outcome.Minted ? "wrote a new secret" : "found one already there and left it alone",
            path,
            string.Join(", ", fields.Keys),
            options.KvMountPath,
            options.Address,
            Activity.Current?.TraceId.ToString() ?? "none"
        );

        return Result<SecretMint>.Success(new(outcome.Minted));
    }

    async Task<WriteOutcome> WriteAsync(
        string path,
        IReadOnlyDictionary<string, string> fields,
        VaultToken token,
        CancellationToken cancellationToken
    ) {
        HttpResponseMessage response;

        try {
            using var request = new HttpRequestMessage(HttpMethod.Post, Url(path)) {
                Content = new StringContent(Payload(fields), Encoding.UTF8, "application/json")
            };

            request.Headers.Add(VaultHeaders.Token, token.Value);

            if (options.Namespace.Length > 0) {
                request.Headers.Add(VaultHeaders.Namespace, options.Namespace);
            }

            if (Activity.Current is { } activity) {
                request.Headers.Add(OpenBaoSecretResolver.CorrelationHeader, activity.TraceId.ToString());
            }

            response = await http.SendAsync(request, cancellationToken);
        }
        catch (HttpRequestException exception) {
            return WriteOutcome.Failed(
                VaultFailures.Unreachable($"{options.Address} could not be reached: {exception.Message}")
            );
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) {
            return WriteOutcome.Failed(
                VaultFailures.Unreachable(
                    $"{options.Address} did not answer within {options.RequestTimeout.TotalSeconds:0.#}s "
                    + "for a mint."
                )
            );
        }

        using (response) {
            switch ((int)response.StatusCode) {
                case >= 200 and < 300:
                    return WriteOutcome.Wrote;

                // ⚠ 400 IS WHERE MINT-ONCE ACTUALLY LANDS, AND IT IS NOT AN ERROR HERE. OpenBao
                // answers a failed check-and-set with 400 and a body naming the check, not with 409 —
                // so the status alone cannot tell "the secret is already there" from "the payload was
                // malformed", and the body has to be read. Anything that is not a cas failure is
                // reported as one of ours, because a malformed payload is this class's bug.
                case 400:
                    return await CheckAndSetFailedAsync(response, cancellationToken)
                        ? WriteOutcome.AlreadyThere
                        : WriteOutcome.Failed(
                            VaultFailures.Unreadable(
                                $"{options.Address} refused a mint of "
                                + $"'{options.KvMountPath}/data/{path}' with HTTP 400 and the reason is "
                                + "not a check-and-set failure. ⚠ The payload is not reproduced here"
                            )
                        );

                case 403:
                    return WriteOutcome.Denied(
                        VaultFailures.PermissionDenied(
                            new() { Path = path, Field = string.Empty },
                            options.Address,
                            options.KvMountPath,
                            options.Role
                        )
                    );

                default:
                    return WriteOutcome.Failed(
                        VaultFailures.Unreachable(
                            $"{options.Address} answered HTTP {(int)response.StatusCode} for a mint of "
                            + $"'{options.KvMountPath}/data/{path}'. A 503 here is a sealed vault."
                        )
                    );
            }
        }
    }

    /// <summary>
    ///     Whether a <c>400</c> is OpenBao saying the path already holds a secret.
    /// </summary>
    /// <remarks>
    ///     ⚠ Matched on the phrase OpenBao puts in <c>errors[]</c> — <c>check-and-set parameter did not
    ///     match the current version</c> — because there is no code to match on. A string test is
    ///     fragile and the alternative is worse: treating every <c>400</c> as "already there" would
    ///     turn a malformed payload into a silent success, and treating none as such would fail every
    ///     reconcile pass after the first.
    /// </remarks>
    static async Task<bool> CheckAndSetFailedAsync(
        HttpResponseMessage response,
        CancellationToken cancellationToken
    ) {
        string body;

        try {
            body = await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (HttpRequestException) {
            return false;
        }

        return body.Contains("check-and-set", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Builds the <c>kv-v2</c> write body: the fields, and <c>cas: 0</c>.</summary>
    static string Payload(IReadOnlyDictionary<string, string> fields) {
        var data = new JsonObject();

        foreach (var pair in fields) {
            data[pair.Key] = JsonValue.Create(pair.Value);
        }

        return new JsonObject {
            ["data"] = data,
            ["options"] = new JsonObject { ["cas"] = 0 }
        }.ToJsonString();
    }

    /// <summary>Builds the <c>kv-v2</c> write URL for a path.</summary>
    /// <remarks>
    ///     ⚠ Each segment is escaped separately so the hierarchy survives and nothing else does — the
    ///     same rule, and the same reason, as <c>OpenBaoSecretResolver.Url</c>.
    /// </remarks>
    string Url(string path) {
        var escaped = string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

        return $"{options.Address.TrimEnd('/')}/v1/{Uri.EscapeDataString(options.KvMountPath)}/data/{escaped}";
    }

    /// <summary>
    ///     Refuses a call this class was made wrongly, logging the detail and returning the short form.
    /// </summary>
    /// <remarks>
    ///     ⚠ Split the way every other refusal in this assembly is — see <see cref="VaultRefusal" /> —
    ///     even though these three say nothing about the vault. The habit is the safeguard: a helper
    ///     that concatenated the two would be reached for by the next refusal, which will be about the
    ///     vault.
    /// </remarks>
    Result<SecretMint> Refuse(ErrorCode code, string tenantMessage, string operatorDetail) {
        logger?.LogError("{Detail}", operatorDetail);

        return Result<SecretMint>.Failure(code, tenantMessage);
    }

    /// <summary>What one write attempt produced.</summary>
    /// <param name="Minted">Whether this attempt wrote. False when a secret was already there.</param>
    /// <param name="Refusal">Why it failed, or <see langword="null" /> when it did not.</param>
    /// <param name="Retryable">Whether a fresh login is worth trying. True only for a <c>403</c>.</param>
    readonly record struct WriteOutcome(bool Minted, VaultRefusal? Refusal, bool Retryable) {
        public static WriteOutcome Wrote => new(true, null, false);

        public static WriteOutcome AlreadyThere => new(false, null, false);

        public static WriteOutcome Failed(VaultRefusal refusal) => new(false, refusal, false);

        public static WriteOutcome Denied(VaultRefusal refusal) => new(false, refusal, true);
    }
}
