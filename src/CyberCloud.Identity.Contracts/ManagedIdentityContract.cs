using CyberCloud.Authorization.Contracts;
using CyberCloud.Core;
using System.Globalization;

namespace CyberCloud.Identity.Contracts;

/// <summary>
///     RFC 8693's vocabulary, as docs/plan/11 § Protocol's Token Exchange row uses it.
/// </summary>
/// <remarks>
///     ⚠ These are URIs from the RFC and not names we chose, for the same reason
///     <c>AccessTokenPrincipalFactory.AmrValue</c> emits IANA's <c>amr</c> values: the whole point of
///     a standard grant is that a client library written against the RFC works without knowing about
///     us. A workload presenting a projected service-account token is very often
///     <c>kubelet</c>-adjacent tooling nobody here wrote.
/// </remarks>
public static class TokenExchange {
    /// <summary>The <c>grant_type</c>. docs/plan/11 § Protocol, the Token Exchange row.</summary>
    public const string GrantType = "urn:ietf:params:oauth:grant-type:token-exchange";

    /// <summary>
    ///     The only <c>subject_token_type</c> that is meaningful here — a projected service-account
    ///     token is a JWT.
    /// </summary>
    public const string JwtSubjectTokenType = "urn:ietf:params:oauth:token-type:jwt";

    /// <summary>The <c>issued_token_type</c> of what comes back: an ordinary platform access token.</summary>
    public const string IssuedTokenType = "urn:ietf:params:oauth:token-type:access_token";

    /// <summary>
    ///     What a Kubernetes projected service-account token's <c>sub</c> starts with —
    ///     <c>system:serviceaccount:{namespace}:{name}</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ <b>The namespace and the name are read out of this string, so both must be
    ///     colon-free.</b> <see cref="WorkloadBinding.Create" /> enforces that on the binding side,
    ///     and the parse below re-checks the segment count rather than splitting on the first two
    ///     colons and hoping — a namespace containing a colon would otherwise let one binding's
    ///     subject read as another's.
    /// </remarks>
    public const string ServiceAccountSubjectPrefix = "system:serviceaccount:";
}

/// <summary>
///     A managed identity's binding to a workload — <c>(cluster, namespace, serviceAccount)</c>.
///     docs/plan/11 § Managed identity, step 2.
/// </summary>
/// <remarks>
///     ⚠ <b>There is no member a credential could ride in, and that is the entire feature.</b>
///     docs/plan/11 § Managed identity: the bad answer to "this workload needs to read a vault
///     secret" is "a client secret in a Kubernetes <c>Secret</c>"; the good answer is that the
///     platform trusts <i>the cluster's own signature</i> over a token the kubelet projected. So the
///     binding names who may present such a token and nothing else — <b>no secret is ever stored, on
///     either side</b>.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.WorkloadBinding")]
public sealed record WorkloadBinding {
    /// <summary>An unbound identity's binding.</summary>
    public static WorkloadBinding None { get; } = new();

    /// <summary>The cluster whose OIDC issuer signs the workload's token.</summary>
    [Id(0)]
    public Guid ClusterId { get; init; }

    /// <summary>The Kubernetes namespace the workload runs in.</summary>
    [Id(1)]
    public string Namespace { get; init; } = string.Empty;

    /// <summary>The service account the workload's pod runs as.</summary>
    [Id(2)]
    public string ServiceAccount { get; init; } = string.Empty;

    /// <summary>Whether this binding names anything.</summary>
    public bool IsEmpty =>
        ClusterId == Guid.Empty || Namespace.Length == 0 || ServiceAccount.Length == 0;

    /// <summary>
    ///     Builds a binding, or explains why the parts are not one.
    /// </summary>
    /// <param name="clusterId">The cluster.</param>
    /// <param name="namespace">The Kubernetes namespace.</param>
    /// <param name="serviceAccount">The service account name.</param>
    /// <remarks>
    ///     ⚠ <b>Both names are validated as DNS-1123 labels, and the property that matters is that
    ///     neither can contain a <c>':'</c>.</b> A projected token's subject is the flat string
    ///     <c>system:serviceaccount:{namespace}:{name}</c>, so a namespace spelled
    ///     <c>prod:default</c> would produce a subject that also reads as
    ///     <c>(prod, default:{name})</c> — one workload's token satisfying another workload's
    ///     binding. Kubernetes rejects such a namespace itself, but this is the side of the trust
    ///     boundary that has to be sure.
    /// </remarks>
    public static Result<WorkloadBinding> Create(Guid clusterId, string? @namespace, string? serviceAccount) {
        if (clusterId == Guid.Empty) {
            return Result<WorkloadBinding>.Failure(
                ErrorCode.InvalidRequestBody,
                "A workload binding names a cluster, and the empty GUID is not one — docs/plan/11 "
                + "§ Managed identity, step 2."
            );
        }

        var validNamespace = EnsureLabel(@namespace, "namespace");
        if (validNamespace.TryGetError(out var namespaceError)) {
            return Result<WorkloadBinding>.Failure(namespaceError);
        }

        var validAccount = EnsureLabel(serviceAccount, "service account");

        return validAccount.TryGetError(out var accountError)
            ? Result<WorkloadBinding>.Failure(accountError)
            : Result<WorkloadBinding>.Success(
                new() {
                    ClusterId = clusterId,
                    Namespace = validNamespace.GetValueOrThrow(),
                    ServiceAccount = validAccount.GetValueOrThrow()
                }
            );
    }

    /// <inheritdoc />
    public override string ToString() =>
        IsEmpty
            ? "(unbound)"
            : ClusterId.ToString("D", CultureInfo.InvariantCulture) + "/" + Namespace + "/" + ServiceAccount;

    /// <summary>
    ///     A DNS-1123 label: 1-63 characters of <c>a-z</c>, <c>0-9</c> and <c>-</c>, starting and
    ///     ending alphanumeric.
    /// </summary>
    static Result<string> EnsureLabel(string? value, string what) {
        if (string.IsNullOrEmpty(value) || value.Length > 63) {
            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{value}' is not a Kubernetes {what}: it must be 1-63 characters."
            );
        }

        foreach (var c in value) {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-') {
                continue;
            }

            return Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{value}' is not a Kubernetes {what}: a DNS-1123 label is lower-case letters, "
                + "digits and '-'. ⚠ A ':' in particular is refused because a projected token's "
                + "subject is 'system:serviceaccount:{namespace}:{name}', and a name containing one "
                + "would make two different workloads produce the same subject."
            );
        }

        return value[0] is '-' || value[^1] is '-'
            ? Result<string>.Failure(
                ErrorCode.InvalidRequestBody,
                $"'{value}' is not a Kubernetes {what}: a DNS-1123 label starts and ends with a "
                + "letter or a digit."
            )
            : Result<string>.Success(value);
    }
}

/// <summary>
///     A tenant cluster's OIDC issuer, as read from its discovery document. docs/plan/11 § Managed
///     identity, step 3: <i>"the platform records the cluster's OIDC issuer URL and JWKS (read once,
///     refreshed)"</i>.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>Everything here is public by construction.</b> A JWKS is the <i>public</i> half of a
///         signing key pair — it is what the cluster's own API server serves unauthenticated to
///         anybody who asks — so recording it is not storing a credential, and a durable-tier backup
///         containing it grants nobody anything. That is what makes docs/plan/11 § Managed identity's
///         "no secret is ever stored, on either side" literally true rather than nearly true.
///     </para>
///     <para>
///         ⚠ <b><see cref="Issuer" /> is the value the discovery document itself claims, not the URL
///         we fetched.</b> OIDC Discovery requires the two to match and the check is not
///         decorative: without it, a cluster could publish a document claiming somebody else's issuer
///         and every token that issuer signed would validate against a key set the cluster chose.
///         <c>IClusterOidcDiscovery</c> is where that check happens, and it happens once — at
///         binding — rather than per exchange.
///     </para>
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ClusterOidcIssuer")]
public sealed record ClusterOidcIssuer {
    /// <summary>An identity whose cluster's issuer has not been read.</summary>
    public static ClusterOidcIssuer None { get; } = new();

    /// <summary>The <c>issuer</c> the discovery document claims, which a token's <c>iss</c> must equal.</summary>
    [Id(0)]
    public string Issuer { get; init; } = string.Empty;

    /// <summary>The <c>jwks_uri</c> from the discovery document.</summary>
    [Id(1)]
    public string KeySetUri { get; init; } = string.Empty;

    /// <summary>
    ///     The key set, verbatim, as the cluster served it.
    /// </summary>
    /// <remarks>
    ///     ⚠ Stored as the document rather than as parsed key material so that a curve or key type we
    ///     do not yet verify is <i>kept</i> rather than silently dropped at read time — a key set that
    ///     loses entries on the way into storage produces "unknown <c>kid</c>" failures that look like
    ///     a rotation problem and are a parsing problem. CC1005 does not fire on this member and
    ///     should not: it is named for what it holds, and what it holds is public.
    /// </remarks>
    [Id(2)]
    public string PublicKeySetJson { get; init; } = string.Empty;

    /// <summary>When the discovery document and key set were last read. "Read once, refreshed".</summary>
    [Id(3)]
    public DateTimeOffset ReadAt { get; init; }

    /// <summary>Whether this names a usable issuer.</summary>
    public bool IsEmpty => Issuer.Length == 0 || PublicKeySetJson.Length == 0;
}

/// <summary>
///     A managed identity, as everything outside the identity module sees one. docs/plan/11 § The
///     object model, § Managed identity.
/// </summary>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ManagedIdentityDescriptor")]
public sealed record ManagedIdentityDescriptor {
    /// <summary>
    ///     The GUID. ⚠ Also the ReBAC subject id — docs/plan/11 § Managed identity, step 6: "ReBAC
    ///     grants are made to <c>managedIdentity:{id}</c> like any other subject."
    /// </summary>
    [Id(0)]
    public Guid ManagedIdentityId { get; init; }

    /// <summary>The tenant that owns it.</summary>
    [Id(1)]
    public Guid TenantId { get; init; }

    /// <summary>
    ///     The name the tenant gave it — the <c>app-prod</c> of
    ///     <c>CyberCloud.ManagedIdentity/userAssignedIdentities/app-prod</c>.
    /// </summary>
    /// <remarks>
    ///     ⚠ An attribute, not the key, for the same reason a user's email is: renaming must not be a
    ///     grain migration, and the ReBAC tuples name the GUID.
    /// </remarks>
    [Id(2)]
    public string Name { get; init; } = string.Empty;

    /// <summary>The workload it is bound to, or <see cref="WorkloadBinding.None" />.</summary>
    [Id(3)]
    public WorkloadBinding Binding { get; init; } = WorkloadBinding.None;

    /// <summary>The cluster's OIDC issuer, recorded at binding.</summary>
    [Id(4)]
    public ClusterOidcIssuer Issuer { get; init; } = ClusterOidcIssuer.None;

    /// <summary>When the identity was created.</summary>
    [Id(5)]
    public DateTimeOffset CreatedAt { get; init; }

    /// <summary>When the binding was last made. Empty while unbound.</summary>
    [Id(6)]
    public DateTimeOffset BoundAt { get; init; }

    /// <summary>Whether a token exchange could succeed against this identity right now.</summary>
    public bool IsExchangeable => !Binding.IsEmpty && !Issuer.IsEmpty;

    /// <summary>This identity as a ReBAC subject — <c>managedIdentity:{id:N}</c>.</summary>
    public SubjectRef Subject =>
        SubjectRef.Of(SubjectTypes.ManagedIdentity, ManagedIdentityId);
}

/// <summary>
///     What a token exchange produces: a platform <i>identity</i>, never a token.
/// </summary>
/// <remarks>
///     ⚠ <b>Deliberately not a token, and not a <c>SignInOutcome</c> either.</b> Minting the token is
///     OpenIddict's job (ADR-015: "it handles the protocol, we own the stores"), and a second thing
///     in this module that could produce one would be a second token factory with its own idea of the
///     claim set. <c>SignInOutcome</c> is the wrong shape for a different reason: it carries a
///     <c>UserId</c> and a <c>SessionId</c>, and a managed identity has neither — there is no human
///     and no sign-in, which is the point of it.
/// </remarks>
[GenerateSerializer]
[Alias("CyberCloud.Identity.ExchangedSubject")]
public sealed record ExchangedSubject {
    /// <summary>The tenant the resulting token is scoped to.</summary>
    [Id(0)]
    public Guid TenantId { get; init; }

    /// <summary>The identity that was assumed.</summary>
    [Id(1)]
    public Guid ManagedIdentityId { get; init; }

    /// <summary>
    ///     Always <see cref="SubjectTypes.ManagedIdentity" /> — the <c>sub_typ</c> claim of the token
    ///     that will be minted from this.
    /// </summary>
    [Id(2)]
    public string SubjectType { get; init; } = SubjectTypes.ManagedIdentity;

    /// <summary>The <c>sub</c> claim: the identity's GUID in <c>N</c> form.</summary>
    [Id(3)]
    public string SubjectId { get; init; } = string.Empty;

    /// <summary>
    ///     When the presented service-account token expires.
    /// </summary>
    /// <remarks>
    ///     ⚠ The platform token minted from this must not outlive it, or a workload whose service
    ///     account was deleted keeps a working platform token until the platform token's own expiry.
    ///     In practice <c>AccessTokenPolicy.AccessTokenLifetime</c> is ten minutes and a projected
    ///     token's remaining life is usually hours, so the cap almost never binds — which is exactly
    ///     why it has to be written down rather than relied on.
    /// </remarks>
    [Id(4)]
    public DateTimeOffset SubjectTokenExpiresAt { get; init; }
}

/// <summary>
///     A projected service-account token that has been verified against a cluster's key set.
/// </summary>
/// <remarks>
///     ⚠ <b>Not <c>[GenerateSerializer]</c>, and the absence is the design.</b> This type is the
///     <i>output</i> of verification and it never crosses a grain boundary: if it could, some caller
///     would construct one and hand it to whatever consumes it, which is an authentication bypass
///     with extra steps. <c>IManagedIdentityGrain.ExchangeAsync</c> takes the raw token and verifies
///     it inside the grain for exactly this reason.
/// </remarks>
/// <param name="Issuer">The token's <c>iss</c>, already checked to equal the trusted issuer.</param>
/// <param name="Namespace">The namespace from <c>system:serviceaccount:{ns}:{name}</c>.</param>
/// <param name="ServiceAccount">The name from the same subject.</param>
/// <param name="ExpiresAt">The token's <c>exp</c>.</param>
public sealed record ValidatedServiceAccount(
    string Issuer,
    string Namespace,
    string ServiceAccount,
    DateTimeOffset ExpiresAt
);

/// <summary>
///     A workload identity bound to a cluster, a namespace and a service account. docs/plan/11 § The
///     object model, § Managed identity.
/// </summary>
/// <remarks>
///     <para>
///         <b>Kind</b> Entity · <b>Tier</b> Durable · <b>Key</b> <c>mi/{managedIdentityId:N}</c>,
///         tenant-qualified. Build it with <c>GrainKeys.ManagedIdentity</c>.
///     </para>
///     <para>
///         ⚠ <b>THIS GRAIN HOLDS NO SECRET AND HAS NOWHERE TO PUT ONE.</b> docs/plan/11 § Managed
///         identity calls this "the feature that removes stored secrets" and says why it is worth 1.2
///         EM: "no secret is ever stored, on either side … it removes an entire incident class". The
///         binding says <i>who may present a token</i>; the issuer record says <i>whose signature to
///         trust</i>, and a JWKS is public. Compare <see cref="IServicePrincipalGrain" />, which holds
///         a <see cref="VaultSecretRef" /> — a handle rather than a value, which is the best a shared
///         secret can be, and still worse than not having one.
///     </para>
///     <para>
///         ⚠ <b>The verification happens <i>in this grain</i> and takes the raw token.</b> The
///         tempting split — a service verifies, the grain matches a
///         <see cref="ValidatedServiceAccount" /> against the binding — puts the trust decision
///         outside the thing that holds the trust anchor, and any caller could then construct the
///         "validated" value. Here the issuer comes from this grain's own state, the signature is
///         checked against that issuer's key set, and only then is the binding compared. One call,
///         one ordering, no way round it.
///     </para>
/// </remarks>
[Alias("CyberCloud.Identity.IManagedIdentityGrain")]
public interface IManagedIdentityGrain : IGrainWithStringKey {
    /// <summary>Creates the identity, unbound.</summary>
    /// <param name="name">What the tenant called it.</param>
    /// <remarks>
    ///     ⚠ A newly created identity cannot exchange anything: <c>ExchangeAsync</c> refuses until
    ///     <see cref="BindAsync" /> has succeeded. Creation and binding are separate because
    ///     docs/plan/11 § Managed identity makes them steps 1 and 2, and because binding is the step
    ///     that can legitimately fail for a reason the tenant has to fix.
    /// </remarks>
    Task<Result<ManagedIdentityDescriptor>> CreateAsync(string name);

    /// <summary>The identity, or <see cref="ErrorCode.ResourceNotFound" />.</summary>
    Task<Result<ManagedIdentityDescriptor>> GetAsync();

    /// <summary>
    ///     Binds the identity to a workload, reading the cluster's OIDC discovery document and key
    ///     set in the process. docs/plan/11 § Managed identity, steps 2 and 3.
    /// </summary>
    /// <param name="binding">Which cluster, namespace and service account.</param>
    /// <param name="clusterIssuerUrl">The issuer URL the cluster advertises.</param>
    /// <remarks>
    ///     <para>
    ///         ⚠ <b>THIS IS WHERE AN UNREACHABLE CLUSTER IS REFUSED, AND THE DOCUMENT IS EXPLICIT
    ///         THAT IT MUST BE HERE.</b> docs/plan/11 § Managed identity: the flow "requires the
    ///         tenant's cluster to expose a <b>publicly reachable</b> OIDC discovery document, or that
    ///         we fetch the JWKS through the <c>AgentInitiated</c> tunnel — for BYO clusters that is
    ///         not automatic, <b>and the portal must say so at binding time rather than failing at
    ///         token exchange</b>."
    ///     </para>
    ///     <para>
    ///         The difference is the whole usability argument. A refusal here is a form that will not
    ///         submit, with a sentence saying what the cluster has to publish — the tenant is sitting
    ///         in front of it and can go and fix the cluster. A refusal at exchange is a workload that
    ///         deployed cleanly and gets <c>401</c>s in production at 3am, with the misconfiguration
    ///         several weeks and one team behind it.
    ///     </para>
    ///     <para>
    ///         Rebinding an already-bound identity is allowed and re-reads the issuer: pointing an
    ///         identity at a new namespace is an ordinary operation, and it must not be done by
    ///         deleting and recreating, because the GUID is the ReBAC subject id and every grant made
    ///         to it would be lost.
    ///     </para>
    /// </remarks>
    Task<Result<ManagedIdentityDescriptor>> BindAsync(WorkloadBinding binding, string clusterIssuerUrl);

    /// <summary>
    ///     Removes the binding. The identity and every ReBAC grant to it survive; nothing can
    ///     exchange for it until it is bound again.
    /// </summary>
    Task<Result<ManagedIdentityDescriptor>> UnbindAsync();

    /// <summary>
    ///     Re-reads the cluster's discovery document and key set. docs/plan/11 § Managed identity,
    ///     step 3: <i>"read once, refreshed"</i>.
    /// </summary>
    /// <remarks>
    ///     ⚠ A cluster rotates its service-account signing keys, and an unrefreshed key set stops
    ///     verifying every workload token in that cluster at once — the same failure
    ///     <c>SigningKeyCache</c> exists to prevent on the client side. This is separate from
    ///     <see cref="BindAsync" /> so that a refresh cannot silently repoint the binding.
    /// </remarks>
    Task<Result<ClusterOidcIssuer>> RefreshIssuerAsync();

    /// <summary>
    ///     RFC 8693 token exchange: a workload's projected service-account token for this platform
    ///     identity. docs/plan/11 § Managed identity, steps 4 and 5.
    /// </summary>
    /// <param name="subjectToken">The projected service-account token, verbatim.</param>
    /// <param name="subjectTokenType">
    ///     The RFC 8693 token type URI. Only <see cref="TokenExchange.JwtSubjectTokenType" /> is
    ///     meaningful for a projected service-account token.
    /// </param>
    /// <returns>
    ///     The identity to mint a token for, or <see cref="ErrorCode.AuthorizationFailed" /> with
    ///     <see cref="ManagedIdentityFailures.Exchange" /> — one message for every reason it can fail.
    /// </returns>
    /// <remarks>
    ///     ⚠ <b>Every refusal is the same sentence, and it names nothing.</b> The caller here is an
    ///     unauthenticated workload; distinguishing "no such managed identity" from "the binding does
    ///     not match" from "the signature is wrong" would let anyone with network access enumerate a
    ///     tenant's identities and their bindings from the token endpoint. The real reason goes to the
    ///     audit pipeline, which is where docs/plan/11 § Auditing wants it. This is the same rule, for
    ///     the same reason, as <see cref="UniformFailures.SignIn" />.
    /// </remarks>
    Task<Result<ExchangedSubject>> ExchangeAsync(string subjectToken, string subjectTokenType);

    /// <summary>Deletes the identity. ⚠ Every ReBAC grant made to it is thereby orphaned.</summary>
    Task<Result> DeleteAsync();

    /// <summary>Drops this activation — see <c>ITenantGrain.DeactivateAsync</c>.</summary>
    Task DeactivateAsync();
}

/// <summary>
///     Reads a tenant cluster's OIDC discovery document and key set. docs/plan/11 § Managed identity,
///     step 3.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The hard part of managed identity is reachability, not cryptography</b>, and this
///         interface is where that lands. docs/plan/11 § Managed identity: the flow "requires the
///         tenant's cluster to expose a <b>publicly reachable</b> OIDC discovery document, or that we
///         fetch the JWKS through the <c>AgentInitiated</c> tunnel (docs/plan/09)". The tunnel is M2,
///         so today an implementation of this either reaches the cluster over the public internet or
///         it fails — and failing is the correct, documented behaviour at binding time.
///     </para>
///     <para>
///         An implementation must check that the document's own <c>issuer</c> equals the URL it was
///         asked about. Skipping that check is how a cluster claims another issuer's identity.
///     </para>
/// </remarks>
public interface IClusterOidcDiscovery {
    /// <summary>Reads <c>{issuerUrl}/.well-known/openid-configuration</c> and the key set it names.</summary>
    /// <param name="issuerUrl">The issuer URL the cluster advertises.</param>
    /// <param name="cancellationToken">Cancels both fetches.</param>
    /// <returns>
    ///     The issuer, or a failure whose message says what the cluster has to publish — this text
    ///     reaches the tenant in the portal, at binding time.
    /// </returns>
    Task<Result<ClusterOidcIssuer>> DiscoverAsync(string issuerUrl, CancellationToken cancellationToken = default);
}

/// <summary>
///     Verifies a projected service-account token against a cluster's recorded issuer.
/// </summary>
/// <remarks>
///     ⚠ <b>The trusted issuer is an argument, never something this reads out of the token.</b> A
///     validator that took the token's own <c>iss</c> and went looking for keys would accept anything
///     signed by anybody who could publish a key set — which is everybody.
/// </remarks>
public interface IProjectedTokenValidator {
    /// <summary>Verifies the token and extracts the service account it belongs to.</summary>
    /// <param name="subjectToken">The compact JWS, verbatim.</param>
    /// <param name="issuer">The cluster's recorded issuer and key set. The only trust anchor.</param>
    /// <param name="now">The current time, for <c>exp</c> and <c>nbf</c>.</param>
    Result<ValidatedServiceAccount> Validate(string subjectToken, ClusterOidcIssuer issuer, DateTimeOffset now);
}

/// <summary>
///     The failures a managed identity produces, phrased so they disclose nothing.
/// </summary>
/// <remarks>
///     ⚠ <see cref="Exchange" /> is a constant rather than a string per call site for the same reason
///     <see cref="UniformFailures.SignIn" /> is: "the same response whatever went wrong" is a
///     property that only survives the next person adding a branch if there is one string.
/// </remarks>
public static class ManagedIdentityFailures {
    /// <summary>
    ///     The one answer to a failed token exchange. No such identity, no binding, a binding that
    ///     does not match, a bad signature, an untrusted issuer, an expired token — all of them,
    ///     identically.
    /// </summary>
    public const string Exchange =
        "The presented token cannot be exchanged for this identity.";

    /// <summary>
    ///     What a tenant is told when a cluster's OIDC discovery document cannot be read, at the
    ///     moment they try to bind. docs/plan/11 § Managed identity.
    /// </summary>
    /// <remarks>
    ///     ⚠ This one says a great deal on purpose, and the asymmetry with <see cref="Exchange" /> is
    ///     the point: the caller here is an authenticated tenant administrator configuring their own
    ///     cluster, and the whole reason the check is at binding time is so somebody who can fix the
    ///     problem is told what it is.
    /// </remarks>
    public const string Unreachable =
        "The cluster does not publish a reachable OIDC discovery document, so a workload token "
        + "signed by it cannot be verified. A managed identity needs the cluster's issuer to serve "
        + "'/.well-known/openid-configuration' and its 'jwks_uri' over the public internet. For a "
        + "cluster Cyber Cloud provisioned this is configured for you; for a bring-your-own cluster "
        + "it is not automatic, and fetching the key set over the agent tunnel instead is not yet "
        + "available. The binding is refused now rather than at token exchange so that the workload "
        + "does not deploy successfully and fail in production.";

    /// <summary>The uniform exchange failure, built.</summary>
    public static Result<ExchangedSubject> RejectExchange() =>
        Result<ExchangedSubject>.Failure(ErrorCode.AuthorizationFailed, Exchange);
}
