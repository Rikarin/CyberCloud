using CyberCloud.Core.Contracts;
using CyberCloud.Identity.Contracts;
using CyberCloud.Identity.Tests.Infrastructure;
using System.Reflection;

namespace CyberCloud.Identity.Tests;

/// <summary>
///     docs/plan/11 § Managed identity — "the feature that removes stored secrets", organised around
///     its failure classes rather than its methods.
/// </summary>
/// <remarks>
///     <para>
///         ⚠ <b>The refusals matter more than the success here, and they are asymmetric on purpose.</b>
///         A binding is made by an authenticated tenant administrator who can fix their cluster, so a
///         binding-time refusal says at length what is wrong. An exchange is attempted by an
///         unauthenticated workload, so every exchange refusal is one sentence that names nothing —
///         otherwise <c>/token</c> enumerates a tenant's identities and their bindings for anybody who
///         can reach it.
///     </para>
/// </remarks>
[Collection(IdentitySuite.Name)]
public sealed class ManagedIdentityTests(IdentityCluster cluster) {
    static readonly Guid ClusterId = Guid.Parse("dddddddd-0000-0000-0000-000000000004");

    static DateTimeOffset Now => TestClock.Instance.UtcNow;

    /// <summary>An identity bound to a reachable cluster, ready to exchange.</summary>
    async Task<(Guid Id, ClusterSigner Signer)> BoundAsync(
        string @namespace = "prod",
        string serviceAccount = "app"
    ) {
        var signer = new ClusterSigner();
        ScriptedClusterOidcDiscovery.Instance.Publish(signer, Now);

        var id = Guid.NewGuid();
        (await cluster.ManagedIdentity(id).CreateAsync("app-prod")).IsSuccess.ShouldBeTrue();

        var bound = await cluster.ManagedIdentity(id).BindAsync(
            WorkloadBinding.Create(ClusterId, @namespace, serviceAccount).GetValueOrThrow(),
            signer.Issuer
        );

        bound.IsSuccess.ShouldBeTrue(bound.Error?.Message);

        return (id, signer);
    }

    // ── Binding: the reachability refusal happens HERE, not at exchange ───────────────────────

    [Fact]
    public async Task ABindingToAClusterWithNoReachableDiscoveryDocumentFailsAtBindTime() {
        // ⚠ THE ONE docs/plan/11 § Managed identity IS EXPLICIT ABOUT: the flow "requires the
        // tenant's cluster to expose a publicly reachable OIDC discovery document, or that we fetch
        // the JWKS through the AgentInitiated tunnel — for BYO clusters that is not automatic, and
        // the portal must say so AT BINDING TIME rather than failing at token exchange."
        var id = Guid.NewGuid();
        (await cluster.ManagedIdentity(id).CreateAsync("byo-app")).IsSuccess.ShouldBeTrue();

        var refused = await cluster.ManagedIdentity(id).BindAsync(
            WorkloadBinding.Create(ClusterId, "prod", "app").GetValueOrThrow(),
            "https://oidc.private-cluster.invalid"
        );

        refused.IsFailure.ShouldBeTrue("a cluster nobody can read cannot be a trust anchor");

        // The message has to be actionable, because the person reading it is the person who can fix
        // it. All four of these are things the administrator needs to know.
        var message = refused.Error!.Message;

        message.ShouldContain("OIDC discovery document");
        message.ShouldContain("jwks_uri");
        message.ShouldContain("bring-your-own");
        message.ShouldContain("rather than at token exchange");
    }

    [Fact]
    public async Task AnIdentityWhoseBindingWasRefusedCannotExchangeAnythingAtAll() {
        // ⚠ The failure has to be a REFUSAL and not a half-binding. A grain that recorded the
        // binding and left the issuer empty would look bound in the portal and fail in production,
        // which is precisely the outcome the binding-time check exists to prevent.
        // ⚠ An issuer nothing has published — the shared discovery is scripted per test, and a
        // cluster that was never made reachable is exactly a BYO cluster with no public endpoint.
        using var unreachable = new ClusterSigner { Issuer = "https://oidc.never-published.invalid" };
        var id = Guid.NewGuid();

        (await cluster.ManagedIdentity(id).CreateAsync("byo-app")).IsSuccess.ShouldBeTrue();

        (await cluster.ManagedIdentity(id).BindAsync(
            WorkloadBinding.Create(ClusterId, "prod", "app").GetValueOrThrow(),
            unreachable.Issuer
        )).IsFailure.ShouldBeTrue();

        var descriptor = (await cluster.ManagedIdentity(id).GetAsync()).GetValueOrThrow();

        descriptor.Binding.IsEmpty.ShouldBeTrue();
        descriptor.Issuer.IsEmpty.ShouldBeTrue();
        descriptor.IsExchangeable.ShouldBeFalse();
    }

    [Fact]
    public async Task ABindingWhoseNamespaceCouldForgeASubjectIsRefused() {
        // ⚠ A projected token's subject is the flat string system:serviceaccount:{ns}:{name}. A
        // namespace containing a ':' would make two different workloads produce the same subject —
        // one workload's token satisfying another's binding.
        var id = Guid.NewGuid();
        (await cluster.ManagedIdentity(id).CreateAsync("app")).IsSuccess.ShouldBeTrue();

        WorkloadBinding.Create(ClusterId, "prod:default", "app").IsFailure.ShouldBeTrue();
        WorkloadBinding.Create(ClusterId, "prod", "app:extra").IsFailure.ShouldBeTrue();
        WorkloadBinding.Create(ClusterId, "PROD", "app").IsFailure.ShouldBeTrue();
        WorkloadBinding.Create(Guid.Empty, "prod", "app").IsFailure.ShouldBeTrue();
    }

    // ── Exchange: what works ─────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AWorkloadTokenSignedByTheBoundClusterExchangesForAManagedIdentitySubject() {
        var (id, signer) = await BoundAsync();

        var exchanged = await cluster.ManagedIdentity(id).ExchangeAsync(
            signer.ProjectedToken("prod", "app", Now.AddHours(1)),
            TokenExchange.JwtSubjectTokenType
        );

        exchanged.IsSuccess.ShouldBeTrue(exchanged.Error?.Message);

        var subject = exchanged.GetValueOrThrow();

        // ⚠ docs/plan/11 § Managed identity step 6: "ReBAC grants are made to `managedIdentity:{id}`
        // like any other subject." The third subject type is a value the exchange produces, not a
        // special case the checker has to know about.
        subject.SubjectType.ShouldBe(SubjectTypes.ManagedIdentity);
        subject.SubjectId.ShouldBe(id.ToString("N"));
        subject.TenantId.ShouldBe(IdentityCluster.Tenant);
        subject.ManagedIdentityId.ShouldBe(id);

        // The platform token minted from this must not outlive the workload's own credential.
        subject.SubjectTokenExpiresAt.ShouldBe(Now.AddHours(1), TimeSpan.FromSeconds(1));
    }

    [Fact]
    public async Task ARsaSignedTokenAlsoVerifies() {
        // A real Kubernetes API server usually signs with RS256. Both arms are exercised so that
        // "the validator refuses everything it does not recognise" is not accidentally "the
        // validator only ever recognised one thing".
        var (id, signer) = await BoundAsync();

        (await cluster.ManagedIdentity(id).ExchangeAsync(
            signer.RsaProjectedToken("prod", "app", Now.AddHours(1)),
            TokenExchange.JwtSubjectTokenType
        )).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task TheManagedIdentityIsAReBacSubjectLikeAnyOther() {
        var (id, _) = await BoundAsync();

        var descriptor = (await cluster.ManagedIdentity(id).GetAsync()).GetValueOrThrow();

        descriptor.Subject.Type.ShouldBe("managedIdentity");
        descriptor.Subject.Id.ShouldBe(id.ToString("N"));
        descriptor.Subject.IsValid.ShouldBeTrue();
        descriptor.Subject.ToString().ShouldBe("managedIdentity:" + id.ToString("N"));
    }

    // ── Exchange: what is refused, and identically ───────────────────────────────────────────

    [Fact]
    public async Task ATokenFromAnUntrustedIssuerIsRefusedAndTheRefusalNamesNothing() {
        // ⚠ THE CENTRAL REFUSAL. A second cluster with its own perfectly valid key set — the shape an
        // attacker who can stand up any Kubernetes cluster has, which is everybody.
        var (id, _) = await BoundAsync();

        using var attacker = new ClusterSigner { Issuer = "https://oidc.attacker.example" };

        var refused = await cluster.ManagedIdentity(id).ExchangeAsync(
            attacker.ProjectedToken("prod", "app", Now.AddHours(1)),
            TokenExchange.JwtSubjectTokenType
        );

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldBe(ManagedIdentityFailures.Exchange);

        // ⚠ And it names nothing internal: not the trusted issuer, not the binding, not the identity,
        // not the tenant, not the reason. A distinguishable refusal here is a directory listing.
        foreach (var leak in new[] {
                     "oidc.cluster.example", "attacker", "prod", "app", "issuer", "signature",
                     "binding", "namespace", "serviceaccount", "jwks", "key",
                     id.ToString("N"), id.ToString("D"), IdentityCluster.Tenant.ToString("D")
                 }) {
            refused.Error.Message.ShouldNotContain(leak, Case.Insensitive);
        }
    }

    [Fact]
    public async Task ATokenSignedByTheRightClusterForTheWrongServiceAccountIsRefused() {
        var (id, signer) = await BoundAsync("prod", "app");

        foreach (var token in new[] {
                     signer.ProjectedToken("prod", "other", Now.AddHours(1)),
                     signer.ProjectedToken("staging", "app", Now.AddHours(1))
                 }) {
            var refused = await cluster.ManagedIdentity(id).ExchangeAsync(
                token,
                TokenExchange.JwtSubjectTokenType
            );

            refused.IsFailure.ShouldBeTrue();
            refused.Error!.Message.ShouldBe(ManagedIdentityFailures.Exchange);
        }
    }

    [Fact]
    public async Task AnAlgNoneTokenIsRefused() {
        // ⚠ The oldest JWT vulnerability there is, on the exact bytes an attacker sends: a header
        // saying `none` and an empty third segment. ProjectedTokenValidator's switch has no arm that
        // returns true without verifying.
        var (id, signer) = await BoundAsync();

        var refused = await cluster.ManagedIdentity(id).ExchangeAsync(
            signer.UnsignedToken("prod", "app", Now.AddHours(1)),
            TokenExchange.JwtSubjectTokenType
        );

        refused.IsFailure.ShouldBeTrue("`alg: none` must never verify");
        refused.Error!.Message.ShouldBe(ManagedIdentityFailures.Exchange);
    }

    [Fact]
    public async Task AnExpiredTokenIsRefusedOnceItIsOutsideTheLeeway() {
        var (id, signer) = await BoundAsync();

        // Inside the 60-second skew allowance, an already-expired token is still taken: clocks
        // between a tenant's cluster and a platform silo genuinely differ.
        var justExpired = signer.ProjectedToken("prod", "app", Now.AddSeconds(-30));

        (await cluster.ManagedIdentity(id).ExchangeAsync(justExpired, TokenExchange.JwtSubjectTokenType))
            .IsSuccess.ShouldBeTrue();

        var longExpired = signer.ProjectedToken("prod", "app", Now.AddMinutes(-30));

        (await cluster.ManagedIdentity(id).ExchangeAsync(longExpired, TokenExchange.JwtSubjectTokenType))
            .IsFailure.ShouldBeTrue();
    }

    [Fact]
    public async Task ATokenExchangeForABindingThatWasDeletedFails() {
        // ⚠ THE REVOCATION PATH. Unbinding is how a tenant says "this workload may no longer act as
        // this identity", and it has to take effect at the next exchange — there is no token to
        // revoke, because the platform never issued the one being presented.
        var (id, signer) = await BoundAsync();
        var token = signer.ProjectedToken("prod", "app", Now.AddHours(1));

        (await cluster.ManagedIdentity(id).ExchangeAsync(token, TokenExchange.JwtSubjectTokenType))
            .IsSuccess.ShouldBeTrue();

        (await cluster.ManagedIdentity(id).UnbindAsync()).IsSuccess.ShouldBeTrue();

        var refused = await cluster.ManagedIdentity(id).ExchangeAsync(token, TokenExchange.JwtSubjectTokenType);

        refused.IsFailure.ShouldBeTrue("the very same token, against an identity that is no longer bound");
        refused.Error!.Message.ShouldBe(ManagedIdentityFailures.Exchange);

        // ⚠ And the issuer went with the binding. Keeping it would leave a trusted key set attached
        // to an identity bound to nothing, which the next bind would silently inherit — including if
        // the next bind names a different cluster.
        var descriptor = (await cluster.ManagedIdentity(id).GetAsync()).GetValueOrThrow();

        descriptor.Issuer.IsEmpty.ShouldBeTrue();
        descriptor.IsExchangeable.ShouldBeFalse();
    }

    [Fact]
    public async Task ATokenExchangeAgainstADeletedIdentityFailsTheSameWay() {
        var (id, signer) = await BoundAsync();
        var token = signer.ProjectedToken("prod", "app", Now.AddHours(1));

        (await cluster.ManagedIdentity(id).DeleteAsync()).IsSuccess.ShouldBeTrue();

        var deleted = await cluster.ManagedIdentity(id).ExchangeAsync(token, TokenExchange.JwtSubjectTokenType);

        // ⚠ Indistinguishable from "never existed" and from "wrong signature". Otherwise a caller
        // walks GUIDs against /token and learns which managed identities a tenant has.
        var neverExisted = await cluster.ManagedIdentity(Guid.NewGuid()).ExchangeAsync(
            token,
            TokenExchange.JwtSubjectTokenType
        );

        deleted.Error!.Message.ShouldBe(ManagedIdentityFailures.Exchange);
        neverExisted.Error!.Message.ShouldBe(ManagedIdentityFailures.Exchange);
        deleted.Error.Code.ShouldBe(neverExisted.Error.Code);
    }

    [Fact]
    public async Task AWrongSubjectTokenTypeIsRefused() {
        var (id, signer) = await BoundAsync();
        var token = signer.ProjectedToken("prod", "app", Now.AddHours(1));

        foreach (var type in new[] {
                     "urn:ietf:params:oauth:token-type:access_token",
                     "urn:ietf:params:oauth:token-type:saml2",
                     "jwt",
                     ""
                 }) {
            (await cluster.ManagedIdentity(id).ExchangeAsync(token, type)).IsFailure.ShouldBeTrue();
        }

        (await cluster.ManagedIdentity(id).ExchangeAsync(token, TokenExchange.JwtSubjectTokenType))
            .IsSuccess.ShouldBeTrue();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-jwt")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("!!!.???.***")]
    public async Task AMalformedTokenIsRefusedWithoutThrowing(string token) {
        // ⚠ An unauthenticated endpoint: a caller who could make this throw would be turning a bad
        // string into a stack trace and, in the worst case, into an activation that never completes.
        var (id, _) = await BoundAsync();

        var refused = await cluster.ManagedIdentity(id).ExchangeAsync(token, TokenExchange.JwtSubjectTokenType);

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldBe(ManagedIdentityFailures.Exchange);
    }

    // ── The reason the feature exists ────────────────────────────────────────────────────────

    [Fact]
    public async Task NoSecretIsStoredAnywhereInTheFlow() {
        // ⚠ docs/plan/11 § Managed identity: "no secret is ever stored, on either side. … it removes
        // an entire incident class." Asserted by reflection over what actually goes into the durable
        // tier rather than by reading the class, because the claim is about every [Id]-annotated
        // member and the next one somebody adds.
        var (id, _) = await BoundAsync();

        var stateTypes = new[] {
            typeof(ManagedIdentityGrainState),
            typeof(ManagedIdentityDescriptor),
            typeof(WorkloadBinding),
            typeof(ClusterOidcIssuer),
            typeof(ExchangedSubject)
        };

        foreach (var type in stateTypes) {
            foreach (var member in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                if (member.GetCustomAttributes(typeof(IdAttribute), inherit: false).Length == 0) {
                    continue;
                }

                // CC1005's own suffix list, docs/plan/00 § Non-negotiables. ⚠ Unlike PasskeyCredential
                // and ServicePrincipalDescriptor, nothing here needs a [SuppressMessage] with an
                // argument, because nothing here is a credential or a handle to one.
                foreach (var suffix in new[] { "Password", "Secret", "Token", "Key" }) {
                    member.Name.EndsWith(suffix, StringComparison.Ordinal).ShouldBeFalse(
                        $"{type.Name}.{member.Name} is serialized grain state and its name says it "
                        + "holds a secret — CC1005, docs/plan/00 § Non-negotiables."
                    );
                }
            }
        }

        // ⚠ And there is no SecretRef either, which is the sharper version of the claim. A
        // service principal holds a handle to a secret — the best a shared secret can be, and still a
        // secret somewhere. A managed identity holds no handle, because there is nothing to point at.
        foreach (var type in stateTypes) {
            foreach (var member in type.GetProperties(BindingFlags.Public | BindingFlags.Instance)) {
                member.PropertyType.ShouldNotBe(typeof(SecretRef), $"{type.Name}.{member.Name}");
            }
        }

        // The state as it actually exists after a successful bind and exchange, serialized: the only
        // key material in it is a public key set the cluster serves to anybody who asks.
        var descriptor = (await cluster.ManagedIdentity(id).GetAsync()).GetValueOrThrow();

        descriptor.Issuer.PublicKeySetJson.ShouldContain("\"kty\"");
        descriptor.Issuer.PublicKeySetJson.ShouldNotContain("\"d\"", Case.Sensitive);
    }

    [Fact]
    public async Task RebindingReReadsTheIssuerAndKeepsTheReBacSubject() {
        // ⚠ Pointing an identity at a new namespace must not be delete-and-recreate: the GUID is the
        // ReBAC subject id, so a new GUID silently revokes every grant made to the old one.
        var (id, first) = await BoundAsync("prod", "app");

        using var second = new ClusterSigner { Issuer = "https://oidc.other-cluster.example" };
        ScriptedClusterOidcDiscovery.Instance.Publish(second, Now);

        var rebound = await cluster.ManagedIdentity(id).BindAsync(
            WorkloadBinding.Create(ClusterId, "staging", "app").GetValueOrThrow(),
            second.Issuer
        );

        rebound.IsSuccess.ShouldBeTrue(rebound.Error?.Message);
        rebound.GetValueOrThrow().ManagedIdentityId.ShouldBe(id);
        rebound.GetValueOrThrow().Issuer.Issuer.ShouldBe(second.Issuer);

        // The old cluster's tokens stop working the moment the binding moves.
        (await cluster.ManagedIdentity(id).ExchangeAsync(
            first.ProjectedToken("prod", "app", Now.AddHours(1)),
            TokenExchange.JwtSubjectTokenType
        )).IsFailure.ShouldBeTrue();

        (await cluster.ManagedIdentity(id).ExchangeAsync(
            second.ProjectedToken("staging", "app", Now.AddHours(1)),
            TokenExchange.JwtSubjectTokenType
        )).IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public async Task RefreshingTheIssuerCannotRepointTheBinding() {
        // ⚠ "Read once, refreshed" (docs/plan/11 § Managed identity, step 3) must not become a second
        // way to bind. RefreshIssuerAsync takes no URL: it re-reads the one already recorded, so a
        // refresh cannot move an identity to a cluster the tenant never approved.
        var (id, _) = await BoundAsync();

        typeof(IManagedIdentityGrain)
            .GetMethod(nameof(IManagedIdentityGrain.RefreshIssuerAsync))!
            .GetParameters()
            .ShouldBeEmpty();

        (await cluster.ManagedIdentity(id).RefreshIssuerAsync()).IsSuccess.ShouldBeTrue();

        // An unbound identity has nothing to refresh, and says so plainly — this caller is an
        // administrator, not a workload.
        (await cluster.ManagedIdentity(id).UnbindAsync()).IsSuccess.ShouldBeTrue();

        var refused = await cluster.ManagedIdentity(id).RefreshIssuerAsync();

        refused.IsFailure.ShouldBeTrue();
        refused.Error!.Message.ShouldContain("not bound");
    }

    [Fact]
    public async Task AnIdentityIsScopedToItsTenantLikeEveryOtherIdentityGrain() {
        var (id, signer) = await BoundAsync();

        // The same GUID in another tenant is another identity, and it does not exist.
        var other = await cluster.ManagedIdentity(id, IdentityCluster.OtherTenant).GetAsync();

        other.IsFailure.ShouldBeTrue();
        other.Error!.Code.ShouldBe(ErrorCode.ResourceNotFound);

        (await cluster.ManagedIdentity(id, IdentityCluster.OtherTenant).ExchangeAsync(
            signer.ProjectedToken("prod", "app", Now.AddHours(1)),
            TokenExchange.JwtSubjectTokenType
        )).IsFailure.ShouldBeTrue();
    }
}
