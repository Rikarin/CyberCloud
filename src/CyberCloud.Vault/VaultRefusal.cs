namespace CyberCloud.Vault;

/// <summary>
///     Why a resolve failed, in the two versions a failure needs: one the tenant may read and one the
///     operator needs.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The split is not decoration, and <c>CyberCloud.Kubernetes</c>'s <c>KubeRefusal</c>
///         established why.</b> A failed <see cref="Result{T}" /> out of
///         <see cref="OpenBaoSecretResolver" /> travels: the reconciler turns it into
///         <c>ReconcileOutcome.Failed</c>, the operation grain streams it to
///         <c>operation-progress</c>, and it lands in <c>ResourceSnapshot.LastFailure</c> where the
///         portal renders it. Nothing between here and there redacts anything. So the message on the
///         <see cref="Result{T}" /> is tenant-visible by construction, and everything worth knowing
///         about the platform's vault goes in <see cref="OperatorDetail" />, which only ever reaches
///         an <c>ILogger</c>.
///     </para>
///     <para>
///         ⚠ <b>Neither string ever holds the secret value, and one of them is nowhere near the
///         value at all.</b> A refusal is built before a value is read or after a read that produced
///         none, and <see cref="VaultFailures" /> has no overload taking one.
///         <c>SecretContainmentTests.NoRefusalCanBeHandedTheValue</c> is the assertion, by
///         reflection, because CC1005 is switched off in this assembly and cannot be.
///     </para>
///     <para>
///         ⚠ <b>Four distinguishable answers, and the whole argument for this type is that none of
///         them is an empty string.</b> <c>UnavailableSecretResolver</c>'s remarks make the case for
///         the unwired default — <i>"an empty password reaching a rendered manifest is a database
///         with no password, applied to a real cluster, reported as a successful provision"</i> — and
///         it applies with more force to the wired one, which fails in ways an empty string would
///         flatten into each other: a vault that is <i>unreachable</i>, a path that <i>does not
///         exist</i>, a permission that is <i>denied</i>, and a path that exists without the field
///         asked for are four different incidents with four different fixes.
///         <c>ResolveFailureTests</c> drives all four against a real OpenBao.
///     </para>
/// </remarks>
public sealed record VaultRefusal {
    /// <summary>The code the caller's <see cref="Result{T}" /> carries.</summary>
    public required ErrorCode Code { get; init; }

    /// <summary>
    ///     What the tenant sees, word for word.
    /// </summary>
    /// <remarks>
    ///     ⚠ Names no address, no mount, no path, no field, no role and no namespace. It says which
    ///     of the four things went wrong and that an operator has to act, which is everything a
    ///     tenant can do something with. <c>RefusalHygieneTests</c> holds the line with a forbidden
    ///     substring list, the same shape as
    ///     <c>KubeFailureMappingTests.AnRbacRefusalNamesNothingInternalToTheTenantAndEverythingToTheOperator</c>.
    /// </remarks>
    public required string TenantMessage { get; init; }

    /// <summary>
    ///     What the operator sees in the log. ⚠ Never returned to a caller.
    /// </summary>
    /// <remarks>
    ///     This one names the address, the mount, the path, the field, the version and the role,
    ///     because an operator at 03:00 needs to know <i>which</i> handle failed against
    ///     <i>which</i> vault. It never names the value, and there is no code path that could put one
    ///     here.
    /// </remarks>
    public required string OperatorDetail { get; init; }
}

/// <summary>
///     Builds the refusal for each way a resolve can fail.
/// </summary>
/// <remarks>
///     ⚠ <b>The auth stage and the read stage fail separately and must say so separately.</b> An
///     OpenBao login refused with <c>403</c> and a <c>kv-v2</c> read refused with <c>403</c> are the
///     same status code and completely different incidents: the first says this silo's pod is not the
///     service account the role is bound to — a deployment fault that will affect every secret — and
///     the second says the policy attached to a perfectly good token does not cover one path. Folding
///     them together sends an operator to reconfigure a role that is fine.
/// </remarks>
public static class VaultFailures {
    /// <summary>The sentence every tenant-visible refusal ends with.</summary>
    /// <remarks>
    ///     ⚠ Deliberately the same closing sentence in all four, because the difference between them
    ///     is not a difference the tenant can act on — it is the leading clause that tells the
    ///     operator reading a support ticket which of the four to go and look at.
    /// </remarks>
    public const string Escalation =
        "The resource cannot be provisioned without it, and this needs an operator rather than a "
        + "change to the request.";

    /// <summary>A handle with no path or no field, which is the caller's mistake and not the vault's.</summary>
    /// <param name="reference">The empty handle.</param>
    /// <remarks>
    ///     ⚠ Refused without a network call. <see cref="SecretRef.IsEmpty" />'s own remarks make the
    ///     case: <i>"a handle with no path or no field is not 'an empty secret' — it is an address
    ///     that resolves to nothing, and a caller that passed one meant to pass a real one."</i>
    ///     Resolving it would ask OpenBao for <c>{mount}/data/</c> and get a <c>404</c>, which would
    ///     report a missing secret rather than a broken handle.
    /// </remarks>
    public static VaultRefusal EmptyHandle(SecretRef reference) {
        ArgumentNullException.ThrowIfNull(reference);

        return new() {
            Code = ErrorCode.InternalError,
            TenantMessage =
                "A credential this resource needs was requested with an incomplete handle. "
                + Escalation,
            OperatorDetail =
                $"A SecretRef with no path or no field reached the resolver: path='{reference.Path}', "
                + $"field='{reference.Field}'. This is a provider bug rather than a vault fault — "
                + "nothing was asked of OpenBao. The handle came from the resource's desired state, "
                + "so the reconciler that built it is where to look.",
        };
    }

    /// <summary>The platform could not authenticate to OpenBao at all.</summary>
    /// <param name="detail">What the login attempt actually produced. Never a token.</param>
    /// <remarks>
    ///     ⚠ <b><see cref="ErrorCode.InternalError" /> and not
    ///     <see cref="ErrorCode.AuthorizationFailed" />, and the difference matters to whoever reads
    ///     the code rather than the message.</b> <c>AuthorizationFailed</c> renders as <c>403</c> and
    ///     says <i>the caller may not do this</i>. The caller may; the platform cannot reach its own
    ///     vault. Handing a tenant a <c>403</c> for the platform's deployment fault sends them to
    ///     check their own permissions.
    /// </remarks>
    public static VaultRefusal AuthenticationFailed(string detail) =>
        new() {
            Code = ErrorCode.InternalError,
            TenantMessage =
                "The platform could not reach its own secret store to read a credential this "
                + "resource needs. " + Escalation,
            OperatorDetail =
                "The silo could not authenticate to OpenBao, so no secret can be resolved on this "
                + $"silo at all until it is fixed. Specifically: {detail}",
        };

    /// <summary>OpenBao did not answer, or did not answer in time.</summary>
    /// <param name="detail">The transport failure. Never a token.</param>
    public static VaultRefusal Unreachable(string detail) =>
        new() {
            Code = ErrorCode.InternalError,
            TenantMessage =
                "The platform's secret store did not answer, so a credential this resource needs "
                + "could not be read. " + Escalation,
            OperatorDetail = $"OpenBao did not answer. Specifically: {detail}",
        };

    /// <summary>The path, or the pinned version of it, is not there.</summary>
    /// <param name="reference">The handle that resolved to nothing.</param>
    /// <param name="address">The vault the read went to.</param>
    /// <param name="mount">The <c>kv-v2</c> mount the read went through.</param>
    /// <remarks>
    ///     ⚠ <b>The pinned-version case lands here too, and it is the one that surprises people.</b>
    ///     <see cref="SecretRef.Version" /> exists so a reconcile pass is reproducible across a
    ///     rotation, and OpenBao answers a <c>?version=</c> that has been destroyed or never existed
    ///     with the same bare <c>404</c> it gives an unknown path — verified against a running
    ///     OpenBao 2.4.1 rather than inferred. So the operator detail names the version, because
    ///     "the path is gone" and "version 3 was destroyed by a rotation" have nothing in common
    ///     except the status code.
    /// </remarks>
    public static VaultRefusal NotFound(SecretRef reference, string address, string mount) {
        ArgumentNullException.ThrowIfNull(reference);

        return new() {
            Code = ErrorCode.ResourceNotFound,
            TenantMessage =
                "The platform's secret store does not hold a credential this resource needs. "
                + Escalation,
            OperatorDetail =
                $"OpenBao at {address} answered 404 for '{mount}/data/{reference.Path}'"
                + (reference.Version.Length == 0 ? string.Empty : $" at version {reference.Version}")
                + ". Either nothing was ever written there, or the version pinned in the resource's "
                + "desired state has been deleted or destroyed — OpenBao answers both with a bare "
                + "404 and an empty error list, so the two cannot be told apart from here.",
        };
    }

    /// <summary>The platform's token is real and its policy does not cover this path.</summary>
    /// <param name="reference">The handle that was refused.</param>
    /// <param name="address">The vault the read went to.</param>
    /// <param name="mount">The <c>kv-v2</c> mount the read went through.</param>
    /// <param name="role">The role the silo logged in as.</param>
    public static VaultRefusal PermissionDenied(
        SecretRef reference,
        string address,
        string mount,
        string role
    ) {
        ArgumentNullException.ThrowIfNull(reference);

        return new() {
            Code = ErrorCode.AuthorizationFailed,
            TenantMessage =
                "The platform is not permitted to read a credential this resource needs. "
                + Escalation,
            OperatorDetail =
                $"OpenBao at {address} answered 403 for '{mount}/data/{reference.Path}'. The login "
                + $"succeeded, so the role '{role}' is bound correctly and this is its policy: the "
                + "token it issues does not carry read on that path. ⚠ Not the same fault as a "
                + "refused login, which fails every path rather than one.",
        };
    }

    /// <summary>The path is there and the field is not.</summary>
    /// <param name="reference">The handle whose field is missing.</param>
    /// <param name="address">The vault the read went to.</param>
    /// <param name="mount">The <c>kv-v2</c> mount the read went through.</param>
    /// <param name="present">The field names that <i>are</i> at that path. ⚠ Names, never values.</param>
    /// <remarks>
    ///     ⚠ <b>This is the case an empty string would hide most quietly, which is why it has its own
    ///     builder.</b> A <c>kv-v2</c> read of an existing path returns <c>200</c> with a
    ///     <c>data.data</c> object, and a missing key in that object is a JSON absence rather than an
    ///     error — the obvious implementation reads it into a <see langword="string" /> that is
    ///     <see langword="null" />, coalesces it to <c>""</c>, and hands back a successful result
    ///     holding an empty password. Everything else in this file is about telling failures apart;
    ///     this one is about a failure that does not look like one.
    ///     <para>
    ///         The operator detail lists the field names present, because the fault is nearly always
    ///         a spelling — <c>adminPassword</c> against a secret written with <c>password</c> — and
    ///         a list of keys turns a hunt into a glance. Key names are not secret; the values they
    ///         index are, and they are not here.
    ///     </para>
    /// </remarks>
    public static VaultRefusal FieldMissing(
        SecretRef reference,
        string address,
        string mount,
        IEnumerable<string> present
    ) {
        ArgumentNullException.ThrowIfNull(reference);
        ArgumentNullException.ThrowIfNull(present);

        var names = string.Join(", ", present);

        return new() {
            Code = ErrorCode.ResourceNotFound,
            TenantMessage =
                "The platform's secret store does not hold a credential this resource needs. "
                + Escalation,
            OperatorDetail =
                $"OpenBao at {address} holds '{mount}/data/{reference.Path}' but it has no "
                + $"'{reference.Field}' field. The fields at that path are: "
                + (names.Length == 0 ? "(none)" : names)
                + ". ⚠ This is reported as a failure rather than as an empty value on purpose — an "
                + "empty password rendered into a manifest is a database with no password, reported "
                + "as a successful provision.",
        };
    }

    /// <summary>OpenBao answered, and the answer was not a shape this client understands.</summary>
    /// <param name="detail">What was wrong with the response. ⚠ Never the response body.</param>
    /// <remarks>
    ///     ⚠ The body is not quoted, and that is the difference between this and every other builder
    ///     here. A <c>kv-v2</c> response body <i>contains the secret</i>, so a malformed-response
    ///     path that echoed what it could not parse would be the one place in this assembly that
    ///     writes a value to a log. <c>CyberCloud.Kubernetes</c>'s <c>Ours</c> builder quotes its
    ///     serializer's message for the same class of fault; that one is not carrying a password.
    /// </remarks>
    public static VaultRefusal Unreadable(string detail) =>
        new() {
            Code = ErrorCode.InternalError,
            TenantMessage =
                "The platform's secret store answered in a way the platform could not read. "
                + Escalation,
            OperatorDetail =
                $"OpenBao's response could not be read: {detail}. ⚠ The body is deliberately not "
                + "quoted here — a kv-v2 response body holds the secret, so echoing an unparseable "
                + "one is the one way this assembly could write a value to a log. This is a fault in "
                + "the platform or a version skew against OpenBao rather than a fault in the request.",
        };
}
